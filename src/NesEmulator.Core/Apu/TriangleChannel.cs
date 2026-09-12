namespace NesEmulator.Core.Apu;

/// <summary>
/// The bass line. A fixed thirty-two step staircase from fifteen down to zero and
/// back, with no volume control at all: it is either sounding or silent. That
/// stepped shape is why the low end of this console has its particular hollow
/// tone rather than a smooth triangle.
///
/// It is clocked every processor cycle instead of every other one, so for the
/// same timer value it sounds an octave below a pulse channel.
/// </summary>
public sealed class TriangleChannel
{
    private static readonly byte[] Steps =
    [
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    ];

    private int _sequence;
    private int _timer;
    private int _timerPeriod;

    private bool _control;
    private bool _linearReload;
    private int _linearPeriod;
    private int _linearCounter;

    public LengthCounter Length { get; } = new();

    public void WriteLinear(byte value)
    {
        _control = (value & 0x80) != 0;
        Length.Halted = _control;
        _linearPeriod = value & 0x7F;
    }

    public void WriteTimerLow(byte value) => _timerPeriod = (_timerPeriod & 0x0700) | value;

    public void WriteTimerHigh(byte value)
    {
        _timerPeriod = (_timerPeriod & 0x00FF) | ((value & 0x07) << 8);
        Length.Load((byte)(value >> 3));
        _linearReload = true;
    }

    public void Clock()
    {
        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = _timerPeriod;

        // Ultrasonic periods still clock the DAC; the output resampler removes
        // those frequencies instead of freezing the waveform at an arbitrary level.
        if (Length.Active && _linearCounter > 0)
        {
            _sequence = (_sequence + 1) & 31;
        }
    }

    /// <summary>
    /// The extra counter this channel has on top of its length counter, which
    /// gives it a much finer grip on note duration.
    /// </summary>
    public void ClockLinear()
    {
        if (_linearReload)
        {
            _linearCounter = _linearPeriod;
        }
        else if (_linearCounter > 0)
        {
            _linearCounter--;
        }

        if (!_control)
        {
            _linearReload = false;
        }
    }

    public int Output() => Steps[_sequence];

    internal void SaveState(BinaryWriter writer)
    {
        Length.SaveState(writer);
        writer.Write(_sequence);
        writer.Write(_timer);
        writer.Write(_timerPeriod);
        writer.Write(_control);
        writer.Write(_linearReload);
        writer.Write(_linearPeriod);
        writer.Write(_linearCounter);
    }

    internal void LoadState(BinaryReader reader)
    {
        Length.LoadState(reader);
        _sequence = reader.ReadInt32();
        _timer = reader.ReadInt32();
        _timerPeriod = reader.ReadInt32();
        _control = reader.ReadBoolean();
        _linearReload = reader.ReadBoolean();
        _linearPeriod = reader.ReadInt32();
        _linearCounter = reader.ReadInt32();
    }
}
