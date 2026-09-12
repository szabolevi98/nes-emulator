namespace NesEmulator.Core.Apu;

/// <summary>
/// The percussion and the explosions. There is no wavetable here: a fifteen bit
/// shift register feeds back on itself, and the bit that falls out is the output.
/// The sequence repeats, but only after 32,767 steps, so it passes for noise.
///
/// Flipping the mode bit taps a different bit of the register, which shortens the
/// cycle to 93 steps. That is short enough to hear as a pitch, and games use it
/// for metallic and engine sounds rather than for hiss.
/// </summary>
public sealed class NoiseChannel
{
    /// <summary>Sixteen periods in CPU cycles; Clock itself runs at CPU/2.</summary>
    private static readonly int[] Periods =
    [
        4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
    ];

    private int _shiftRegister = 1;
    private int _timer;
    private int _timerPeriod = Periods[0];
    private bool _shortMode;

    public LengthCounter Length { get; } = new();

    public Envelope Envelope { get; } = new();

    public void WriteControl(byte value)
    {
        Length.Halted = (value & 0x20) != 0;
        Envelope.Loop = (value & 0x20) != 0;
        Envelope.ConstantVolume = (value & 0x10) != 0;
        Envelope.Volume = value & 0x0F;
    }

    public void WritePeriod(byte value)
    {
        _shortMode = (value & 0x80) != 0;
        _timerPeriod = Periods[value & 0x0F];
    }

    public void WriteLength(byte value)
    {
        Length.Load((byte)(value >> 3));
        Envelope.Restart();
    }

    public void Clock()
    {
        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = _timerPeriod / 2 - 1;

        int tap = _shortMode ? (_shiftRegister >> 6) & 1 : (_shiftRegister >> 1) & 1;
        int feedback = (_shiftRegister & 1) ^ tap;
        _shiftRegister = (_shiftRegister >> 1) | (feedback << 14);
    }

    public int Output()
    {
        // The output is inverted: a set low bit means silence.
        if ((_shiftRegister & 1) != 0 || !Length.Active)
        {
            return 0;
        }

        return Envelope.Output;
    }

    internal void SaveState(BinaryWriter writer)
    {
        Length.SaveState(writer);
        Envelope.SaveState(writer);
        writer.Write(_shiftRegister);
        writer.Write(_timer);
        writer.Write(_timerPeriod);
        writer.Write(_shortMode);
    }

    internal void LoadState(BinaryReader reader)
    {
        Length.LoadState(reader);
        Envelope.LoadState(reader);
        _shiftRegister = reader.ReadInt32();
        _timer = reader.ReadInt32();
        _timerPeriod = reader.ReadInt32();
        _shortMode = reader.ReadBoolean();
    }
}
