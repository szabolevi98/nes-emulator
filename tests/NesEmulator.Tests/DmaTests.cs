using NesEmulator.Core;
using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;

internal static class DmaTests
{
    public static void Run(Action<string, bool> check)
    {
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
