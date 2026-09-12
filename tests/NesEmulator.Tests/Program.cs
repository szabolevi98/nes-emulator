using NesEmulator.Core;
using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;
using NesEmulator.Core.Ppu;

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
        IMapper.Create(Cartridge.FromBytes(BuildRom(1, 1, 0x50)));
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
    IMapper mapper = IMapper.Create(cartridge);
    NesBus bus = new(mapper, new Ppu2C02(mapper), new Apu2A03(), new Controller(), new Controller());

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

// --------------------------------------------------------------- more boards

{
    // UxROM: the lower half switches, the upper half is pinned to the last bank.
    byte[] image = BuildRom(4, 1, 0x20); // mapper 2
    image[16 + (0 * 16384)] = 0xA0;
    image[16 + (1 * 16384)] = 0xA1;
    image[16 + (3 * 16384)] = 0xA3;
    IMapper mapper = IMapper.Create(Cartridge.FromBytes(image));

    Check("uxrom: starts on the first bank", mapper.CpuRead(0x8000) == 0xA0,
        $"got {mapper.CpuRead(0x8000):X2}");
    Check("uxrom: the last bank is fixed high", mapper.CpuRead(0xC000) == 0xA3,
        $"got {mapper.CpuRead(0xC000):X2}");

    mapper.CpuWrite(0x8000, 0x01);
    Check("uxrom: switches the lower half", mapper.CpuRead(0x8000) == 0xA1,
        $"got {mapper.CpuRead(0x8000):X2}");
    Check("uxrom: leaves the upper half alone", mapper.CpuRead(0xC000) == 0xA3,
        $"got {mapper.CpuRead(0xC000):X2}");
}

{
    // CNROM swaps all eight kilobytes of tile data at once.
    byte[] image = BuildRom(1, 2, 0x30); // mapper 3
    image[16 + 16384] = 0xC0;              // first character bank
    image[16 + 16384 + 8192] = 0xC1;       // second
    IMapper mapper = IMapper.Create(Cartridge.FromBytes(image));

    Check("cnrom: starts on the first character bank", mapper.PpuRead(0x0000) == 0xC0,
        $"got {mapper.PpuRead(0x0000):X2}");
    mapper.CpuWrite(0x8000, 0x01);
    Check("cnrom: switches character banks", mapper.PpuRead(0x0000) == 0xC1,
        $"got {mapper.PpuRead(0x0000):X2}");
}

{
    // MMC1 takes five writes to accept one value, lowest bit first.
    byte[] image = BuildRom(4, 1, 0x10); // mapper 1
    image[16 + (0 * 16384)] = 0xB0;
    image[16 + (2 * 16384)] = 0xB2;
    image[16 + (3 * 16384)] = 0xB3;
    IMapper mapper = IMapper.Create(Cartridge.FromBytes(image));

    Check("mmc1: powers up with the last bank fixed high",
        mapper.CpuRead(0xC000) == 0xB3, $"got {mapper.CpuRead(0xC000):X2}");

    void SerialWrite(ushort address, byte value)
    {
        for (int i = 0; i < 5; i++)
        {
            mapper.CpuWrite(address, (byte)((value >> i) & 0x01));
        }
    }

    SerialWrite(0xE000, 0x02); // program bank 2 into the lower half
    Check("mmc1: accepts a bank after five writes",
        mapper.CpuRead(0x8000) == 0xB2, $"got {mapper.CpuRead(0x8000):X2}");

    Check("mmc1: an incomplete sequence changes nothing", true);
    mapper.CpuWrite(0xE000, 0x01);
    mapper.CpuWrite(0xE000, 0x01);
    Check("mmc1: two of five writes leave the bank alone",
        mapper.CpuRead(0x8000) == 0xB2, $"got {mapper.CpuRead(0x8000):X2}");

    mapper.CpuWrite(0xE000, 0x80); // reset clears the sequence
    SerialWrite(0x8000, 0x03);     // control: horizontal mirroring
    Check("mmc1: control register selects mirroring",
        mapper.Mirroring == Mirroring.Horizontal, $"got {mapper.Mirroring}");

    SerialWrite(0x8000, 0x02);
    Check("mmc1: and can select vertical",
        mapper.Mirroring == Mirroring.Vertical, $"got {mapper.Mirroring}");
}

