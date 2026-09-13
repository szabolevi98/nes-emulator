using NesEmulator.Core;
using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;
using System.IO.Compression;

internal static class DmaTests
{
    public static void Run(Action<string, bool> check)
    {
        StopTests(check);
        foreach (int parity in new[] { 0, 1 })
        {
            Rig rig = new([0xEA, 0xEA, 0xEA, 0xEA]);
            if ((rig.Cpu.Cycles & 1) != parity) rig.Cpu.DmaRead(0);
            long start = rig.Cpu.Cycles;
            rig.StartDmc();
            check($"DMC load phase {parity}: status stays active before the fetch", rig.Nes.Apu.Dmc.Active);
            rig.Trace.Clear();
            for (int i = 0; i < 3; i++) rig.Cpu.Step();
            var fetches = rig.Trace.Where(a => a.Address == 0xC000).ToArray();
            int delay = parity == 0 ? 4 : 3;
            check($"DMC load phase {parity}: second GET halt then three DMA cycles",
                fetches.Length == 1 && fetches[0].Cycle == start + delay + 2 &&
                rig.Cpu.Cycles - start == 9 && (fetches[0].Cycle & 1) == 0);
            check($"DMC load phase {parity}: last fetch clears active and raises IRQ before playback",
                !rig.Nes.Apu.Dmc.Active && rig.Nes.Apu.Dmc.IrqPending && rig.Nes.Apu.Dmc.OutputLevel == 0);
        }

        foreach (bool rmw in new[] { false, true })
        {
            Rig rig = new(rmw ? [0x06, 0x20, 0xEA] : [0x85, 0x20, 0xEA]); // ASL or STA zp; NOP
            if (rmw) rig.Cpu.DmaRead(0); // GET: load request falls on ASL's first write
            rig.Cpu.A = 0x55;
            rig.Nes.Bus.Write(0x20, 3);
            long start = rig.Cpu.Cycles;
            rig.StartDmc();
            rig.Trace.Clear();
            int instruction = rig.Cpu.Step();
            int next = rig.Cpu.Step();
            var writes = rig.Trace.Where(a => a.Write).ToArray();
            check($"DMC halt waits through {(rmw ? "both RMW writes" : "STA")}",
                instruction == (rmw ? 5 : 3) && next == (rmw ? 5 : 6) &&
                writes.Select(a => a.Value).SequenceEqual(rmw ? new byte[] { 3, 6 } : new byte[] { 0x55 }) &&
                rig.Trace.Single(a => a.Address == 0xC000).Cycle == start + (rmw ? 8 : 7));
        }

        {
            Rig rig = new([0x02]); // JAM still exposes its held bus reads to DMA
            rig.StartDmc();
            rig.Cpu.Step();
            long before = rig.Cpu.Cycles;
            int elapsed = rig.Cpu.Step();
            check("DMC: a jammed CPU reports the stolen cycles without resuming", elapsed == 4 && rig.Cpu.Cycles - before == elapsed && rig.Cpu.Jammed);
        }

        foreach (int parity in new[] { 0, 1 })
        {
            Rig rig = new([0xEA]);
            if ((rig.Cpu.Cycles & 1) != parity) rig.Cpu.DmaRead(0);
            for (int i = 0; i < 256; i++) rig.Nes.Bus.Write((ushort)(0x300 + i), (byte)(i ^ 0xA5));
            rig.StartDmc();
            rig.Nes.Bus.Write(0x4014, 3);
            rig.Trace.Clear();
            int cycles = rig.Cpu.Step();
            var writes = rig.Trace.Where(a => a.Address == 0x2004 && a.Write).ToArray();
            check($"DMC/OAM phase {parity}: overlap adds two cycles",
                cycles == 513 + parity + 2 + 2 && rig.Trace.Count(a => a.Address == 0xC000) == 1);
            check($"DMC/OAM phase {parity}: all 256 bytes survive in order",
                writes.Select(a => a.Value).SequenceEqual(Enumerable.Range(0, 256).Select(i => (byte)(i ^ 0xA5))) &&
                writes.All(a => (a.Cycle & 1) == 1));
        }

        {
            Rig rig = new([0x90, 0x02, 0xEA, 0xEA, 0xE8]); // BCC; INX at target
            rig.Cpu.P = 0x20;
            rig.StartDmc(); // last-byte IRQ arrives while the branch's extra read is stalled
            check("DMC/branch: stolen cycles do not change a taken branch's early poll",
                rig.Cpu.Step() == 6 && rig.Cpu.Step() == 2 && rig.Cpu.X == 1 && rig.Cpu.PC == 0x205);
            check("DMC/branch: deferred IRQ follows the target instruction", rig.Cpu.Step() == 7 && rig.Cpu.PC == 0x5555);
        }

        foreach (ushort port in new ushort[] { 0x4016, 0x4017 })
        {
            Rig rig = new([0xAD, (byte)port, 0x40]);
            rig.Cpu.DmaRead(0); // GET: the load DMA lands on LDA's data read
            Controller pad = port == 0x4016 ? rig.Nes.Port1 : rig.Nes.Port2;
            pad.Buttons = NesButton.B;
            rig.Nes.Bus.Write(0x4016, 1); rig.Nes.Bus.Write(0x4016, 0);
            rig.StartDmc();
            rig.Cpu.Step();
            check($"DMA ${port:X4}: contiguous halted reads clock the pad once", (rig.Cpu.A & 1) == 1);
            for (int i = 0; i < 6; i++) pad.Read();
            check($"controller ${port:X4}: reads after the eight buttons return one", (pad.Read() & 1) == 1);
        }

        {
            DmcChannel dmc = new();
            dmc.WriteControl(0x8F); dmc.WriteLength(0); dmc.SetEnabled(true);
            for (int i = 0; i < 4; i++) dmc.Clock();
            dmc.CompleteDma(0xFF);
            check("DMC: IRQ describes the reader, not the output shifter", dmc.IrqPending && !dmc.Active && dmc.OutputLevel == 0);
            dmc.SetEnabled(false);
            using MemoryStream state = new();
            dmc.SaveState(new BinaryWriter(state));
            DmcChannel restored = new();
            state.Position = 0; restored.LoadState(new BinaryReader(state));
            for (int i = 0; i < 1000; i++) restored.Clock();
            check("DMC: disabling and saving retains an unread sample byte", restored.OutputLevel == 16 && !restored.Active);
        }

        {
            DmcChannel dmc = new();
            dmc.WriteControl(0x0F); dmc.SetEnabled(true);
            for (int i = 0; i < 4; i++) dmc.Clock();
            dmc.CompleteDma(0x01);
            dmc.WriteAddress(1); dmc.WriteLength(1); dmc.SetEnabled(true);
            check("DMC: restarting with a full buffer does not overwrite it", dmc.Active && !dmc.DmaPending);
            for (int i = 0; i < 1000 && !dmc.DmaPending; i++) dmc.Clock();
            check("DMC: emptying the retained buffer requests the new sample", dmc.DmaPending && dmc.DmaAddress == 0xC040);
        }

        {
            List<ushort> addresses = [];
            DmcChannel dmc = new() { ReadMemory = a => { addresses.Add(a); return 0x55; } };
            dmc.WriteControl(0x0F); dmc.WriteAddress(0xFF); dmc.WriteLength(4); dmc.SetEnabled(true);
            for (int i = 0; i < 54 * 8 * 70; i++) dmc.Clock();
            check("DMC: the memory reader wraps FFFF to 8000", addresses.Count == 65 &&
                addresses.Take(64).SequenceEqual(Enumerable.Range(0xFFC0, 64).Select(a => (ushort)a)) && addresses[^1] == 0x8000);
        }

        foreach (int instructions in new[] { 0, 1, 2, 30 })
        {
            Nes nes = NewNes();
            nes.Cpu.PC = 0x200;
            nes.Bus.Write(0x200, 0x4C); nes.Bus.Write(0x201, 0); nes.Bus.Write(0x202, 2);
            nes.Apu.WriteRegister(0x4010, 0x4F);
            nes.Apu.WriteRegister(0x4015, 0x10);
            for (int i = 0; i < instructions; i++) nes.StepInstruction();
            byte[] state = Save(nes);
            for (int i = 0; i < 400; i++) nes.StepInstruction();
            byte[] expected = Save(nes);
            nes.LoadState(new MemoryStream(state));
            for (int i = 0; i < 400; i++) nes.StepInstruction();
            check($"DMC state: pending/filled/playing reader replays after {instructions} instructions", Save(nes).SequenceEqual(expected));
        }

        {
            Nes nes = NewNes();
            nes.Apu.WriteRegister(0x4015, 0x10);
            nes.Bus.Write(0x4014, 3);
            long cycles = nes.Cpu.Cycles;
            bool nextGet = nes.Apu.Dmc.NextCycleIsGet;
            nes.Reset();
            check("reset: cancels pending OAM and DMC halts", nes.Cpu.Cycles - cycles == 7 && !nes.Apu.Dmc.Active && nes.Bus.RunDma(nes.Cpu) == 0);
            check("reset: DMA phase advances with the seven reset cycles", nes.Apu.Dmc.NextCycleIsGet != nextGet);
        }
    }

