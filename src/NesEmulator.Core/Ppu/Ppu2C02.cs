using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;

namespace NesEmulator.Core.Ppu;

/// <summary>
/// The 2C02 picture unit.
///
/// It does not draw a frame and hand it over; it produces one pixel per cycle
/// while a television beam sweeps the screen, 341 cycles across and 262 lines
/// down, three of its cycles for every processor cycle. Games use that: they
/// change the scroll position or swap tiles partway down a frame, which is how a
/// status bar stays still while the level scrolls underneath it. So the loop
/// below follows the beam rather than rendering tile by tile.
///
/// The scroll position lives in two 15-bit registers usually called v and t,
/// packed as yyy NN YYYYY XXXXX: fine y, nametable, coarse y, coarse x. The odd
/// looking bit surgery in this file is that layout being incremented and copied
/// the way the hardware does it.
/// </summary>
public sealed class Ppu2C02
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;

    private const int CyclesPerScanline = 341;
    private const int PreRenderScanline = -1;
    private const int LastScanline = 260;

    private readonly IMapper _mapper;

    /// <summary>Name table memory. Four screens worth, though most boards wire up two.</summary>
    private readonly byte[] _vram = new byte[0x1000];

    private readonly byte[] _paletteRam = new byte[32];

    /// <summary>Object attribute memory: sixty-four sprites of four bytes each.</summary>
    private readonly byte[] _oam = new byte[256];

    private byte _ctrl;
    private byte _mask;
    private byte _status;
    private byte _oamAddress;

    // The scroll registers. _v is where the beam is reading from, _t is what the
    // game has staged, _fineX selects a pixel inside a tile and _writeLatch
    // tracks which half of a two-write register is next.
    private ushort _v;
    private ushort _t;
    private byte _fineX;
    private bool _writeLatch;

    /// <summary>Reads through $2007 are one fetch behind, except from palette memory.</summary>
    private byte _readBuffer;

    // Background fetch pipeline.
    private byte _nameTableByte;
    private byte _attributeByte;
    private byte _patternLow;
    private byte _patternHigh;
    private ushort _patternShiftLow;
    private ushort _patternShiftHigh;
    private ushort _attributeShiftLow;
    private ushort _attributeShiftHigh;

    // Sprites selected for the line being drawn.
    private readonly byte[] _lineSprites = new byte[8 * 4];
    private readonly byte[] _spriteShiftLow = new byte[8];
    private readonly byte[] _spriteShiftHigh = new byte[8];
    private int _lineSpriteCount;
    private bool _spriteZeroOnLine;
    private bool _spriteZeroRendering;

    private bool _oddFrame;
    private bool _nmiPending;

    public Ppu2C02(IMapper mapper)
    {
        _mapper = mapper;
        Scanline = PreRenderScanline;
    }

    /// <summary>One palette index per pixel, ready for a display to colour in.</summary>
    public byte[] FrameBuffer { get; } = new byte[ScreenWidth * ScreenHeight];

    public int Scanline { get; private set; }

    public int Cycle { get; private set; }

    /// <summary>Raised when the beam reaches the bottom, so a host can present the frame.</summary>
    public bool FrameComplete { get; set; }

    public long FrameCount { get; private set; }

    public void Reset()
    {
        _ctrl = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _v = 0;
        _t = 0;
        _fineX = 0;
        _writeLatch = false;
        _readBuffer = 0;
        Scanline = PreRenderScanline;
        Cycle = 0;
        _oddFrame = false;
        _nmiPending = false;
        _lineSpriteCount = 0;
        Array.Clear(FrameBuffer);
    }

    /// <summary>True once, when the unit has just asked for a non-maskable interrupt.</summary>
    public bool ConsumeNmi()
    {
        if (!_nmiPending)
        {
            return false;
        }

        _nmiPending = false;
        return true;
    }

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    private bool ShowBackground => (_mask & 0x08) != 0;

    private bool ShowSprites => (_mask & 0x10) != 0;

    // ------------------------------------------------------ processor facing

    /// <summary>The eight registers at $2000, as the processor sees them.</summary>
    public byte ReadRegister(ushort address)
    {
        switch (address & 7)
        {
            case 2:
            {
                // The unused low bits return whatever was last on the data bus.
                byte value = (byte)((_status & 0xE0) | (_readBuffer & 0x1F));
                _status &= 0x7F;      // reading clears the vertical blank flag
                _writeLatch = false;  // and resets the two-write sequence
                return value;
            }

            case 4:
                return _oam[_oamAddress];

            case 7:
            {
                byte value = _readBuffer;
                _readBuffer = PpuRead(_v);

                // Palette memory answers immediately; everything else is a fetch behind.
                if ((_v & 0x3FFF) >= 0x3F00)
                {
                    value = _readBuffer;
                }

                _v = (ushort)(_v + AddressIncrement());
                return value;
            }

            default:
                // The write-only registers return the last value on the bus.
                return _readBuffer;
        }
    }

    public void WriteRegister(ushort address, byte value)
    {
        _readBuffer = value;

        switch (address & 7)
        {
            case 0:
            {
                bool wasNmiEnabled = (_ctrl & 0x80) != 0;
                _ctrl = value;
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10));

                // Enabling the interrupt while the flag is already up fires it at once.
                if (!wasNmiEnabled && (_ctrl & 0x80) != 0 && (_status & 0x80) != 0)
                {
                    _nmiPending = true;
                }

                break;
            }

            case 1:
                _mask = value;
                break;

            case 3:
                _oamAddress = value;
                break;

            case 4:
                _oam[_oamAddress] = value;
                _oamAddress++;
                break;

            case 5:
                if (!_writeLatch)
                {
                    _fineX = (byte)(value & 0x07);
                    _t = (ushort)((_t & 0xFFE0) | (value >> 3));
                    _writeLatch = true;
                }
                else
                {
                    _t = (ushort)((_t & 0x8FFF) | ((value & 0x07) << 12));
                    _t = (ushort)((_t & 0xFC1F) | ((value & 0xF8) << 2));
                    _writeLatch = false;
                }

                break;

            case 6:
                if (!_writeLatch)
                {
                    _t = (ushort)((_t & 0x00FF) | ((value & 0x3F) << 8));
                    _writeLatch = true;
                }
                else
                {
                    _t = (ushort)((_t & 0xFF00) | value);
                    _v = _t;
                    _writeLatch = false;
                }

                break;

            case 7:
                PpuWrite(_v, value);
                _v = (ushort)(_v + AddressIncrement());
                break;
        }
    }

    /// <summary>Sprite memory, filled in one go by a direct memory transfer from $4014.</summary>
    public void WriteOam(byte offset, byte value) => _oam[offset] = value;

    private int AddressIncrement() => (_ctrl & 0x04) != 0 ? 32 : 1;

    // ------------------------------------------------------- the beam itself

    /// <summary>Advances one picture unit cycle, which is a third of a processor cycle.</summary>
    public void Step()
    {
        if (Scanline is >= PreRenderScanline and < ScreenHeight)
        {
            // On odd frames with rendering on, the pre-render line is one cycle short.
            if (Scanline == PreRenderScanline && Cycle == 0 && _oddFrame && RenderingEnabled)
            {
                Cycle = 1;
            }

            if (Scanline == PreRenderScanline && Cycle == 1)
            {
                _status &= 0x1F; // clears vertical blank, sprite zero hit and overflow
            }

            if (RenderingEnabled)
            {
                StepBackgroundFetch();
                StepSprites();

                // Boards that count lines are clocked here. On hardware they watch
                // an address line rise as the fetch pattern moves between the two
                // halves of tile memory, which lands around this point in the line.
                if (Cycle == 260)
                {
                    _mapper.OnScanline();
                }
            }
        }

        if (Scanline == ScreenHeight + 1 && Cycle == 1)
        {
            _status |= 0x80;
            if ((_ctrl & 0x80) != 0)
            {
                _nmiPending = true;
            }
        }

        if (Scanline >= 0 && Scanline < ScreenHeight && Cycle >= 1 && Cycle <= ScreenWidth)
        {
            FrameBuffer[(Scanline * ScreenWidth) + (Cycle - 1)] = ComposePixel();
        }

        Cycle++;
        if (Cycle >= CyclesPerScanline)
        {
            Cycle = 0;
            Scanline++;
            if (Scanline > LastScanline)
            {
                Scanline = PreRenderScanline;
                _oddFrame = !_oddFrame;
                FrameComplete = true;
                FrameCount++;
            }
        }
    }

    private void StepBackgroundFetch()
    {
        if ((Cycle >= 2 && Cycle < 258) || (Cycle >= 321 && Cycle < 338))
        {
            ShiftBackground();

            switch ((Cycle - 1) % 8)
            {
                case 0:
                    LoadBackgroundShifters();
                    _nameTableByte = PpuRead((ushort)(0x2000 | (_v & 0x0FFF)));
                    break;

                case 2:
                {
                    // One attribute byte covers four tiles by four tiles; which two
                    // bits apply depends on which quadrant the tile sits in.
                    ushort address = (ushort)(0x23C0
                        | (_v & 0x0C00)
                        | ((_v >> 4) & 0x38)
                        | ((_v >> 2) & 0x07));
                    _attributeByte = PpuRead(address);

                    if ((_v & 0x0040) != 0)
                    {
                        _attributeByte >>= 4;
                    }

                    if ((_v & 0x0002) != 0)
                    {
                        _attributeByte >>= 2;
                    }

                    _attributeByte &= 0x03;
                    break;
                }

                case 4:
                    _patternLow = PpuRead(BackgroundPatternAddress(0));
                    break;

                case 6:
                    _patternHigh = PpuRead(BackgroundPatternAddress(8));
                    break;

                case 7:
                    IncrementCoarseX();
                    break;
            }
        }

        if (Cycle == 256)
        {
            IncrementY();
        }

        if (Cycle == 257)
        {
            LoadBackgroundShifters();
            CopyHorizontalBits();
        }

        // Two redundant name table reads that some mappers count on to time themselves.
        if (Cycle == 338 || Cycle == 340)
        {
            _nameTableByte = PpuRead((ushort)(0x2000 | (_v & 0x0FFF)));
        }

        if (Scanline == PreRenderScanline && Cycle >= 280 && Cycle < 305)
        {
            CopyVerticalBits();
        }
    }

    private ushort BackgroundPatternAddress(int plane) => (ushort)(
        (((_ctrl & 0x10) != 0 ? 1 : 0) << 12)
        + (_nameTableByte << 4)
        + ((_v >> 12) & 0x07)
        + plane);

    private void ShiftBackground()
    {
        if (!ShowBackground)
        {
            return;
        }

        _patternShiftLow <<= 1;
        _patternShiftHigh <<= 1;
        _attributeShiftLow <<= 1;
        _attributeShiftHigh <<= 1;
    }

    private void LoadBackgroundShifters()
    {
        _patternShiftLow = (ushort)((_patternShiftLow & 0xFF00) | _patternLow);
        _patternShiftHigh = (ushort)((_patternShiftHigh & 0xFF00) | _patternHigh);

        // The two attribute bits apply to all eight pixels, so they are smeared out.
        _attributeShiftLow = (ushort)((_attributeShiftLow & 0xFF00)
            | ((_attributeByte & 0x01) != 0 ? 0xFF : 0x00));
        _attributeShiftHigh = (ushort)((_attributeShiftHigh & 0xFF00)
            | ((_attributeByte & 0x02) != 0 ? 0xFF : 0x00));
    }

    /// <summary>Steps one tile right, rolling over into the neighbouring name table.</summary>
    private void IncrementCoarseX()
    {
        if ((_v & 0x001F) == 31)
        {
            _v = (ushort)(_v & ~0x001F);
            _v ^= 0x0400;
        }
        else
        {
            _v++;
        }
    }

    /// <summary>
    /// Steps one pixel down. Coarse y counts to 29, not 31, because the last two
    /// rows of the name table hold attribute bytes rather than tiles.
    /// </summary>
    private void IncrementY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v += 0x1000;
            return;
        }

        _v = (ushort)(_v & ~0x7000);
        int coarseY = (_v & 0x03E0) >> 5;

        if (coarseY == 29)
        {
            coarseY = 0;
            _v ^= 0x0800;
        }
        else if (coarseY == 31)
        {
            coarseY = 0;
        }
        else
        {
            coarseY++;
        }

        _v = (ushort)((_v & ~0x03E0) | (coarseY << 5));
    }

    private void CopyHorizontalBits() => _v = (ushort)((_v & ~0x041F) | (_t & 0x041F));

    private void CopyVerticalBits() => _v = (ushort)((_v & ~0x7BE0) | (_t & 0x7BE0));

    // ------------------------------------------------------------- sprites

    private void StepSprites()
    {
        // Everything is done in two bursts rather than spread across the line: the
        // search at 257 and the pattern fetches at the end. The visible result is
        // the same unless a game rewrites sprite memory mid-line.
        if (Cycle == 257)
        {
            EvaluateSprites();
        }

        if (Cycle == 340)
        {
            LoadSpriteShifters();
        }

        if (Cycle >= 2 && Cycle < 258 && ShowSprites)
        {
            for (int i = 0; i < _lineSpriteCount; i++)
            {
                if (_lineSprites[(i * 4) + 3] > 0)
                {
                    _lineSprites[(i * 4) + 3]--;
                }
                else
                {
                    _spriteShiftLow[i] <<= 1;
                    _spriteShiftHigh[i] <<= 1;
                }
            }
        }
    }

    /// <summary>Finds the first eight sprites that touch the next line.</summary>
    private void EvaluateSprites()
    {
        Array.Fill(_lineSprites, (byte)0xFF);
        _lineSpriteCount = 0;
        _spriteZeroOnLine = false;

        int height = (_ctrl & 0x20) != 0 ? 16 : 8;

        for (int i = 0; i < 64; i++)
        {
            int top = _oam[i * 4];
            int row = Scanline - top;

            if (row < 0 || row >= height)
            {
                continue;
            }

            if (_lineSpriteCount == 8)
            {
                // The hardware sets this flag with a buggy search; games mostly use
                // it as a hint that the line is crowded.
                _status |= 0x20;
                break;
            }

            if (i == 0)
            {
                _spriteZeroOnLine = true;
            }

            Array.Copy(_oam, i * 4, _lineSprites, _lineSpriteCount * 4, 4);
            _lineSpriteCount++;
        }
    }

    private void LoadSpriteShifters()
    {
        int height = (_ctrl & 0x20) != 0 ? 16 : 8;

        for (int i = 0; i < _lineSpriteCount; i++)
        {
            byte top = _lineSprites[i * 4];
            byte tile = _lineSprites[(i * 4) + 1];
            byte attributes = _lineSprites[(i * 4) + 2];

            int row = Scanline - top;
            if ((attributes & 0x80) != 0)
            {
                row = height - 1 - row; // flipped vertically
            }

            ushort address;
            if (height == 8)
            {
                address = (ushort)((((_ctrl & 0x08) != 0 ? 1 : 0) << 12) | (tile << 4) | row);
            }
            else
            {
                // Tall sprites pick their own pattern table from the tile number, and
                // the bottom half lives in the next tile along.
                address = (ushort)(((tile & 0x01) << 12)
                    | (((tile & 0xFE) + (row >= 8 ? 1 : 0)) << 4)
                    | (row & 0x07));
            }

            byte low = PpuRead(address);
            byte high = PpuRead((ushort)(address + 8));

            if ((attributes & 0x40) != 0)
            {
                low = ReverseBits(low);
                high = ReverseBits(high);
            }

            _spriteShiftLow[i] = low;
            _spriteShiftHigh[i] = high;
        }
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }

    // --------------------------------------------------------- pixel output

    private byte ComposePixel()
    {
        int backgroundPixel = 0;
        int backgroundPalette = 0;

        if (ShowBackground && (Cycle > 8 || (_mask & 0x02) != 0))
        {
            ushort select = (ushort)(0x8000 >> _fineX);
            backgroundPixel = ((_patternShiftLow & select) != 0 ? 1 : 0)
                | ((_patternShiftHigh & select) != 0 ? 2 : 0);
            backgroundPalette = ((_attributeShiftLow & select) != 0 ? 1 : 0)
                | ((_attributeShiftHigh & select) != 0 ? 2 : 0);
        }

        int spritePixel = 0;
        int spritePalette = 0;
        bool spriteInFront = false;
        _spriteZeroRendering = false;

        if (ShowSprites && (Cycle > 8 || (_mask & 0x04) != 0))
        {
            for (int i = 0; i < _lineSpriteCount; i++)
            {
                if (_lineSprites[(i * 4) + 3] != 0)
                {
                    continue; // not reached this sprite yet
                }

                int pixel = ((_spriteShiftLow[i] & 0x80) != 0 ? 1 : 0)
                    | ((_spriteShiftHigh[i] & 0x80) != 0 ? 2 : 0);

                if (pixel == 0)
                {
                    continue; // transparent, keep looking behind it
                }

                byte attributes = _lineSprites[(i * 4) + 2];
                spritePixel = pixel;
                spritePalette = (attributes & 0x03) + 4;
                spriteInFront = (attributes & 0x20) == 0;

                if (i == 0 && _spriteZeroOnLine)
                {
                    _spriteZeroRendering = true;
                }

                break; // the lowest numbered sprite wins
            }
        }

        int pixelValue;
        int paletteValue;

        if (backgroundPixel == 0)
        {
            pixelValue = spritePixel;
            paletteValue = spritePixel == 0 ? 0 : spritePalette;
        }
        else if (spritePixel == 0)
        {
            pixelValue = backgroundPixel;
            paletteValue = backgroundPalette;
        }
        else
        {
            pixelValue = spriteInFront ? spritePixel : backgroundPixel;
            paletteValue = spriteInFront ? spritePalette : backgroundPalette;

            // Sprite zero overlapping the background is how a game finds out where
            // the beam is: it polls this flag and changes the scroll the moment the
            // status bar has been drawn.
            if (_spriteZeroRendering && ShowBackground && ShowSprites && Cycle - 1 < 255)
            {
                _status |= 0x40;
            }
        }

        byte colour = ReadPalette((ushort)(0x3F00 + (paletteValue << 2) + pixelValue));

        // The grayscale bit masks the hue away and keeps only the brightness.
        return (_mask & 0x01) != 0 ? (byte)(colour & 0x30) : colour;
    }

    // -------------------------------------------------- picture address space

    private byte PpuRead(ushort address)
    {
        address &= 0x3FFF;

        if (address < 0x2000)
        {
            return _mapper.PpuRead(address);
        }

        if (address < 0x3F00)
        {
            return _vram[NameTableOffset(address)];
        }

        return ReadPalette(address);
    }

    private void PpuWrite(ushort address, byte value)
    {
        address &= 0x3FFF;

        if (address < 0x2000)
        {
            _mapper.PpuWrite(address, value);
        }
        else if (address < 0x3F00)
        {
            _vram[NameTableOffset(address)] = value;
        }
        else
        {
            _paletteRam[PaletteOffset(address)] = value;
        }
    }

    private byte ReadPalette(ushort address) => _paletteRam[PaletteOffset(address)];

    /// <summary>
    /// Two kilobytes of name table memory has to cover four screens, so the board
    /// wires the same memory into two of the four slots. Which two is the mirroring.
    /// </summary>
    private int NameTableOffset(ushort address)
    {
        int index = (address - 0x2000) & 0x0FFF;
        int table = index >> 10;
        int offset = index & 0x03FF;

        return _mapper.Mirroring switch
        {
            Mirroring.Horizontal => ((table >> 1) << 10) | offset,
            Mirroring.Vertical => ((table & 1) << 10) | offset,
            Mirroring.SingleScreenLower => offset,
            Mirroring.SingleScreenUpper => 0x0400 | offset,
            _ => index, // four screen boards bring their own memory
        };
    }

    /// <summary>
    /// The backdrop entry of each sprite palette is the same memory as the
    /// background one, so $3F10 and $3F00 are one and the same byte.
    /// </summary>
    private static int PaletteOffset(ushort address)
    {
        int index = (address - 0x3F00) & 0x1F;
        if ((index & 0x13) == 0x10)
        {
            index &= ~0x10;
        }

        return index;
    }
}