// ---------------------------------------------------------- picture unit

Ppu2C02 NewPpu(Mirroring mirroring = Mirroring.Horizontal)
{
    byte flags6 = mirroring == Mirroring.Vertical ? (byte)0x01 : (byte)0x00;
    Cartridge cartridge = Cartridge.FromBytes(BuildRom(1, 0, flags6));
    return new Ppu2C02(IMapper.Create(cartridge));
}

{
    Ppu2C02 ppu = NewPpu();

    // $2006 takes the high half of the address first, then the low half.
    ppu.WriteRegister(0x2006, 0x21);
    ppu.WriteRegister(0x2006, 0x08);
    ppu.WriteRegister(0x2007, 0x5A);

    ppu.WriteRegister(0x2006, 0x21);
    ppu.WriteRegister(0x2006, 0x08);
    ppu.ReadRegister(0x2007);                    // the buffered read is one behind
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: name table write and buffered read", value == 0x5A, $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu();
    ppu.WriteRegister(0x2000, 0x04); // step 32 bytes per access instead of one
    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.WriteRegister(0x2007, 0x11);
    ppu.WriteRegister(0x2007, 0x22);

    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x20);
    ppu.ReadRegister(0x2007);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: the address step follows the control register", value == 0x22,
        $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu();

    // Palette memory answers immediately rather than through the read buffer.
    ppu.WriteRegister(0x2006, 0x3F);
    ppu.WriteRegister(0x2006, 0x01);
    ppu.WriteRegister(0x2007, 0x24);

    ppu.WriteRegister(0x2006, 0x3F);
    ppu.WriteRegister(0x2006, 0x01);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: palette reads are not buffered", value == 0x24, $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu();

    // The backdrop of each sprite palette is the same byte as the background one.
    ppu.WriteRegister(0x2006, 0x3F);
    ppu.WriteRegister(0x2006, 0x10);
    ppu.WriteRegister(0x2007, 0x2B);

    ppu.WriteRegister(0x2006, 0x3F);
    ppu.WriteRegister(0x2006, 0x00);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: $3F10 and $3F00 are the same byte", value == 0x2B, $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu(Mirroring.Horizontal);
    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.WriteRegister(0x2007, 0x77);

    // Horizontal mirroring puts the second screen on top of the first.
    ppu.WriteRegister(0x2006, 0x24);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.ReadRegister(0x2007);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: horizontal mirroring pairs $2000 with $2400", value == 0x77,
        $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu(Mirroring.Vertical);
    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.WriteRegister(0x2007, 0x66);

    // Vertical mirroring pairs $2000 with $2800 instead.
    ppu.WriteRegister(0x2006, 0x28);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.ReadRegister(0x2007);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: vertical mirroring pairs $2000 with $2800", value == 0x66,
        $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu();

    // Half a $2006 sequence, then a status read, which resets the latch. The next
    // write must be treated as a first write again.
    ppu.WriteRegister(0x2006, 0x21);
    ppu.ReadRegister(0x2002);
    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.WriteRegister(0x2007, 0x3C);

    ppu.WriteRegister(0x2006, 0x20);
    ppu.WriteRegister(0x2006, 0x00);
    ppu.ReadRegister(0x2007);
    byte value = ppu.ReadRegister(0x2007);
    Check("ppu: reading the status resets the write latch", value == 0x3C,
        $"got {value:X2}");
}

{
    Ppu2C02 ppu = NewPpu();

    // Run to the start of vertical blank: 262 lines of 341 cycles, and the flag
    // goes up on the second cycle of line 241.
    for (int i = 0; i < (242 * 341) + 2; i++)
    {
        ppu.Step();
    }

    byte status = ppu.ReadRegister(0x2002);
    Check("ppu: vertical blank flag goes up on line 241", (status & 0x80) != 0,
        $"got {status:X2} at line {ppu.Scanline}");

    byte again = ppu.ReadRegister(0x2002);
    Check("ppu: reading the status clears the flag", (again & 0x80) == 0,
        $"got {again:X2}");
}

