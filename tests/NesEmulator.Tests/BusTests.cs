using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Input;

/// <summary>
/// The processor data bus itself: what happens when nothing drives it, which
/// reads leave it alone, and how the controller ports are strobed and clocked.
/// </summary>
internal static class BusTests
{
    public static void Run(Action<string, bool> check)
    {
        OpenBusTests(check);
        ControllerTests(check);
        DmaConflictTests(check);
        PictureBusTests(check);
        RenderingAccessTests(check);
    }

    /// <summary>
    /// $2004 and $2007 reach memory the beam is already using, so while a line is
    /// being drawn they behave differently from the same access in blanking.
    /// </summary>
    private static void RenderingAccessTests(Action<string, bool> check)
    {
        Nes Drawing()
        {
            Nes nes = Machine();
            for (int i = 0; i < 256; i++) nes.Ppu.WriteOam((byte)i, (byte)i);
            nes.Bus.Write(0x2001, 0x1E);                 // both layers on
            while (nes.Ppu.Scanline != 10 || nes.Ppu.Cycle != 100) nes.Ppu.Step();
            return nes;
        }

        {
            Nes nes = Drawing();
            nes.Bus.Write(0x2003, 0x05);
            nes.Bus.Write(0x2004, 0xAA);
            nes.Bus.Write(0x2001, 0x00);                 // stop drawing to look at it
            byte atAddress = nes.Bus.Read(0x2004);
            nes.Bus.Write(0x2003, 0x05);
            check("rendering: a $2004 write never reaches sprite memory", nes.Bus.Read(0x2004) == 0x05);
            check("rendering: a $2004 write bumps only the sprite index", atAddress == 0x08);
        }

        {
            // The same write in blanking lands normally and steps by one. Entry 8
            // is a sprite's Y coordinate, so neither byte is an attribute byte.
            Nes nes = Machine();
            for (int i = 0; i < 256; i++) nes.Ppu.WriteOam((byte)i, (byte)i);
            nes.Bus.Write(0x2003, 0x08);
            nes.Bus.Write(0x2004, 0xAA);
            byte next = nes.Bus.Read(0x2004);
            nes.Bus.Write(0x2003, 0x08);
            check("blanking: a $2004 write lands and steps by one",
                nes.Bus.Read(0x2004) == 0xAA && next == 0x09);
        }

        {
            // $2007 shares the scroll counters, so an access while drawing clocks
            // both of them instead of adding the configured step.
            Nes nes = Drawing();
            nes.Bus.Write(0x2006, 0x21);
            nes.Bus.Write(0x2006, 0x08);                 // coarse 8/8, fine Y 2
            nes.Bus.Read(0x2007);
            check("rendering: a $2007 read clocks both scroll counters",
                nes.Ppu.ScrollAddress == 0x3109);
        }

        {
            Nes nes = Machine();
            nes.Bus.Write(0x2006, 0x21);
            nes.Bus.Write(0x2006, 0x08);
            nes.Bus.Read(0x2007);
            check("blanking: a $2007 read steps by the configured amount",
                nes.Ppu.ScrollAddress == 0x2109);
        }

        {
            // The pattern shifters' serial input is tied high, so every shift
            // brings a one in at the bottom. Dot 100 is not a reload dot, so the
            // shift is all that happens there.
            Nes nes = Machine();
            nes.Bus.Write(0x2001, 0x08);                 // background on
            while (nes.Ppu.Scanline != 20 || nes.Ppu.Cycle != 100) nes.Ppu.Step();
            ushort before = nes.Ppu.PatternShiftLow;
            nes.Ppu.Step();
            check("rendering: the pattern shifters bring in a one",
                nes.Ppu.PatternShiftLow == (ushort)((before << 1) | 1));
        }

        {
            // The chip's own bus supplies the one line $4015 leaves alone, and a
            // transfer's cycles never drive it.
            Nes nes = Machine();
            for (int i = 0; i < 256; i++) nes.Bus.Write((ushort)(0x0200 + i), 0xFF);
            nes.Bus.Write(0x0010, 0x00);                 // both buses low
            nes.Bus.Write(0x4014, 0x02);
            nes.Bus.RunDma(nes.Cpu, 0x4001);             // drives the external bus high
            check("transfer: the chip's own bus keeps $4015's unused line",
                (nes.Bus.Read(0x4015) & 0x20) == 0);
        }
    }

