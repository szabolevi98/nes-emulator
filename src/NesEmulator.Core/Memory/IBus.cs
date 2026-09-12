namespace NesEmulator.Core.Memory;

/// <summary>
/// Everything the CPU can reach through its 16-bit address space. The CPU knows
/// nothing about what lives behind an address, which is what lets the test suite
/// run it against a flat array of RAM instead of a whole console.
/// </summary>
public interface IBus
{
    byte Read(ushort address);

    void Write(ushort address, byte value);

    /// <summary>
    /// Reads without side effects, for the disassembler and the debugger. Some
    /// hardware registers change state when read, and a trace window must not
    /// alter the run it is describing.
    /// </summary>
    byte Peek(ushort address) => Read(address);
}
