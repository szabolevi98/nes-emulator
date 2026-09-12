namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 2. The simplest form of bank switching there is: the lower half of the
/// window is whichever 16 KB bank the game last asked for, and the upper half is
/// permanently the final bank, so the interrupt vectors and the switching code
/// itself never move out from under the processor. Mega Man, Castlevania and
/// Contra shipped on boards like this.
/// </summary>
public sealed class UxRom : IMapper
{
    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;
    private readonly int _bankCount;
    private int _bank;

    public UxRom(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgRam = new byte[cartridge.PrgRamSize];
        _bankCount = cartridge.PrgRom.Length / Cartridge.PrgBankSize;
    }

    public Mirroring Mirroring => _cartridge.Mirroring;

    public byte CpuRead(ushort address)
    {
        if (address >= 0xC000)
        {
            int offset = ((_bankCount - 1) * Cartridge.PrgBankSize) + (address - 0xC000);
            return _cartridge.PrgRom[offset];
        }

        if (address >= 0x8000)
        {
            int offset = (_bank * Cartridge.PrgBankSize) + (address - 0x8000);
            return _cartridge.PrgRom[offset];
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

    public byte PpuRead(ushort address) => _cartridge.Chr[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[address & 0x1FFF] = value;
        }
    }
}
