using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Memory;

int failures = 0;
int total = 0;

void Check(string name, bool condition, string detail = "")
{
    total++;
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL  {name}  {detail}");
    }
}

// A machine with the program loaded at $8000 and the reset vector pointing at it.
(Cpu6502 Cpu, FlatBus Bus) Machine(params byte[] program)
{
    FlatBus bus = new();
    bus.Memory[0xFFFC] = 0x00;
    bus.Memory[0xFFFD] = 0x80;
    program.CopyTo(bus.Memory, 0x8000);

    Cpu6502 cpu = new(bus);
    cpu.Reset();
    return (cpu, bus);
}

// ------------------------------------------------------------- opcode table

Check("table: 256 entries", OpcodeTable.Entries.Length == 256,
    $"got {OpcodeTable.Entries.Length}");

int jamCount = OpcodeTable.Entries.Count(e => e.Op == Op.JAM);
Check("table: 12 jam opcodes", jamCount == 12, $"got {jamCount}");

Check("table: $A9 is LDA immediate",
    OpcodeTable.Entries[0xA9] is { Op: Op.LDA, Mode: Am.Immediate, Cycles: 2 });
Check("table: $6C is JMP indirect",
    OpcodeTable.Entries[0x6C] is { Op: Op.JMP, Mode: Am.Indirect, Cycles: 5 });
Check("table: $9D is STA absolute,X with no page penalty",
    OpcodeTable.Entries[0x9D] is { Op: Op.STA, Mode: Am.AbsoluteX, Cycles: 5, PageCross: false });
Check("table: $BD is LDA absolute,X with a page penalty",
    OpcodeTable.Entries[0xBD] is { Op: Op.LDA, Mode: Am.AbsoluteX, Cycles: 4, PageCross: true });

// Only reads pay the page-crossing penalty. A store already takes the long path.
bool penaltyOnlyOnReads = true;
for (int i = 0; i < 256; i++)
{
    OpcodeInfo entry = OpcodeTable.Entries[i];
    if (entry.PageCross && entry.Op is Op.STA or Op.STX or Op.STY or Op.SAX)
    {
        penaltyOnlyOnReads = false;
    }
}

Check("table: stores never carry a page penalty", penaltyOnlyOnReads);

// --------------------------------------------------------------- power on

{
    (Cpu6502 cpu, _) = Machine();
    Check("reset: program counter from the vector", cpu.PC == 0x8000, $"got {cpu.PC:X4}");
    Check("reset: stack pointer", cpu.S == 0xFD, $"got {cpu.S:X2}");
    Check("reset: status", cpu.P == 0x24, $"got {cpu.P:X2}");
    Check("reset: seven cycles", cpu.Cycles == 7, $"got {cpu.Cycles}");
}

// ---------------------------------------------------------- addressing modes

{
    // LDA $FF,X with X = 2 wraps inside the zero page to $01.
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA2, 0x02, 0xB5, 0xFF);
    bus.Memory[0x0001] = 0x42;
    cpu.Step();
    cpu.Step();
    Check("zero page,X wraps inside the page", cpu.A == 0x42, $"got {cpu.A:X2}");
}

{
    // LDA ($FF,X) with X = 1 wraps to pointer $00, read from $0000 and $0001.
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA2, 0x01, 0xA1, 0xFF);
    bus.Memory[0x0000] = 0x34;
    bus.Memory[0x0001] = 0x12;
    bus.Memory[0x1234] = 0x77;
    cpu.Step();
    cpu.Step();
    Check("indexed indirect wraps inside the zero page", cpu.A == 0x77, $"got {cpu.A:X2}");
}

{
    // The indirect jump bug: the pointer at $30FF takes its high byte from $3000.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x6C, 0xFF, 0x30);
    bus.Memory[0x30FF] = 0x00;
    bus.Memory[0x3000] = 0x40;
    bus.Memory[0x3100] = 0x80;
    cpu.Step();
    Check("indirect jump reproduces the page wrap bug", cpu.PC == 0x4000, $"got {cpu.PC:X4}");
}

