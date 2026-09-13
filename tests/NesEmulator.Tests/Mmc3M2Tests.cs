using System.IO.Compression;
using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;

internal static class Mmc3M2Tests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (Mmc3IrqRevision revision in Enum.GetValues<Mmc3IrqRevision>())
        {
            // M2 falls after dots 12, 15, 18, 21... A12 changes before a
            // coincident fall. The three alignments require 7, 9 or 8 low dots.
            int[] minimumWidth = [7, 9, 8];
            for (int phase = 0; phase < 3; phase++)
            for (int width = 0; width <= 12; width++)
            {
                Mmc3 mapper = NewMapper(revision);
                int low = 12 + phase, high = low + width;
                mapper.OnPpuAddress(0x1000, low - 1);
                for (int dot = low; dot <= high; dot++)
                {
                    if (dot == low) mapper.OnPpuAddress(0, dot);
                    if (dot == high) mapper.OnPpuAddress(0x1000, dot);
                    if (dot % 3 == 0) mapper.OnM2FallingEdge();
                }
                check($"MMC3 {revision}: A12 low width {width}, M2 phase {phase}",
                    mapper.IrqPending == (width >= minimumWidth[phase]));
            }

            Mmc3 pulse = NewMapper(revision);
            pulse.OnPpuAddress(0x1000, 1);
            pulse.OnPpuAddress(0, 2);
            pulse.OnPpuAddress(0x1000, 10000);
            check($"MMC3 {revision}: elapsed PPU time alone cannot qualify A12", !pulse.IrqPending);
            pulse.OnPpuAddress(0, 10001);
            pulse.OnM2FallingEdge(); pulse.OnM2FallingEdge();
            pulse.OnPpuAddress(0x1000, 10008);
            pulse.OnPpuAddress(0, 10008); // Pulse entirely between M2 falls.
            pulse.OnM2FallingEdge();
            pulse.OnPpuAddress(0x1000, 10011);
            check($"MMC3 {revision}: unsampled high pulse clears filter progress", !pulse.IrqPending);
            pulse.OnPpuAddress(0, 10012);
            for (int i = 0; i < 300; i++)
            {
                pulse.OnM2FallingEdge();
                pulse.OnPpuAddress(0x0008, 10012 + i * 3);
            }
            pulse.OnPpuAddress(0x1000, 11000);
            check($"MMC3 {revision}: low-address changes preserve saturated filter", pulse.IrqPending);

            for (int progress = 0; progress <= 3; progress++)
            {
                Nes nes = NewConsole(revision);
                Low(nes);
                long start = nes.Ppu.Clock;
                for (int i = 0; i < progress; i++) nes.Cpu.DmaRead(0x0200);
                byte[] saved = Save(nes);
                High(nes);
                check($"MMC3 {revision}: console clocks {progress} M2 falls at three dots per CPU cycle",
                    nes.Mapper.IrqPending == (progress == 3) && nes.Ppu.Clock - start == progress * 3);
                nes.LoadState(new MemoryStream(saved));
                for (int i = progress; i < 3; i++) nes.Cpu.DmaRead(0x0200);
                High(nes);
                byte[] expected = Save(nes);
                nes.LoadState(new MemoryStream(saved));
                for (int i = progress; i < 3; i++) nes.Cpu.DmaRead(0x0200);
                High(nes);
                check($"MMC3 {revision}: save preserves filter progress {progress}",
                    nes.Mapper.IrqPending && Save(nes).SequenceEqual(expected));
            }

            Nes oam = NewConsole(revision);
            Low(oam);
            oam.Bus.Write(0x4014, 2);
            long before = oam.Ppu.Clock;
            int stolen = oam.Bus.RunDma(oam.Cpu);
            High(oam);
            check($"MMC3 {revision}: OAM stalls also clock M2", stolen is 513 or 514
                && oam.Mapper.IrqPending && oam.Ppu.Clock - before == 3 * stolen);

            Nes dmc = NewConsole(revision);
            dmc.Apu.WriteRegister(0x4010, 15);
            dmc.Apu.WriteRegister(0x4015, 0x10);
            for (int i = 0; i < 4 && !dmc.Apu.Dmc.DmaPending; i++) dmc.Cpu.DmaRead(0x0200);
            Low(dmc); // Only the stolen cycles may qualify this new low interval.
            before = dmc.Ppu.Clock;
            stolen = dmc.Bus.RunDma(dmc.Cpu);
            High(dmc);
            check($"MMC3 {revision}: DMC stalls also clock M2", stolen is 3 or 4
                && dmc.Mapper.IrqPending && dmc.Ppu.Clock - before == 3 * stolen);

            Nes reset = NewConsole(revision);
            Low(reset);
            before = reset.Ppu.Clock;
            reset.Reset();
            High(reset);
            check($"MMC3 {revision}: reset advances M2 without restarting elapsed PPU time",
                reset.Mapper.IrqPending && reset.Ppu.Clock - before == 21);

            string fixture = $"v6-mmc3-{revision.ToString().ToLowerInvariant()}-low.state.gz";
            using Stream resource = typeof(Mmc3M2Tests).Assembly.GetManifestResourceStream(fixture)!;
            using GZipStream gzip = new(resource, CompressionMode.Decompress);
            using MemoryStream bytes = new();
            gzip.CopyTo(bytes);
            for (int further = 0; further <= 2; further++)
            {
                Nes migrated = NewConsole(revision);
                migrated.LoadState(new MemoryStream(bytes.ToArray()));
                bool original = migrated.Cpu.Cycles == 11 && migrated.Ppu.Clock == 33 && migrated.Cpu.PC == 0x0203;
                for (int i = 0; i < further; i++) migrated.Cpu.DmaRead(0x0200);
                High(migrated);
                check($"state v6 {revision}: one elapsed M2 fall plus {further} after migration",
                    original && migrated.Mapper.IrqPending == (further == 2));
            }
        }
    }

    private static Mmc3 NewMapper(Mmc3IrqRevision revision)
    {
        Mmc3 mapper = new(Cartridge.FromBytes(Image()), revision);
        mapper.CpuWrite(0xC001, 0);
        mapper.CpuWrite(0xE001, 0);
        return mapper;
    }

    private static Nes NewConsole(Mmc3IrqRevision revision)
    {
        Nes nes = new(Cartridge.FromBytes(Image()), mmc3Revision: revision);
        High(nes); // Clear the filter before enabling IRQ.
        nes.Mapper.CpuWrite(0xC001, 0);
        nes.Mapper.CpuWrite(0xE001, 0);
        return nes;
    }

    private static void High(Nes nes) { nes.Ppu.WriteRegister(0x2006, 0x10); nes.Ppu.WriteRegister(0x2006, 0); }
    private static void Low(Nes nes) { nes.Ppu.WriteRegister(0x2006, 0); nes.Ppu.WriteRegister(0x2006, 0); }

    private static byte[] Image()
    {
        byte[] image = new byte[16 + 32768 + 8192];
        "NES\u001a"u8.CopyTo(image);
        image[4] = 2; image[5] = 1; image[6] = 0x40;
        image[16 + 0x6000] = 0x4C;
        image[16 + 0x6002] = 0xE0;
        image[16 + 0x7FFD] = 0xE0;
        return image;
    }

    private static byte[] Save(Nes nes)
    {
        using MemoryStream stream = new();
        nes.SaveState(stream);
        return stream.ToArray();
    }
}
