using System.IO.Compression;
using NesEmulator.Core;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Ppu;

internal static class SpriteEvaluationTests
{
    public static void Run(Action<string, bool> check)
    {
        foreach (byte mask in new byte[] { 0, 8, 16, 24 })
        {
            Nes nes = Machine();
            nes.Ppu.WriteRegister(0x2001, mask);
            At(nes.Ppu, 10, 130);
            check($"sprite evaluation mask ${mask:X2}: ninth Y has not been compared before dot 130", !Overflow(nes.Ppu));
            nes.Ppu.Step();
            check($"sprite evaluation mask ${mask:X2}: ninth Y comparison at dot 130", Overflow(nes.Ppu) == (mask != 0));
        }
        {
            Nes nes = Machine();
            // Eight initial sprites, then a false positive from sprite 9's tile.
            nes.Ppu.WriteOam(32, 255);
            nes.Ppu.WriteOam(37, 10);
            At(nes.Ppu, 10, 132);
            check("sprite overflow: diagonal tile comparison waits until dot 132", !Overflow(nes.Ppu));
            nes.Ppu.Step();
            check("sprite overflow: tile byte can cause a false positive", Overflow(nes.Ppu));
        }
        {
            Nes nes = Machine();
            nes.Ppu.WriteOam(32, 255);
            nes.Ppu.WriteOam(36, 10); // Ninth real Y, skipped by the diagonal search.
            At(nes.Ppu, 10, 257);
            check("sprite overflow: diagonal search can miss a ninth real sprite", !Overflow(nes.Ppu));
        }
        {
            Nes nes = Machine();
            nes.Ppu.WriteOam(32, 255);
            nes.Ppu.WriteOam(255, 10);
            At(nes.Ppu, 10, 240);
            check("sprite overflow: late diagonal X comparison has not fired before dot 240", !Overflow(nes.Ppu));
            nes.Ppu.Step();
            check("sprite overflow: last OAM byte can trigger at dot 240", Overflow(nes.Ppu));
        }
        {
            Nes nes = Machine();
            for (int i = 0; i < 64; i++) nes.Ppu.WriteOam((byte)(i * 4), (byte)(i is >= 2 and <= 9 ? 10 : 255));
            nes.Ppu.WriteOam(1, 10);
            At(nes.Ppu, 10, 241);
            check("sprite overflow: search stops when the primary address wraps", !Overflow(nes.Ppu));
            nes.Ppu.Step();
            check("sprite evaluation: exhausted search resumes Y reads, not the diagonal byte", nes.Ppu.ReadRegister(0x2004) == 255);
        }
        foreach (byte y in new byte[] { 239, 240, 255 })
        {
            Nes nes = Machine();
            for (int i = 0; i < 9; i++) nes.Ppu.WriteOam((byte)(i * 4), y);
            At(nes.Ppu, 239, 131);
            check($"sprite overflow: bottom boundary Y={y}", Overflow(nes.Ppu) == (y == 239));
        }
        foreach (byte control in new byte[] { 0, 32 })
        {
            Nes nes = Machine();
            nes.Ppu.WriteRegister(0x2000, control);
            for (int i = 0; i < 9; i++) nes.Ppu.WriteOam((byte)(i * 4), 0);
            At(nes.Ppu, 8, 1);
            // Inspect this line's secondary copy; a flag from an earlier line
            // would hide a height-comparison error here.
            At(nes.Ppu, 8, 67);
            check($"sprite height {(control == 0 ? 8 : 16)}: comparison uses current height",
                Secondary(nes.Ppu)[1] == 255 && Secondary(nes.Ppu)[0] == 0);
            At(nes.Ppu, 8, 69);
            check($"sprite height {(control == 0 ? 8 : 16)}: only in-range Y copies its tile",
                Secondary(nes.Ppu)[1] == (control == 0 ? 255 : 0));
        }
        {
            Nes nes = Machine();
            At(nes.Ppu, 10, 65);
            nes.Ppu.Step(); // Read Y.
            nes.Ppu.WriteOam(0, 200);
            nes.Ppu.Step(); // Compare/copy the already read Y.
            check("sprite evaluation: primary write after Y read cannot change that comparison", Secondary(nes.Ppu)[0] == 10);
            nes.Ppu.WriteOam(1, 7);
            nes.Ppu.Step(); // Read tile.
            nes.Ppu.WriteOam(1, 9);
            nes.Ppu.Step();
            check("sprite evaluation: tile read and secondary write use distinct dots", Secondary(nes.Ppu)[1] == 7);
            At(nes.Ppu, 11, 1);
            byte[] before = Secondary(nes.Ppu);
            nes.Ppu.Step();
            check("secondary OAM: odd clear dot drives FF without overwriting RAM",
                nes.Ppu.ReadRegister(0x2004) == 255 && Secondary(nes.Ppu).SequenceEqual(before));
            nes.Ppu.Step();
            check("secondary OAM: even clear dot replaces one byte", Secondary(nes.Ppu)[0] == 255
                && Secondary(nes.Ppu).AsSpan(1).SequenceEqual(before.AsSpan(1)));
            At(nes.Ppu, 11, 65);
            check("secondary OAM: all 32 bytes are clear after dot 64", Secondary(nes.Ppu).All(b => b == 255));
        }
        {
            Nes nes = Machine();
            for (int i = 1; i < 64; i++) nes.Ppu.WriteOam((byte)(i * 4), 200);
            nes.Ppu.WriteOam(1, 7); nes.Ppu.WriteOam(2, 64); nes.Ppu.WriteOam(3, 12);
            At(nes.Ppu, 10, 257);
            check("secondary OAM: first unused slot retains the last rejected Y",
                Secondary(nes.Ppu).AsSpan(4, 4).SequenceEqual(new byte[] { 200, 255, 255, 255 }));
            byte[] reads = new byte[8];
            for (int i = 0; i < 8; i++) { nes.Ppu.Step(); reads[i] = nes.Ppu.ReadRegister(0x2004); }
            check("OAMDATA: fetch exposes Y, tile, attribute and five X reads",
                reads.SequenceEqual(new byte[] { 10, 7, 64, 12, 12, 12, 12, 12 }));
        }
        {
            Nes nes = Machine();
            At(nes.Ppu, 11, 9);
            check("sprite output: clearing next-line OAM does not erase current pixels",
                nes.Ppu.FrameBuffer.AsSpan(11 * 256, 8).ToArray().All(b => b == 0x21));
            check("sprite output: pre-render evaluation cannot create first-line sprites",
                nes.Ppu.FrameBuffer.AsSpan(0, 256).ToArray().All(b => b == 0x0F));
        }
        {
            Nes nes = Machine();
            nes.Ppu.WriteOam(3, 8);
            At(nes.Ppu, 11, 1);
            nes.Ppu.WriteRegister(0x2001, 0x0A); // Hide sprites, keep rendering running.
            At(nes.Ppu, 11, 9);
            nes.Ppu.WriteRegister(0x2001, 0x14);
            nes.Ppu.Step();
            check("sprite output: X counters run while only backgrounds are shown", nes.Ppu.FrameBuffer[11 * 256 + 8] == 0x21);
        }
        {
            byte[] Frame(bool hideBackground)
            {
                Nes nes = Machine();
                nes.Ppu.WriteRegister(0x2006, 0x3F); nes.Ppu.WriteRegister(0x2006, 1);
                nes.Ppu.WriteRegister(0x2007, 0x12);
                nes.Ppu.WriteRegister(0x2005, 0); nes.Ppu.WriteRegister(0x2005, 0);
                nes.Ppu.WriteRegister(0x2001, hideBackground ? (byte)0x10 : (byte)0x0A);
                At(nes.Ppu, 0, 9);
                nes.Ppu.WriteRegister(0x2001, 0x0A);
                At(nes.Ppu, 0, 33);
                return nes.Ppu.FrameBuffer.AsSpan(8, 24).ToArray();
            }
            byte[] reference = Frame(false);
            check("background output: shifters run while only sprites are shown",
                reference.All(b => b == 0x12) && Frame(true).SequenceEqual(reference));
        }

        foreach (int dot in new[] { 1, 2, 65, 66, 67, 68, 127, 128, 129, 130, 131, 136, 257, 260, 261, 263, 320 })
        {
            Nes nes = Machine();
            At(nes.Ppu, 10, dot);
            byte[] saved = Save(nes);
            for (int i = 0; i < 7; i++) nes.Ppu.Step();
            byte[] expected = Save(nes);
            nes.LoadState(new MemoryStream(saved));
            for (int i = 0; i < 7; i++) nes.Ppu.Step();
            check($"sprite state: replay from next dot {dot}", Save(nes).SequenceEqual(expected));
        }

        foreach (int dot in new[] { 100, 270 })
        {
            Nes nes = Machine();
            using Stream resource = typeof(SpriteEvaluationTests).Assembly.GetManifestResourceStream($"v7-sprites-dot-{dot}.state.gz")!;
            using GZipStream gzip = new(resource, CompressionMode.Decompress);
            nes.LoadState(gzip);
            check($"state v7: sprite fixture at dot {dot} loads", nes.Ppu.Scanline == 10 && nes.Ppu.Cycle == dot);
            At(nes.Ppu, 11, 256);
            check($"state v7: next-line sprites survive migration from dot {dot}",
                nes.Ppu.FrameBuffer.AsSpan(11 * 256, 72).ToArray().Count(b => b == 0x21) == 64);
        }
    }