    private static void StopTests(Action<string, bool> check)
    {
        {
            DmcChannel dmc = new();
            for (int i = 0; i < 175; i++) dmc.Clock();
            // The running divider now happens to equal the new period minus
            // one. Changing the rate must not invent an output reload edge.
            dmc.WriteControl(0x85);
            dmc.SetEnabled(true);
            dmc.CompleteDma(0xFF);
            check("DMC rate change: matching divider values do not duplicate a sample", !dmc.Active && !dmc.DmaPending && dmc.IrqPending);
        }
        foreach (int phase in new[] { 0, 1 })
        {
            DmcChannel dmc = new();
            if (phase == 1) dmc.Clock();
            dmc.SetEnabled(true);
            dmc.SetEnabled(false);
            int delay = phase == 0 ? 3 : 2;
            dmc.Clock();
            dmc.SetEnabled(false); // A second write cannot postpone the stop.
            dmc.SetEnabled(true); // Re-enabling an active reader cannot restart it.
            for (int i = 1; i < delay - 1; i++) dmc.Clock();
            check($"DMC stop phase {phase}: status stays active until propagation completes", dmc.Active);
            dmc.Clock();
            check($"DMC stop phase {phase}: repeated writes preserve the original deadline", !dmc.Active && !dmc.DmaPending);
        }
        Rig Prime()
        {
            Rig rig = new([0xEA]);
            rig.Nes.Apu.WriteRegister(0x4010, 0x4F);
            rig.Nes.Apu.WriteRegister(0x4015, 0x10);
            while (!rig.Nes.Apu.Dmc.DmaPending) rig.Cpu.DmaRead(0);
            rig.Nes.Bus.RunDma(rig.Cpu);
            return rig;
        }
        Rig probe = Prime();
        while (!probe.Nes.Apu.Dmc.DmaPending) probe.Cpu.DmaRead(0);
        long halt = probe.Cpu.Cycles + 1;
        foreach (int distance in new[] { 6, 5, 4, 3, 2, 1 })
        {
            Rig rig = Prime();
            while (rig.Cpu.Cycles < halt - distance) rig.Cpu.DmaRead(0);
            rig.Nes.Apu.WriteRegister(0x4015, 0);
            rig.Trace.Clear();
            int stolen = 0;
            while (rig.Cpu.Cycles < halt + 8)
            {
                stolen += rig.Nes.Bus.RunDma(rig.Cpu, 0x200);
                rig.Cpu.DmaRead(0x200);
            }
            int expected = distance is 2 or 3 ? 1 : distance == 1 ? 4 : 0;
            check($"DMC explicit stop {distance} cycles before reload: {expected} stolen cycles", stolen == expected);
            check($"DMC explicit stop {distance}: only a started transfer finishes its bus read",
                rig.Trace.Count(a => a.Address == 0xC000) == (distance == 1 ? 1 : 0) && !rig.Nes.Apu.Dmc.Active);
        }

        foreach (bool write in new[] { false, true })
        {
            Rig rig = Prime();
            while (rig.Cpu.Cycles < halt - 3) rig.Cpu.DmaRead(0);
            rig.Nes.Apu.WriteRegister(0x4015, 0);
            while (rig.Cpu.Cycles < halt - 1) rig.Cpu.DmaRead(0);
            if (write) rig.Cpu.DmaWrite(0x20, 0xA5);
            int cycles = rig.Nes.Bus.RunDma(rig.Cpu, 0x200);
            check($"DMC aborted halt: {(write ? "a write cancels it instead of deferring" : "a read is held for one cycle")}", cycles == (write ? 0 : 1));
        }

        foreach (ushort port in new ushort[] { 0x4016, 0x4017 })
        {
            Rig rig = Prime();
            Controller pad = port == 0x4016 ? rig.Nes.Port1 : rig.Nes.Port2;
            pad.Buttons = NesButton.B;
            rig.Nes.Bus.Write(0x4016, 1); rig.Nes.Bus.Write(0x4016, 0);
            while (rig.Cpu.Cycles < halt - 3) rig.Cpu.DmaRead(0);
            rig.Nes.Apu.WriteRegister(0x4015, 0);
            while (rig.Cpu.Cycles < halt - 1) rig.Cpu.DmaRead(0);
            int cycles = rig.Nes.Bus.RunDma(rig.Cpu, port);
            byte value = rig.Cpu.DmaRead(port);
            check($"DMC aborted ${port:X4} read: halt and resumed read share one controller clock",
                cycles == 1 && (value & 1) == 0 && (pad.Read() & 1) == 1);
        }

        {
            Rig rig = Prime();
            while (rig.Cpu.Cycles < halt - 3) rig.Cpu.DmaRead(0);
            for (int i = 0; i < 256; i++) rig.Nes.Bus.Write((ushort)(0x300 + i), (byte)(i ^ 0xA5));
            rig.Nes.Apu.WriteRegister(0x4015, 0);
            rig.Nes.Bus.Write(0x4014, 3);
            rig.Trace.Clear();
            int cycles = rig.Nes.Bus.RunDma(rig.Cpu, 0x200);
            check("DMC abort during OAM: the abort adds no transfer cycles", cycles == 513 && !rig.Trace.Any(a => a.Address == 0xC000));
            check("DMC abort during OAM: all 256 sprite writes still complete",
                rig.Trace.Where(a => a.Address == 0x2004 && a.Write).Select(a => a.Value)
                    .SequenceEqual(Enumerable.Range(0, 256).Select(i => (byte)(i ^ 0xA5))));
        }

        foreach (int phase in new[] { 0, 1 })
        foreach (int elapsed in new[] { 0, 1, 2 })
        {
            Nes nes = NewNes();
            nes.Cpu.PC = 0x200;
            nes.Bus.Write(0x200, 0x4C); nes.Bus.Write(0x201, 0); nes.Bus.Write(0x202, 2);
            nes.Apu.WriteRegister(0x4010, 0x4F);
            nes.Apu.WriteRegister(0x4015, 0x10);
            nes.StepInstruction(); nes.StepInstruction();
            if ((nes.Cpu.Cycles & 1) != phase) nes.Cpu.DmaRead(0);
            nes.Apu.WriteRegister(0x4015, 0);
            for (int i = 0; i < elapsed; i++) nes.Cpu.DmaRead(0);
            byte[] saved = Save(nes);
            for (int i = 0; i < 300; i++) nes.StepInstruction();
            byte[] expected = Save(nes);
            nes.LoadState(new MemoryStream(saved));
            for (int i = 0; i < 300; i++) nes.StepInstruction();
            check($"DMC stop state: phase {phase}, delay advanced {elapsed} cycles replays", Save(nes).SequenceEqual(expected));
        }

        foreach (int distance in new[] { 11, 9, 7, 5 })
        foreach (bool write in new[] { false, true })
        {
            Rig rig = Prime();
            rig.Nes.Apu.WriteRegister(0x4010, 0x8F); // One byte, no loop, IRQ.
            rig.Nes.Apu.WriteRegister(0x4015, 0);
            // The retained byte enters the shifter at the first reload boundary.
            // Arrange a new load around the next boundary, eight 54-cycle bits later.
            long nextHalt = halt + 8 * 54;
            while (rig.Cpu.Cycles < nextHalt - distance) rig.Cpu.DmaRead(0);
            rig.Nes.Apu.WriteRegister(0x4015, 0x10);
            rig.Trace.Clear();
            int stolen = 0;
            while (rig.Cpu.Cycles < nextHalt + 10)
            {
                if (write && rig.Cpu.Cycles + 1 == nextHalt) rig.Cpu.DmaWrite(0x20, 0xA5);
                else
                {
                    stolen += rig.Nes.Bus.RunDma(rig.Cpu, 0x200);
                    rig.Cpu.DmaRead(0x200);
                }
            }
            var fetches = rig.Trace.Where(a => a.Address == 0xC000).ToArray();
            // A duplicate follows immediately: the CPU never resumes to write
            // between the load and its second fetch. Only the later abort can
            // collide with a CPU write.
            int expected = distance == 7 ? 7 : distance == 9 && !write ? 4 : 3;
            check($"DMC implicit stop: load {distance} cycles before reload, halt write {write}", stolen == expected);
            check($"DMC implicit stop {distance}/{write}: duplicate fetch uses the same address and retains IRQ",
                fetches.Length == (distance == 7 ? 2 : 1) && rig.Nes.Apu.Dmc.IrqPending && !rig.Nes.Apu.Dmc.Active);
        }

        foreach (int steps in new[] { 0, 20 })
        {
            Nes expected = NewNes();
            expected.Cpu.PC = 0x200;
            expected.Bus.Write(0x200, 0x4C); expected.Bus.Write(0x201, 0); expected.Bus.Write(0x202, 2);
            expected.Apu.WriteRegister(0x4010, 0x4F); expected.Apu.WriteRegister(0x4015, 0x10);
            for (int i = 0; i < steps; i++) expected.StepInstruction();
            Nes actual = NewNes();
            using Stream resource = typeof(DmaTests).Assembly.GetManifestResourceStream($"v8-dmc-{steps}.state.gz")!;
            using GZipStream gzip = new(resource, CompressionMode.Decompress);
            actual.LoadState(gzip);
            check($"state v8: DMC fixture after {steps} instructions loads without a pending stop", Save(actual).SequenceEqual(Save(expected)));
            for (int i = 0; i < 300; i++) { expected.StepInstruction(); actual.StepInstruction(); }
            check($"state v8: DMC fixture after {steps} instructions continues identically", Save(actual).SequenceEqual(Save(expected)));
        }

        {
            // v9 wrote no $AB profile byte. Such a state was produced by a core
            // that always used the $EE mask, so it may only load into that profile.
            Nes expected = NewNes();
            expected.Cpu.PC = 0x200;
            expected.Bus.Write(0x200, 0x4C); expected.Bus.Write(0x201, 0); expected.Bus.Write(0x202, 2);
            expected.Apu.WriteRegister(0x4010, 0x4F); expected.Apu.WriteRegister(0x4015, 0x10);
            for (int i = 0; i < 20; i++) expected.StepInstruction();

            byte[] Fixture()
            {
                using Stream resource = typeof(DmaTests).Assembly.GetManifestResourceStream("v9-dmc-20.state.gz")!;
                using GZipStream gzip = new(resource, CompressionMode.Decompress);
                using MemoryStream plain = new();
                gzip.CopyTo(plain);
                return plain.ToArray();
            }

            Nes actual = NewNes();
            actual.LoadState(new MemoryStream(Fixture()));
            check("state v9: the fixture migrates into the default $EE profile", Save(actual).SequenceEqual(Save(expected)));
            for (int i = 0; i < 300; i++) { expected.StepInstruction(); actual.StepInstruction(); }
            check("state v9: the migrated fixture continues identically", Save(actual).SequenceEqual(Save(expected)));

            byte[] image = new byte[16 + 32768 + 8192];
            image[0] = 0x4E; image[1] = 0x45; image[2] = 0x53; image[3] = 0x1A;
            image[4] = 2; image[5] = 1;
            Array.Fill(image, (byte)0x55, 16, 32768);
            Nes other = new(Cartridge.FromBytes(image), abProfile: AbOpcodeProfile.MaskFF);
            bool refused = false;
            try { other.LoadState(new MemoryStream(Fixture())); }
            catch (InvalidDataException) { refused = true; }
            check("state v9: the fixture is refused by the $FF profile", refused);
        }
    }

