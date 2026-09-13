using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Memory;
using NesEmulator.Core.Ppu;

internal static class RegressionTests
{
    public static void Run(Action<string, bool> check)
    {
        InterruptTiming(check);
        void BusTrace(string name, byte[] program, ushort[] reads, Action<Cpu6502, TraceBus>? setup = null)
        {
            TraceBus bus = new();
            program.CopyTo(bus.Memory, 0x8000);
            Cpu6502 cpu = new(bus) { PC = 0x8000, S = 0xFD };
            setup?.Invoke(cpu, bus);
            int clocks = 0;
            cpu.OnCycle = () => { clocks++; bus.Clock = clocks; };
            cpu.Step();
            check(name, bus.Accesses.Select(a => a.Address).SequenceEqual(reads)
                && bus.Accesses.Select(a => a.Clock).SequenceEqual(Enumerable.Range(1, clocks))
                && clocks == cpu.Cycles);
        }

        BusTrace("bus: implied instruction reads the next opcode", [0xEA], [0x8000, 0x8001]);
        BusTrace("bus: indexed zero page reads before wrapping", [0xB5, 0xFF], [0x8000, 0x8001, 0xFF, 0], (c, _) => c.X = 1);
        BusTrace("bus: indexed indirect reads the unindexed pointer", [0xA1, 0xFE], [0x8000, 0x8001, 0xFE, 0xFF, 0, 0], (c, _) => c.X = 1);
        BusTrace("bus: page crossing reads the uncorrected address", [0xBD, 0xFF, 0x20], [0x8000, 0x8001, 0x8002, 0x2000, 0x2100], (c, _) => c.X = 1);
        BusTrace("bus: indexed store has a dummy read without crossing", [0x9D, 0, 0x20], [0x8000, 0x8001, 0x8002, 0x2001, 0x2001], (c, _) => c.X = 1);
        BusTrace("bus: indexed RMW includes the dummy read and both writes", [0xFE, 0xFF, 0x20], [0x8000, 0x8001, 0x8002, 0x2000, 0x2100, 0x2100, 0x2100], (c, _) => c.X = 1);
        BusTrace("bus: taken branch reads the old PC", [0xD0, 0x02], [0x8000, 0x8001, 0x8002]);
        BusTrace("bus: crossing branch reads the intermediate page", [0xD0, 0xFC], [0x8000, 0x8001, 0x8002, 0x80FE]);
        BusTrace("bus: JSR fetches high byte after stacking return address", [0x20, 0, 0x90], [0x8000, 0x8001, 0x1FD, 0x1FD, 0x1FC, 0x8002]);
        BusTrace("bus: RTS reads stack and return address", [0x60], [0x8000, 0x8001, 0x1FD, 0x1FE, 0x1FF, 0x1234],
            (_, b) => { b.Memory[0x1FE] = 0x34; b.Memory[0x1FF] = 0x12; });
        BusTrace("bus: RTI performs the discarded stack read", [0x40], [0x8000, 0x8001, 0x1FD, 0x1FE, 0x1FF, 0x100]);
        BusTrace("bus: PLA reads the old stack pointer", [0x68], [0x8000, 0x8001, 0x1FD, 0x1FE]);
        BusTrace("bus: BRK reads its padding byte", [0x00], [0x8000, 0x8001, 0x1FD, 0x1FC, 0x1FB, 0xFFFE, 0xFFFF]);
        BusTrace("bus: NMI reads the PC twice before pushing", [0xEA], [0x8000, 0x8000, 0x1FD, 0x1FC, 0x1FB, 0xFFFA, 0xFFFB], (c, _) => c.RaiseNmi());

        {
            TraceBus bus = new();
            Cpu6502 cpu = new(bus) { A = 0x34, X = 0x56, Y = 0x78, S = 0x10, PC = 0x8000 };
            cpu.Reset();
            check("reset: performs seven reads and no writes", bus.Accesses.Select(a => a.Address).SequenceEqual(
                new ushort[] { 0x8000, 0x8000, 0x110, 0x10F, 0x10E, 0xFFFC, 0xFFFD }) && bus.Accesses.All(a => !a.Write));
            check("reset: preserves registers and decrements stack pointer", cpu.A == 0x34 && cpu.X == 0x56 && cpu.Y == 0x78 && cpu.S == 0x0D);
        }
        {
            TraceBus bus = new();
            byte[] program = [0x58, 0x78, 0xEA]; // CLI; SEI; NOP
            program.CopyTo(bus.Memory, 0x8000);
            bus.Memory[0xFFFF] = 0x90;
            Cpu6502 cpu = new(bus) { PC = 0x8000, S = 0xFD };
            cpu.SetIrqLine(true);
            cpu.Step(); cpu.Step();
            check("irq: SEI after CLI still permits the sampled IRQ", cpu.Step() == 7 && cpu.PC == 0x9000);
            check("irq: status pushed after SEI contains I", (bus.Memory[0x1FB] & 4) != 0);
        }
        {
            TraceBus bus = new();
            bus.Memory[0xFFFB] = 0x90;
            Cpu6502 cpu = new(bus) { PC = 0x8000, S = 0xFD };
            cpu.OnCycle = () => { if (cpu.Cycles == 4) cpu.RaiseNmi(); };
            cpu.Step();
            check("nmi: hijacks BRK while preserving stacked B", cpu.PC == 0x9000 && (bus.Memory[0x1FB] & 0x10) != 0);
        }

        foreach (int parity in new[] { 0, 1 })
        {
            Nes nes = MakeNes();
            nes.Cpu.Cycles = parity;
            nes.Bus.Write(0x0200, 0x42);
            nes.Ppu.WriteRegister(0x2003, 0x80);
            nes.Bus.Write(0x4014, 2);
            check($"dma: transfer waits for the CPU bus ({parity})", nes.Ppu.ReadRegister(0x2004) == 0);
            int dots = nes.Ppu.Cycle;
            long before = nes.Cpu.Cycles;
            int elapsed = nes.Bus.RunDma(nes.Cpu);
            check($"dma: parity {parity} takes {513 + parity} cycles", elapsed == 513 + parity && nes.Cpu.Cycles - before == elapsed);
            check($"dma: advances the PPU during transfer ({parity})", nes.Ppu.Cycle == (dots + elapsed * 3) % 341);
            nes.Ppu.WriteRegister(0x2003, 0x80);
            check($"dma: starts at OAMADDR ({parity})", nes.Ppu.ReadRegister(0x2004) == 0x42);
        }

        {
            Nes nes = MakeNes();
            nes.RunFrame();
            byte[] state = Save(nes);
            Nes other = MakeNes(1);
            byte[] before = Save(other);
            check("state: rejects different ROM with the same mapper", Reject(other, state) && Save(other).SequenceEqual(before));
            byte[] damaged = (byte[])state.Clone();
            damaged[^20] ^= 1;
            check("state: rejects corrupted payload without changing console", Reject(nes, damaged) && Save(nes).SequenceEqual(state));
            check("state: rejects truncated payload without changing console", Reject(nes, state[..^10]) && Save(nes).SequenceEqual(state));
            byte[] old = (byte[])state.Clone(); old[0] = 1;
            check("state: rejects obsolete state format", Reject(nes, old));
            Nes reloaded = MakeNes();
            reloaded.LoadState(new MemoryStream(state));
            check("state: accepts the same image loaded into another console", Save(reloaded).SequenceEqual(state));
            nes.RunFrame(); reloaded.RunFrame();
            check("state: complete machine replay is deterministic", Save(nes).SequenceEqual(Save(reloaded)));
        }
        {
            FramePacer pacer = new();
            pacer.Reset(0);
            int frames = 0;
            for (int i = 1; i <= 1000; i++) frames += pacer.FramesDue(i / 1000.0);
            check("pacing: frequent callbacks still yield 60 NTSC frames per second", frames == 60);
            check("pacing: a long stall is limited to two frames", pacer.FramesDue(10) == 2 && pacer.FramesDue(10) == 0);
            pacer.Reset(100);
            check("pacing: resume does not catch up paused time", pacer.FramesDue(100) == 0 && pacer.FramesDue(100.017) == 1);
            pacer.Reset(0);
            frames = 0;
            for (int i = 1; i <= 6000; i++) frames += pacer.FramesDue(i / 100.0);
            check("pacing: fractional frames accumulate over one minute", frames == (int)(60 * FramePacer.FramesPerSecond));
        }
        {
            byte[] rom = Image(0); rom[6] = 0x40;
            IMapper mapper = IMapper.Create(Cartridge.FromBytes(rom));
            mapper.CpuWrite(0xC000, 1);
            mapper.CpuWrite(0xC001, 0);
            mapper.CpuWrite(0xE001, 0);
            mapper.OnPpuAddress(0, 0); mapper.OnPpuAddress(0x1000, 10); // reload
            mapper.OnPpuAddress(0, 11); mapper.OnPpuAddress(0x1000, 15); // too short
            check("mmc3: filters short A12 low pulses", !mapper.IrqPending);
            mapper.OnPpuAddress(0, 16); mapper.OnPpuAddress(0x1000, 26);
            check("mmc3: qualified A12 edge decrements counter", mapper.IrqPending);
        }
        {
            Nes nes = MakeNes();
            void Address(ushort a) { nes.Ppu.WriteRegister(0x2006, (byte)(a >> 8)); nes.Ppu.WriteRegister(0x2006, (byte)a); }
            Address(0x2000); nes.Ppu.WriteRegister(0x2007, 0x42);
            Address(0x2000); nes.Ppu.ReadRegister(0x2007); // prime read buffer
            nes.Ppu.WriteRegister(0x2000, 0);
            Address(0x2000);
            check("ppu: register writes do not overwrite the PPUDATA buffer", nes.Ppu.ReadRegister(0x2007) == 0x42);
            Address(0x2F00); nes.Ppu.WriteRegister(0x2007, 0x31);
            Address(0x3F00); nes.Ppu.WriteRegister(0x2007, 0x12);
            Address(0x3F00);
            check("ppu: palette reads bypass the buffer", (nes.Ppu.ReadRegister(0x2007) & 0x3F) == 0x12);
            Address(0x2000);
            check("ppu: palette reads refill buffer from mirrored nametable", nes.Ppu.ReadRegister(0x2007) == 0x31);
        }
        {
            byte[] rom = Image(0);
            for (int row = 0; row < 8; row++) rom[16 + 16384 + 16 + row] = 0x80;
            Ppu2C02 ppu = new(IMapper.Create(Cartridge.FromBytes(rom)));
            for (int i = 0; i < 256; i++) ppu.WriteOam((byte)i, 0xFF);
            ppu.WriteOam(0, 10); ppu.WriteOam(1, 1); ppu.WriteOam(2, 0); ppu.WriteOam(3, 12);
            ppu.WriteRegister(0x2006, 0x3F); ppu.WriteRegister(0x2006, 0x11); ppu.WriteRegister(0x2007, 0x21);
            ppu.WriteRegister(0x2001, 0x14);
            while (!ppu.FrameComplete) ppu.Step();
            check("sprites: fetch slots preserve the sprite X position", ppu.FrameBuffer[11 * 256 + 12] == 0x21
                && ppu.FrameBuffer[11 * 256 + 11] == 0 && ppu.FrameBuffer[11 * 256 + 13] == 0);
        }
    }

