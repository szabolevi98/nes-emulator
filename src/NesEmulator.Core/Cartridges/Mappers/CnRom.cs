namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 3. The mirror image of mapper 2: the program never moves, but the
/// entire 8 KB of tile data is swapped in one go. A game that needs more art than
/// code uses this, which is why it is full of puzzle games and licensed titles.
/// </summary>
public sealed class CnRom : IMapper
{
    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;
    private readonly int _prgMask;
    private readonly int _bankCount;
    private int _bank;

    public CnRom(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgRam = new byte[cartridge.PrgRamSize];
        _prgMask = cartridge.PrgRom.Length == Cartridge.PrgBankSize ? 0x3FFF : 0x7FFF;
        _bankCount = Math.Max(1, cartridge.Chr.Length / Cartridge.ChrBankSize);
    }

    public Mirroring Mirroring => _cartridge.Mirroring;

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000)
        {
            return _cartridge.PrgRom[address & _prgMask];
        }

        if (address >= 0x6000 && _prgRam.Length > 0)
        {
            return _prgRam[(address - 0x6000) % _prgRam.Length];
        }

        return 0;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _bank = value % _bankCount;
            return;
        }

        if (address >= 0x6000 && _prgRam.Length > 0)
        {
            _prgRam[(address - 0x6000) % _prgRam.Length] = value;
        }
    }

    public byte PpuRead(ushort address) =>
        _cartridge.Chr[(_bank * Cartridge.ChrBankSize) + (address & 0x1FFF)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[(_bank * Cartridge.ChrBankSize) + (address & 0x1FFF)] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgRam);
        writer.Write(_bank);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_prgRam);
        _bank = reader.ReadInt32();
    }
}
