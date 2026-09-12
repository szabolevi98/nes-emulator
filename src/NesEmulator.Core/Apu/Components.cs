namespace NesEmulator.Core.Apu;

/// <summary>
/// How long a note keeps playing after the game stops asking for it. Loading a
/// channel's length register picks one of these, and the counter ticks down
/// twice a frame until it reaches zero and silences the channel. It is why a
/// game can fire a sound effect and forget about it.
/// </summary>
public sealed class LengthCounter
{
    public static readonly byte[] Table =
    [
        10, 254, 20,  2, 40,  4, 80,  6, 160,  8, 60, 10, 14, 12, 26, 14,
        12,  16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    ];

    public bool Enabled { get; set; }

    public bool Halted { get; set; }

    public byte Value { get; private set; }

    public bool Active => Value > 0;

    public void Load(byte index)
    {
        if (Enabled)
        {
            Value = Table[index & 0x1F];
        }
    }

    public void Clock()
    {
        if (!Halted && Value > 0)
        {
            Value--;
        }
    }

    public void Disable() => Value = 0;
}

/// <summary>
/// The volume shaper on the pulse and noise channels. It either holds a constant
/// volume or decays from fifteen down to zero at a chosen speed, which is the
/// whole reason those channels can produce a plucked note rather than a flat beep.
/// </summary>
public sealed class Envelope
{
    private bool _start;
    private int _divider;
    private int _decay;

    public bool Loop { get; set; }

    public bool ConstantVolume { get; set; }

    /// <summary>Doubles as the divider period when the envelope is decaying.</summary>
    public int Volume { get; set; }

    public int Output => ConstantVolume ? Volume : _decay;

    public void Restart() => _start = true;

    public void Clock()
    {
        if (_start)
        {
            _start = false;
            _decay = 15;
            _divider = Volume;
            return;
        }

        if (_divider > 0)
        {
            _divider--;
            return;
        }

        _divider = Volume;

        if (_decay > 0)
        {
            _decay--;
        }
        else if (Loop)
        {
            _decay = 15;
        }
    }
}
