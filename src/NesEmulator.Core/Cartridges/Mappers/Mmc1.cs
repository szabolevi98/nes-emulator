namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 1, the MMC1, which is the board behind Zelda, Metroid and Mega Man 2.
///
/// It has only one data line. A game cannot write a bank number in one go: it
/// writes five times, one bit at a time, lowest bit first, and the chip clocks
/// them into a shift register. On the fifth write the assembled value lands in
/// one of four internal registers, chosen by which quarter of the address space
/// the last write went to. Writing any value with the top bit set resets the
/// sequence, which is also how a game puts the board into a known state.
/// </summary>
public sealed class Mmc1 : IMapper
{
    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;
    private readonly int _prgBankCount;

    private byte _shiftRegister = 0x10;
    private byte _control = 0x0C; // program mode 3: the last bank is fixed at $C000
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;

    public Mmc1(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgRam = new byte[Math.Max(cartridge.PrgRamSize, 8 * 1024)];
        _prgBankCount = cartridge.PrgRom.Length / Cartridge.PrgBankSize;
    }

    public Mirroring Mirroring => (_control & 0x03) switch
    {
        0 => Cartridges.Mirroring.SingleScreenLower,
        1 => Cartridges.Mirroring.SingleScreenUpper,
        2 => Cartridges.Mirroring.Vertical,
        _ => Cartridges.Mirroring.Horizontal,
    };

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000)
        {
            return _cartridge.PrgRom[PrgOffset(address)];
        }

        if (address >= 0x6000)
        {
            return _prgRam[(address - 0x6000) % _prgRam.Length];
        }

        return 0;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x6000)
        {
            return;
        }

        if (address < 0x8000)
        {
            _prgRam[(address - 0x6000) % _prgRam.Length] = value;
            return;
        }

        if ((value & 0x80) != 0)
        {
            // A reset leaves the program mode fixed at the top, whatever it was.
            _shiftRegister = 0x10;
            _control |= 0x0C;
            return;
        }

        bool complete = (_shiftRegister & 0x01) != 0;
        _shiftRegister = (byte)((_shiftRegister >> 1) | ((value & 0x01) << 4));

        if (!complete)
        {
            return;
        }

        byte assembled = (byte)(_shiftRegister & 0x1F);
        _shiftRegister = 0x10;

        switch ((address >> 13) & 0x03)
        {
            case 0: _control = assembled; break;
            case 1: _chrBank0 = assembled; break;
            case 2: _chrBank1 = assembled; break;
            default: _prgBank = assembled; break;
        }
    }

    private int PrgOffset(ushort address)
    {
        int mode = (_control >> 2) & 0x03;
        int bank = _prgBank & 0x0F;
        int offset = address - 0x8000;

        // Modes 0 and 1 ignore the low bit and swap all 32 KB at once.
        if (mode <= 1)
        {
            return ((bank & ~1) * Cartridge.PrgBankSize) + offset;
        }

        bool upperHalf = address >= 0xC000;
        int selected = mode == 2
            ? (upperHalf ? bank : 0)                      // first bank pinned low
            : (upperHalf ? _prgBankCount - 1 : bank);     // last bank pinned high

        selected %= _prgBankCount;
        return (selected * Cartridge.PrgBankSize) + (offset & 0x3FFF);
    }

    private int ChrOffset(ushort address)
    {
        const int half = 4 * 1024;
        int index = address & 0x1FFF;

        // Character mode 0 swaps all 8 KB together, mode 1 swaps two halves apart.
        if ((_control & 0x10) == 0)
        {
            return (((_chrBank0 & ~1) * half) + index) % _cartridge.Chr.Length;
        }

        int bank = index < half ? _chrBank0 : _chrBank1;
        return ((bank * half) + (index & (half - 1))) % _cartridge.Chr.Length;
    }

    public byte PpuRead(ushort address) => _cartridge.Chr[ChrOffset(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[ChrOffset(address)] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgRam);
        writer.Write(_shiftRegister);
        writer.Write(_control);
        writer.Write(_chrBank0);
        writer.Write(_chrBank1);
        writer.Write(_prgBank);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_prgRam);
        _shiftRegister = reader.ReadByte();
        _control = reader.ReadByte();
        _chrBank0 = reader.ReadByte();
        _chrBank1 = reader.ReadByte();
        _prgBank = reader.ReadByte();
    }
}