{
    // LDA $80FF,X with X = 1 crosses into the next page and costs a fifth cycle.
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA2, 0x01, 0xBD, 0xFF, 0x80);
    bus.Memory[0x8100] = 0x5A;
    cpu.Step();
    int cycles = cpu.Step();
    Check("absolute,X pays for crossing a page", cycles == 5, $"got {cycles}");
    Check("absolute,X reads the right byte", cpu.A == 0x5A, $"got {cpu.A:X2}");
}

{
    // Same instruction, staying inside the page.
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA2, 0x01, 0xBD, 0x00, 0x80);
    bus.Memory[0x8001] = 0x5B;
    cpu.Step();
    int cycles = cpu.Step();
    Check("absolute,X costs nothing extra inside a page", cycles == 4, $"got {cycles}");
}

{
    // STA $80FF,X always takes five cycles, crossing or not.
    (Cpu6502 cpu, _) = Machine(0xA2, 0x01, 0x9D, 0xFF, 0x80);
    cpu.Step();
    int cycles = cpu.Step();
    Check("stores do not pay the crossing penalty", cycles == 5, $"got {cycles}");
}

// ------------------------------------------------------------ arithmetic

// The textbook signed overflow cases for ADC.
(byte A, byte Operand, byte Result, bool Carry, bool Overflow)[] adcCases =
[
    (0x50, 0x10, 0x60, false, false),
    (0x50, 0x50, 0xA0, false, true),
    (0x50, 0x90, 0xE0, false, false),
    (0x50, 0xD0, 0x20, true, false),
    (0xD0, 0x90, 0x60, true, true),
];

foreach ((byte a, byte operand, byte result, bool carry, bool overflow) in adcCases)
{
    (Cpu6502 cpu, _) = Machine(0xA9, a, 0x18, 0x69, operand); // LDA #a; CLC; ADC #operand
    cpu.Step();
    cpu.Step();
    cpu.Step();

    bool gotCarry = (cpu.P & Cpu6502.FlagCarry) != 0;
    bool gotOverflow = (cpu.P & Cpu6502.FlagOverflow) != 0;
    Check($"adc: {a:X2} + {operand:X2}",
        cpu.A == result && gotCarry == carry && gotOverflow == overflow,
        $"got {cpu.A:X2} C={gotCarry} V={gotOverflow}");
}

{
    // SBC of a negative from a positive overflows: 80 - (-80) does not fit.
    (Cpu6502 cpu, _) = Machine(0xA9, 0x50, 0x38, 0xE9, 0xB0); // LDA #$50; SEC; SBC #$B0
    cpu.Step();
    cpu.Step();
    cpu.Step();
    Check("sbc: 50 - B0 overflows",
        cpu.A == 0xA0 && (cpu.P & Cpu6502.FlagOverflow) != 0 && (cpu.P & Cpu6502.FlagCarry) == 0,
        $"got {cpu.A:X2} P={cpu.P:X2}");
}

{
    // Decimal mode is fused off in the 2A03, so SED must not change the sum.
    (Cpu6502 cpu, _) = Machine(0xF8, 0xA9, 0x09, 0x18, 0x69, 0x01); // SED; LDA #$09; CLC; ADC #$01
    cpu.Step();
    cpu.Step();
    cpu.Step();
    cpu.Step();
    Check("decimal mode is ignored", cpu.A == 0x0A, $"got {cpu.A:X2}");
}

{
    (Cpu6502 cpu, _) = Machine(0xA9, 0x40, 0xC9, 0x40); // LDA #$40; CMP #$40
    cpu.Step();
    cpu.Step();
    Check("cmp: equal sets carry and zero",
        (cpu.P & Cpu6502.FlagCarry) != 0 && (cpu.P & Cpu6502.FlagZero) != 0,
        $"P={cpu.P:X2}");
}

