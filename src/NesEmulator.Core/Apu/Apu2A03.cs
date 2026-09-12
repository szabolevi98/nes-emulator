namespace NesEmulator.Core.Apu;

/// <summary>
/// The sound half of the 2A03: two square waves, a triangle, a noise generator
/// and a sample player, mixed together into one output.
///
/// Two things drive it. The channel timers run off the processor clock, and a
/// separate frame counter ticks four or five times a frame to clock the
/// envelopes, the sweeps and the length counters — the parts that shape a note
/// over time rather than produce the waveform. That counter is also the one
/// interrupt source a game can get without a cartridge that provides its own.
///
/// The mixer is deliberately not linear. On the real chip the channels feed a
/// resistor ladder, so a loud channel compresses the others; the formulas here
/// are the published approximations of that ladder. Summing the channels evenly
/// instead is the usual reason an emulator sounds harsh.
/// </summary>
public sealed class Apu2A03
{
    /// <summary>Processor cycles in one second on an NTSC console.</summary>
    public const double ClockRate = 1789773.0;

    // The four points in a frame where the counter fires, in processor cycles.
    private const int Step1 = 7457;
    private const int Step2 = 14913;
    private const int Step3 = 22371;
    private const int Step4 = 29829;
    private const int Step5 = 37281;

    private static readonly float[] PulseMix = BuildPulseMix();
    private static readonly float[] TndMix = BuildTndMix();

    private readonly float[] _samples;
    private readonly int _sampleRate;

    private int _writeIndex;
    private int _readIndex;
    private int _bufferedCount;

    private long _cycle;
    private int _frameCounter;
    private bool _fiveStepMode;
    private bool _frameIrqDisabled;
    private bool _frameIrqPending;

    /// <summary>Fractional accumulator deciding when the next sample is due.</summary>
    private double _sampleCounter;

    public Apu2A03(int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        _samples = new float[sampleRate]; // a second of slack is plenty
    }

    public PulseChannel Pulse1 { get; } = new(isFirstChannel: true);

    public PulseChannel Pulse2 { get; } = new(isFirstChannel: false);

    public TriangleChannel Triangle { get; } = new();

    public NoiseChannel Noise { get; } = new();

    public DmcChannel Dmc { get; } = new();

    /// <summary>True while either interrupt source is asking to be serviced.</summary>
    public bool IrqPending => _frameIrqPending || Dmc.IrqPending;

    public int AvailableSamples => _bufferedCount;

    public void Reset()
    {
        _cycle = 0;
        _frameCounter = 0;
        _frameIrqPending = false;
        _writeIndex = 0;
        _readIndex = 0;
        _bufferedCount = 0;
        _sampleCounter = 0;
        WriteRegister(0x4015, 0);
    }

    /// <summary>Takes finished samples out of the ring, returning how many were copied.</summary>
    public int ReadSamples(float[] destination, int count)
    {
        int taken = Math.Min(count, _bufferedCount);
        for (int i = 0; i < taken; i++)
        {
            destination[i] = _samples[_readIndex];
            _readIndex = (_readIndex + 1) % _samples.Length;
        }

        _bufferedCount -= taken;
        return taken;
    }

    public void DiscardSamples()
    {
        _readIndex = _writeIndex;
        _bufferedCount = 0;
    }

    // ------------------------------------------------------------- registers

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000: Pulse1.WriteControl(value); break;
            case 0x4001: Pulse1.WriteSweep(value); break;
            case 0x4002: Pulse1.WriteTimerLow(value); break;
            case 0x4003: Pulse1.WriteTimerHigh(value); break;

            case 0x4004: Pulse2.WriteControl(value); break;
            case 0x4005: Pulse2.WriteSweep(value); break;
            case 0x4006: Pulse2.WriteTimerLow(value); break;
            case 0x4007: Pulse2.WriteTimerHigh(value); break;

            case 0x4008: Triangle.WriteLinear(value); break;
            case 0x400A: Triangle.WriteTimerLow(value); break;
            case 0x400B: Triangle.WriteTimerHigh(value); break;

            case 0x400C: Noise.WriteControl(value); break;
            case 0x400E: Noise.WritePeriod(value); break;
            case 0x400F: Noise.WriteLength(value); break;

            case 0x4010: Dmc.WriteControl(value); break;
            case 0x4011: Dmc.WriteDirectLoad(value); break;
            case 0x4012: Dmc.WriteAddress(value); break;
            case 0x4013: Dmc.WriteLength(value); break;

