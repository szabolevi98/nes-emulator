namespace NesEmulator.Core.Cartridges.Mappers;

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
/// The line counter is clocked here once per visible line. On hardware it
/// actually watches one address line of the picture unit rise as the fetch
/// pattern moves between tile memory halves, which can fire at other moments too;
/// per line is the usual simplification and holds for ordinary rendering.
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

    public Mmc3(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _prgSlotCount = Math.Max(1, cartridge.PrgRom.Length / PrgSlotSize);
        _mirroring = cartridge.Mirroring;
    }

    public Mirroring Mirroring => _cartridge.Mirroring == Mirroring.FourScreen
        ? Mirroring.FourScreen
        : _mirroring;

    public bool IrqPending => _irqPending;

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
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
        {
            _irqPending = true;
        }
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
}