{
    (Cpu6502 cpu, _) = Machine(0xA9, 0x10, 0xC9, 0x40); // LDA #$10; CMP #$40
    cpu.Step();
    cpu.Step();
    Check("cmp: smaller clears carry and sets negative",
        (cpu.P & Cpu6502.FlagCarry) == 0 && (cpu.P & Cpu6502.FlagNegative) != 0,
        $"P={cpu.P:X2}");
}

// --------------------------------------------------------- shifts and flags

{
    (Cpu6502 cpu, _) = Machine(0xA9, 0x81, 0x0A); // LDA #$81; ASL A
    cpu.Step();
    cpu.Step();
    Check("asl: shifts out into carry",
        cpu.A == 0x02 && (cpu.P & Cpu6502.FlagCarry) != 0, $"A={cpu.A:X2} P={cpu.P:X2}");
}

{
    (Cpu6502 cpu, _) = Machine(0x38, 0xA9, 0x01, 0x6A); // SEC; LDA #$01; ROR A
    cpu.Step();
    cpu.Step();
    cpu.Step();
    Check("ror: rotates the carry in at the top",
        cpu.A == 0x80 && (cpu.P & Cpu6502.FlagCarry) != 0, $"A={cpu.A:X2} P={cpu.P:X2}");
}

{
    (Cpu6502 cpu, _) = Machine(0xA9, 0x00); // LDA #$00
    cpu.Step();
    Check("lda: zero sets the zero flag", (cpu.P & Cpu6502.FlagZero) != 0, $"P={cpu.P:X2}");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0x0F, 0x24, 0x20); // LDA #$0F; BIT $20
    bus.Memory[0x0020] = 0xC0;
    cpu.Step();
    cpu.Step();
    Check("bit: takes N and V straight from memory",
        (cpu.P & Cpu6502.FlagNegative) != 0
        && (cpu.P & Cpu6502.FlagOverflow) != 0
        && (cpu.P & Cpu6502.FlagZero) != 0,
        $"P={cpu.P:X2}");
}

// --------------------------------------------------------------- branches

{
    // Taken, staying inside the page: three cycles.
    FlatBus bus = new();
    bus.Memory[0xFFFC] = 0xF0;
    bus.Memory[0xFFFD] = 0x80;
    bus.Memory[0x80F0] = 0xA9; // LDA #$00, to set the zero flag
    bus.Memory[0x80F1] = 0x00;
    bus.Memory[0x80F2] = 0xF0; // BEQ +$0B
    bus.Memory[0x80F3] = 0x0B;

    Cpu6502 cpu = new(bus);
    cpu.Reset();
    cpu.Step();
    int cycles = cpu.Step();
    Check("branch: taken inside the page costs three",
        cycles == 3 && cpu.PC == 0x80FF, $"cycles={cycles} pc={cpu.PC:X4}");
}

{
    // Taken across a page boundary: four cycles.
    FlatBus bus = new();
    bus.Memory[0xFFFC] = 0xF0;
    bus.Memory[0xFFFD] = 0x80;
    bus.Memory[0x80F0] = 0xA9;
    bus.Memory[0x80F1] = 0x00;
    bus.Memory[0x80F2] = 0xF0; // BEQ +$20 -> $8114
    bus.Memory[0x80F3] = 0x20;

    Cpu6502 cpu = new(bus);
    cpu.Reset();
    cpu.Step();
    int cycles = cpu.Step();
    Check("branch: taken across a page costs four",
        cycles == 4 && cpu.PC == 0x8114, $"cycles={cycles} pc={cpu.PC:X4}");
}

{
    // Not taken: two cycles, and the operand is stepped over.
    (Cpu6502 cpu, _) = Machine(0xA9, 0x01, 0xF0, 0x20); // LDA #$01; BEQ +$20
    cpu.Step();
    int cycles = cpu.Step();
    Check("branch: not taken costs two",
        cycles == 2 && cpu.PC == 0x8004, $"cycles={cycles} pc={cpu.PC:X4}");
}

