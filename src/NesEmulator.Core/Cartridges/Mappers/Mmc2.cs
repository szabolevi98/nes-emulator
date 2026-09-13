namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mappers 9 and 10, the MMC2 and MMC4. One chip was made for one game — the
/// MMC2 exists because of Punch-Out!! — and the MMC4 is the same idea on a
/// slightly larger board, behind Fire Emblem and Famicom Wars.
///
/// What makes it unusual is that the game does not choose the tile bank. The
/// chip watches the picture unit's address bus, and when a fetch lands on tile
/// $FD or $FE it switches the half of the pattern table that fetch came from,
/// ready for the next one. Punch-Out!! uses it to animate an opponent the size
/// of half the screen: the tiles are laid out so that the two banks alternate as
/// the beam crosses the sprite, and one boxer takes far more tile memory than
/// the console can address at once.
///
/// The switch takes effect after the fetch that caused it, so the fetch of tile
/// $FD is answered from the old bank and everything after it from the new one.
/// </summary>
public sealed class Mmc2 : IMapper
{
    private const int ChrWindow = 4 * 1024;

    private readonly Cartridge _cartridge;
    private readonly byte[] _prgRam;

    /// <summary>
    /// The MMC4 switches sixteen kilobytes of program at a time and the MMC2
    /// eight, and the MMC4 reacts to a whole row of addresses where the MMC2
    /// reacts to one. Everything else about the two is the same.
    /// </summary>
    private readonly bool _mmc4;

    private readonly int _prgWindow;
    private readonly int _prgBankCount;
    private readonly int _chrBankCount;

    private int _prgBank;

    /// <summary>Tile bank per window, chosen by that window's latch: [window, $FD or $FE].</summary>
    private readonly int[,] _chrBanks = new int[2, 2];

    /// <summary>Which of the two banks each window is currently showing.</summary>
    private readonly bool[] _latchIsFe = new bool[2];

    private bool _horizontal;

    public Mmc2(Cartridge cartridge, bool mmc4)
    {
        _cartridge = cartridge;
        _mmc4 = mmc4;
        _prgRam = new byte[mmc4 ? Math.Max(cartridge.PrgRamSize, 8 * 1024) : cartridge.PrgRamSize];
        _prgWindow = mmc4 ? 16 * 1024 : 8 * 1024;
        _prgBankCount = Math.Max(1, cartridge.PrgRom.Length / _prgWindow);
        _chrBankCount = Math.Max(1, cartridge.Chr.Length / ChrWindow);
        _horizontal = cartridge.Mirroring != Mirroring.Vertical;
    }

    public Mirroring Mirroring => _horizontal ? Cartridges.Mirroring.Horizontal : Cartridges.Mirroring.Vertical;

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

    private int PrgOffset(ushort address)
    {
        // The switchable window is at the bottom; what is above it is pinned to
        // the end of the ROM, so the vectors and the switching code stay put.
        int top = 0x8000 + _prgWindow;
        if (address < top)
        {
            return ((_prgBank % _prgBankCount) * _prgWindow) + (address - 0x8000);
        }

        int fixedBanks = (0x10000 - top) / _prgWindow;
        int bank = _prgBankCount - fixedBanks + ((address - top) / _prgWindow);
        return (bank * _prgWindow) + ((address - top) % _prgWindow);
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address < 0x6000)
        {
            return;
        }

        if (address < 0x8000)
        {
            if (_prgRam.Length > 0)
            {
                _prgRam[(address - 0x6000) % _prgRam.Length] = value;
            }

            return;
        }

        // Only the top four bits of the address choose a register; anything
        // below $A000 is ignored entirely.
        switch (address & 0xF000)
        {
            case 0xA000: _prgBank = value & 0x0F; break;
            case 0xB000: _chrBanks[0, 0] = value & 0x1F; break;
            case 0xC000: _chrBanks[0, 1] = value & 0x1F; break;
            case 0xD000: _chrBanks[1, 0] = value & 0x1F; break;
            case 0xE000: _chrBanks[1, 1] = value & 0x1F; break;
            case 0xF000: _horizontal = (value & 0x01) != 0; break;
        }
    }

    public byte PpuRead(ushort address)
    {
        address &= 0x1FFF;
        byte value = _cartridge.Chr[ChrOffset(address)];
        UpdateLatch(address);
        return value;
    }

    private int ChrOffset(ushort address)
    {
        int window = address >> 12;
        int bank = _chrBanks[window, _latchIsFe[window] ? 1 : 0] % _chrBankCount;
        return (bank * ChrWindow) + (address & 0x0FFF);
    }

    /// <summary>
    /// Watches for a fetch of tile $FD or $FE. Only the high plane of the tile
    /// counts, which is what the $8 in the address is: the MMC2 matches the top
    /// row of the tile alone, the MMC4 any row of it.
    /// </summary>
    private void UpdateLatch(ushort address)
    {
        int window = address >> 12;
        int within = address & 0x0FFF;

        // The MMC2 answers only $0FD8/$0FE8 in the lower window; its upper
        // window, and both of the MMC4's, take the whole eight-address row.
        bool wholeRow = _mmc4 || window == 1;

        if (within is >= 0x0FD8 and <= 0x0FDF && (wholeRow || within == 0x0FD8))
        {
            _latchIsFe[window] = false;
        }
        else if (within is >= 0x0FE8 and <= 0x0FEF && (wholeRow || within == 0x0FE8))
        {
            _latchIsFe[window] = true;
        }
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (_cartridge.ChrIsRam)
        {
            _cartridge.Chr[ChrOffset((ushort)(address & 0x1FFF))] = value;
        }
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_prgRam);
        writer.Write(_prgBank);
        for (int window = 0; window < 2; window++)
        {
            writer.Write(_chrBanks[window, 0]);
            writer.Write(_chrBanks[window, 1]);
            writer.Write(_latchIsFe[window]);
        }

        writer.Write(_horizontal);
    }

    public void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_prgRam);
        _prgBank = reader.ReadInt32();
        for (int window = 0; window < 2; window++)
        {
            _chrBanks[window, 0] = reader.ReadInt32();
            _chrBanks[window, 1] = reader.ReadInt32();
            _latchIsFe[window] = reader.ReadBoolean();
        }

        _horizontal = reader.ReadBoolean();
    }
}
