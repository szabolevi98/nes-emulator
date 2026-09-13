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

    /// <summary>
    /// The controller port read on the previous processor cycle, or zero. A port's
    /// /OE only rises between reads of different addresses, so two contiguous reads
    /// of the same port clock its shift register once and read the same bit twice.
    /// This covers a read-modify-write's double read and DMA-stalled reads alike.
    /// </summary>
    private ushort _portAddress;
    private byte _portValue;

    public int RunDma(Cpu6502 cpu) => RunDma(cpu, cpu.PC);

    internal void CancelDma() => _dmaPage = -1;

    internal int RunDma(Cpu6502 cpu, ushort haltedAddress)
    {
        if (_dmaPage < 0 && !_apu.Dmc.DmaPending) return 0;
        int page = _dmaPage;
        _dmaPage = -1;
        long start = cpu.Cycles;
        bool halt = true;
        bool oam = page >= 0;
        bool oamByteReady = false;
        byte oamByte = 0;
        int offset = 0;
        int dmcStage = 0; // 0: inactive, 1: dummy, 2: waiting for GET
        try
        {
            while (oam || dmcStage != 0 || _apu.Dmc.DmaPending)
            {
                bool get = _apu.Dmc.NextCycleIsGet;
                bool dmcHalt = dmcStage == 0 && _apu.Dmc.DmaPending;
                int previousStage = dmcStage;
                if (halt)
                {
                    cpu.DmaRead(haltedAddress);
                }
                else if (dmcStage == 2 && get)
                {
                    // The DMC owns this GET. OAM must realign before its next read.
                    cpu.DmaRead(_apu.Dmc.DmaAddress, _apu.Dmc.CompleteDma);
                    dmcStage = 0;
                }
                else if (oam && get)
                {
                    oamByte = cpu.DmaRead((ushort)((page << 8) | offset));
                    oamByteReady = true;
                }
                else if (oam && oamByteReady)
                {
                    cpu.DmaWrite(0x2004, oamByte);
                    oamByteReady = false;
                    oam = ++offset < 256;
                }
                else
                {
                    cpu.DmaRead(haltedAddress);
                }

                // DMC halt/dummy/alignment cycles can overlap OAM reads and writes.
                if (dmcHalt) dmcStage = _apu.Dmc.DmaPending ? 1 : 0;
                else if (previousStage == 1) dmcStage = 2;
                halt = false;
            }
        }
        finally
        {
            // An aborted halt may return straight to the same controller read,
            // without a DMA fetch to deassert /OE between the two cycles. That is
            // the same contiguous-read rule the bus already tracks.
        }
        return (int)(cpu.Cycles - start);
    }

    public byte Read(ushort address)
    {
        bool port = address is 0x4016 or 0x4017;
        if (port && address == _portAddress)
        {
            // Still the same read as far as the port is concerned.
            return _openBus = _portValue;
        }

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
            // Only the low data lines are driven by the port; the rest float.
            value = (byte)((_openBus & 0xE0) | _port1.Read());
        }
        else if (address == 0x4017)
        {
            value = (byte)((_openBus & 0xE0) | _port2.Read());
        }
        else if (address == 0x4015)
        {
            // The 2A03 answers this one from inside the chip, so the external data
            // bus is never driven: its unused bit reads back whatever was already
            // on the bus, and the bus keeps that value for the following cycle.
            return (byte)((_apu.ReadStatus() & 0xDF) | (_openBus & 0x20));
        }
        else if (address < 0x4020)
        {
            value = _openBus;
        }
        else
        {
            // Nothing decodes this address on the cartridge: the bus floats and
            // keeps whatever the previous cycle left on it.
            value = _mapper.DrivesCpuRead(address) ? _mapper.CpuRead(address) : _openBus;
        }

        _openBus = value;
        // Only a port keeps presenting its value; anything else releases /OE.
        _portAddress = port ? address : (ushort)0;
        _portValue = port ? value : (byte)0;
        return value;
    }

    public void Write(ushort address, byte value)
    {
        // A write cycle deasserts every port's /OE, so the next read clocks again.
        _portAddress = 0;
        _portValue = 0;
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
        writer.Write(_portAddress);
        writer.Write(_portValue);
    }

    internal void LoadState(BinaryReader reader, bool legacyPort)
    {
        reader.ReadExactly(_ram);
        _openBus = reader.ReadByte();
        _dmaPage = reader.ReadInt32();
        // Older states were written between instructions, where no port read can
        // be in progress, so starting with a deasserted /OE reproduces them.
        _portAddress = legacyPort ? (ushort)0 : reader.ReadUInt16();
        _portValue = legacyPort ? (byte)0 : reader.ReadByte();
    }
}
