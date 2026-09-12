using System.Text;
using NesEmulator.Core.Memory;

namespace NesEmulator.Core.Cpu;

/// <summary>
/// Turns bytes back into readable instructions. The opcode table already knows
/// every mnemonic and operand shape, so this is mostly formatting — which is the
/// payoff for having written the table out in full.
///
/// It reads through <see cref="IBus.Peek"/> so that watching a program can never
/// change it.
/// </summary>
public static class Disassembler
{
    /// <summary>Decodes the instruction at <paramref name="address"/>.</summary>
    public static string Describe(IBus bus, ushort address, out int length)
    {
        byte opcode = bus.Peek(address);
        OpcodeInfo info = OpcodeTable.Entries[opcode];
        length = OpcodeTable.Length(info.Mode);

        byte low = bus.Peek((ushort)(address + 1));
        byte high = bus.Peek((ushort)(address + 2));
        ushort word = (ushort)(low | (high << 8));

        string operand = info.Mode switch
        {
            Am.Implied => string.Empty,
            Am.Accumulator => "A",
            Am.Immediate => $"#${low:X2}",
            Am.ZeroPage => $"${low:X2}",
            Am.ZeroPageX => $"${low:X2},X",
            Am.ZeroPageY => $"${low:X2},Y",
            Am.Relative => $"${(ushort)(address + 2 + (sbyte)low):X4}",
            Am.Absolute => $"${word:X4}",
            Am.AbsoluteX => $"${word:X4},X",
            Am.AbsoluteY => $"${word:X4},Y",
            Am.Indirect => $"(${word:X4})",
            Am.IndexedIndirect => $"(${low:X2},X)",
            Am.IndirectIndexed => $"(${low:X2}),Y",
            _ => "?",
        };

        return operand.Length == 0 ? info.Op.ToString() : $"{info.Op} {operand}";
    }

    /// <summary>
    /// One line of execution trace, in the shape the nestest reference log uses:
    /// address, raw bytes, the decoded instruction, then the register file.
    /// </summary>
    public static string TraceLine(Cpu6502 cpu, IBus bus)
    {
        string text = Describe(bus, cpu.PC, out int length);

        StringBuilder bytes = new(9);
        for (int i = 0; i < length; i++)
        {
            bytes.Append(bus.Peek((ushort)(cpu.PC + i)).ToString("X2")).Append(' ');
        }

        return $"{cpu.PC:X4}  {bytes.ToString().PadRight(9)} {text,-31} " +
               $"A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.P:X2} SP:{cpu.S:X2} CYC:{cpu.Cycles}";
    }
}
