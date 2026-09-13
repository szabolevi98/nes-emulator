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

    /// <summary>
    /// Whether the board drives the data bus for this address. A cartridge only
    /// answers the addresses it decodes; everything else leaves the bus floating,
    /// so the processor reads back whatever was last carried on it.
    /// </summary>
    bool DrivesCpuRead(ushort address) => address >= 0x8000;

    void CpuWrite(ushort address, byte value);

    /// <summary>Handles the pattern tables, $0000 to $1FFF of the picture unit address space.</summary>
    byte PpuRead(ushort address);

    void PpuWrite(ushort address, byte value);

    /// <summary>Mirroring can be fixed by the board or switched by the game.</summary>
    Mirroring Mirroring { get; }

    /// <summary>
    /// Clocks a board's line counter directly for diagnostics. During console
    /// execution MMC3 derives qualified clocks from PPU A12 and CPU M2 instead.
    /// </summary>
    void OnScanline()
    {
    }

    /// <summary>External PPU address bus, timestamped in PPU dots (including CPU register accesses).</summary>
    void OnPpuAddress(ushort address, long cycle) { }

    /// <summary>Falling edge of CPU M2, including stalled and reset cycles.</summary>
    void OnM2FallingEdge() { }

    /// <summary>Whether the board is holding the maskable interrupt line down.</summary>
    bool IrqPending => false;

    static IMapper Create(Cartridge cartridge, Mmc3IrqRevision mmc3Revision = Mmc3IrqRevision.Standard) => cartridge.MapperNumber switch
    {
        0 => new Nrom(cartridge),
        1 => new Mmc1(cartridge),
        2 => new UxRom(cartridge),
        3 => new CnRom(cartridge),
        4 => new Mmc3(cartridge, mmc3Revision),
        7 => new AxRom(cartridge),
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