// ------------------------------------------------------- stack and returns

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0x99, 0x48, 0xA9, 0x00, 0x68);
    cpu.Step(); // LDA #$99
    cpu.Step(); // PHA
    Check("pha: writes below the stack pointer", bus.Memory[0x01FD] == 0x99,
        $"got {bus.Memory[0x01FD]:X2}");
    cpu.Step(); // LDA #$00
    cpu.Step(); // PLA
    Check("pla: brings the value back", cpu.A == 0x99, $"got {cpu.A:X2}");
    Check("pla: restores the stack pointer", cpu.S == 0xFD, $"got {cpu.S:X2}");
}

{
    // PHP always pushes with the break and unused bits set.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x08);
    cpu.Step();
    Check("php: pushes with B and bit 5 set",
        (bus.Memory[0x01FD] & 0x30) == 0x30, $"got {bus.Memory[0x01FD]:X2}");
}

{
    // PLP ignores the break bit and always sets bit 5.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x28);
    bus.Memory[0x01FE] = 0xFF;
    cpu.Step();
    Check("plp: drops B and keeps bit 5",
        (cpu.P & Cpu6502.FlagBreak) == 0 && (cpu.P & Cpu6502.FlagUnused) != 0,
        $"got {cpu.P:X2}");
}

{
    // JSR pushes the address of its own last byte; RTS adds one back.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x20, 0x05, 0x80, 0xEA, 0xEA, 0x60);
    cpu.Step();
    Check("jsr: jumps to the target", cpu.PC == 0x8005, $"got {cpu.PC:X4}");
    Check("jsr: pushes the return address minus one",
        bus.Memory[0x01FD] == 0x80 && bus.Memory[0x01FC] == 0x02,
        $"got {bus.Memory[0x01FD]:X2}{bus.Memory[0x01FC]:X2}");
    cpu.Step();
    Check("rts: returns after the call", cpu.PC == 0x8003, $"got {cpu.PC:X4}");
}

{
    // BRK pushes the address after the padding byte and takes the IRQ vector.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x00);
    bus.Memory[0xFFFE] = 0x00;
    bus.Memory[0xFFFF] = 0x90;
    cpu.Step();
    Check("brk: takes the interrupt vector", cpu.PC == 0x9000, $"got {cpu.PC:X4}");
    Check("brk: skips the padding byte",
        bus.Memory[0x01FD] == 0x80 && bus.Memory[0x01FC] == 0x02,
        $"got {bus.Memory[0x01FD]:X2}{bus.Memory[0x01FC]:X2}");
    Check("brk: pushes the status with B set",
        (bus.Memory[0x01FB] & Cpu6502.FlagBreak) != 0, $"got {bus.Memory[0x01FB]:X2}");
    Check("brk: masks further interrupts",
        (cpu.P & Cpu6502.FlagInterruptDisable) != 0, $"got {cpu.P:X2}");
}

{
    // RTI puts everything back where BRK found it.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x00);
    bus.Memory[0xFFFE] = 0x00;
    bus.Memory[0xFFFF] = 0x90;
    bus.Memory[0x9000] = 0x40; // RTI
    cpu.Step();
    cpu.Step();
    Check("rti: returns to the instruction after BRK", cpu.PC == 0x8002, $"got {cpu.PC:X4}");
}

// -------------------------------------------------------------- interrupts

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xEA);
    bus.Memory[0xFFFA] = 0x00;
    bus.Memory[0xFFFB] = 0xA0;
    cpu.RaiseNmi();
    int cycles = cpu.Step();
    Check("nmi: takes its own vector", cpu.PC == 0xA000, $"got {cpu.PC:X4}");
    Check("nmi: costs seven cycles", cycles == 7, $"got {cycles}");
    Check("nmi: pushes the status with B clear",
        (bus.Memory[0x01FB] & Cpu6502.FlagBreak) == 0, $"got {bus.Memory[0x01FB]:X2}");
}

{
    // Reset leaves interrupts masked, so a plain IRQ must be ignored.
    (Cpu6502 cpu, FlatBus bus) = Machine(0xEA);
    bus.Memory[0xFFFE] = 0x00;
    bus.Memory[0xFFFF] = 0xB0;
    cpu.SetIrqLine(true);
    cpu.Step();
    Check("irq: ignored while masked", cpu.PC == 0x8001, $"got {cpu.PC:X4}");
}