    /// <summary>
    /// The picture unit's own data lines: what a register that drives nothing
    /// answers with, how long that charge lasts, and what palette memory returns.
    /// </summary>
    private static void PictureBusTests(Action<string, bool> check)
    {
        // Roughly a second of picture unit clock, well past the decay window.
        const int Second = 5_369_318;

        {
            Nes nes = Machine();
            nes.Bus.Write(0x2000, 0xA5);
            bool held = nes.Bus.Read(0x2000) == 0xA5;
            for (int i = 0; i < Second; i++) nes.Ppu.Step();
            check("picture bus: a write-only register answers with the lines' charge",
                held && nes.Bus.Read(0x2000) == 0x00);
        }

        {
            // Reading $2002 drives only its three status lines, so the five below
            // them keep whatever charge they have — here, none.
            Nes nes = Machine();
            nes.Bus.Write(0x2000, 0xFF);
            for (int i = 0; i < Second; i++) nes.Ppu.Step();
            byte status = nes.Bus.Read(0x2002);
            check("picture bus: $2002 refreshes only its own lines",
                (status & 0x1F) == 0 && nes.Bus.Read(0x2000) == (status & 0xE0));
        }

        {
            // Palette memory drives six lines; the two above them keep their charge.
            Nes nes = Machine();
            byte Read(byte entry)
            {
                nes.Bus.Write(0x2006, 0x3F);
                nes.Bus.Write(0x2006, entry);
                return nes.Bus.Read(0x2007);
            }

            nes.Bus.Write(0x2006, 0x3F);
            nes.Bus.Write(0x2006, 0x01);
            nes.Bus.Write(0x2007, 0x2B);      // the write itself is never masked
            byte full = Read(0x01);
            nes.Bus.Write(0x2001, 0x01);      // greyscale
            byte grey = Read(0x01);
            check("picture bus: a palette read is six bits wide", full == 0x2B);
            check("picture bus: greyscale masks the hue out of a palette read", grey == 0x20);
            nes.Bus.Write(0x2001, 0x00);
            check("picture bus: greyscale leaves the stored colour alone", Read(0x01) == 0x2B);
        }
    }

    /// <summary>
    /// While a transfer holds the processor, the 2A03 still decodes its own
    /// registers: the stalled processor address says whether they answer, and the
    /// address on the pins picks which one, mirrored every $20 bytes.
    /// </summary>
    private static void DmaConflictTests(Action<string, bool> check)
    {
        // A sprite transfer from an unmapped page, stalled inside $4000-$401F.
        Nes Transfer(byte page, ushort stalled)
        {
            Nes nes = Machine(0xAD, 0x16, 0x40); // LDA $4016
            nes.Port1.Buttons = NesButton.A;
            Strobe(nes);
            nes.Bus.Write(0x4014, page);
            nes.Bus.RunDma(nes.Cpu, stalled);
            return nes;
        }

        byte Sprite(Nes nes, byte offset)
        {
            nes.Bus.Write(0x2003, offset);
            return nes.Bus.Read(0x2004);
        }

        {
            Nes nes = Transfer(0x50, 0x4001);
            check("dma conflict: a mirrored read collects the pad's data lines",
                (Sprite(nes, 0x16) & 1) == 1 && (Sprite(nes, 0x17) & 1) == 0);
        }

        {
            // The same transfer with the processor stalled outside that range
            // reaches no register at all.
            Nes nes = Transfer(0x50, 0x0300);
            check("dma conflict: a stall outside $4000-$401F reaches no register",
                (Sprite(nes, 0x16) & 1) == 0);
        }

        {
            // Work RAM drives the same lines harder than a pad does.
            Nes nes = Machine(0xAD, 0x16, 0x40);
            nes.Port1.Buttons = NesButton.A;
            Strobe(nes);
            for (int i = 0; i < 256; i++) nes.Bus.Write((ushort)(0x0200 + i), 0xFF);
            nes.Bus.Write(0x4014, 0x02);
            nes.Bus.RunDma(nes.Cpu, 0x4001);
            // Offset $17 is not an attribute byte, so the whole value survives.
            check("dma conflict: work RAM hides the pad", Sprite(nes, 0x17) == 0xFF);
        }

        {
            // The status register answers wherever it is mirrored, and reading it
            // acknowledges the frame interrupt even when nobody asked for it.
            Nes nes = Machine(0xAD, 0x16, 0x40);
            nes.Apu.WriteRegister(0x4017, 0x00);
            for (int i = 0; i < 30000; i++) nes.Apu.Step();
            bool raised = nes.Apu.IrqPending;
            nes.Bus.Write(0x4014, 0x50);
            nes.Bus.RunDma(nes.Cpu, 0x4001);
            check("dma conflict: a mirrored status read acknowledges the frame interrupt",
                raised && !nes.Apu.IrqPending);
        }

        {
            // And it leaves the bus alone, so the next unmapped read is unchanged.
            Nes nes = Machine();
            nes.Bus.Write(0x0010, 0x20);
            nes.Bus.RunDma(nes.Cpu, 0x4001);
            check("dma conflict: the status read does not drive the bus", nes.Bus.Read(0x5000) == 0x20);
        }
    }