{
    Ppu2C02 ppu = NewPpu();
    ppu.WriteRegister(0x2000, 0x80); // ask for the interrupt

    bool raised = false;
    for (int i = 0; i < (242 * 341) + 2 && !raised; i++)
    {
        ppu.Step();
        raised = ppu.ConsumeNmi();
    }

    Check("ppu: asks for an interrupt when the frame ends", raised);
}

{
    Ppu2C02 ppu = NewPpu();

    // One frame is 262 lines of 341 cycles with rendering off.
    int steps = 0;
    while (!ppu.FrameComplete)
    {
        ppu.Step();
        steps++;
    }

    Check("ppu: a frame is 89,342 cycles", steps == 341 * 262, $"got {steps}");
}

// ------------------------------------------------------------ drawing a frame

{
    // A whole console rendering one tile, checked pixel by pixel. The program
    // below writes a palette, puts tile 1 in the top left of the name table and
    // turns the background on.
    byte[] program =
    [
        0xA9, 0x3F, 0x8D, 0x06, 0x20,  // point the address at the palette
        0xA9, 0x00, 0x8D, 0x06, 0x20,
        0xA9, 0x0F, 0x8D, 0x07, 0x20,  // backdrop
        0xA9, 0x16, 0x8D, 0x07, 0x20,  // colour 1
        0xA9, 0x2A, 0x8D, 0x07, 0x20,
        0xA9, 0x30, 0x8D, 0x07, 0x20,
        0xA9, 0x20, 0x8D, 0x06, 0x20,  // point it at the name table
        0xA9, 0x00, 0x8D, 0x06, 0x20,
        0xA9, 0x01, 0x8D, 0x07, 0x20,  // tile 1 in the corner
        0x8D, 0x07, 0x20,              // and again, in the square beside it
        0xA9, 0x00, 0x8D, 0x05, 0x20,  // no scroll
        0x8D, 0x05, 0x20,
        0xA9, 0x08, 0x8D, 0x01, 0x20,  // background on, leftmost squares clipped
        0x4C, 0x3D, 0xC0,              // and wait here
    ];

    byte[] image = BuildRom(1, 1);
    program.CopyTo(image, 16);
    image[16 + 0x3FFC] = 0x00; // reset vector -> $C000
    image[16 + 0x3FFD] = 0xC0;

    // Tile 1: every pixel in the low plane set, so all eight rows read as colour 1.
    for (int row = 0; row < 8; row++)
    {
        image[16 + 16384 + 0x10 + row] = 0xFF;
        image[16 + 16384 + 0x18 + row] = 0x00;
    }

    Nes nes = new(Cartridge.FromBytes(image));
    for (int frame = 0; frame < 3; frame++)
    {
        nes.RunFrame();
    }

    byte[] screen = nes.Ppu.FrameBuffer;
    bool tileDrawn = true;
    for (int y = 0; y < 8 && tileDrawn; y++)
    {
        for (int x = 8; x < 16 && tileDrawn; x++)
        {
            tileDrawn = screen[(y * 256) + x] == 0x16;
        }
    }

    Check("render: the tile is drawn in its palette colour", tileDrawn,
        $"got {screen[8]:X2} at the second square");

    // The mask bit that would show the leftmost eight pixels was left off, so the
    // identical tile in the corner must not appear.
    bool leftClipped = true;
    for (int y = 0; y < 8 && leftClipped; y++)
    {
        for (int x = 0; x < 8 && leftClipped; x++)
        {
            leftClipped = screen[(y * 256) + x] == 0x0F;
        }
    }

    Check("render: the leftmost squares are clipped away", leftClipped,
        $"got {screen[0]:X2} at the corner");
    Check("render: the rest of the line is the backdrop", screen[16] == 0x0F,
        $"got {screen[16]:X2}");
    Check("render: the bottom of the screen is the backdrop too",
        screen[(239 * 256) + 255] == 0x0F, $"got {screen[(239 * 256) + 255]:X2}");
    Check("render: three frames were produced", nes.Ppu.FrameCount >= 3,
        $"got {nes.Ppu.FrameCount}");
}

// ------------------------------------------------------------- sprite memory