{
    // With the mask cleared it is serviced.
    (Cpu6502 cpu, FlatBus bus) = Machine(0x58, 0xEA); // CLI; NOP
    bus.Memory[0xFFFE] = 0x00;
    bus.Memory[0xFFFF] = 0xB0;
    cpu.SetIrqLine(true);
    cpu.Step(); // CLI
    cpu.Step(); // the interrupt, before the NOP
    Check("irq: serviced once unmasked", cpu.PC == 0xB000, $"got {cpu.PC:X4}");
}

// ------------------------------------------------------ undocumented opcodes

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA7, 0x20); // LAX $20
    bus.Memory[0x0020] = 0x5C;
    cpu.Step();
    Check("lax: loads A and X together", cpu.A == 0x5C && cpu.X == 0x5C,
        $"A={cpu.A:X2} X={cpu.X:X2}");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0xF0, 0xA2, 0x3C, 0x87, 0x20); // LDA; LDX; SAX $20
    cpu.Step();
    cpu.Step();
    cpu.Step();
    Check("sax: stores A AND X", bus.Memory[0x0020] == 0x30, $"got {bus.Memory[0x0020]:X2}");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0x10, 0xC7, 0x20); // LDA #$10; DCP $20
    bus.Memory[0x0020] = 0x11;
    cpu.Step();
    cpu.Step();
    Check("dcp: decrements then compares",
        bus.Memory[0x0020] == 0x10 && (cpu.P & Cpu6502.FlagZero) != 0,
        $"mem={bus.Memory[0x0020]:X2} P={cpu.P:X2}");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0x38, 0xA9, 0x05, 0xE7, 0x20); // SEC; LDA #$05; ISC $20
    bus.Memory[0x0020] = 0x01;
    cpu.Step();
    cpu.Step();
    cpu.Step();
    Check("isc: increments then subtracts",
        bus.Memory[0x0020] == 0x02 && cpu.A == 0x03,
        $"mem={bus.Memory[0x0020]:X2} A={cpu.A:X2}");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0x01, 0x07, 0x20); // LDA #$01; SLO $20
    bus.Memory[0x0020] = 0x40;
    cpu.Step();
    cpu.Step();
    Check("slo: shifts memory then ors it in",
        bus.Memory[0x0020] == 0x80 && cpu.A == 0x81,
        $"mem={bus.Memory[0x0020]:X2} A={cpu.A:X2}");
}

{
    (Cpu6502 cpu, _) = Machine(0x02); // JAM
    cpu.Step();
    Check("jam: locks the processor up", cpu.Jammed);
    Check("jam: parks on the offending opcode", cpu.PC == 0x8000, $"got {cpu.PC:X4}");
}

// ------------------------------------------------------ read-modify-write

{
    // INC writes the unchanged value back before the new one, as the hardware does.
    FlatBus bus = new();
    bus.Memory[0xFFFC] = 0x00;
    bus.Memory[0xFFFD] = 0x80;
    bus.Memory[0x8000] = 0xE6; // INC $20
    bus.Memory[0x8001] = 0x20;
    bus.Memory[0x0020] = 0x07;

    RecordingBus recorder = new(bus);
    Cpu6502 cpu = new(recorder);
    cpu.Reset();
    cpu.Step();

    Check("rmw: performs the hardware double write",
        recorder.Writes.Count == 2
        && recorder.Writes[0] == (0x0020, 0x07)
        && recorder.Writes[1] == (0x0020, 0x08),
        $"got [{string.Join(", ", recorder.Writes.Select(w => $"{w.Address:X4}={w.Value:X2}"))}]");
}

// ------------------------------------------------------------- disassembler

{
    (_, FlatBus bus) = Machine(0x4C, 0xF5, 0xC5);
    string text = Disassembler.Describe(bus, 0x8000, out int length);
    Check("disassembler: absolute jump", text == "JMP $C5F5" && length == 3, $"got '{text}'");
}

