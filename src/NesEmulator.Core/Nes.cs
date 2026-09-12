using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Memory;

namespace NesEmulator.Core;

/// <summary>
/// One console: a cartridge, the board logic inside it, the address space they
/// share and the processor driving all of it. The picture and sound units join
/// this list once they exist.
/// </summary>
public sealed class Nes
{
    public Nes(Cartridge cartridge)
    {
        Cartridge = cartridge;
        Mapper = IMapper.Create(cartridge);
        Bus = new NesBus(Mapper);
        Cpu = new Cpu6502(Bus);
        Cpu.Reset();
    }

    public Cartridge Cartridge { get; }

    public IMapper Mapper { get; }

    public NesBus Bus { get; }

    public Cpu6502 Cpu { get; }

    public static Nes FromFile(string path) => new(Cartridges.Cartridge.FromFile(path));

    public void Reset() => Cpu.Reset();

    /// <summary>Runs a single instruction and returns the cycles it took.</summary>
    public int StepInstruction() => Cpu.Step();

    /// <summary>
    /// Runs instructions until the given number of processor cycles have passed.
    /// A real frame is 29,780 cycles; this stands in until the picture unit can
    /// end a frame properly.
    /// </summary>
    public void RunCycles(long count)
    {
        long target = Cpu.Cycles + count;
        while (Cpu.Cycles < target && !Cpu.Jammed)
        {
            Cpu.Step();
        }
    }
}