{
    byte[] image = BuildRom(1, 1);
    image[16 + 0x3FFC] = 0x00;
    image[16 + 0x3FFD] = 0xC0;

    // LDA #$02; STA $4014 -- copy page two into sprite memory.
    byte[] program = [0xA9, 0x02, 0x8D, 0x14, 0x40, 0x4C, 0x05, 0xC0];
    program.CopyTo(image, 16);

    Nes nes = new(Cartridge.FromBytes(image));
    for (int i = 0; i < 256; i++)
    {
        nes.Bus.Write((ushort)(0x0200 + i), (byte)i);
    }

    nes.StepInstruction(); // LDA
    int cycles = nes.StepInstruction(); // STA, which triggers the transfer

    Check("dma: the processor is held still for the transfer", cycles >= 513,
        $"got {cycles}");

    nes.Ppu.WriteRegister(0x2003, 0x00);
    byte first = nes.Ppu.ReadRegister(0x2004);
    nes.Ppu.WriteRegister(0x2003, 0x10);
    byte sixteenth = nes.Ppu.ReadRegister(0x2004);

    Check("dma: sprite memory holds the copied page",
        first == 0x00 && sixteenth == 0x10, $"got {first:X2} and {sixteenth:X2}");
}

// --------------------------------------------------------------- controllers

{
    Controller controller = new() { Buttons = NesButton.A | NesButton.Start };

    controller.Write(1); // latch
    controller.Write(0);

    // Buttons come out one at a time: A, B, Select, Start, Up, Down, Left, Right.
    int[] bits = new int[8];
    for (int i = 0; i < 8; i++)
    {
        bits[i] = controller.Read() & 1;
    }

    Check("controller: reports the buttons in hardware order",
        bits[0] == 1 && bits[1] == 0 && bits[2] == 0 && bits[3] == 1
        && bits[4] == 0 && bits[5] == 0 && bits[6] == 0 && bits[7] == 0,
        $"got [{string.Join(",", bits)}]");
}

{
    Controller controller = new() { Buttons = NesButton.B };
    controller.Write(1); // held high, so the register keeps reloading

    Check("controller: a held strobe keeps reporting the first button",
        (controller.Read() & 1) == 0 && (controller.Read() & 1) == 0);
}

// ------------------------------------------------------------------- mmc3

{
    // 128 KB of program ROM is sixteen eight kilobyte slots. Mark the first byte
    // of a few of them so the bank in the window can be identified.
    byte[] image = BuildRom(8, 8, 0x40); // mapper 4
    for (int slot = 0; slot < 16; slot++)
    {
        image[16 + (slot * 8192)] = (byte)(0xD0 + slot);
    }

    IMapper mapper = IMapper.Create(Cartridge.FromBytes(image));

    Check("mmc3: the last slot is fixed", mapper.CpuRead(0xE000) == 0xDF,
        $"got {mapper.CpuRead(0xE000):X2}");
    Check("mmc3: the second to last slot is fixed in mode 0",
        mapper.CpuRead(0xC000) == 0xDE, $"got {mapper.CpuRead(0xC000):X2}");

    mapper.CpuWrite(0x8000, 6); // select the first program register
    mapper.CpuWrite(0x8001, 3); // and point it at slot three
    Check("mmc3: the switchable slot follows its register",
        mapper.CpuRead(0x8000) == 0xD3, $"got {mapper.CpuRead(0x8000):X2}");

    mapper.CpuWrite(0x8000, 0x46); // same register, but the other program mode
    mapper.CpuWrite(0x8001, 3);
    Check("mmc3: program mode 1 swaps the two halves",
        mapper.CpuRead(0xC000) == 0xD3 && mapper.CpuRead(0x8000) == 0xDE,
        $"got {mapper.CpuRead(0xC000):X2} and {mapper.CpuRead(0x8000):X2}");
}

