namespace NesEmulator.Core.Apu;

/// <summary>
/// One of the two square wave channels, which between them carry the melody and
/// the harmony of almost every game on the console.
///
/// The waveform is one of four duty patterns clocked round at a rate set by an
/// eleven bit timer, so pitch is a divider rather than a frequency. On top of
/// that sits an envelope for volume and a sweep unit that walks the timer up or
/// down on its own, which is where sirens and laser sounds come from.
/// </summary>
public sealed class PulseChannel(bool isFirstChannel)
{
    private static readonly byte[][] DutyPatterns =
    [
        [0, 1, 0, 0, 0, 0, 0, 0], // one eighth
        [0, 1, 1, 0, 0, 0, 0, 0], // one quarter
        [0, 1, 1, 1, 1, 0, 0, 0], // half
        [1, 0, 0, 1, 1, 1, 1, 1], // one quarter, the other way up
    ];

    /// <summary>
    /// The two channels negate a descending sweep differently: the first is one
    /// short, which is audible as a semitone of drift between them.
    /// </summary>
    private readonly bool _isFirstChannel = isFirstChannel;

    private int _duty;
    private int _sequence;
    private int _timer;
    private int _timerPeriod;

    private bool _sweepEnabled;
    private bool _sweepNegate;
    private bool _sweepReload;
    private int _sweepPeriod;
    private int _sweepShift;
    private int _sweepDivider;

    public LengthCounter Length { get; } = new();

    public Envelope Envelope { get; } = new();

    /// <summary>
    /// A period below eight, or one that would sweep past the top, is silenced
    /// rather than played. Without this a sweeping channel would wrap around and
    /// screech.
    /// </summary>
    private bool Muted => _timerPeriod < 8 || TargetPeriod > 0x7FF;

    private int TargetPeriod
    {
        get
        {
            int change = _timerPeriod >> _sweepShift;
            if (!_sweepNegate)
            {
                return _timerPeriod + change;
            }

            return _timerPeriod - change - (_isFirstChannel ? 1 : 0);
        }
    }

    public void WriteControl(byte value)
    {
        _duty = value >> 6;
        Length.Halted = (value & 0x20) != 0;
        Envelope.Loop = (value & 0x20) != 0;
        Envelope.ConstantVolume = (value & 0x10) != 0;
        Envelope.Volume = value & 0x0F;
    }

    public void WriteSweep(byte value)
    {
        _sweepEnabled = (value & 0x80) != 0;
        _sweepPeriod = (value >> 4) & 0x07;
        _sweepNegate = (value & 0x08) != 0;
        _sweepShift = value & 0x07;
        _sweepReload = true;
    }

    public void WriteTimerLow(byte value) => _timerPeriod = (_timerPeriod & 0x0700) | value;

    public void WriteTimerHigh(byte value)
    {
        _timerPeriod = (_timerPeriod & 0x00FF) | ((value & 0x07) << 8);
        Length.Load((byte)(value >> 3));
        _sequence = 0;
        Envelope.Restart();
    }

    /// <summary>Clocked every other processor cycle.</summary>
    public void Clock()
    {
        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = _timerPeriod;
        _sequence = (_sequence + 1) & 7;
    }

    public void ClockSweep()
    {
        if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !Muted)
        {
            _timerPeriod = TargetPeriod;
        }

        if (_sweepDivider == 0 || _sweepReload)
        {
            _sweepDivider = _sweepPeriod;
            _sweepReload = false;
        }
        else
        {
            _sweepDivider--;
        }
    }

    public int Output()
    {
        if (Muted || !Length.Active || DutyPatterns[_duty][_sequence] == 0)
        {
            return 0;
        }

        return Envelope.Output;
    }

    internal void SaveState(BinaryWriter writer)
    {
        Length.SaveState(writer);
        Envelope.SaveState(writer);
        writer.Write(_duty);
        writer.Write(_sequence);
        writer.Write(_timer);
        writer.Write(_timerPeriod);
        writer.Write(_sweepEnabled);
        writer.Write(_sweepNegate);
        writer.Write(_sweepReload);
        writer.Write(_sweepPeriod);
        writer.Write(_sweepShift);
        writer.Write(_sweepDivider);
    }

    internal void LoadState(BinaryReader reader)
    {
        Length.LoadState(reader);
        Envelope.LoadState(reader);
        _duty = reader.ReadInt32();
        _sequence = reader.ReadInt32();
        _timer = reader.ReadInt32();
        _timerPeriod = reader.ReadInt32();
        _sweepEnabled = reader.ReadBoolean();
        _sweepNegate = reader.ReadBoolean();
        _sweepReload = reader.ReadBoolean();
        _sweepPeriod = reader.ReadInt32();
        _sweepShift = reader.ReadInt32();
        _sweepDivider = reader.ReadInt32();
    }
}