    private static void OpenBusTests(Action<string, bool> check)
    {
        {
            // LDA $5501: nothing on the cartridge answers, so the processor reads
            // back the last thing carried on the bus, the operand's high byte.
            Nes nes = Machine(0xAD, 0x01, 0x55);
            nes.StepInstruction();
            check("open bus: an unmapped read returns the last value on the bus", nes.Cpu.A == 0x55);
        }

        {
            // LDY #$10; LDA $50F8,Y crosses a page, so the fixup read at $5008 and
            // the real read at $5108 are both open bus and neither changes it.
            Nes nes = Machine(0xA0, 0x10, 0xB9, 0xF8, 0x50);
            nes.StepInstruction();
            nes.StepInstruction();
            check("open bus: an indexed read that crosses a page keeps the operand's high byte", nes.Cpu.A == 0x50);
        }

        {
            // LDX #$16; LDA $40FF,X reads $4015 on the fixup cycle and $4115 on the
            // real one. $4015 is answered inside the 2A03 without driving the bus,
            // so the second read still sees the operand's high byte.
            Nes nes = Machine(0xA2, 0x16, 0xBD, 0xFF, 0x40);
            nes.StepInstruction();
            nes.StepInstruction();
            check("open bus: reading $4015 does not drive the data bus", nes.Cpu.A == 0x40);
        }

        {
            // The unused bit of $4015 is not driven either: it reads back the bus,
            // and the read leaves that value in place for the following cycle.
            Nes nes = Machine();
            nes.Bus.Write(0x0010, 0x20);
            byte status = nes.Bus.Read(0x4015);
            check("open bus: the unused $4015 bit comes from the bus", (status & 0x20) != 0);
            check("open bus: reading $4015 leaves the bus untouched", nes.Bus.Read(0x5000) == 0x20);
        }

        {
            // A write always drives the bus, including a write to $4015.
            Nes nes = Machine(0xA9, 0x3C, 0x8D, 0x15, 0x40, 0xAD, 0x00, 0x50); // LDA #$3C; STA $4015; LDA $5000
            nes.StepInstruction();
            nes.StepInstruction();
            nes.StepInstruction();
            check("open bus: a write leaves its value on the bus", nes.Cpu.A == 0x50);
        }
    }