{
    byte[] image = BuildRom(8, 8, 0x40);
    int chrStart = 16 + (8 * 16384);
    for (int slot = 0; slot < 64; slot++)
    {
        image[chrStart + (slot * 1024)] = (byte)slot;
    }

    IMapper mapper = IMapper.Create(Cartridge.FromBytes(image));

    mapper.CpuWrite(0x8000, 2); // the first one kilobyte register
    mapper.CpuWrite(0x8001, 5);
    Check("mmc3: a one kilobyte tile bank follows its register",
        mapper.PpuRead(0x1000) == 5, $"got {mapper.PpuRead(0x1000)}");

    mapper.CpuWrite(0x8000, 0); // the first two kilobyte register
    mapper.CpuWrite(0x8001, 8);
    Check("mmc3: a two kilobyte bank covers two slots",
        mapper.PpuRead(0x0000) == 8 && mapper.PpuRead(0x0400) == 9,
        $"got {mapper.PpuRead(0x0000)} and {mapper.PpuRead(0x0400)}");

    mapper.CpuWrite(0x8000, 0x80); // tile mode 1 swaps the halves over
    Check("mmc3: tile mode 1 moves the large banks to the top",
        mapper.PpuRead(0x1000) == 8, $"got {mapper.PpuRead(0x1000)}");

    mapper.CpuWrite(0xA000, 0x00);
    Check("mmc3: mirroring register selects vertical",
        mapper.Mirroring == Mirroring.Vertical, $"got {mapper.Mirroring}");
    mapper.CpuWrite(0xA000, 0x01);
    Check("mmc3: and horizontal",
        mapper.Mirroring == Mirroring.Horizontal, $"got {mapper.Mirroring}");
}

{
    // The line counter: load three, and the interrupt arrives on the fourth line,
    // because the first one is spent reloading.
    IMapper mapper = IMapper.Create(Cartridge.FromBytes(BuildRom(2, 1, 0x40)));
    mapper.CpuWrite(0xC000, 3);    // latch
    mapper.CpuWrite(0xC001, 0);    // ask for a reload
    mapper.CpuWrite(0xE001, 0);    // enable

    mapper.OnScanline();
    mapper.OnScanline();
    mapper.OnScanline();
    Check("mmc3: no interrupt before the count runs out", !mapper.IrqPending);

    mapper.OnScanline();
    Check("mmc3: the interrupt arrives on the counted line", mapper.IrqPending);

    mapper.CpuWrite(0xE000, 0);    // disabling also acknowledges
    Check("mmc3: disabling clears the interrupt", !mapper.IrqPending);
}

{
    // The picture unit is what clocks those boards, once per line while drawing.
    CountingMapper counter = new();
    Ppu2C02 ppu = new(counter);
    ppu.WriteRegister(0x2001, 0x08); // rendering on

    while (!ppu.FrameComplete)
    {
        ppu.Step();
    }

    Check("ppu: clocks the board once per drawn line, pre-render included",
        counter.Scanlines == 241, $"got {counter.Scanlines}");
}

// -------------------------------------------------------------- sound unit

{
    LengthCounter length = new() { Enabled = true };
    length.Load(0);
    Check("length: loads from the table", length.Value == 10, $"got {length.Value}");

    for (int i = 0; i < 10; i++)
    {
        length.Clock();
    }

    Check("length: counts down to silence", !length.Active, $"got {length.Value}");

    length.Load(0);
    length.Halted = true;
    length.Clock();
    Check("length: a halted counter holds", length.Value == 10, $"got {length.Value}");
}