            case 0x4015:
                SetEnabled(Pulse1.Length, (value & 0x01) != 0);
                SetEnabled(Pulse2.Length, (value & 0x02) != 0);
                SetEnabled(Triangle.Length, (value & 0x04) != 0);
                SetEnabled(Noise.Length, (value & 0x08) != 0);
                Dmc.SetEnabled((value & 0x10) != 0);
                Dmc.ClearIrq();
                break;

            case 0x4017:
                _fiveStepMode = (value & 0x80) != 0;
                _frameIrqDisabled = (value & 0x40) != 0;
                if (_frameIrqDisabled)
                {
                    _frameIrqPending = false;
                }

                _frameCounter = 0;

                // Switching to five step mode clocks everything once immediately.
                if (_fiveStepMode)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }

                break;
        }
    }

    private static void SetEnabled(LengthCounter counter, bool enabled)
    {
        counter.Enabled = enabled;
        if (!enabled)
        {
            counter.Disable();
        }
    }

    /// <summary>$4015 tells the game which channels are still sounding.</summary>
    public byte ReadStatus()
    {
        byte value = 0;
        if (Pulse1.Length.Active) value |= 0x01;
        if (Pulse2.Length.Active) value |= 0x02;
        if (Triangle.Length.Active) value |= 0x04;
        if (Noise.Length.Active) value |= 0x08;
        if (Dmc.Active) value |= 0x10;
        if (_frameIrqPending) value |= 0x40;
        if (Dmc.IrqPending) value |= 0x80;

        // Reading acknowledges the frame interrupt but not the sample one.
        _frameIrqPending = false;
        return value;
    }

    // ----------------------------------------------------------------- clock

    /// <summary>Advances one processor cycle.</summary>
    public void Step()
    {
        // The triangle runs at the full rate; everything else at half of it.
        Triangle.Clock();

        if ((_cycle & 1) == 0)
        {
            Pulse1.Clock();
            Pulse2.Clock();
            Noise.Clock();
        }

        Dmc.Clock();

        StepFrameCounter();

        _cycle++;

        _sampleCounter += _sampleRate;
        if (_sampleCounter >= ClockRate)
        {
            _sampleCounter -= ClockRate;
            PushSample(Mix());
        }
    }

    private void StepFrameCounter()
    {
        _frameCounter++;

        if (!_fiveStepMode)
        {
            switch (_frameCounter)
            {
                case Step1:
                case Step3:
                    ClockQuarterFrame();
                    break;

                case Step2:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;

                case Step4:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (!_frameIrqDisabled)
                    {
                        _frameIrqPending = true;
                    }

                    _frameCounter = 0;
                    break;
            }

            return;
        }

        switch (_frameCounter)
        {
            case Step1:
            case Step3:
                ClockQuarterFrame();
                break;

            case Step2:
                ClockQuarterFrame();
                ClockHalfFrame();
                break;

            case Step5:
                ClockQuarterFrame();
                ClockHalfFrame();
                _frameCounter = 0;
                break;
        }
    }

    /// <summary>Volume shaping, four times a frame.</summary>
    private void ClockQuarterFrame()
    {
        Pulse1.Envelope.Clock();
        Pulse2.Envelope.Clock();
        Noise.Envelope.Clock();
        Triangle.ClockLinear();
    }

    /// <summary>Note length and pitch sweeps, twice a frame.</summary>
    private void ClockHalfFrame()
    {
        Pulse1.Length.Clock();
        Pulse2.Length.Clock();
        Triangle.Length.Clock();
        Noise.Length.Clock();
        Pulse1.ClockSweep();
        Pulse2.ClockSweep();
    }

    // ----------------------------------------------------------------- mixer

    private float Mix()
    {
        int pulses = Pulse1.Output() + Pulse2.Output();
        int tnd = (3 * Triangle.Output()) + (2 * Noise.Output()) + Dmc.Output();
        return PulseMix[pulses] + TndMix[tnd];
    }

    private static float[] BuildPulseMix()
    {
        float[] table = new float[31];
        for (int i = 1; i < table.Length; i++)
        {
            table[i] = (float)(95.88 / ((8128.0 / i) + 100.0));
        }

        return table;
    }

    private static float[] BuildTndMix()
    {
        float[] table = new float[203];
        for (int i = 1; i < table.Length; i++)
        {
            table[i] = (float)(163.67 / ((24329.0 / i) + 100.0));
        }

        return table;
    }

    private void PushSample(float value)
    {
        if (_bufferedCount == _samples.Length)
        {
            // Nothing is draining the ring; drop the oldest rather than block.
            _readIndex = (_readIndex + 1) % _samples.Length;
            _bufferedCount--;
        }

        _samples[_writeIndex] = value;
        _writeIndex = (_writeIndex + 1) % _samples.Length;
        _bufferedCount++;
    }
}
