namespace NesEmulator.Core.Cpu;

/// <summary>
/// Every operation the 2A03 can perform, including the undocumented ones.
/// A handful of games and most accuracy test ROMs rely on the illegal opcodes,
/// so they are first-class citizens here rather than an afterthought.
/// </summary>
public enum Op : byte
{
    // Documented.
    ADC, AND, ASL, BCC, BCS, BEQ, BIT, BMI, BNE, BPL, BRK, BVC, BVS, CLC,
    CLD, CLI, CLV, CMP, CPX, CPY, DEC, DEX, DEY, EOR, INC, INX, INY, JMP,
    JSR, LDA, LDX, LDY, LSR, NOP, ORA, PHA, PHP, PLA, PLP, ROL, ROR, RTI,
    RTS, SBC, SEC, SED, SEI, STA, STX, STY, TAX, TAY, TSX, TXA, TXS, TYA,

    // Undocumented. The first eight are plain combinations of two documented
    // operations; the rest are unstable and depend on analog behaviour.
    SLO, RLA, SRE, RRA, SAX, LAX, DCP, ISC,
    ANC, ALR, ARR, AXS, XAA, LAS, AHX, SHY, SHX, TAS,

    /// <summary>Locks the processor up until reset. Also known as KIL or HLT.</summary>
    JAM,
}

/// <summary>How an instruction works out the address of its operand.</summary>
public enum Am : byte
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Relative,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndexedIndirect, // (zp,X)
    IndirectIndexed, // (zp),Y
}

/// <param name="Op">The operation to perform.</param>
/// <param name="Mode">How to find its operand.</param>
/// <param name="Cycles">Base cycle count, before any penalties.</param>
/// <param name="PageCross">
/// Whether crossing a page boundary while indexing costs an extra cycle. Only
/// reads pay it; writes always take the longer path, so their base count
/// already includes it.
/// </param>
public readonly record struct OpcodeInfo(Op Op, Am Mode, byte Cycles, bool PageCross = false);