{
    (_, FlatBus bus) = Machine(0xA1, 0x80);
    string text = Disassembler.Describe(bus, 0x8000, out _);
    Check("disassembler: indexed indirect", text == "LDA ($80,X)", $"got '{text}'");
}

{
    (Cpu6502 cpu, FlatBus bus) = Machine(0xA9, 0x01);
    string line = Disassembler.TraceLine(cpu, bus);
    Check("disassembler: trace line starts with the address and bytes",
        line.StartsWith("8000  A9 01     LDA #$01", StringComparison.Ordinal)
        && line.Contains("A:00 X:00 Y:00 P:24 SP:FD CYC:7", StringComparison.Ordinal),
        $"got '{line}'");
}

// ---------------------------------------------------------------- cartridge

byte[] BuildRom(int prgBanks, int chrBanks, byte flags6 = 0, byte flags7 = 0)
{
    int size = 16 + (prgBanks * 16384) + (chrBanks * 8192);
    byte[] image = new byte[size];
    image[0] = (byte)'N';
    image[1] = (byte)'E';
    image[2] = (byte)'S';
    image[3] = 0x1A;
    image[4] = (byte)prgBanks;
    image[5] = (byte)chrBanks;
    image[6] = flags6;
    image[7] = flags7;
    return image;
}

{
    byte[] image = BuildRom(2, 1);
    Cartridge cartridge = Cartridge.FromBytes(image);
    Check("rom: program size", cartridge.PrgRom.Length == 32768, $"got {cartridge.PrgRom.Length}");
    Check("rom: character size", cartridge.Chr.Length == 8192, $"got {cartridge.Chr.Length}");
    Check("rom: mapper zero", cartridge.MapperNumber == 0, $"got {cartridge.MapperNumber}");
    Check("rom: horizontal mirroring by default",
        cartridge.Mirroring == Mirroring.Horizontal, $"got {cartridge.Mirroring}");
}

{
    // Mapper number is split across the two flag bytes: $3 low, $7 high, so mapper 115.
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 1, 0x30, 0x70));
    Check("rom: mapper number spans both nibbles",
        cartridge.MapperNumber == 0x73, $"got {cartridge.MapperNumber}");
}

{
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 1, 0x01));
    Check("rom: vertical mirroring flag",
        cartridge.Mirroring == Mirroring.Vertical, $"got {cartridge.Mirroring}");
}

{
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 0));
    Check("rom: no character banks means character RAM",
        cartridge.ChrIsRam && cartridge.Chr.Length == 8192, $"got {cartridge.Chr.Length}");
}

{
    bool threw = false;
    try
    {
        Cartridge.FromBytes(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    }
    catch (InvalidDataException)
    {
        threw = true;
    }

    Check("rom: rejects a file that is not a NES image", threw);
}

{
    bool threw = false;
    try
    {
        byte[] truncated = BuildRom(2, 0);
        Cartridge.FromBytes(truncated.AsSpan(0, 1000));
    }
    catch (InvalidDataException)
    {
        threw = true;
    }

    Check("rom: rejects a truncated image", threw);
}

// ------------------------------------------------------------------ mapper

{
    // A 16 KB cartridge appears twice, so the vectors at the top hit the same bytes.
    byte[] image = BuildRom(1, 1);
    image[16] = 0xAA;              // first byte of the program ROM
    image[16 + 0x3FFF] = 0xBB;     // last byte
    Cartridge cartridge = Cartridge.FromBytes(image);
    IMapper mapper = IMapper.Create(cartridge);

    Check("nrom: 16 KB mirrors into the upper half",
        mapper.CpuRead(0x8000) == 0xAA && mapper.CpuRead(0xC000) == 0xAA,
        $"got {mapper.CpuRead(0x8000):X2} and {mapper.CpuRead(0xC000):X2}");
    Check("nrom: the last byte lands at $FFFF",
        mapper.CpuRead(0xFFFF) == 0xBB, $"got {mapper.CpuRead(0xFFFF):X2}");
}

{
    byte[] image = BuildRom(2, 1);
    image[16] = 0xAA;
    image[16 + 0x4000] = 0xCC;
    Cartridge cartridge = Cartridge.FromBytes(image);
    IMapper mapper = IMapper.Create(cartridge);

    Check("nrom: 32 KB fills the window once",
        mapper.CpuRead(0x8000) == 0xAA && mapper.CpuRead(0xC000) == 0xCC,
        $"got {mapper.CpuRead(0x8000):X2} and {mapper.CpuRead(0xC000):X2}");
}

{
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 1));
    IMapper mapper = IMapper.Create(cartridge);
    mapper.CpuWrite(0x6000, 0x5E);
    Check("nrom: save RAM at $6000 holds a value",
        mapper.CpuRead(0x6000) == 0x5E, $"got {mapper.CpuRead(0x6000):X2}");

    mapper.CpuWrite(0x8000, 0x11);
    Check("nrom: writes to program ROM are discarded",
        mapper.CpuRead(0x8000) == 0x00, $"got {mapper.CpuRead(0x8000):X2}");
}