    private static void InterruptTiming(Action<string, bool> check)
    {
        foreach (bool brk in new[] { false, true })
        for (int edge = 1; edge <= 7; edge++)
        {
            TraceBus bus = new();
            bus.Memory[0x7FFF] = 0xEA;
            bus.Memory[0x8000] = brk ? (byte)0x00 : (byte)0xEA;
            bus.Memory[0xFFFA] = 0x56; bus.Memory[0xFFFB] = 0xA0;
            bus.Memory[0xFFFE] = 0x12; bus.Memory[0xFFFF] = 0x90;
            bus.Memory[0xA056] = bus.Memory[0x9012] = 0x38; // SEC
            bus.Memory[0xA057] = 0x40; // RTI
            Cpu6502 cpu = new(bus) { PC = brk ? (ushort)0x8000 : (ushort)0x7FFF, S = 0xFD, P = 0x20 };
            if (!brk)
            {
                cpu.SetIrqLine(true);
                cpu.Step(); // sample IRQ during the preceding NOP
                cpu.SetIrqLine(false);
            }
            long start = cpu.Cycles;
            cpu.NmiInput = () => cpu.Cycles - start >= edge;
            bus.Accesses.Clear();
            int elapsed = cpu.Step();
            bool hijacked = edge <= 4;
            ushort handler = hijacked ? (ushort)0xA056 : (ushort)0x9012;
            ushort vector = hijacked ? (ushort)0xFFFA : (ushort)0xFFFE;
            string name = $"NMI during {(brk ? "BRK" : "IRQ")} cycle {edge}";
            check($"{name}: vector and seven bus accesses", elapsed == 7 && cpu.PC == handler &&
                bus.Accesses.Select(a => a.Address).SequenceEqual(new ushort[]
                { 0x8000, brk ? (ushort)0x8001 : (ushort)0x8000, 0x1FD, 0x1FC, 0x1FB, vector, (ushort)(vector + 1) }) &&
                bus.Accesses.Select(a => a.Write).SequenceEqual(new[] { false, false, true, true, true, false, false }));
            check($"{name}: preserves the original return address and B flag",
                bus.Memory[0x1FD] == 0x80 && bus.Memory[0x1FC] == (brk ? 2 : 0) &&
                bus.Memory[0x1FB] == (brk ? 0x30 : 0x20) && cpu.S == 0xFA);

            // Restore at the instruction boundary with the NMI line still held.
            using MemoryStream saved = new();
            cpu.SaveState(new BinaryWriter(saved));
            Cpu6502 restored = new(bus);
            saved.Position = 0;
            restored.LoadState(new BinaryReader(saved));
            restored.NmiInput = () => true;
            check($"{name}: handler executes SEC first after save/load",
                restored.Step() == 2 && restored.PC == handler + 1 && (restored.P & Cpu6502.FlagCarry) != 0);
            if (hijacked)
            {
                check($"{name}: held NMI does not trigger twice",
                    restored.Step() == 6 && restored.PC == (brk ? 0x8002 : 0x8000) && restored.S == 0xFD);
            }
            else
            {
                check($"{name}: late NMI follows the first handler instruction",
                    restored.Step() == 7 && restored.PC == 0xA056 &&
                    bus.Memory[0x1FA] == 0x90 && bus.Memory[0x1F9] == 0x13 && bus.Memory[0x1F8] == 0x25);
            }
        }

        foreach (bool nmi in new[] { false, true })
        foreach ((string name, ushort pc, byte opcode, byte operand, int cycles, ushort target, int poll) in new[]
        {
            ("untaken branch", (ushort)0x8000, (byte)0xB0, (byte)0x02, 2, (ushort)0x8002, 1),
            ("taken branch in page", (ushort)0x8000, (byte)0x90, (byte)0x02, 3, (ushort)0x8004, 1),
            ("taken branch across page", (ushort)0x80FC, (byte)0x90, (byte)0x02, 4, (ushort)0x8100, 3),
            ("absolute JMP", (ushort)0x8000, (byte)0x4C, (byte)0x04, 3, (ushort)0x8004, 2),
        })
        for (int edge = 1; edge <= cycles; edge++)
        {
            TraceBus bus = new();
            bus.Memory[pc] = opcode; bus.Memory[pc + 1] = operand; bus.Memory[pc + 2] = 0x80;
            bus.Memory[target] = 0xE8; // INX makes a deferred interrupt observable
            bus.Memory[0xFFFB] = 0xA0; bus.Memory[0xFFFF] = 0x90;
            Cpu6502 cpu = new(bus) { PC = pc, S = 0xFD, P = 0x20 };
            cpu.NmiInput = () => nmi && cpu.Cycles >= edge;
            cpu.OnCycleComplete = () => { if (!nmi && cpu.Cycles >= edge) cpu.SetIrqLine(true); };
            int elapsed = cpu.Step();
            bool immediate = edge <= poll;
            int next = cpu.Step();
            ushort handler = nmi ? (ushort)0xA000 : (ushort)0x9000;
            bool matches = elapsed == cycles && (immediate
                ? next == 7 && cpu.PC == handler && cpu.X == 0
                : next == 2 && cpu.PC == target + 1 && cpu.X == 1 && cpu.Step() == 7 && cpu.PC == handler);
            check($"{name}: {(nmi ? "NMI" : "IRQ")} asserted on cycle {edge}", matches);
        }
    }

