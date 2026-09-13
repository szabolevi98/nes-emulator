using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Memory;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Core;

/// <summary>
/// One console: a cartridge, the board logic inside it, the address space they
/// share, the processor, the picture unit and the sound unit.
///
/// The chips run off the same clock at fixed ratios — three picture cycles and
/// one sound cycle to every processor cycle. The processor spends its cycles one
/// at a time and hands each to <see cref="Tick"/> as it goes, so the three run
/// alongside each other through an instruction rather than one after the other.
/// A game that reads a picture register partway through an instruction therefore
/// sees what the hardware would have shown it.
/// </summary>
public sealed class Nes
{
    public Nes(Cartridge cartridge, int sampleRate = 44100, Mmc3IrqRevision mmc3Revision = Mmc3IrqRevision.Standard,
        AbOpcodeProfile abProfile = AbOpcodeProfile.MaskEE)
    {
        Cartridge = cartridge;
        Mapper = IMapper.Create(cartridge, mmc3Revision);
        Ppu = new Ppu2C02(Mapper);
        Apu = new Apu2A03(sampleRate);
        Bus = new NesBus(Mapper, Ppu, Apu, Port1, Port2);
        Cpu = new Cpu6502(Bus, abProfile);

        // Both DMA units can hold a CPU read, but never a CPU write.
        Cpu.BeforeRead = address => Bus.RunDma(Cpu, address);

        // The processor drives the clock for everything else.
        Cpu.OnCycle = Tick;
        Cpu.OnCycleComplete = CompleteTick;
        Cpu.NmiInput = () => Ppu.NmiLine;

        Cpu.Reset();
    }

    public Cartridge Cartridge { get; }

    public IMapper Mapper { get; }

    public NesBus Bus { get; }

    public Cpu6502 Cpu { get; }

    public Ppu2C02 Ppu { get; }

    public Apu2A03 Apu { get; }

    public Controller Port1 { get; } = new();

    public Controller Port2 { get; } = new();

    public static Nes FromFile(string path, int sampleRate = 44100, Mmc3IrqRevision mmc3Revision = Mmc3IrqRevision.Standard,
        AbOpcodeProfile abProfile = AbOpcodeProfile.MaskEE) =>
        new(Cartridges.Cartridge.FromFile(path), sampleRate, mmc3Revision, abProfile);

    public void Reset()
    {
        // Reset takes priority over a DMA request waiting at the next CPU read.
        Apu.WriteRegister(0x4015, 0);
        Apu.Dmc.CancelDma();
        Bus.CancelDma();
        Cpu.Reset();
        Ppu.Reset();
        Apu.Reset();
    }

    // ----------------------------------------------------------- save states

    private const uint StateMagic = 0x53454E0F; // "NES" and a format version
    private const uint WriteFilterStateMagic = 0x53454E0E;
    private const uint EmphasisStateMagic = 0x53454E0D;
    private const uint BusStateMagic = 0x53454E0C;
    private const uint PortStateMagic = 0x53454E0B;
    private const uint AbStateMagic = 0x53454E0A;
    private const uint StopStateMagic = 0x53454E09;
    private const uint SpriteStateMagic = 0x53454E08;
    private const uint M2StateMagic = 0x53454E07;
    private const uint IrqStateMagic = 0x53454E06;
    private const uint DmcStateMagic = 0x53454E05;
    private const uint ApuStateMagic = 0x53454E04;
    private const uint NmiStateMagic = 0x53454E03;
    private const uint LegacyStateMagic = 0x53454E02;

    /// <summary>
    /// Writes everything that makes this console what it is at this instant. The
    /// cartridge ROM is not part of it — only the memory that can change, which is
    /// what keeps a state small enough to take one many times a second for rewind.
    /// </summary>
    public void SaveState(Stream stream)
    {
        BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(StateMagic);
        writer.Write(Cartridge.MapperNumber);
        writer.Write(Cartridge.Identity.Span);
        writer.Write((byte)StateIrqRevision);
        writer.Write((byte)Cpu.AbProfile);

        using MemoryStream payload = new();
        WriteStatePayload(new BinaryWriter(payload));
        byte[] data = payload.ToArray();
        writer.Write(data.Length);
        writer.Write(System.Security.Cryptography.SHA256.HashData(data));
        writer.Write(data);
    }

