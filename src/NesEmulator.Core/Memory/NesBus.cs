using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Input;
using NesEmulator.Core.Ppu;
using NesEmulator.Core.Cpu;

namespace NesEmulator.Core.Memory;

/// <summary>
/// The processor side of the console address space.
///
/// <code>
/// $0000-$07FF  2 KB of work RAM
/// $0800-$1FFF  three more copies of it, because only 11 address lines are decoded
/// $2000-$2007  picture unit registers
/// $2008-$3FFF  those eight registers repeated every eight bytes
/// $4000-$4013  sound unit registers
/// $4014        sprite memory transfer
/// $4016-$4017  controller ports, and more sound registers on write
/// $4018-$401F  disabled test registers
/// $4020-$FFFF  the cartridge, through its mapper
/// </code>
/// </summary>
public sealed class NesBus(
    IMapper mapper,
    Ppu2C02 ppu,
    Apu2A03 apu,
    Controller port1,
    Controller port2) : IBus
{
    private readonly IMapper _mapper = mapper;
    private readonly Ppu2C02 _ppu = ppu;
    private readonly Apu2A03 _apu = apu;
    private readonly Controller _port1 = port1;
    private readonly Controller _port2 = port2;
    private readonly byte[] _ram = new byte[0x0800];

    /// <summary>
    /// The value last driven onto the bus. Reading an address that nothing answers
    /// returns it, because the wires still hold the previous charge.
    /// </summary>
    private byte _openBus;

    /// <summary>
    /// Page latched by OAM DMA, or -1 when no transfer is pending. An RMW write
    /// can replace this page before the processor reaches the next read cycle.
    /// </summary>
    private int _dmaPage = -1;

    public int RunDma(Cpu6502 cpu)
    {
        if (_dmaPage < 0) return 0;
        int page = _dmaPage;
        _dmaPage = -1;
        long start = cpu.Cycles;
        bool align = (start & 1) != 0;
        cpu.DmaRead(cpu.PC); // halt cycle
        if (align) cpu.DmaRead(cpu.PC);
        for (int i = 0; i < 256; i++)
        {
            byte value = cpu.DmaRead((ushort)((page << 8) | i));
            cpu.DmaWrite(0x2004, value);
        }
        return (int)(cpu.Cycles - start);
    }

    public byte Read(ushort address)
    {
        byte value;

        if (address < 0x2000)
        {
            value = _ram[address & 0x07FF];
        }
        else if (address < 0x4000)
        {
            value = _ppu.ReadRegister(address);
        }
        else if (address == 0x4016)
        {
            value = _port1.Read();
        }
        else if (address == 0x4017)
        {
            value = _port2.Read();
        }
        else if (address == 0x4015)
        {
            value = _apu.ReadStatus();
        }
        else if (address < 0x4020)
        {
            value = _openBus;
        }
        else
        {
            value = _mapper.CpuRead(address);
        }

        _openBus = value;
        return value;
    }

    public void Write(ushort address, byte value)
    {
        _openBus = value;

        if (address < 0x2000)
        {
            _ram[address & 0x07FF] = value;
        }
        else if (address < 0x4000)
        {
            _ppu.WriteRegister(address, value);
        }
        else if (address == 0x4014)
        {
            _dmaPage = value;
        }
        else if (address == 0x4016)
        {
            _port1.Write(value);
            _port2.Write(value);
        }
        else if (address < 0x4020)
        {
            _apu.WriteRegister(address, value);
        }
        else
        {
            _mapper.CpuWrite(address, value);
        }
    }

    /// <summary>Reads without disturbing anything, for the trace and debugger views.</summary>
    public byte Peek(ushort address)
    {
        if (address < 0x2000)
        {
            return _ram[address & 0x07FF];
        }

        if (address < 0x4020)
        {
            return 0;
        }

        return _mapper.CpuRead(address);
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_ram);
        writer.Write(_openBus);
        writer.Write(_dmaPage);
    }

    internal void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_ram);
        _openBus = reader.ReadByte();
        _dmaPage = reader.ReadInt32();
    }
}
