namespace NesEmulator.Core.Cartridges;

/// <summary>How the two on-board name tables are mirrored across the four slots.</summary>
public enum Mirroring
{
    Horizontal,
    Vertical,
    FourScreen,
    SingleScreenLower,
    SingleScreenUpper,
}

/// <summary>
/// A cartridge image in the iNES format, which is what nearly every dumped ROM
/// uses: a sixteen byte header, an optional 512 byte trainer, then the program
/// ROM and the character ROM back to back.
/// </summary>
public sealed class Cartridge
{
    public const int PrgBankSize = 16 * 1024;
    public const int ChrBankSize = 8 * 1024;

    private Cartridge(byte[] prgRom, byte[] chr, bool chrIsRam)
    {
        PrgRom = prgRom;
        Chr = chr;
        ChrIsRam = chrIsRam;
    }

    public byte[] PrgRom { get; }

    /// <summary>SHA-256 of the loaded image, fixed before any CHR RAM can change.</summary>
    public ReadOnlyMemory<byte> Identity { get; private init; }

    /// <summary>Character memory. Backed by ROM on most cartridges, by RAM on the rest.</summary>
    public byte[] Chr { get; }

    public bool ChrIsRam { get; private init; }

    public int MapperNumber { get; private init; }

    public Mirroring Mirroring { get; private init; }

    /// <summary>Battery-backed save RAM at $6000, which is how games kept high scores.</summary>
    public bool HasBattery { get; private init; }

    public bool IsNes20 { get; private init; }

    public int PrgRamSize { get; private init; }

    /// <summary>
    /// The battery-backed part of the save RAM. A NES 2.0 header sizes the
    /// volatile and non-volatile chips separately; iNES 1.0 cannot tell them
    /// apart and only says whether a battery is present at all.
    /// </summary>
    public int PrgNvramSize { get; private init; }

    /// <summary>
    /// Which variant of a board the cartridge is, where the mapper number alone
    /// is ambiguous. Always zero for an iNES 1.0 header.
    /// </summary>
    public int Submapper { get; private init; }

    /// <summary>The console the cartridge was made for. This emulator is an NTSC one.</summary>
    public ConsoleTiming Timing { get; private init; }

    public byte[]? Trainer { get; private init; }

