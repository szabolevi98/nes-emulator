using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Core;

/// <summary>
/// One console: a cartridge, the board logic inside it, the address space they
/// share, the processor and the picture unit.
///
/// The two chips run off the same clock at a fixed ratio of three picture cycles
/// to one processor cycle. Here the processor is allowed to finish an instruction
/// and the picture unit is then caught up, which is accurate at instruction
/// boundaries but not inside one. Games that poll a picture register in a tight
/// loop can therefore see a change a few cycles later than hardware would show
/// it; moving to a per-cycle interleave is what fixes that.
/// </summary>
public sealed class Nes
{
    public Nes(Cartridge cartridge)
    {
        Cartridge = cartridge;
        Mapper = IMapper.Create(cartridge);
        Ppu = new Ppu2C02(Mapper);
        Bus = new NesBus(Mapper, Ppu, Port1, Port2);
        Cpu = new Cpu6502(Bus);
        Cpu.Reset();
    }

    public Cartridge Cartridge { get; }

    public IMapper Mapper { get; }

    public NesBus Bus { get; }

    public Cpu6502 Cpu { get; }

    public Ppu2C02 Ppu { get; }

    public Controller Port1 { get; } = new();

    public Controller Port2 { get; } = new();

    public static Nes FromFile(string path) => new(Cartridges.Cartridge.FromFile(path));

    public void Reset()
    {
        Cpu.Reset();
        Ppu.Reset();
    }

    /// <summary>Runs a single instruction and catches the picture unit up to it.</summary>
    public int StepInstruction()
    {
        int cycles = Cpu.Step();
        cycles += Bus.TakeDmaCycles();

        for (int i = 0; i < cycles * 3; i++)
        {
            Ppu.Step();
            if (Ppu.ConsumeNmi())
            {
                Cpu.RaiseNmi();
            }
        }

        return cycles;
    }

    /// <summary>
    /// Runs until the beam reaches the bottom of the screen, leaving a finished
    /// picture in <see cref="Ppu2C02.FrameBuffer"/>.
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
