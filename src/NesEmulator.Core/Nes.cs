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
/// one sound cycle to every processor cycle. Here the processor is allowed to
/// finish an instruction and the other two are then caught up, which is accurate
/// at instruction boundaries but not inside one. Games that poll a picture
/// register in a tight loop can therefore see a change a few cycles later than
/// hardware would show it; moving to a per-cycle interleave is what fixes that.
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

    /// <summary>Runs a single instruction and catches the other chips up to it.</summary>
    public int StepInstruction()
    {
        int cycles = Cpu.Step();
        cycles += Bus.TakeDmaCycles();

        for (int i = 0; i < cycles; i++)
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