    private void WriteStatePayload(BinaryWriter writer)
    {
        Cpu.SaveState(writer);
        Ppu.SaveState(writer);
        Apu.SaveState(writer);
        Bus.SaveState(writer);
        Mapper.SaveState(writer);
        Port1.SaveState(writer);
        Port2.SaveState(writer);

        // Tile memory is only worth keeping when the cartridge can write to it.
        if (Cartridge.ChrIsRam)
        {
            writer.Write(Cartridge.Chr);
        }
    }

    public void LoadState(Stream stream)
    {
        BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        uint magic = reader.ReadUInt32();
        bool legacy = magic == LegacyStateMagic;
        bool legacyApu = magic < ApuStateMagic;
        bool legacyDmc = magic < DmcStateMagic;
        if (magic != StateMagic && magic != WriteFilterStateMagic && magic != EmphasisStateMagic && magic != BusStateMagic && magic != PortStateMagic && magic != AbStateMagic && magic != StopStateMagic && magic != SpriteStateMagic && magic != M2StateMagic && magic != IrqStateMagic && magic != DmcStateMagic && magic != ApuStateMagic && magic != NmiStateMagic && !legacy)
        {
            throw new InvalidDataException("Unsupported save state format. A v2 through v15 state is required.");
        }

        if (reader.ReadInt32() != Cartridge.MapperNumber)
        {
            throw new InvalidDataException("This save state belongs to a different cartridge.");
        }

        byte[] identity = reader.ReadBytes(32);
        if (!identity.AsSpan().SequenceEqual(Cartridge.Identity.Span))
            throw new InvalidDataException("This save state belongs to a different ROM image.");

        // v2-v5 were written with the standard MMC3 behavior. Check configuration
        // before mutating anything, just as for the cartridge identity.
        Mmc3IrqRevision revision = magic >= IrqStateMagic
            ? (Mmc3IrqRevision)reader.ReadByte() : Mmc3IrqRevision.Standard;
        if (revision != StateIrqRevision)
            throw new InvalidDataException("This save state uses a different MMC3 IRQ revision. Select that revision before loading it.");

        AbOpcodeProfile abProfile = magic >= AbStateMagic ? (AbOpcodeProfile)reader.ReadByte() : AbOpcodeProfile.MaskEE;
        if (abProfile != Cpu.AbProfile)
            throw new InvalidDataException("This save state uses a different $AB opcode profile. Select that profile before loading it.");

        // Read and verify the complete payload before touching live console state.
        using MemoryStream current = new();
        WriteStatePayload(new BinaryWriter(current));
        int length = reader.ReadInt32();
        // v3 adds the CPU's sampled NMI line and the PPU's previous-dot render
        // latch. The old PPU pending-event byte becomes its suppression latch.
        // v4 also saves the pending APU frame-counter reset delay.
        // v5 adds the DMC buffer, DMA request delay and GET/PUT phase (7 bytes).
        bool legacyFilter = magic < M2StateMagic;
        bool legacySprites = magic < SpriteStateMagic;
        bool legacyStop = magic < StopStateMagic;
        // v11 adds the controller port's /OE address and its presented value.
        bool legacyPort = magic < PortStateMagic;
        // v12 adds a decay stamp for each of the picture unit's data lines and
        // the 2A03's own data bus.
        bool legacyBusDecay = magic < BusStateMagic;
        // v13 widens each pixel to carry the emphasis bits alongside its colour.
        bool legacyEmphasis = magic < EmphasisStateMagic;
        // v14 adds the MMC1 serial port's consecutive-write filter.
        bool legacyWriteFilter = magic < WriteFilterStateMagic;
        // v15 adds the MMC3 save RAM enable and write-protect bits.
        bool legacyRamGuard = magic < StateMagic;
        // v7 adds one filter byte only for MMC3 cartridges.
        if (length != current.Length - (legacy ? 2 : 0) - (legacyApu ? 4 : 0) - (legacyDmc ? 7 : 0)
            - (legacyFilter && Mapper is Mmc3 ? 1 : 0) - (legacySprites ? Ppu2C02.SpriteEvaluationStateSize : 0) - (legacyStop ? 5 : 0)
            - (legacyPort ? 3 : 0) - (legacyBusDecay ? 65 : 0) - (legacyEmphasis ? Ppu2C02.ScreenWidth * Ppu2C02.ScreenHeight : 0)
            - (legacyWriteFilter && Mapper is Mmc1 ? 2 : 0) - (legacyRamGuard && Mapper is Mmc3 ? 2 : 0))
            throw new InvalidDataException("The save state has an incompatible size.");
        byte[] checksum = reader.ReadBytes(32);
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length || !checksum.AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(data)))
            throw new InvalidDataException("The save state is incomplete or damaged.");

        ReadStatePayload(new BinaryReader(new MemoryStream(data)), legacy, legacyApu, legacyDmc, legacyFilter, legacySprites, legacyStop, legacyPort, legacyBusDecay, legacyEmphasis, legacyWriteFilter, legacyRamGuard);
    }

    private Mmc3IrqRevision StateIrqRevision => Mapper is Mmc3 mmc3
        ? mmc3.IrqRevision : Mmc3IrqRevision.Standard;

    private void ReadStatePayload(BinaryReader reader, bool legacy, bool legacyApu, bool legacyDmc, bool legacyFilter, bool legacySprites, bool legacyStop, bool legacyPort, bool legacyBusDecay, bool legacyEmphasis, bool legacyWriteFilter, bool legacyRamGuard)
    {
        Cpu.LoadState(reader, legacy);
        bool legacyPpuNmiPending = Ppu.LoadState(reader, legacy, legacySprites, legacyBusDecay, legacyEmphasis);
        Apu.LoadState(reader, legacyApu, legacyDmc, legacyStop);
        Bus.LoadState(reader, legacyPort, legacyBusDecay);
        if (Mapper is Mmc3 mmc3) mmc3.LoadState(reader, legacyFilter, Ppu.Clock, legacyRamGuard);
        else if (Mapper is Mmc1 mmc1) mmc1.LoadState(reader, legacyWriteFilter);
        else Mapper.LoadState(reader);
        Port1.LoadState(reader);
        Port2.LoadState(reader);

        if (Cartridge.ChrIsRam)
        {
            reader.ReadExactly(Cartridge.Chr);
        }
        if (legacy) Cpu.RestoreLegacyNmiInput(Ppu.NmiLine, legacyPpuNmiPending);
    }

    /// <summary>
    /// Begins a CPU bus cycle: clock the APU and the first two PPU dots. The
    /// remaining dot is clocked by CompleteTick after the read or write, keeping
    /// register accesses and interrupt sampling on distinct phases.
    /// </summary>
    private void Tick()
    {
        Apu.Step();

        // This NTSC clock alignment places the register access before the last
        // PPU dot of the CPU cycle. NMI is sampled after that final dot.
        for (int dot = 0; dot < 2; dot++)
        {
            Ppu.Step();
        }
    }

    private void CompleteTick()
    {
        Ppu.Step();
        // The ports load while the strobe is high, on the get-to-put transition.
        if (Apu.Dmc.OnGetCycle)
        {
            Port1.SampleStrobe();
            Port2.SampleStrobe();
        }

        // Selected NTSC alignment: M2 falls after this cycle's final PPU dot,
        // before interrupt sampling. This path also runs during DMA and reset.
        Mapper.OnM2FallingEdge();
        Cpu.SetIrqLine(Apu.IrqPending || Mapper.IrqPending);
    }

    /// <summary>Runs a single instruction, with the other chips running alongside it.</summary>
    public int StepInstruction()
    {
        int cycles = Cpu.Step();

        // DMA cycles are spent at the read they halt, inside Cpu.Step().

        // Both the sound unit and some cartridge boards hold the maskable
        // interrupt line down until the game acknowledges them, so it is a level
        // rather than an event.
        Cpu.SetIrqLine(Apu.IrqPending || Mapper.IrqPending);

        return cycles;
    }

    /// <summary>
    /// Runs until the beam reaches the bottom of the screen, leaving a finished
    /// picture in <see cref="Ppu2C02.FrameBuffer"/> and a frame of audio in the
    /// sound unit's buffer.
    /// </summary>
    public void RunFrame()
    {
        Ppu.FrameComplete = false;

        while (!Ppu.FrameComplete)
        {
            StepInstruction();
        }
    }
}
