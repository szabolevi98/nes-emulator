namespace NesEmulator.Core.Input;

[Flags]
public enum NesButton : byte
{
    None = 0,
    A = 0x01,
    B = 0x02,
    Select = 0x04,
    Start = 0x08,
    Up = 0x10,
    Down = 0x20,
    Left = 0x40,
    Right = 0x80,
}

/// <summary>
/// A standard controller. There are only three wires to it, so the console
/// cannot ask for all eight buttons at once: it pulses the strobe line to latch
/// the current state into a shift register, then clocks the buttons out one at a
/// time, A first and Right last.
/// </summary>
public sealed class Controller
{
    private byte _shiftRegister;
    private bool _strobe;

    /// <summary>What is held down right now, set by whatever is driving the emulator.</summary>
    public NesButton Buttons { get; set; }

    public void Write(byte value)
    {
        // Raising the line does not itself load the register: the parallel load is
        // level triggered and only takes effect while a put cycle ends. A one-cycle
        // pulse therefore latches only when the write that raised it was a put.
        _strobe = (value & 0x01) != 0;
    }

    /// <summary>
    /// The processor is crossing from a get cycle into a put cycle. The parallel
    /// load happens on that edge, so a strobe raised on a put cycle and dropped
    /// again on the next get cycle never reaches the shift register.
    /// </summary>
    internal void SampleStrobe()
    {
        if (_strobe)
        {
            _shiftRegister = (byte)Buttons;
        }
    }

    public byte Read()
    {
        // While the strobe is held high the register keeps reloading, so the port
        // reports the A button over and over.
        if (_strobe)
        {
            _shiftRegister = (byte)Buttons;
        }

        byte bit = (byte)(_shiftRegister & 0x01);
        // A standard pad shifts in ones after its eight buttons are exhausted.
        _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);

        // Only the data line is driven. The expansion lines read back low on a
        // plain controller, and the bus supplies the three bits above them.
        return bit;
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write((byte)Buttons);
        writer.Write(_shiftRegister);
        writer.Write(_strobe);
    }

    internal void LoadState(BinaryReader reader)
    {
        Buttons = (NesButton)reader.ReadByte();
        _shiftRegister = reader.ReadByte();
        _strobe = reader.ReadBoolean();
    }
}
