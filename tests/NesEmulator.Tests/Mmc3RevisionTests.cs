using System.IO.Compression;
using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;

internal static class Mmc3RevisionTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (Mmc3IrqRevision revision in Enum.GetValues<Mmc3IrqRevision>())
        {
            bool standard = revision == Mmc3IrqRevision.Standard;
            Nes nes = new(Cartridge.FromBytes(Image()), mmc3Revision: revision);
            Mmc3 mapper = (Mmc3)nes.Mapper;
            long dot = 0;
            void Edge()
            {
                mapper.OnPpuAddress(0, ++dot);
                mapper.OnPpuAddress(0x1000, dot += 12);
            }
            void Acknowledge()
            {
                mapper.CpuWrite(0xE000, 0);
                mapper.CpuWrite(0xE001, 0);
            }

            mapper.CpuWrite(0xE001, 0);
            mapper.CpuWrite(0xC000, 0);
            Edge();
            check($"MMC3 {revision}: natural zero reload IRQ", mapper.IrqPending == standard);
            Acknowledge();
            mapper.CpuWrite(0xC001, 0);
            check($"MMC3 {revision}: reload write does not assert immediately", !mapper.IrqPending);
            Edge();
            check($"MMC3 {revision}: explicit zero reload asserts", mapper.IrqPending);
            Acknowledge();
            Edge();
            check($"MMC3 {revision}: explicit reload only affects one edge", mapper.IrqPending == standard);
            Acknowledge();
            mapper.CpuWrite(0xC000, 2);
            mapper.CpuWrite(0xC001, 0);
            Edge(); Edge();
            check($"MMC3 {revision}: nonzero count has not fired early", !mapper.IrqPending);
            Edge();
            check($"MMC3 {revision}: decrement to zero asserts", mapper.IrqPending);
            mapper.CpuWrite(0xC000, 3);
            Edge();
            check($"MMC3 {revision}: reload cannot clear a pending IRQ", mapper.IrqPending);
            Acknowledge();
            mapper.CpuWrite(0xE000, 0);
            Edge(); Edge(); Edge(); // Counter reaches zero with IRQ disabled.
            mapper.CpuWrite(0xC000, 0);
            mapper.CpuWrite(0xE001, 0);
            check($"MMC3 {revision}: enable does not assert without an edge", !mapper.IrqPending);
            Edge();
            check($"MMC3 {revision}: counter advances while IRQ is disabled", mapper.IrqPending == standard);
            Acknowledge();
            mapper.CpuWrite(0xC001, 0);
            mapper.OnPpuAddress(0, ++dot);
            mapper.OnPpuAddress(0x1000, dot += 7);
            check($"MMC3 {revision}: short A12 pulse cannot consume reload", !mapper.IrqPending);
            Edge();
            check($"MMC3 {revision}: next qualified edge consumes reload", mapper.IrqPending);

            // Replay the counter through the real console serializer, including
            // a latched IRQ, an explicit reload and a partly elapsed low period.
            for (int stage = 0; stage < 3; stage++)
            {
                if (stage == 1) { Acknowledge(); mapper.CpuWrite(0xC001, 0); }
                if (stage == 2) { Edge(); Acknowledge(); mapper.OnPpuAddress(0, ++dot); }
                long savedDot = dot;
                byte[] saved = Save(nes);
                for (int i = 0; i < 8; i++) { Edge(); nes.StepInstruction(); }
                byte[] expected = Save(nes);
                nes.LoadState(new MemoryStream(saved));
                dot = savedDot;
                for (int i = 0; i < 8; i++) { Edge(); nes.StepInstruction(); }
                check($"MMC3 {revision}: save replay at IRQ stage {stage}", Save(nes).SequenceEqual(expected));
            }

            Nes other = new(Cartridge.FromBytes(Image()), mmc3Revision: standard
                ? Mmc3IrqRevision.Alternate : Mmc3IrqRevision.Standard);
            byte[] before = Save(other);
            check($"MMC3 {revision}: wrong revision save is rejected before mutation",
                Reject(other, Save(nes)) && Save(other).SequenceEqual(before));
            byte[] invalid = Save(nes);
            invalid[40] = 255; // v6 configuration byte, after magic/mapper/SHA-256.
            before = Save(nes);
            check($"MMC3 {revision}: unknown saved revision is rejected before mutation",
                Reject(nes, invalid) && Save(nes).SequenceEqual(before));
        }

        using Stream file = typeof(Mmc3RevisionTests).Assembly.GetManifestResourceStream("v5-mmc3-dmc.state.gz")!;
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream legacy = new();
        gzip.CopyTo(legacy);
        Nes restored = new(Cartridge.FromBytes(Image()));
        restored.LoadState(new MemoryStream(legacy.ToArray()));
        check("state v5: real MMC3/DMC fixture retains reader, counter and RAM",
            restored.Cpu.PC == 0xE000 && restored.Cpu.Cycles == 907 && restored.Bus.Read(0x6000) == 0xA5
            && restored.Apu.Dmc.Active && !restored.Mapper.IrqPending);
        restored.Mapper.OnPpuAddress(0, 41);
        restored.Mapper.OnPpuAddress(0x1000, 53);
        check("state v5: restored nonzero MMC3 counter reaches IRQ on the next edge", restored.Mapper.IrqPending);
        byte[] migrated = Save(restored);
        for (int i = 0; i < 200; i++) restored.StepInstruction();
        byte[] replay = Save(restored);
        restored.LoadState(new MemoryStream(migrated));
        for (int i = 0; i < 200; i++) restored.StepInstruction();
        check("state v5: migrated pending DMC fetch replays deterministically", Save(restored).SequenceEqual(replay));
        Nes alternate = new(Cartridge.FromBytes(Image()), mmc3Revision: Mmc3IrqRevision.Alternate);
        byte[] initial = Save(alternate);
        check("state v5: standard-only legacy state cannot load into alternate hardware",
            Reject(alternate, legacy.ToArray()) && Save(alternate).SequenceEqual(initial));
    }

    private static byte[] Image()
    {
        byte[] image = new byte[16 + 32768 + 8192];
        "NES\u001a"u8.CopyTo(image);
        image[4] = 2; image[5] = 1; image[6] = 0x40;
        image[16 + 0x6000] = 0x4C;
        image[16 + 0x6002] = 0xE0; // JMP $E000
        image[16 + 0x7FFD] = 0xE0;
        return image;
    }

    private static byte[] Save(Nes nes)
    {
        using MemoryStream stream = new();
        nes.SaveState(stream);
        return stream.ToArray();
    }

    private static bool Reject(Nes nes, byte[] state)
    {
        try { nes.LoadState(new MemoryStream(state)); return false; }
        catch (InvalidDataException) { return true; }
    }
}
