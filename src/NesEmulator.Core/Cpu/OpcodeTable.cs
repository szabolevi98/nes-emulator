namespace NesEmulator.Core.Cpu;

/// <summary>
/// The full 6502 opcode matrix, one entry per byte value. Laid out four to a
/// line with the opcode of the first in a comment, so a row here lines up with
/// a row of the published opcode matrix and can be checked against it by eye.
///
/// A trailing <c>true</c> marks the instructions that pay an extra cycle when
/// indexing crosses a page boundary.
/// </summary>
public static class OpcodeTable
{
    public static readonly OpcodeInfo[] Entries =
    [
        /* 00 */ new(Op.BRK, Am.Implied, 7),         new(Op.ORA, Am.IndexedIndirect, 6), new(Op.JAM, Am.Implied, 2),         new(Op.SLO, Am.IndexedIndirect, 8),
        /* 04 */ new(Op.NOP, Am.ZeroPage, 3),        new(Op.ORA, Am.ZeroPage, 3),        new(Op.ASL, Am.ZeroPage, 5),        new(Op.SLO, Am.ZeroPage, 5),
        /* 08 */ new(Op.PHP, Am.Implied, 3),         new(Op.ORA, Am.Immediate, 2),       new(Op.ASL, Am.Accumulator, 2),     new(Op.ANC, Am.Immediate, 2),
        /* 0C */ new(Op.NOP, Am.Absolute, 4),        new(Op.ORA, Am.Absolute, 4),        new(Op.ASL, Am.Absolute, 6),        new(Op.SLO, Am.Absolute, 6),

        /* 10 */ new(Op.BPL, Am.Relative, 2),        new(Op.ORA, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.SLO, Am.IndirectIndexed, 8),
        /* 14 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.ORA, Am.ZeroPageX, 4),       new(Op.ASL, Am.ZeroPageX, 6),       new(Op.SLO, Am.ZeroPageX, 6),
        /* 18 */ new(Op.CLC, Am.Implied, 2),         new(Op.ORA, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.SLO, Am.AbsoluteY, 7),
        /* 1C */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.ORA, Am.AbsoluteX, 4, true), new(Op.ASL, Am.AbsoluteX, 7),       new(Op.SLO, Am.AbsoluteX, 7),

        /* 20 */ new(Op.JSR, Am.Absolute, 6),        new(Op.AND, Am.IndexedIndirect, 6), new(Op.JAM, Am.Implied, 2),         new(Op.RLA, Am.IndexedIndirect, 8),
        /* 24 */ new(Op.BIT, Am.ZeroPage, 3),        new(Op.AND, Am.ZeroPage, 3),        new(Op.ROL, Am.ZeroPage, 5),        new(Op.RLA, Am.ZeroPage, 5),
        /* 28 */ new(Op.PLP, Am.Implied, 4),         new(Op.AND, Am.Immediate, 2),       new(Op.ROL, Am.Accumulator, 2),     new(Op.ANC, Am.Immediate, 2),
        /* 2C */ new(Op.BIT, Am.Absolute, 4),        new(Op.AND, Am.Absolute, 4),        new(Op.ROL, Am.Absolute, 6),        new(Op.RLA, Am.Absolute, 6),

