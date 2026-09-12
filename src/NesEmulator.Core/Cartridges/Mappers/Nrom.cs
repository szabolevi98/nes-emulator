namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 0, the plain board with no switching logic at all. The program ROM is
/// wired straight to $8000, and a 16 KB cartridge simply appears twice so that
/// the interrupt vectors at the very top of the address space still land on it.
/// Super Mario Bros, Donkey Kong and Duck Hunt all shipped on this board.
/// </summary>
public sealed class Nrom : IMapper
{
    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;
    private readonly int _prgMask;

    public Nrom(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgRam = new byte[cartridge.PrgRamSize];

        // 16 KB mirrors into both halves, 32 KB fills the window once.
        _prgMask = cartridge.PrgRom.Length == Cartridge.PrgBankSize ? 0x3FFF : 0x7FFF;
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
        // Writes above $8000 hit ROM and are discarded, exactly as on hardware.
        if (address >= 0x6000 && address < 0x8000 && _prgRam.Length > 0)
        {
            _prgRam[(address - 0x6000) % _prgRam.Length] = value;
        }
    }

    public byte PpuRead(ushort address) => _cartridge.Chr[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[address & 0x1FFF] = value;
        }
    }

    public void SaveState(BinaryWriter writer) => writer.Write(_prgRam);

    public void LoadState(BinaryReader reader) => reader.ReadExactly(_prgRam);
}
