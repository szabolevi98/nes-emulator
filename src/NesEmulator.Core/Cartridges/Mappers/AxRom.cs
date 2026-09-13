namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 7, the AxROM boards: Battletoads, Marble Madness, Solar Jetman and the
/// rest of Rare's catalogue, plus Wizards &amp; Warriors.
///
/// It is the bluntest board in wide use. There are no fixed halves and no split
/// windows: one write swaps the entire 32 KB the processor can see, vectors and
/// all, so the game has to arrange for the code that does the swapping to exist
/// at the same address in both banks. Tiles are always RAM, drawn by the program
/// rather than burned into a second chip.
///
/// The same write also picks which of the two name tables the whole screen uses.
/// These boards wire only one, so there is no horizontal or vertical arrangement
/// to speak of: the picture is single screen, and bit four says which one. The
/// flicker Battletoads is remembered for comes partly from this — a game that
/// wants two pictures has to draw them one after the other.
/// </summary>
public sealed class AxRom : IMapper
{
    /// <summary>The window is the whole 32 KB, so a bank is two 16 KB units.</summary>
    private const int BankSize = 2 * Cartridge.PrgBankSize;

    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;
    private readonly int _bankCount;

    /// <summary>
    /// Whether a write is ANDed with the byte already at that address. The latch
    /// on these boards usually drives the bus on its own, but a few variants let
    /// the ROM answer at the same time, and then both values reach the chip. NES
    /// 2.0 submapper 2 says a cartridge is one of those; anything else is taken
    /// as the ordinary case, since a game written for a board without conflicts
    /// will happily write a bank number the ROM would mangle.
    /// </summary>
    private readonly bool _busConflicts;

    private int _bank;
    private bool _upperNameTable;

    public AxRom(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgRam = new byte[cartridge.PrgRamSize];
        _bankCount = Math.Max(1, cartridge.PrgRom.Length / BankSize);
        _busConflicts = cartridge.Submapper == 2;
    }

    public Mirroring Mirroring =>
        _upperNameTable ? Cartridges.Mirroring.SingleScreenUpper : Cartridges.Mirroring.SingleScreenLower;

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000)
        {
            return _cartridge.PrgRom[PrgOffset(address)];
        }

        if (address >= 0x6000 && _prgRam.Length > 0)
        {
            return _prgRam[(address - 0x6000) % _prgRam.Length];
        }

        return 0;
    }

    public bool DrivesCpuRead(ushort address) =>
        address >= 0x8000 || (address >= 0x6000 && _prgRam.Length > 0);

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            if (_busConflicts)
            {
                value &= _cartridge.PrgRom[PrgOffset(address)];
            }

            _bank = value & 0x0F;
            _upperNameTable = (value & 0x10) != 0;
            return;
        }

        if (address >= 0x6000 && _prgRam.Length > 0)
        {
            _prgRam[(address - 0x6000) % _prgRam.Length] = value;
        }
    }

    private int PrgOffset(ushort address) =>
        ((_bank % _bankCount) * BankSize) + (address - 0x8000);

    public byte PpuRead(ushort address) => _cartridge.Chr[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[address & 0x1FFF] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgRam);
        writer.Write(_bank);
        writer.Write(_upperNameTable);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_prgRam);
        _bank = reader.ReadInt32();
        _upperNameTable = reader.ReadBoolean();
    }
}