        /* 30 */ new(Op.BMI, Am.Relative, 2),        new(Op.AND, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.RLA, Am.IndirectIndexed, 8),
        /* 34 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.AND, Am.ZeroPageX, 4),       new(Op.ROL, Am.ZeroPageX, 6),       new(Op.RLA, Am.ZeroPageX, 6),
        /* 38 */ new(Op.SEC, Am.Implied, 2),         new(Op.AND, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.RLA, Am.AbsoluteY, 7),
        /* 3C */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.AND, Am.AbsoluteX, 4, true), new(Op.ROL, Am.AbsoluteX, 7),       new(Op.RLA, Am.AbsoluteX, 7),

        /* 40 */ new(Op.RTI, Am.Implied, 6),         new(Op.EOR, Am.IndexedIndirect, 6), new(Op.JAM, Am.Implied, 2),         new(Op.SRE, Am.IndexedIndirect, 8),
        /* 44 */ new(Op.NOP, Am.ZeroPage, 3),        new(Op.EOR, Am.ZeroPage, 3),        new(Op.LSR, Am.ZeroPage, 5),        new(Op.SRE, Am.ZeroPage, 5),
        /* 48 */ new(Op.PHA, Am.Implied, 3),         new(Op.EOR, Am.Immediate, 2),       new(Op.LSR, Am.Accumulator, 2),     new(Op.ALR, Am.Immediate, 2),
        /* 4C */ new(Op.JMP, Am.Absolute, 3),        new(Op.EOR, Am.Absolute, 4),        new(Op.LSR, Am.Absolute, 6),        new(Op.SRE, Am.Absolute, 6),

        /* 50 */ new(Op.BVC, Am.Relative, 2),        new(Op.EOR, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.SRE, Am.IndirectIndexed, 8),
        /* 54 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.EOR, Am.ZeroPageX, 4),       new(Op.LSR, Am.ZeroPageX, 6),       new(Op.SRE, Am.ZeroPageX, 6),
        /* 58 */ new(Op.CLI, Am.Implied, 2),         new(Op.EOR, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.SRE, Am.AbsoluteY, 7),
        /* 5C */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.EOR, Am.AbsoluteX, 4, true), new(Op.LSR, Am.AbsoluteX, 7),       new(Op.SRE, Am.AbsoluteX, 7),

        /* 60 */ new(Op.RTS, Am.Implied, 6),         new(Op.ADC, Am.IndexedIndirect, 6), new(Op.JAM, Am.Implied, 2),         new(Op.RRA, Am.IndexedIndirect, 8),
        /* 64 */ new(Op.NOP, Am.ZeroPage, 3),        new(Op.ADC, Am.ZeroPage, 3),        new(Op.ROR, Am.ZeroPage, 5),        new(Op.RRA, Am.ZeroPage, 5),
        /* 68 */ new(Op.PLA, Am.Implied, 4),         new(Op.ADC, Am.Immediate, 2),       new(Op.ROR, Am.Accumulator, 2),     new(Op.ARR, Am.Immediate, 2),
        /* 6C */ new(Op.JMP, Am.Indirect, 5),        new(Op.ADC, Am.Absolute, 4),        new(Op.ROR, Am.Absolute, 6),        new(Op.RRA, Am.Absolute, 6),

        /* 70 */ new(Op.BVS, Am.Relative, 2),        new(Op.ADC, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.RRA, Am.IndirectIndexed, 8),
        /* 74 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.ADC, Am.ZeroPageX, 4),       new(Op.ROR, Am.ZeroPageX, 6),       new(Op.RRA, Am.ZeroPageX, 6),
        /* 78 */ new(Op.SEI, Am.Implied, 2),         new(Op.ADC, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.RRA, Am.AbsoluteY, 7),
        /* 7C */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.ADC, Am.AbsoluteX, 4, true), new(Op.ROR, Am.AbsoluteX, 7),       new(Op.RRA, Am.AbsoluteX, 7),

        /* 80 */ new(Op.NOP, Am.Immediate, 2),       new(Op.STA, Am.IndexedIndirect, 6), new(Op.NOP, Am.Immediate, 2),       new(Op.SAX, Am.IndexedIndirect, 6),
        /* 84 */ new(Op.STY, Am.ZeroPage, 3),        new(Op.STA, Am.ZeroPage, 3),        new(Op.STX, Am.ZeroPage, 3),        new(Op.SAX, Am.ZeroPage, 3),
        /* 88 */ new(Op.DEY, Am.Implied, 2),         new(Op.NOP, Am.Immediate, 2),       new(Op.TXA, Am.Implied, 2),         new(Op.XAA, Am.Immediate, 2),
        /* 8C */ new(Op.STY, Am.Absolute, 4),        new(Op.STA, Am.Absolute, 4),        new(Op.STX, Am.Absolute, 4),        new(Op.SAX, Am.Absolute, 4),

        /* 90 */ new(Op.BCC, Am.Relative, 2),        new(Op.STA, Am.IndirectIndexed, 6), new(Op.JAM, Am.Implied, 2),         new(Op.AHX, Am.IndirectIndexed, 6),
        /* 94 */ new(Op.STY, Am.ZeroPageX, 4),       new(Op.STA, Am.ZeroPageX, 4),       new(Op.STX, Am.ZeroPageY, 4),       new(Op.SAX, Am.ZeroPageY, 4),
        /* 98 */ new(Op.TYA, Am.Implied, 2),         new(Op.STA, Am.AbsoluteY, 5),       new(Op.TXS, Am.Implied, 2),         new(Op.TAS, Am.AbsoluteY, 5),
        /* 9C */ new(Op.SHY, Am.AbsoluteX, 5),       new(Op.STA, Am.AbsoluteX, 5),       new(Op.SHX, Am.AbsoluteY, 5),       new(Op.AHX, Am.AbsoluteY, 5),

        /* A0 */ new(Op.LDY, Am.Immediate, 2),       new(Op.LDA, Am.IndexedIndirect, 6), new(Op.LDX, Am.Immediate, 2),       new(Op.LAX, Am.IndexedIndirect, 6),
        /* A4 */ new(Op.LDY, Am.ZeroPage, 3),        new(Op.LDA, Am.ZeroPage, 3),        new(Op.LDX, Am.ZeroPage, 3),        new(Op.LAX, Am.ZeroPage, 3),
        /* A8 */ new(Op.TAY, Am.Implied, 2),         new(Op.LDA, Am.Immediate, 2),       new(Op.TAX, Am.Implied, 2),         new(Op.LAX, Am.Immediate, 2),
        /* AC */ new(Op.LDY, Am.Absolute, 4),        new(Op.LDA, Am.Absolute, 4),        new(Op.LDX, Am.Absolute, 4),        new(Op.LAX, Am.Absolute, 4),

        /* B0 */ new(Op.BCS, Am.Relative, 2),        new(Op.LDA, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.LAX, Am.IndirectIndexed, 5, true),
        /* B4 */ new(Op.LDY, Am.ZeroPageX, 4),       new(Op.LDA, Am.ZeroPageX, 4),       new(Op.LDX, Am.ZeroPageY, 4),       new(Op.LAX, Am.ZeroPageY, 4),
        /* B8 */ new(Op.CLV, Am.Implied, 2),         new(Op.LDA, Am.AbsoluteY, 4, true), new(Op.TSX, Am.Implied, 2),         new(Op.LAS, Am.AbsoluteY, 4, true),
        /* BC */ new(Op.LDY, Am.AbsoluteX, 4, true), new(Op.LDA, Am.AbsoluteX, 4, true), new(Op.LDX, Am.AbsoluteY, 4, true), new(Op.LAX, Am.AbsoluteY, 4, true),

        /* C0 */ new(Op.CPY, Am.Immediate, 2),       new(Op.CMP, Am.IndexedIndirect, 6), new(Op.NOP, Am.Immediate, 2),       new(Op.DCP, Am.IndexedIndirect, 8),
        /* C4 */ new(Op.CPY, Am.ZeroPage, 3),        new(Op.CMP, Am.ZeroPage, 3),        new(Op.DEC, Am.ZeroPage, 5),        new(Op.DCP, Am.ZeroPage, 5),
        /* C8 */ new(Op.INY, Am.Implied, 2),         new(Op.CMP, Am.Immediate, 2),       new(Op.DEX, Am.Implied, 2),         new(Op.AXS, Am.Immediate, 2),
        /* CC */ new(Op.CPY, Am.Absolute, 4),        new(Op.CMP, Am.Absolute, 4),        new(Op.DEC, Am.Absolute, 6),        new(Op.DCP, Am.Absolute, 6),

        /* D0 */ new(Op.BNE, Am.Relative, 2),        new(Op.CMP, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.DCP, Am.IndirectIndexed, 8),
        /* D4 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.CMP, Am.ZeroPageX, 4),       new(Op.DEC, Am.ZeroPageX, 6),       new(Op.DCP, Am.ZeroPageX, 6),
        /* D8 */ new(Op.CLD, Am.Implied, 2),         new(Op.CMP, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.DCP, Am.AbsoluteY, 7),
        /* DC */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.CMP, Am.AbsoluteX, 4, true), new(Op.DEC, Am.AbsoluteX, 7),       new(Op.DCP, Am.AbsoluteX, 7),

        /* E0 */ new(Op.CPX, Am.Immediate, 2),       new(Op.SBC, Am.IndexedIndirect, 6), new(Op.NOP, Am.Immediate, 2),       new(Op.ISC, Am.IndexedIndirect, 8),
        /* E4 */ new(Op.CPX, Am.ZeroPage, 3),        new(Op.SBC, Am.ZeroPage, 3),        new(Op.INC, Am.ZeroPage, 5),        new(Op.ISC, Am.ZeroPage, 5),
        /* E8 */ new(Op.INX, Am.Implied, 2),         new(Op.SBC, Am.Immediate, 2),       new(Op.NOP, Am.Implied, 2),         new(Op.SBC, Am.Immediate, 2),
        /* EC */ new(Op.CPX, Am.Absolute, 4),        new(Op.SBC, Am.Absolute, 4),        new(Op.INC, Am.Absolute, 6),        new(Op.ISC, Am.Absolute, 6),

        /* F0 */ new(Op.BEQ, Am.Relative, 2),        new(Op.SBC, Am.IndirectIndexed, 5, true), new(Op.JAM, Am.Implied, 2),   new(Op.ISC, Am.IndirectIndexed, 8),
        /* F4 */ new(Op.NOP, Am.ZeroPageX, 4),       new(Op.SBC, Am.ZeroPageX, 4),       new(Op.INC, Am.ZeroPageX, 6),       new(Op.ISC, Am.ZeroPageX, 6),
        /* F8 */ new(Op.SED, Am.Implied, 2),         new(Op.SBC, Am.AbsoluteY, 4, true), new(Op.NOP, Am.Implied, 2),         new(Op.ISC, Am.AbsoluteY, 7),
        /* FC */ new(Op.NOP, Am.AbsoluteX, 4, true), new(Op.SBC, Am.AbsoluteX, 4, true), new(Op.INC, Am.AbsoluteX, 7),       new(Op.ISC, Am.AbsoluteX, 7),
    ];

    /// <summary>How many bytes an instruction in this mode occupies, opcode included.</summary>
    public static int Length(Am mode) => mode switch
    {
        Am.Implied or Am.Accumulator => 1,
        Am.Immediate or Am.ZeroPage or Am.ZeroPageX or Am.ZeroPageY
            or Am.Relative or Am.IndexedIndirect or Am.IndirectIndexed => 2,
        _ => 3,
    };
}