    public static Cartridge FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    public static Cartridge FromBytes(ReadOnlySpan<byte> image)
    {
        if (image.Length < 16)
        {
            throw new InvalidDataException("File is too short to hold an iNES header.");
        }

        if (image[0] != 'N' || image[1] != 'E' || image[2] != 'S' || image[3] != 0x1A)
        {
            throw new InvalidDataException("Missing the iNES signature; this is not a NES ROM.");
        }

        byte flags6 = image[6];
        byte flags7 = image[7];
        bool isNes20 = (flags7 & 0x0C) == 0x08;

        // NES 2.0 widens both sizes by a nibble each from byte 9, and reserves the
        // value $F there for a size written as an exponent instead of a count.
        int prgSize = RomSize(image[4], isNes20 ? image[9] & 0x0F : 0, PrgBankSize);
        int chrSize = RomSize(image[5], isNes20 ? image[9] >> 4 : 0, ChrBankSize);

        if (prgSize == 0)
        {
            throw new InvalidDataException("Header declares no program ROM.");
        }

        // Several early dumping tools wrote their own name into bytes 7 to 15, which
        // would otherwise be read as a mapper number in the hundreds. If the tail of
        // the header is not clear, only the low nibble can be trusted.
        bool headerTailIsClean = true;
        for (int i = 12; i < 16 && headerTailIsClean; i++)
        {
            headerTailIsClean = image[i] == 0;
        }

        int mapper = flags6 >> 4;
        if (isNes20 || headerTailIsClean)
        {
            mapper |= flags7 & 0xF0;
        }

        if (isNes20)
        {
            mapper |= (image[8] & 0x0F) << 8;
        }

        bool hasTrainer = (flags6 & 0x04) != 0;
        int offset = 16;

        byte[]? trainer = null;
        if (hasTrainer)
        {
            if (image.Length < offset + 512)
            {
                throw new InvalidDataException("Header promises a trainer the file does not contain.");
            }

            trainer = image.Slice(offset, 512).ToArray();
            offset += 512;
        }

        if (image.Length < offset + prgSize)
        {
            throw new InvalidDataException(
                $"Header promises {prgSize} bytes of program ROM but only {image.Length - offset} remain.");
        }

        byte[] prgRom = image.Slice(offset, prgSize).ToArray();
        offset += prgSize;

        // No character ROM means the cartridge carries RAM instead, which the game
        // fills with tiles at runtime. NES 2.0 says how much; iNES 1.0 implies the
        // single 8 KB chip every such board of its era had.
        bool chrIsRam = chrSize == 0;
        byte[] chr;
        if (chrIsRam)
        {
            int chrRam = isNes20
                ? RamSize(image[11] & 0x0F) + RamSize(image[11] >> 4)
                : ChrBankSize;
            chr = new byte[chrRam == 0 ? ChrBankSize : chrRam];
        }
        else
        {
            if (image.Length < offset + chrSize)
            {
                throw new InvalidDataException(
                    $"Header promises {chrSize} bytes of character ROM but only {image.Length - offset} remain.");
            }

            chr = image.Slice(offset, chrSize).ToArray();
        }

        Mirroring mirroring = (flags6 & 0x08) != 0
            ? Mirroring.FourScreen
            : (flags6 & 0x01) != 0
                ? Mirroring.Vertical
                : Mirroring.Horizontal;

        // iNES 1.0 counts 8 KB chips in byte 8 and treats zero as one chip. NES 2.0
        // sizes the volatile and battery-backed parts separately in byte 10, each
        // as a shift count; a board with neither gets nothing.
        int prgNvram = isNes20 ? RamSize(image[10] >> 4) : 0;
        int prgRamSize = isNes20
            ? RamSize(image[10] & 0x0F) + prgNvram
            : (image[8] == 0 ? 8 * 1024 : image[8] * 8 * 1024);

        return new Cartridge(prgRom, chr, chrIsRam)
        {
            Identity = System.Security.Cryptography.SHA256.HashData(image),
            MapperNumber = mapper,
            Mirroring = mirroring,
            HasBattery = (flags6 & 0x02) != 0,
            IsNes20 = isNes20,
            PrgRamSize = prgRamSize,
            PrgNvramSize = prgNvram,
            Submapper = isNes20 ? image[8] >> 4 : 0,
            Timing = isNes20 ? (ConsoleTiming)(image[12] & 0x03) : ConsoleTiming.Ntsc,
            Trainer = trainer,
        };
    }

    /// <summary>
    /// A size written either as a count of banks, widened by the high nibble a
    /// NES 2.0 header adds, or — when that nibble is $F — as an exponent and a
    /// small odd multiplier, which is how the format reaches sizes no count of
    /// whole banks could express.
    /// </summary>
    private static int RomSize(byte low, int high, int bankSize)
    {
        if (high != 0x0F)
        {
            return ((high << 8) | low) * bankSize;
        }

        int exponent = (low >> 2) & 0x3F;
        int multiplier = ((low & 0x03) * 2) + 1;
        return exponent >= 31 ? 0 : (1 << exponent) * multiplier;
    }

    /// <summary>A NES 2.0 memory size: a shift count, where zero means none at all.</summary>
    private static int RamSize(int shift) => shift == 0 ? 0 : 64 << shift;

    public override string ToString() =>
        $"mapper {MapperNumber}, {PrgRom.Length / 1024} KB PRG, " +
        $"{Chr.Length / 1024} KB CHR {(ChrIsRam ? "RAM" : "ROM")}, {Mirroring} mirroring" +
        (HasBattery ? ", battery" : string.Empty);
}

/// <summary>Which console a cartridge was made for, as a NES 2.0 header states it.</summary>
public enum ConsoleTiming
{
    Ntsc,
    Pal,
    Either,
    Dendy,
}
