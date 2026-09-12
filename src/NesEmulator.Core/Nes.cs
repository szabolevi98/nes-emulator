using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Core;

/// <summary>
/// One console: a cartridge, the board logic inside it, the address space they
/// share, the processor, the picture unit and the sound unit.
///
/// The chips run off the same clock at fixed ratios — three picture cycles and
/// one sound cycle to every processor cycle. The processor spends its cycles one
/// at a time and hands each to <see cref="Tick"/> as it goes, so the three run
/// alongside each other through an instruction rather than one after the other.
/// A game that reads a picture register partway through an instruction therefore
/// sees what the hardware would have shown it.
/// </summary>
public sealed class Nes
{
    public Nes(Cartridge cartridge, int sampleRate = 44100)
    {
        Cartridge = cartridge;
        Mapper = IMapper.Create(cartridge);
        Ppu = new Ppu2C02(Mapper);
        Apu = new Apu2A03(sampleRate);
        Bus = new NesBus(Mapper, Ppu, Apu, Port1, Port2);
        Cpu = new Cpu6502(Bus);

        // The sample channel reads cartridge memory on its own while a game runs.
        Apu.Dmc.ReadMemory = Bus.Read;

        // The processor drives the clock for everything else.
        Cpu.OnCycle = Tick;

        Cpu.Reset();
    }

    public Cartridge Cartridge { get; }

    public IMapper Mapper { get; }

    public NesBus Bus { get; }

    public Cpu6502 Cpu { get; }

    public Ppu2C02 Ppu { get; }

    public Apu2A03 Apu { get; }

    public Controller Port1 { get; } = new();

    public Controller Port2 { get; } = new();

    public static Nes FromFile(string path, int sampleRate = 44100) =>
        new(Cartridges.Cartridge.FromFile(path), sampleRate);

    public void Reset()
    {
        Cpu.Reset();
        Ppu.Reset();
        Apu.Reset();
    }

    // ----------------------------------------------------------- save states

    private const uint StateMagic = 0x53454E01; // "NES" and a format version

    /// <summary>
    /// Writes everything that makes this console what it is at this instant. The
    /// cartridge ROM is not part of it — only the memory that can change, which is
    /// what keeps a state small enough to take one many times a second for rewind.
    /// </summary>
    public void SaveState(Stream stream)
    {
        BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(StateMagic);
        writer.Write(Cartridge.MapperNumber);

        Cpu.SaveState(writer);
        Ppu.SaveState(writer);
        Apu.SaveState(writer);
        Bus.SaveState(writer);
        Mapper.SaveState(writer);
        Port1.SaveState(writer);
        Port2.SaveState(writer);

        // Tile memory is only worth keeping when the cartridge can write to it.
        if (Cartridge.ChrIsRam)
        {
            writer.Write(Cartridge.Chr);
        }
    }

    public void LoadState(Stream stream)
    {
        BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != StateMagic)
        {
            throw new InvalidDataException("Not a save state for this emulator.");
        }

        if (reader.ReadInt32() != Cartridge.MapperNumber)
        {
            throw new InvalidDataException("This save state belongs to a different cartridge.");
        }

        Cpu.LoadState(reader);
        Ppu.LoadState(reader);
        Apu.LoadState(reader);
        Bus.LoadState(reader);
        Mapper.LoadState(reader);
        Port1.LoadState(reader);
        Port2.LoadState(reader);

        if (Cartridge.ChrIsRam)
        {
            reader.ReadExactly(Cartridge.Chr);
        }
    }

    /// <summary>
    /// One processor cycle for the rest of the console: the sound unit once, the
    /// picture unit three times. The processor calls this as it spends each cycle,
    /// so the three stay in step through an instruction rather than after it.
    /// </summary>
    private void Tick()
    {
        Apu.Step();

        for (int dot = 0; dot < 3; dot++)
        {
            Ppu.Step();
            if (Ppu.ConsumeNmi())
            {
                Cpu.RaiseNmi();
            }
        }
    }

    /// <summary>Runs a single instruction, with the other chips running alongside it.</summary>
    public int StepInstruction()
    {
        int cycles = Cpu.Step();

        // The sprite transfer holds the processor still but not the clock, so its
        // cycles are spent here rather than inside the instruction that started it.
        int transfer = Bus.TakeDmaCycles();
        for (int i = 0; i < transfer; i++)
        {
            Tick();
        }

        Cpu.Cycles += transfer;
        cycles += transfer;

        // Both the sound unit and some cartridge boards hold the maskable
        // interrupt line down until the game acknowledges them, so it is a level
        // rather than an event.
        Cpu.SetIrqLine(Apu.IrqPending || Mapper.IrqPending);

        return cycles;
    }

    /// <summary>
    /// Runs until the beam reaches the bottom of the screen, leaving a finished
    /// picture in <see cref="Ppu2C02.FrameBuffer"/> and a frame of audio in the
    /// sound unit's buffer.
    /// </summary>
    public void RunFrame()
    {
        Ppu.FrameComplete = false;

        while (!Ppu.FrameComplete)
        {
            if (Cpu.Jammed)
            {
                // Nothing will advance any more, but the beam still has to finish
                // the frame or the display would freeze mid-picture.
                Ppu.Step();
                continue;
            }

            StepInstruction();
        }
    }
}