    private static Nes Machine()
    {
        byte[] image = new byte[16 + 16384];
        "NES\u001a"u8.CopyTo(image); image[4] = 1;
        image[16] = 0x4C; image[18] = 0x80; image[16 + 0x3FFD] = 0x80;
        Nes nes = new(Cartridge.FromBytes(image));
        for (int i = 0; i < 256; i++) nes.Ppu.WriteOam((byte)i, 255);
        for (int i = 0; i < 9; i++)
        {
            nes.Ppu.WriteOam((byte)(i * 4), 10); nes.Ppu.WriteOam((byte)(i * 4 + 1), 0);
            nes.Ppu.WriteOam((byte)(i * 4 + 2), 0); nes.Ppu.WriteOam((byte)(i * 4 + 3), (byte)(i * 8));
        }
        void Address(ushort address) { nes.Ppu.WriteRegister(0x2006, (byte)(address >> 8)); nes.Ppu.WriteRegister(0x2006, (byte)address); }
        Address(0);
        for (int i = 0; i < 8; i++) nes.Ppu.WriteRegister(0x2007, 255);
        Address(0x3F00); nes.Ppu.WriteRegister(0x2007, 0x0F);
        Address(0x3F11); nes.Ppu.WriteRegister(0x2007, 0x21);
        nes.Ppu.WriteRegister(0x2001, 0x14); // Sprites, including the left eight pixels.
        return nes;
    }

    private static void At(Ppu2C02 ppu, int line, int dot) { while (ppu.Scanline != line || ppu.Cycle != dot) ppu.Step(); }
    private static bool Overflow(Ppu2C02 ppu) => (ppu.ReadRegister(0x2002) & 32) != 0;
    private static byte[] Secondary(Ppu2C02 ppu)
    {
        using MemoryStream stream = new();
        ppu.SaveState(new BinaryWriter(stream));
        return stream.ToArray()[^Ppu2C02.SpriteEvaluationStateSize..^9];
    }
    private static byte[] Save(Nes nes) { using MemoryStream stream = new(); nes.SaveState(stream); return stream.ToArray(); }
}
