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
    private byte _ioBus;
    private readonly long[] _ioBusRefresh = new long[8];
    private long _clock;

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

    internal const int SpriteEvaluationStateSize = 41;
    private readonly byte[] _secondaryOam = new byte[32];
    private byte _evalN, _evalM, _secondaryIndex, _nextSpriteCount, _oamData;
    private byte _copyRemaining, _overflowRemaining;
    private bool _evalDone, _nextSpriteZero;

    /// <summary>
    /// A reset leaves the picture unit unable to accept the four registers that
    /// steer it until it has settled, about a frame later. Games written for the
    /// hardware wait for two vertical blanks before touching them for exactly
    /// this reason; one that does not would otherwise appear to configure a chip
    /// that was not listening. Sprite memory and the data port are unaffected.
    /// </summary>
    private long _warmUpUntil;

    private const long WarmUpDots = 29658 * 3;

    private bool _oddFrame;
    private bool _suppressVblank;
    private bool _renderingAtPreviousDot;

    public Ppu2C02(IMapper mapper)
    {
        _mapper = mapper;
        Array.Fill(_secondaryOam, (byte)0xFF);
        Scanline = PreRenderScanline;
    }

    /// <summary>
    /// One pixel per entry, ready for a display to colour in: the palette index in
    /// the low six bits and the emphasis setting that was in force when the beam
    /// passed in the three above it. Emphasis is recorded per pixel because a game
    /// is free to change it partway down a frame.
    /// </summary>
    public ushort[] FrameBuffer { get; } = new ushort[ScreenWidth * ScreenHeight];

    public int Scanline { get; private set; }

    public int Cycle { get; private set; }

    /// <summary>The address counter behind $2006 and the scroll, for offline checks.</summary>
    internal ushort ScrollAddress => _v;

    /// <summary>The background's low pattern shifter, for offline checks.</summary>
    internal ushort PatternShiftLow => _patternShiftLow;

    /// <summary>Raised when the beam reaches the bottom, so a host can present the frame.</summary>
    public bool FrameComplete { get; set; }

    public long FrameCount { get; private set; }

    /// <summary>Elapsed dots; unlike the beam position this does not restart on reset.</summary>
    internal long Clock => _clock;

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
        _ioBus = 0;
        Array.Clear(_ioBusRefresh);
        Scanline = PreRenderScanline;
        Cycle = 0;
        _oddFrame = false;
        _suppressVblank = false;
        _renderingAtPreviousDot = false;
        _lineSpriteCount = 0;
        BeginSpriteEvaluation();
        Array.Clear(FrameBuffer);
        _warmUpUntil = _clock + WarmUpDots;
    }

    /// <summary>Whether the steering registers are still being ignored after a reset.</summary>
    internal bool WarmingUp => _clock < _warmUpUntil;

    /// <summary>Asserted while both the vblank flag and PPUCTRL's NMI enable are set.</summary>
    public bool NmiLine => (_status & _ctrl & 0x80) != 0;

    private bool RenderingEnabled => (_mask & 0x18) != 0;

    private bool ShowBackground => (_mask & 0x08) != 0;

    private bool ShowSprites => (_mask & 0x10) != 0;

    // ----------------------------------------------------------- save states

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_vram);
        writer.Write(_paletteRam);
        writer.Write(_oam);
        writer.Write(_ctrl);
        writer.Write(_mask);
        writer.Write(_status);
        writer.Write(_oamAddress);
        writer.Write(_v);
        writer.Write(_t);
        writer.Write(_fineX);
        writer.Write(_writeLatch);
        writer.Write(_readBuffer);
        writer.Write(_ioBus);
        foreach (long refresh in _ioBusRefresh) writer.Write(refresh);
        writer.Write(_clock);
        writer.Write(_nameTableByte);
        writer.Write(_attributeByte);
        writer.Write(_patternLow);
        writer.Write(_patternHigh);
        writer.Write(_patternShiftLow);
        writer.Write(_patternShiftHigh);
        writer.Write(_attributeShiftLow);
        writer.Write(_attributeShiftHigh);
        writer.Write(_lineSprites);
        writer.Write(_spriteShiftLow);
        writer.Write(_spriteShiftHigh);
        writer.Write(_lineSpriteCount);
        writer.Write(_spriteZeroOnLine);
        writer.Write(_spriteZeroRendering);
        writer.Write(_oddFrame);
        writer.Write(_suppressVblank);
        writer.Write(_renderingAtPreviousDot);
        writer.Write(Scanline);
        writer.Write(Cycle);
        writer.Write(FrameCount);
        foreach (ushort pixel in FrameBuffer) writer.Write(pixel);
        writer.Write(_secondaryOam);
        writer.Write(_evalN); writer.Write(_evalM); writer.Write(_secondaryIndex);
        writer.Write(_nextSpriteCount); writer.Write(_oamData);
        writer.Write(_copyRemaining); writer.Write(_overflowRemaining);
        writer.Write(_evalDone); writer.Write(_nextSpriteZero);
    }

    internal bool LoadState(BinaryReader reader, bool legacy = false, bool legacySprites = false, bool legacyBusDecay = false, bool legacyEmphasis = false)
    {
        reader.ReadExactly(_vram);
        reader.ReadExactly(_paletteRam);
        reader.ReadExactly(_oam);
        _ctrl = reader.ReadByte();
        _mask = reader.ReadByte();
        _status = reader.ReadByte();
        _oamAddress = reader.ReadByte();
        _v = reader.ReadUInt16();
        _t = reader.ReadUInt16();
        _fineX = reader.ReadByte();
        _writeLatch = reader.ReadBoolean();
        _readBuffer = reader.ReadByte();
        _ioBus = reader.ReadByte();
        for (int bit = 0; bit < 8; bit++) _ioBusRefresh[bit] = legacyBusDecay ? 0 : reader.ReadInt64();
        _clock = reader.ReadInt64();
        // Older states carry no decay stamps. Lines still holding charge are taken
        // as just refreshed, which is the most a state written between instructions
        // can say; a latch of zero is already indistinguishable from a decayed one.
        if (legacyBusDecay && _ioBus != 0) Array.Fill(_ioBusRefresh, _clock);
        _nameTableByte = reader.ReadByte();
        _attributeByte = reader.ReadByte();
        _patternLow = reader.ReadByte();
        _patternHigh = reader.ReadByte();
        _patternShiftLow = reader.ReadUInt16();
        _patternShiftHigh = reader.ReadUInt16();
        _attributeShiftLow = reader.ReadUInt16();
        _attributeShiftHigh = reader.ReadUInt16();
        reader.ReadExactly(_lineSprites);
        reader.ReadExactly(_spriteShiftLow);
        reader.ReadExactly(_spriteShiftHigh);
        _lineSpriteCount = reader.ReadInt32();
        _spriteZeroOnLine = reader.ReadBoolean();
        _spriteZeroRendering = reader.ReadBoolean();
        _oddFrame = reader.ReadBoolean();
        bool savedNmi = reader.ReadBoolean();
        _suppressVblank = !legacy && savedNmi;
        _renderingAtPreviousDot = legacy ? RenderingEnabled : reader.ReadBoolean();
        Scanline = reader.ReadInt32();
        Cycle = reader.ReadInt32();
        FrameCount = reader.ReadInt64();

        // The picture is part of the state so that loading mid-frame does not show
        // half of the old one and half of the new.
        for (int i = 0; i < FrameBuffer.Length; i++) FrameBuffer[i] = legacyEmphasis ? reader.ReadByte() : reader.ReadUInt16();
        if (legacySprites)
        {
            // Batched evaluation had no in-flight state. Rebuild the next-line
            // search from the saved primary OAM, without touching live shifters
            // or the saved status flags. Historical mid-line OAM writes cannot
            // be recovered from these older formats.
            BeginSpriteEvaluation();
            Array.Fill(_secondaryOam, (byte)0xFF);
            if (Scanline is >= 0 and < ScreenHeight && Cycle <= 257 && RenderingEnabled)
            {
                byte status = _status;
                for (int dot = 1; dot < Cycle; dot++) EvaluateSpriteDot(dot);
                _status = status;
            }
            else
            {
                _lineSprites.CopyTo(_secondaryOam, 0);
                _nextSpriteCount = (byte)_lineSpriteCount;
                _nextSpriteZero = _spriteZeroOnLine;
            }
        }
        else
        {
            reader.ReadExactly(_secondaryOam);
            _evalN = reader.ReadByte(); _evalM = reader.ReadByte(); _secondaryIndex = reader.ReadByte();
            _nextSpriteCount = reader.ReadByte(); _oamData = reader.ReadByte();
            _copyRemaining = reader.ReadByte(); _overflowRemaining = reader.ReadByte();
            _evalDone = reader.ReadBoolean(); _nextSpriteZero = reader.ReadBoolean();
        }
        return legacy && savedNmi;
    }

    // ------------------------------------------------------ processor facing

    /// <summary>
    /// The picture unit has no pull-ups on its data lines: a value written or read
    /// is held only as charge on the wires, and each bit leaks away on its own.
    /// Hardware keeps them for roughly 600 ms, so a register that answers with the
    /// bus reads back zero a while after the last access that refreshed it.
    /// </summary>
    private const long IoBusDecay = 3_221_591; // ~600 ms of picture unit clock

    /// <summary>Reads the latch, dropping every bit that has leaked away.</summary>
    private byte IoBus()
    {
        for (int bit = 0; bit < 8; bit++)
        {
            if (_clock - _ioBusRefresh[bit] > IoBusDecay) _ioBus &= (byte)~(1 << bit);
        }

        return _ioBus;
    }

    /// <summary>Drives <paramref name="mask"/>'s lines and refreshes their charge.</summary>
    private void RefreshIoBus(byte value, byte mask = 0xFF)
    {
        _ioBus = (byte)((IoBus() & ~mask) | (value & mask));
        for (int bit = 0; bit < 8; bit++)
        {
            if ((mask & (1 << bit)) != 0) _ioBusRefresh[bit] = _clock;
        }
    }

    /// <summary>The eight registers at $2000, as the processor sees them.</summary>
    public byte ReadRegister(ushort address)
    {
        switch (address & 7)
        {
            case 2:
            {
                // Cycle names the next dot to execute. A read after dot 0 can
                // suppress the vblank set on dot 1, as well as its NMI output.
                if (Scanline == 241 && Cycle == 1) _suppressVblank = true;
                // The unused low bits return whatever was last on the data bus.
                // Only the three status lines are driven; the rest keep their charge.
                byte value = (byte)((_status & 0xE0) | (IoBus() & 0x1F));
                _status &= 0x7F;      // reading clears the vertical blank flag
                _writeLatch = false;  // and resets the two-write sequence
                RefreshIoBus(value, 0xE0);
                return value;
            }

            case 4:
            {
                byte value = RenderingEnabled && Scanline is >= PreRenderScanline and < ScreenHeight
                    ? _oamData : _oam[_oamAddress];
                RefreshIoBus(value);
                return value;
            }

            case 7:
            {
                byte value = _readBuffer;
                _readBuffer = PpuRead(_v);

                // Palette memory answers immediately; everything else is a fetch
                // behind. Only its six colour lines are driven, and the greyscale
                // bit masks the hue away on the way out, not on the way in.
                byte mask = 0xFF;
                if ((_v & 0x3FFF) >= 0x3F00)
                {
                    byte colour = (byte)(_readBuffer & ((_mask & 0x01) != 0 ? 0x30 : 0x3F));
                    value = (byte)(colour | (IoBus() & 0xC0));
                    _readBuffer = PpuRead((ushort)(_v - 0x1000));
                    mask = 0x3F;
                }

                StepAddress();
                RefreshIoBus(value, mask);
                return value;
            }

            default:
                // The write-only registers drive nothing: the lines answer with
                // whatever charge they are still holding.
                return IoBus();
        }
    }

    public void WriteRegister(ushort address, byte value)
    {
        // Every register write drives all eight lines, whatever the register does,
        // including one the chip is not yet listening to.
        RefreshIoBus(value);

        // $2000, $2001, $2005 and $2006 are ignored until the reset has settled.
        if (WarmingUp && (address & 7) is 0 or 1 or 5 or 6)
        {
            return;
        }

        switch (address & 7)
        {
            case 0:
            {
                _ctrl = value;
                _t = (ushort)((_t & 0xF3FF) | ((value & 0x03) << 10));

                break;
            }

            case 1:
                _mask = value;
                break;

            case 3:
                _oamAddress = value;
                break;

            case 4:
                if (RenderingEnabled && Scanline is >= PreRenderScanline and < ScreenHeight)
                {
                    // Sprite evaluation owns sprite memory while the beam is on a
                    // line: the write never lands, and only the sprite index part
                    // of the address moves on.
                    _oamAddress = (byte)((_oamAddress + 4) & 0xFC);
                    break;
                }

                WriteOam(_oamAddress, value);
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
                    _mapper.OnPpuAddress((ushort)(_v & 0x3FFF), _clock);
                    _writeLatch = false;
                }

                break;

            case 7:
                PpuWrite(_v, value);
                StepAddress();
                break;
        }
    }

    /// <summary>
    /// Sprite memory, filled one byte at a time or in one go by a transfer from
    /// $4014. Three bits of each sprite's attribute byte have no storage behind
    /// them on this chip, so they are dropped on the way in and read back as zero.
    /// </summary>
    public void WriteOam(byte offset, byte value) =>
        _oam[offset] = (offset & 3) == 2 ? (byte)(value & 0xE3) : value;

    private int AddressIncrement() => (_ctrl & 0x04) != 0 ? 32 : 1;

    /// <summary>
    /// Moves the address on after a $2007 access. While the beam is drawing, the
    /// same counters are being used for scrolling, so the access clocks them both
    /// instead of adding a plain step.
    /// </summary>
    private void StepAddress()
    {
        if (RenderingEnabled && Scanline is >= PreRenderScanline and < ScreenHeight)
        {
            IncrementCoarseX();
            IncrementY();
        }
        else
        {
            _v = (ushort)(_v + AddressIncrement());
        }

        _mapper.OnPpuAddress((ushort)(_v & 0x3FFF), _clock);
    }

    // ------------------------------------------------------- the beam itself

    /// <summary>Advances one picture unit cycle, which is a third of a processor cycle.</summary>
    public void Step()
    {
        _clock++;
        if (Scanline is >= PreRenderScanline and < ScreenHeight)
        {
            if (Scanline == PreRenderScanline && Cycle == 1)
            {
                _status &= 0x1F; // clears vertical blank, sprite zero hit and overflow
                _suppressVblank = false;
            }

            if (RenderingEnabled)
            {
                StepBackgroundFetch();
                StepSprites();
            }
            else
            {
                ClockSpriteUnits(rendering: false);
            }
        }

        if (Scanline == ScreenHeight + 1 && Cycle == 1)
        {
            if (!_suppressVblank) _status |= 0x80;
        }

        if (Scanline >= 0 && Scanline < ScreenHeight && Cycle >= 1 && Cycle <= ScreenWidth)
        {
            FrameBuffer[(Scanline * ScreenWidth) + (Cycle - 1)] = ComposePixel();
        }

        // The odd-frame skip uses render enable from the preceding dot. Keep
        // that latch separate from the beam jump: the 10-even_odd_timing ROM
        // measures writes immediately on either side of this sampling boundary.
        bool skipLastDot = Scanline == PreRenderScanline && Cycle == 339
            && _oddFrame && _renderingAtPreviousDot;
        _renderingAtPreviousDot = RenderingEnabled;
        if (skipLastDot)
        {
            Cycle = 0;
            Scanline = 0;
            return;
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
        // The shifters start advancing at dot 2, but the first nametable
        // address is already driven at dot 1.
        if (Cycle == 1)
        {
            _nameTableByte = PpuRead((ushort)(0x2000 | (_v & 0x0FFF)));
        }

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

        // The first redundant nametable fetch is already made at dot 337.
        if (Cycle == 339)
        {
            _nameTableByte = PpuRead((ushort)(0x2000 | (_v & 0x0FFF)));
        }

        // The final dot starts an aborted pattern fetch: the address reaches
        // the cartridge even though no data is read. It breaks the long A12-low
        // interval before the next line when backgrounds use $1000. The odd
        // pre-render skip omits this dot and can therefore add one MMC3 clock.
        if (Cycle == 340)
        {
            _mapper.OnPpuAddress(BackgroundPatternAddress(0), _clock);
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
        // The pattern shifters have their serial input tied high, so a one enters
        // at the bottom on every shift. A reload overwrites the low byte before
        // those ones can reach the output, which is why they only become visible
        // when rendering is switched off long enough to skip the reloads.
        _patternShiftLow = (ushort)((_patternShiftLow << 1) | 1);
        _patternShiftHigh = (ushort)((_patternShiftHigh << 1) | 1);
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
        // The current line's output units remain independent while secondary
        // OAM is cleared and populated for the following line.
        if (Scanline >= 0 && Cycle is >= 1 and <= 256) EvaluateSpriteDot(Cycle);
        if (Cycle == 257)
        {
            _lineSpriteCount = Scanline < 0 ? 0 : _nextSpriteCount;
            _spriteZeroOnLine = Scanline >= 0 && _nextSpriteZero;
        }

        if (Cycle >= 257 && Cycle <= 320)
        {
            int slot = (Cycle - 257) / 8;
            int phase = (Cycle - 257) % 8;
            _oamAddress = 0;
            _oamData = _secondaryOam[slot * 4 + Math.Min(phase, 3)];
            if (phase < 4) _lineSprites[slot * 4 + phase] = _oamData;
            if (phase is 0 or 2) PpuRead((ushort)(0x2000 | (_v & 0x0FFF)));
            if (phase is 4 or 6) FetchSpritePattern(slot, phase == 4 ? 0 : 8);
        }

        if (Cycle >= 321 || Cycle == 0) _oamData = _secondaryOam[0];

        ClockSpriteUnits(rendering: true);
    }

    /// <summary>
    /// Clocks each output unit once.
    ///
    /// The counter that waits out a sprite's X position runs off the dot and keeps
    /// going through forced blanking: switching the picture off for part of a line
    /// does not move where the sprite lands. The shifter behind it only moves
    /// while rendering, so a sprite caught half drawn stays where it is and
    /// resumes there when the picture comes back.
    /// </summary>
    private void ClockSpriteUnits(bool rendering)
    {
        if (Cycle < 2 || Cycle >= 257)
        {
            return;
        }

        for (int i = 0; i < _lineSpriteCount; i++)
        {
            if (_lineSprites[(i * 4) + 3] > 0)
            {
                _lineSprites[(i * 4) + 3]--;
            }
            else if (rendering)
            {
                _spriteShiftLow[i] <<= 1;
                _spriteShiftHigh[i] <<= 1;
            }
        }
    }

    private void BeginSpriteEvaluation()
    {
        _evalN = _evalM = _secondaryIndex = _nextSpriteCount = 0;
        _copyRemaining = _overflowRemaining = 0;
        _evalDone = _nextSpriteZero = false;
        _oamData = 0xFF;
    }

    private bool SpriteInRange(byte y) => Scanline >= y && Scanline - y < ((_ctrl & 0x20) != 0 ? 16 : 8);

    private void AdvanceSprite()
    {
        _evalN = (byte)((_evalN + 1) & 63);
        if (_evalN == 0) _evalDone = true;
    }

    private void AdvanceSpriteByte()
    {
        _evalM = (byte)((_evalM + 1) & 3);
        if (_evalM == 0) AdvanceSprite();
    }

    private void EvaluateSpriteDot(int dot)
    {
        if (dot == 1) BeginSpriteEvaluation();
        if (dot <= 64)
        {
            _oamData = 0xFF;
            if ((dot & 1) == 0) _secondaryOam[dot / 2 - 1] = _oamData;
            return;
        }

        if (dot == 65) { _evalN = (byte)(_oamAddress >> 2); _evalM = (byte)(_oamAddress & 3); }
        if ((dot & 1) != 0)
        {
            _oamData = _oam[_evalN * 4 + _evalM];
            return;
        }

        byte candidate = _oamData;
        if (_evalDone)
        {
            _oamData = _secondaryOam[_secondaryIndex & 31];
            AdvanceSprite();
            return;
        }

        if (_overflowRemaining != 0)
        {
            AdvanceSpriteByte();
            if (--_overflowRemaining == 0) { _evalM = 0; _evalDone = true; }
        }
        else if (_secondaryIndex == 32)
        {
            if (SpriteInRange(candidate))
            {
                _status |= 0x20;
                _overflowRemaining = 3;
                AdvanceSpriteByte();
            }
            else
            {
                // With writes disabled, the low and high address counters both
                // advance, without carry: tile/attribute/X bytes become Y tests.
                AdvanceSprite();
                _evalM = (byte)((_evalM + 1) & 3);
            }
        }
        else
        {
            _secondaryOam[_secondaryIndex] = candidate;
            if (_copyRemaining != 0)
            {
                _secondaryIndex++;
                _copyRemaining--;
                AdvanceSpriteByte();
            }
            else if (SpriteInRange(candidate))
            {
                _nextSpriteZero |= _evalN == 0;
                _nextSpriteCount++;
                _secondaryIndex++;
                _copyRemaining = 3;
                AdvanceSpriteByte();
            }
            else AdvanceSprite();
        }
        if (_evalDone) _evalM = 0;
        if (_secondaryIndex == 32) _oamData = _secondaryOam[0];
    }

    private void FetchSpritePattern(int i, int plane)
    {
        int height = (_ctrl & 0x20) != 0 ? 16 : 8;
        byte top = _lineSprites[i * 4];
        byte tile = _lineSprites[(i * 4) + 1];
        byte attributes = _lineSprites[(i * 4) + 2];

        int row = (Scanline - top) & (height - 1);
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

        byte pattern = PpuRead((ushort)(address + plane));

        if ((attributes & 0x40) != 0)
        {
            pattern = ReverseBits(pattern);
        }

        if (plane == 0) _spriteShiftLow[i] = pattern;
        else _spriteShiftHigh[i] = pattern;
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        value = (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
        return value;
    }

    // --------------------------------------------------------- pixel output

    private ushort ComposePixel()
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
        byte index = (_mask & 0x01) != 0 ? (byte)(colour & 0x30) : colour;

        // The emphasis bits belong to the pixel, not to the frame: a game can move
        // them mid-screen, and the display has to honour where they changed.
        return (ushort)(index | ((_mask & 0xE0) >> 5 << 6));
    }

    // -------------------------------------------------- picture address space

    private byte PpuRead(ushort address)
    {
        address &= 0x3FFF;
        _mapper.OnPpuAddress(address, _clock);

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
        _mapper.OnPpuAddress(address, _clock);

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