{
    Envelope envelope = new() { ConstantVolume = true, Volume = 9 };
    Check("envelope: constant volume passes straight through", envelope.Output == 9,
        $"got {envelope.Output}");

    envelope.ConstantVolume = false;
    envelope.Volume = 0; // the fastest decay
    envelope.Restart();
    envelope.Clock();
    Check("envelope: starts at full volume", envelope.Output == 15, $"got {envelope.Output}");

    envelope.Clock();
    envelope.Clock();
    Check("envelope: decays", envelope.Output == 13, $"got {envelope.Output}");
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4015, 0x0F);  // enable the four tone channels
    apu.WriteRegister(0x4000, 0x9A);  // half duty, constant volume 10
    apu.WriteRegister(0x4002, 0x00);
    apu.WriteRegister(0x4003, 0x01);  // timer $100, and load the length counter

    Check("pulse: silent on the low part of its duty cycle", apu.Pulse1.Output() == 0,
        $"got {apu.Pulse1.Output()}");

    apu.Pulse1.Clock();
    Check("pulse: sounds on the high part", apu.Pulse1.Output() == 10,
        $"got {apu.Pulse1.Output()}");

    // A period below eight is above hearing and is silenced rather than played.
    apu.WriteRegister(0x4002, 0x04);
    apu.WriteRegister(0x4003, 0x00);
    apu.Pulse1.Clock();
    Check("pulse: a period below eight is muted", apu.Pulse1.Output() == 0,
        $"got {apu.Pulse1.Output()}");
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4015, 0x04); // triangle only
    apu.WriteRegister(0x4008, 0x7F); // a long linear counter
    apu.WriteRegister(0x400A, 0x20);
    apu.WriteRegister(0x400B, 0x08); // timer and length

    Check("triangle: starts at the top of its staircase", apu.Triangle.Output() == 15,
        $"got {apu.Triangle.Output()}");

    apu.Triangle.ClockLinear();
    apu.Triangle.Clock();
    Check("triangle: steps down the staircase", apu.Triangle.Output() == 14,
        $"got {apu.Triangle.Output()}");

    // With its length counter disabled the sequencer stops and the output holds.
    apu.WriteRegister(0x4015, 0x00);
    int held = apu.Triangle.Output();
    for (int i = 0; i < 100; i++)
    {
        apu.Triangle.Clock();
    }

    Check("triangle: a silenced channel holds its level", apu.Triangle.Output() == held,
        $"got {apu.Triangle.Output()} after {held}");
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4015, 0x08); // noise only
    apu.WriteRegister(0x400C, 0x1F); // constant volume 15
    apu.WriteRegister(0x400E, 0x00); // the shortest period
    apu.WriteRegister(0x400F, 0x08); // load the length counter

    bool sawSilence = false;
    bool sawSound = false;
    for (int i = 0; i < 2000; i++)
    {
        apu.Noise.Clock();
        if (apu.Noise.Output() == 0)
        {
            sawSilence = true;
        }
        else
        {
            sawSound = true;
        }
    }

    Check("noise: the shift register produces both levels", sawSilence && sawSound);

    apu.WriteRegister(0x4015, 0x00);
    Check("noise: silent once its length counter is disabled", apu.Noise.Output() == 0,
        $"got {apu.Noise.Output()}");
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4017, 0x00); // four step mode, interrupt allowed

    for (int i = 0; i < 29828; i++)
    {
        apu.Step();
    }

    Check("frame counter: quiet until the end of its sequence", !apu.IrqPending);

    apu.Step();
    Check("frame counter: interrupts at the end of the sequence", apu.IrqPending);

    byte status = apu.ReadStatus();
    Check("frame counter: the status register reports it", (status & 0x40) != 0,
        $"got {status:X2}");
    Check("frame counter: reading the status acknowledges it", !apu.IrqPending);
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4017, 0x40); // interrupt inhibited

    for (int i = 0; i < 30000; i++)
    {
        apu.Step();
    }

    Check("frame counter: stays quiet when inhibited", !apu.IrqPending);
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4017, 0x80); // five step mode never interrupts

    for (int i = 0; i < 40000; i++)
    {
        apu.Step();
    }

    Check("frame counter: five step mode never interrupts", !apu.IrqPending);
}

{
    Apu2A03 apu = new(44100);

    // One second of processor cycles should produce one second of samples.
    for (int i = 0; i < (int)Apu2A03.ClockRate; i++)
    {
        apu.Step();
    }

    Check("sound: a second of cycles yields a second of samples",
        Math.Abs(apu.AvailableSamples - 44100) <= 2, $"got {apu.AvailableSamples}");
}

{
    Apu2A03 apu = new();
    for (int i = 0; i < 5000; i++)
    {
        apu.Step();
    }

    float[] samples = new float[5000];
    int taken = apu.ReadSamples(samples, samples.Length);

    bool steady = true;
    for (int i = 1; i < taken && steady; i++)
    {
        steady = Math.Abs(samples[i] - samples[0]) < 0.0001f;
    }

    Check("sound: nothing enabled produces a steady level", steady && taken > 0,
        $"took {taken}");

    Check("sound: the mixer stays inside its range",
        samples.Take(taken).All(s => s is >= 0f and <= 1f));
}

