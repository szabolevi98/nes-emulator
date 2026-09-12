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

    /// <summary>Character memory. Backed by ROM on most cartridges, by RAM on the rest.</summary>
    public byte[] Chr { get; }

    public bool ChrIsRam { get; private init; }

    public int MapperNumber { get; private init; }

    public Mirroring Mirroring { get; private init; }

    /// <summary>Battery-backed save RAM at $6000, which is how games kept high scores.</summary>
    public bool HasBattery { get; private init; }

    public bool IsNes20 { get; private init; }

    public int PrgRamSize { get; private init; }

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

        int prgBanks = image[4];
        int chrBanks = image[5];
        byte flags6 = image[6];
        byte flags7 = image[7];

        if (prgBanks == 0)
        {
            throw new InvalidDataException("Header declares no program ROM.");
        }

        bool isNes20 = (flags7 & 0x0C) == 0x08;

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

        int prgSize = prgBanks * PrgBankSize;
        if (image.Length < offset + prgSize)
        {
            throw new InvalidDataException(
                $"Header promises {prgSize} bytes of program ROM but only {image.Length - offset} remain.");
        }

        byte[] prgRom = image.Slice(offset, prgSize).ToArray();
        offset += prgSize;

        // A character bank count of zero means the cartridge carries RAM instead,
        // which the game fills with tiles at runtime.
        bool chrIsRam = chrBanks == 0;
        byte[] chr;
        if (chrIsRam)
        {
            chr = new byte[ChrBankSize];
        }
        else
        {
            int chrSize = chrBanks * ChrBankSize;
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

        int prgRamSize = image[8] == 0 ? 8 * 1024 : image[8] * 8 * 1024;
        if (isNes20)
        {
            prgRamSize = 8 * 1024;
        }

        return new Cartridge(prgRom, chr, chrIsRam)
        {
            MapperNumber = mapper,
            Mirroring = mirroring,
            HasBattery = (flags6 & 0x02) != 0,
            IsNes20 = isNes20,
            PrgRamSize = prgRamSize,
            Trainer = trainer,
        };
    }

    public override string ToString() =>
        $"mapper {MapperNumber}, {PrgRom.Length / 1024} KB PRG, " +
        $"{Chr.Length / 1024} KB CHR {(ChrIsRam ? "RAM" : "ROM")}, {Mirroring} mirroring" +
        (HasBattery ? ", battery" : string.Empty);
}