{
    bool threw = false;
    try
    {
        IMapper.Create(Cartridge.FromBytes(BuildRom(1, 1, 0x10)));
    }
    catch (NotSupportedException)
    {
        threw = true;
    }

    Check("mapper: an unimplemented board is reported clearly", threw);
}

// --------------------------------------------------------------------- bus

{
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 1));
    NesBus bus = new(IMapper.Create(cartridge));

    bus.Write(0x0000, 0x42);
    Check("bus: work RAM mirrors three more times",
        bus.Read(0x0800) == 0x42 && bus.Read(0x1000) == 0x42 && bus.Read(0x1800) == 0x42,
        $"got {bus.Read(0x0800):X2} {bus.Read(0x1000):X2} {bus.Read(0x1800):X2}");

    bus.Write(0x07FF, 0x24);
    Check("bus: the top of work RAM mirrors too",
        bus.Read(0x1FFF) == 0x24, $"got {bus.Read(0x1FFF):X2}");
}

{
    // A whole console, booting from the reset vector in the cartridge.
    byte[] image = BuildRom(1, 1);
    image[16 + 0x3FFC] = 0x00; // reset vector low
    image[16 + 0x3FFD] = 0xC0; // reset vector high
    image[16 + 0x0000] = 0xA9; // LDA #$77 at $C000
    image[16 + 0x0001] = 0x77;

    Nes nes = new(Cartridge.FromBytes(image));
    Check("console: boots from the cartridge reset vector",
        nes.Cpu.PC == 0xC000, $"got {nes.Cpu.PC:X4}");

    nes.StepInstruction();
    Check("console: runs an instruction out of cartridge ROM",
        nes.Cpu.A == 0x77, $"got {nes.Cpu.A:X2}");
}

// ------------------------------------------------------------------ summary

Console.WriteLine();
Console.WriteLine($"{total - failures}/{total} passed");
return failures == 0 ? 0 : 1;

/// <summary>Flat 64 KB of memory, so the processor can be exercised without a console around it.</summary>
sealed class FlatBus : IBus
{
    public readonly byte[] Memory = new byte[0x10000];

    public byte Read(ushort address) => Memory[address];

    public void Write(ushort address, byte value) => Memory[address] = value;

    public byte Peek(ushort address) => Memory[address];
}

/// <summary>Wraps a bus and notes every write, to check access patterns rather than results.</summary>
sealed class RecordingBus(IBus inner) : IBus
{
    private readonly IBus _inner = inner;

    public List<(ushort Address, byte Value)> Writes { get; } = [];

    public byte Read(ushort address) => _inner.Read(address);

    public void Write(ushort address, byte value)
    {
        Writes.Add((address, value));
        _inner.Write(address, value);
    }

    public byte Peek(ushort address) => _inner.Peek(address);
}
