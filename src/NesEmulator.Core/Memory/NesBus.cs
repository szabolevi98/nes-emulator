using NesEmulator.Core.Cartridges.Mappers;

namespace NesEmulator.Core.Memory;

/// <summary>
/// The processor side of the console address space.
///
/// <code>
/// $0000-$07FF  2 KB of work RAM
/// $0800-$1FFF  three more copies of it, because only 11 address lines are decoded
/// $2000-$2007  picture unit registers
/// $2008-$3FFF  those eight registers repeated every eight bytes
/// $4000-$4017  sound unit and controller ports
/// $4018-$401F  disabled test registers
/// $4020-$FFFF  the cartridge, through its mapper
/// </code>
/// </summary>
public sealed class NesBus(IMapper mapper) : IBus
{
    private readonly IMapper _mapper = mapper;
    private readonly byte[] _ram = new byte[0x0800];

    /// <summary>
    /// The value last driven onto the bus. Reading an address that nothing answers
    /// returns it, because the wires still hold the previous charge.
    /// </summary>
    private byte _openBus;

    public byte Read(ushort address)
    {
        byte value;

        if (address < 0x2000)
        {
            value = _ram[address & 0x07FF];
        }
        else if (address < 0x4000)
        {
            // TODO: picture unit registers, mirrored every eight bytes.
            value = _openBus;
        }
        else if (address < 0x4020)
        {
            // TODO: sound unit registers and the two controller ports.
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
            // TODO: picture unit registers.
        }
        else if (address < 0x4020)
        {
            // TODO: sound unit registers, controller strobe and sprite DMA.
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
}