    private static byte[] Save(Nes nes) { using MemoryStream stream = new(); nes.SaveState(stream); return stream.ToArray(); }

    private static Nes NewNes()
    {
        byte[] image = new byte[16 + 32768 + 8192];
        image[0] = 0x4E; image[1] = 0x45; image[2] = 0x53; image[3] = 0x1A;
        image[4] = 2; image[5] = 1;
        Array.Fill(image, (byte)0x55, 16, 32768);
        return new(Cartridge.FromBytes(image));
    }

    private sealed class Rig
    {
        public Nes Nes { get; } = NewNes();
        public Cpu6502 Cpu { get; }
        public List<(long Cycle, ushort Address, bool Write, byte Value)> Trace { get; } = [];
        public Rig(byte[] program)
        {
            Cpu = new(new TraceBus(this)) { PC = 0x200, S = 0xFD, Cycles = Nes.Cpu.Cycles };
            for (int i = 0; i < program.Length; i++) Nes.Bus.Write((ushort)(0x200 + i), program[i]);
            Cpu.BeforeRead = a => Nes.Bus.RunDma(Cpu, a);
            Cpu.OnCycle = () => { Nes.Apu.Step(); Nes.Ppu.Step(); Nes.Ppu.Step(); };
            Cpu.OnCycleComplete = () => { Nes.Ppu.Step(); Cpu.SetIrqLine(Nes.Apu.IrqPending); };
        }
        public void StartDmc()
        {
            Nes.Apu.WriteRegister(0x4010, 0x8F);
            Nes.Apu.WriteRegister(0x4013, 0);
            Nes.Apu.WriteRegister(0x4015, 0x10);
        }
    }

    private sealed class TraceBus(Rig rig) : IBus
    {
        public byte Read(ushort address)
        {
            byte value = rig.Nes.Bus.Read(address);
            rig.Trace.Add((rig.Cpu.Cycles, address, false, value));
            return value;
        }
        public void Write(ushort address, byte value)
        {
            rig.Trace.Add((rig.Cpu.Cycles, address, true, value));
            rig.Nes.Bus.Write(address, value);
        }
        public byte Peek(ushort address) => rig.Nes.Bus.Peek(address);
    }
}