    private static byte[] Image(byte marker)
    {
        byte[] image = new byte[16 + 16384 + 8192];
        image[0] = (byte)'N'; image[1] = (byte)'E'; image[2] = (byte)'S'; image[3] = 0x1A;
        image[4] = 1; image[5] = 1;
        image[16] = 0x4C; image[17] = 0; image[18] = 0xC0; image[19] = marker;
        image[16 + 0x3FFD] = 0xC0;
        return image;
    }
    private static Nes MakeNes(byte marker = 0) => new(Cartridge.FromBytes(Image(marker)));
    private static byte[] Save(Nes nes) { using MemoryStream stream = new(); nes.SaveState(stream); return stream.ToArray(); }
    private static bool Reject(Nes nes, byte[] state)
    {
        try { nes.LoadState(new MemoryStream(state)); return false; }
        catch (InvalidDataException) { return true; }
    }
    private sealed class TraceBus : IBus
    {
        public readonly byte[] Memory = new byte[65536];
        public int Clock;
        public List<(ushort Address, bool Write, int Clock)> Accesses { get; } = [];
        public byte Read(ushort address) { Accesses.Add((address, false, Clock)); return Memory[address]; }
        public void Write(ushort address, byte value) { Accesses.Add((address, true, Clock)); Memory[address] = value; }
        public byte Peek(ushort address) => Memory[address];
    }
}
