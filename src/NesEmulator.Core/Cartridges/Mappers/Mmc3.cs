namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>IRQ behavior differs even between chips bearing the same MMC3B marking.</summary>
public enum Mmc3IrqRevision : byte
{
    Standard,
    Alternate,
}

/// <summary>
/// Mapper 4, the MMC3, the most common board of the console's later years:
/// Super Mario Bros 3, Mega Man 3 onwards, Kirby, Double Dragon.
///
/// Two things make it worth the extra complexity. The program window is split
/// into four eight kilobyte slots instead of two sixteen kilobyte ones, and tile
/// memory into eight banks instead of one, so a game can keep common code and art
/// resident while swapping the rest. And it counts screen lines: a game loads a
/// number, and the board interrupts the processor that many lines down the
/// picture. That is what a split screen status bar is really made of, and it is
/// far steadier than watching the sprite zero hit flag.
///
/// The counter watches rising edges on PPU A12, filtered by the preceding low
/// period. CPU accesses to PPUADDR and PPUDATA can clock it as well as rendering.
/// </summary>
public sealed class Mmc3 : IMapper
{
    private const int PrgSlotSize = 8 * 1024;
    private const int ChrSlotSize = 1024;

    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam = new byte[8 * 1024];
    private readonly byte[] _banks = new byte[8];
    private readonly int _prgSlotCount;

    private byte _bankSelect;
    private Mirroring _mirroring;

    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _a12High;
    private long _a12LowSince;

    public Mmc3(Cartridge cartridge, Mmc3IrqRevision irqRevision = Mmc3IrqRevision.Standard)
    {
        if (!Enum.IsDefined(irqRevision)) throw new ArgumentOutOfRangeException(nameof(irqRevision));
        _cartridge = cartridge;
        IrqRevision = irqRevision;
        _prgSlotCount = Math.Max(1, cartridge.PrgRom.Length / PrgSlotSize);
        _mirroring = cartridge.Mirroring;
    }

    public Mirroring Mirroring => _cartridge.Mirroring == Mirroring.FourScreen
        ? Mirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

    public Mmc3IrqRevision IrqRevision { get; }

    public byte CpuRead(ushort address)
    {
        if (address >= 0x8000)
        {
            return _cartridge.PrgRom[PrgOffset(address)];
        }

        if (address >= 0x6000)
        {
            return _prgRam[address - 0x6000];
        }

        return 0;
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x6000 && address < 0x8000)
        {
            _prgRam[address - 0x6000] = value;
            return;
        }

        if (address < 0x8000)
        {
            return;
        }

        // Registers come in pairs: the even address of a range selects, the odd
        // one acts. Which pair depends on the quarter of the window written to.
        bool odd = (address & 1) != 0;

        switch (address & 0xE000)
        {
            case 0x8000:
                if (odd)
                {
                    _banks[_bankSelect & 0x07] = value;
                }
                else
                {
                    _bankSelect = value;
                }

                break;

            case 0xA000:
                if (!odd)
                {
                    _mirroring = (value & 1) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
                }

                // The odd address guards save RAM, which this does not model.
                break;

            case 0xC000:
                if (odd)
                {
                    // Does not reload now: it asks for a reload on the next line.
                    _irqReload = true;
                    _irqCounter = 0;
                }
                else
                {
                    _irqLatch = value;
                }

                break;

            case 0xE000:
                _irqEnabled = odd;
                if (!odd)
                {
                    _irqPending = false;
                }

                break;
        }
    }

    public void OnScanline()
    {
        // MMC3A/non-Sharp MMC3B do not assert on a natural zero-to-zero reload.
        // An explicit $C001 reload still permits an IRQ, even from zero.
        bool canAssert = IrqRevision == Mmc3IrqRevision.Standard || _irqCounter != 0 || _irqReload;
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (canAssert && _irqCounter == 0 && _irqEnabled)
        {
            _irqPending = true;
        }
    }

    public void OnPpuAddress(ushort address, long cycle)
    {
        bool high = (address & 0x1000) != 0;
        if (!high && _a12High) _a12LowSince = cycle;
        if (high && !_a12High && cycle - _a12LowSince >= 8) OnScanline();
        _a12High = high;
    }

    private int PrgOffset(ushort address)
    {
        int slot = (address - 0x8000) / PrgSlotSize;
        bool swapped = (_bankSelect & 0x40) != 0;

        int bank = slot switch
        {
            0 => swapped ? _prgSlotCount - 2 : _banks[6],
            1 => _banks[7],
            2 => swapped ? _banks[6] : _prgSlotCount - 2,
            _ => _prgSlotCount - 1,
        };

        bank = ((bank % _prgSlotCount) + _prgSlotCount) % _prgSlotCount;
        return (bank * PrgSlotSize) + (address % PrgSlotSize);
    }

    private int ChrOffset(ushort address)
    {
        int index = address & 0x1FFF;
        int slot = index / ChrSlotSize;

        // The two kilobyte banks sit in the first half of tile memory, the one
        // kilobyte banks in the second — unless the mode bit swaps the halves.
        if ((_bankSelect & 0x80) != 0)
        {
            slot ^= 4;
        }

        int bank = slot switch
        {
            0 => _banks[0] & 0xFE,
            1 => (_banks[0] & 0xFE) + 1,
            2 => _banks[1] & 0xFE,
            3 => (_banks[1] & 0xFE) + 1,
            4 => _banks[2],
            5 => _banks[3],
            6 => _banks[4],
            _ => _banks[5],
        };

        int slots = Math.Max(1, _cartridge.Chr.Length / ChrSlotSize);
        bank %= slots;
        return (bank * ChrSlotSize) + (index % ChrSlotSize);
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
        writer.Write(_banks);
        writer.Write(_bankSelect);
        writer.Write((int)_mirroring);
        writer.Write(_irqLatch);
        writer.Write(_irqCounter);
        writer.Write(_irqReload);
        writer.Write(_irqEnabled);
        writer.Write(_irqPending);
        writer.Write(_a12High);
        writer.Write(_a12LowSince);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_prgRam);
        reader.ReadExactly(_banks);
        _bankSelect = reader.ReadByte();
        _mirroring = (Mirroring)reader.ReadInt32();
        _irqLatch = reader.ReadByte();
        _irqCounter = reader.ReadByte();
        _irqReload = reader.ReadBoolean();
        _irqEnabled = reader.ReadBoolean();
        _irqPending = reader.ReadBoolean();
        _a12High = reader.ReadBoolean();
        _a12LowSince = reader.ReadInt64();
    }
}