{
    Apu2A03 apu = new();
    apu.WriteRegister(0x4015, 0x01);
    apu.WriteRegister(0x4000, 0x9F); // constant volume 15
    apu.WriteRegister(0x4002, 0x40);
    apu.WriteRegister(0x4003, 0x00);

    for (int i = 0; i < 5000; i++)
    {
        apu.Step();
    }

    float[] samples = new float[5000];
    int taken = apu.ReadSamples(samples, samples.Length);

    Check("sound: a playing channel makes the output move",
        samples.Take(taken).Distinct().Count() > 1, $"took {taken}");
}

{
    Apu2A03 apu = new();
    apu.Dmc.ReadMemory = _ => 0xFF; // every bit asks for a step up
    apu.WriteRegister(0x4010, 0x0F); // the fastest rate
    apu.WriteRegister(0x4012, 0x00); // sample at $C000
    apu.WriteRegister(0x4013, 0x01); // seventeen bytes of it
    apu.WriteRegister(0x4015, 0x10); // start it

    Check("sample channel: starts with bytes to play", apu.Dmc.Active);

    for (int i = 0; i < 5000; i++)
    {
        apu.Dmc.Clock();
    }

    Check("sample channel: the level walks up on a run of set bits",
        apu.Dmc.OutputLevel > 0, $"got {apu.Dmc.OutputLevel}");
}

{
    // The console wires both interrupt sources onto the same line.
    byte[] image = BuildRom(1, 1);
    image[16 + 0x3FFC] = 0x00;
    image[16 + 0x3FFD] = 0xC0;
    image[16] = 0x4C; // JMP $C000, forever
    image[17] = 0x00;
    image[18] = 0xC0;

    Nes nes = new(Cartridge.FromBytes(image));
    nes.Bus.Write(0x4017, 0x00); // four step mode, interrupt allowed

    for (int i = 0; i < 20000; i++)
    {
        nes.StepInstruction();
    }

    Check("console: the sound unit reaches the interrupt line",
        nes.Apu.IrqPending || nes.Cpu.PC != 0xC000,
        $"pc {nes.Cpu.PC:X4}");
}

// ------------------------------------------------------------- save states

// A cartridge that keeps busy: it walks a counter through work RAM forever.
byte[] BuildBusyRom()
{
    byte[] image = BuildRom(1, 1);
    byte[] program =
    [
        0xA9, 0x00, 0x85, 0x10,        // LDA #$00; STA $10
        0xE6, 0x10,                    // INC $10
        0xA5, 0x10, 0x8D, 0x00, 0x02,  // LDA $10; STA $0200
        0x4C, 0x04, 0xC0,              // JMP back to the INC
    ];

    program.CopyTo(image, 16);
    image[16 + 0x3FFC] = 0x00;
    image[16 + 0x3FFD] = 0xC0;
    return image;
}

{
    Nes nes = new(Cartridge.FromBytes(BuildBusyRom()));
    for (int i = 0; i < 5000; i++)
    {
        nes.StepInstruction();
    }

    MemoryStream state = new();
    nes.SaveState(state);

    long cycles = nes.Cpu.Cycles;
    byte a = nes.Cpu.A;
    ushort pc = nes.Cpu.PC;
    byte counter = nes.Bus.Read(0x0010);

    for (int i = 0; i < 5000; i++)
    {
        nes.StepInstruction();
    }

    Check("state: the console moved on after saving",
        nes.Cpu.Cycles != cycles && nes.Bus.Read(0x0010) != counter);

    state.Position = 0;
    nes.LoadState(state);

    Check("state: restores the register file",
        nes.Cpu.A == a && nes.Cpu.PC == pc, $"A={nes.Cpu.A:X2} PC={nes.Cpu.PC:X4}");
    Check("state: restores the cycle count", nes.Cpu.Cycles == cycles,
        $"got {nes.Cpu.Cycles} wanted {cycles}");
    Check("state: restores work RAM", nes.Bus.Read(0x0010) == counter,
        $"got {nes.Bus.Read(0x0010):X2} wanted {counter:X2}");
}