    private static void ControllerTests(Action<string, bool> check)
    {
        {
            // The three bits above a controller port are not driven by the pad.
            Nes nes = Machine(0xAD, 0x16, 0x40); // LDA $4016
            nes.StepInstruction();
            check("controller: the undriven bits come from the bus", (nes.Cpu.A & 0xE0) == 0x40);
        }

        {
            // A read-modify-write reads the same address on two contiguous cycles.
            // The port's output enable never rises between them, so it is clocked
            // once: a pad shifts ones in after its eight buttons, and counting how
            // many further reads it takes to reach one says how often it clocked.
            int Reads(byte[] program)
            {
                Nes nes = Machine(program);
                Strobe(nes);                                 // no buttons held
                foreach (byte _ in program) { }
                nes.StepInstruction();                       // LDX #0
                nes.StepInstruction();                       // the clocking instruction
                for (int reads = 1; reads < 12; reads++)
                {
                    nes.Cpu.PC = 0x8005;                     // LDA $4016
                    nes.StepInstruction();
                    if ((nes.Cpu.A & 1) != 0) return reads;
                }

                return 0;
            }

            // SLO $4016,X with X = 0 reads the port twice; LDA $4016 reads it once.
            int doubleRead = Reads([0xA2, 0x00, 0x1F, 0x16, 0x40, 0xAD, 0x16, 0x40]);
            int singleRead = Reads([0xA2, 0x00, 0xAD, 0x16, 0x40, 0xAD, 0x16, 0x40]);
            check("controller: a double read clocks the shift register once",
                doubleRead == singleRead && doubleRead != 0);
        }

        {
            // A one-cycle strobe pulse only reaches the shift register on one of
            // the two processor alignments: DEC $4016 writes $41 and then $40 on
            // contiguous cycles, and only a pulse covering a get-to-put edge loads.
            bool[] latched = new bool[2];
            bool[] getCycle = new bool[2];
            for (int alignment = 0; alignment < 2; alignment++)
            {
                Nes nes = Machine(0xEA, 0xCE, 0x16, 0x40, 0xAD, 0x16, 0x40); // NOP; DEC $4016; LDA $4016
                // A must be held: DEC reads $41 back and writes $41 then $40, which
                // is the one-cycle pulse. B is what the following read reports if
                // the pulse never reached the shift register.
                nes.Port1.Buttons = NesButton.A | NesButton.B;
                Strobe(nes);                             // the register now holds A and B
                nes.Port1.Buttons = NesButton.None;
                nes.Cpu.PC = 0x8001;                     // past the NOP
                if (alignment == 1) nes.Cpu.DmaRead(0);  // one cycle shifts the phase
                getCycle[alignment] = nes.Apu.Dmc.OnGetCycle;
                nes.StepInstruction();                   // DEC $4016
                nes.StepInstruction();                   // LDA $4016
                latched[alignment] = (nes.Cpu.A & 1) == 0;
            }

            check("controller: a one-cycle strobe latches on exactly one alignment",
                latched[0] != latched[1]);
            check("controller: the alignment that latches is the one starting on a put cycle",
                latched[getCycle[0] ? 1 : 0]);
        }

        {
            // Writing a value without bit 0 never strobes: the register still holds
            // the buttons latched earlier, not the empty pad.
            Nes nes = Machine(0xAD, 0x16, 0x40); // LDA $4016
            nes.Port1.Buttons = NesButton.A;
            Strobe(nes);
            nes.Port1.Buttons = NesButton.None;
            nes.Bus.Write(0x4016, 2);
            for (int i = 0; i < 4; i++) nes.Cpu.DmaRead(0);
            nes.StepInstruction();
            check("controller: a write without bit 0 does not strobe", (nes.Cpu.A & 1) == 1);
        }
    }

    /// <summary>Strobes both ports the way a game does, with cycles in between.</summary>
    private static void Strobe(Nes nes)
    {
        nes.Bus.Write(0x4016, 1);
        for (int i = 0; i < 3; i++) nes.Cpu.DmaRead(0);
        nes.Bus.Write(0x4016, 0);
    }

    /// <summary>A plain cartridge running <paramref name="program"/> from $8000.</summary>
    private static Nes Machine(params byte[] program)
    {
        byte[] image = new byte[16 + 32768 + 8192];
        image[0] = 0x4E; image[1] = 0x45; image[2] = 0x53; image[3] = 0x1A;
        image[4] = 2; image[5] = 1;
        program.CopyTo(image, 16);
        image[16 + 0x7FFC] = 0x00;
        image[16 + 0x7FFD] = 0x80;
        return new Nes(Cartridge.FromBytes(image));
    }
}
