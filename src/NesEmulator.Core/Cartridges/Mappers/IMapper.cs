namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// The extra logic soldered into a cartridge. The console itself can only see
/// 32 KB of program ROM and 8 KB of tiles at a time, so anything larger relies
/// on the cartridge swapping banks underneath it. Each mapper number is a
/// different board design with a different way of doing that.
/// </summary>
public interface IMapper
{
    /// <summary>Handles the part of the processor address space above $4020.</summary>
    byte CpuRead(ushort address);

    void CpuWrite(ushort address, byte value);

    /// <summary>Handles the pattern tables, $0000 to $1FFF of the picture unit address space.</summary>
    byte PpuRead(ushort address);

    void PpuWrite(ushort address, byte value);

    /// <summary>Mirroring can be fixed by the board or switched by the game.</summary>
    Mirroring Mirroring { get; }

    /// <summary>
    /// Called once per visible line while the picture unit is drawing. Boards that
    /// count lines so a game can be interrupted partway down the screen use this;
    /// the rest ignore it.
    /// </summary>
    void OnScanline()
    {
    }

    /// <summary>Whether the board is holding the maskable interrupt line down.</summary>
    bool IrqPending => false;

    static IMapper Create(Cartridge cartridge) => cartridge.MapperNumber switch
    {
        0 => new Nrom(cartridge),
        1 => new Mmc1(cartridge),
        2 => new UxRom(cartridge),
        3 => new CnRom(cartridge),
        4 => new Mmc3(cartridge),
        _ => throw new NotSupportedException(
            $"Mapper {cartridge.MapperNumber} is not implemented yet."),
    };

    /// <summary>
    /// Writes whatever the board is holding: bank registers, counters, save RAM.
    /// A board with no state of its own can leave both of these alone.
    /// </summary>
    void SaveState(BinaryWriter writer)
    {
    }

    void LoadState(BinaryReader reader)
    {
    }
}