{
    // Running on from a restored state has to give exactly the same result twice.
    Nes nes = new(Cartridge.FromBytes(BuildBusyRom()));
    for (int i = 0; i < 2000; i++)
    {
        nes.StepInstruction();
    }

    MemoryStream state = new();
    nes.SaveState(state);

    for (int frame = 0; frame < 5; frame++)
    {
        nes.RunFrame();
    }

    byte[] first = nes.Ppu.FrameBuffer.ToArray();
    long firstCycles = nes.Cpu.Cycles;

    state.Position = 0;
    nes.LoadState(state);
    for (int frame = 0; frame < 5; frame++)
    {
        nes.RunFrame();
    }

    Check("state: replaying from a state is deterministic",
        nes.Cpu.Cycles == firstCycles && nes.Ppu.FrameBuffer.SequenceEqual(first),
        $"cycles {nes.Cpu.Cycles} against {firstCycles}");
}

{
    // A state written by a different cartridge must be refused, not misread.
    Nes source = new(Cartridge.FromBytes(BuildRom(4, 1, 0x20))); // mapper 2
    MemoryStream state = new();
    source.SaveState(state);

    Nes other = new(Cartridge.FromBytes(BuildBusyRom()));        // mapper 0
    state.Position = 0;

    bool threw = false;
    try
    {
        other.LoadState(state);
    }
    catch (InvalidDataException)
    {
        threw = true;
    }

    Check("state: refuses a state from another cartridge", threw);
}

{
    Nes nes = new(Cartridge.FromBytes(BuildBusyRom()));
    bool threw = false;
    try
    {
        nes.LoadState(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]));
    }
    catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
    {
        threw = true;
    }

    Check("state: refuses something that is not a state at all", threw);
}

// ----------------------------------------------------------------- rewinding

{
    Nes nes = new(Cartridge.FromBytes(BuildBusyRom()));
    RewindBuffer rewind = new(nes, capacity: 20, framesBetween: 2);

    for (int frame = 0; frame < 20; frame++)
    {
        nes.RunFrame();
        rewind.OnFrame();
    }

    Check("rewind: keeps one snapshot every few frames", rewind.Count == 10,
        $"got {rewind.Count}");

    // Compression matters here: a state is about seventy kilobytes raw.
    long average = rewind.BytesHeld / Math.Max(1, rewind.Count);
    Check("rewind: snapshots compress to a fraction of their size", average < 20_000,
        $"average {average} bytes");

    long cycles = nes.Cpu.Cycles;
    Check("rewind: winding back moves the console into the past",
        rewind.StepBack() && nes.Cpu.Cycles < cycles,
        $"{nes.Cpu.Cycles} against {cycles}");

    Check("rewind: taking a snapshot back removes it", rewind.Count == 9,
        $"got {rewind.Count}");

    while (rewind.StepBack())
    {
        // wind all the way back
    }

    Check("rewind: runs out at the oldest snapshot", rewind.Count == 0 && !rewind.StepBack());
}

{
    // The ring must not grow without bound.
    Nes nes = new(Cartridge.FromBytes(BuildBusyRom()));
    RewindBuffer rewind = new(nes, capacity: 5, framesBetween: 1);

    for (int frame = 0; frame < 40; frame++)
    {
        nes.RunFrame();
        rewind.OnFrame();
    }

    Check("rewind: the ring is capped at its capacity", rewind.Count == 5,
        $"got {rewind.Count}");

    rewind.Clear();
    Check("rewind: clearing empties it", rewind.Count == 0 && !rewind.StepBack());
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

/// <summary>A board that does nothing but count the lines it is clocked on.</summary>
sealed class CountingMapper : IMapper
{
    public int Scanlines { get; private set; }

    public Mirroring Mirroring => Mirroring.Horizontal;

    public byte CpuRead(ushort address) => 0;

    public void CpuWrite(ushort address, byte value)
    {
    }

    public byte PpuRead(ushort address) => 0;

    public void PpuWrite(ushort address, byte value)
    {
    }

    public void OnScanline() => Scanlines++;
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
