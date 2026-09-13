namespace NesEmulator.Core.Apu;

/// <summary>
/// The sample channel, and the odd one out: it plays back recorded audio rather
/// than synthesising it. The recording is stored as one bit per sample — each bit
/// says whether the output level should step up or down by two — which fits a
/// drum hit or a grunted word into very little cartridge space at the cost of
/// sounding rough.
///
/// It reads straight out of cartridge memory while the game runs, which is why it
/// takes the processor's bus with it.
/// </summary>
public sealed class DmcChannel
{
    /// <summary>Sixteen playback rates, in processor cycles per bit.</summary>
    private static readonly int[] Rates =
    [
        428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
    ];

    private int _timer;
    private int _timerPeriod = Rates[0];

    private bool _irqEnabled;
    private bool _loop;

    private ushort _sampleAddress = 0xC000;
    private int _sampleLength = 1;
    private ushort _currentAddress;
    private int _bytesRemaining;

    private byte _shiftRegister;
    private int _bitsRemaining;
    private bool _silence = true;
    private byte _sampleBuffer;
    private bool _bufferEmpty = true;
    private int _dmaDelay = -1;
    private bool _getCycle = true;

    /// <summary>Optional reader for standalone channel tests. The console uses DMA instead.</summary>
    public Func<ushort, byte>? ReadMemory { get; set; }

    public int OutputLevel { get; private set; }

    public bool IrqPending { get; private set; }

    public bool Active => _bytesRemaining > 0;

    internal bool DmaPending => Active && _bufferEmpty && _dmaDelay is >= 0 and <= 1;
    internal bool NextCycleIsGet => !_getCycle;
    internal ushort DmaAddress => _currentAddress;

    public void WriteControl(byte value)
    {
        _irqEnabled = (value & 0x80) != 0;
        _loop = (value & 0x40) != 0;
        _timerPeriod = Rates[value & 0x0F];

        if (!_irqEnabled)
        {
            IrqPending = false;
        }
    }

    /// <summary>Sets the level directly, which games also use for crude sample playback.</summary>
    public void WriteDirectLoad(byte value) => OutputLevel = value & 0x7F;

    public void WriteAddress(byte value) => _sampleAddress = (ushort)(0xC000 + (value * 64));

    public void WriteLength(byte value) => _sampleLength = (value * 16) + 1;

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _bytesRemaining = 0;
            _dmaDelay = -1;
            return;
        }

        if (_bytesRemaining == 0)
        {
            Restart();
            if (_bufferEmpty) _dmaDelay = _getCycle ? 4 : 3;
        }
    }

    public void ClearIrq() => IrqPending = false;

    private void Restart()
    {
        _currentAddress = _sampleAddress;
        _bytesRemaining = _sampleLength;
    }

    public void Clock()
    {
        _getCycle = !_getCycle;
        if (_dmaDelay > 0) _dmaDelay--;
        if (_dmaDelay == 0 && ReadMemory is not null && Active && _bufferEmpty)
            CompleteDma(ReadMemory(_currentAddress));

        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = _timerPeriod - 1;

        if (!_silence)
        {
            // Each bit nudges the level by two, and the level cannot leave 0 to 127.
            if ((_shiftRegister & 1) != 0)
            {
                if (OutputLevel <= 125)
                {
                    OutputLevel += 2;
                }
            }
            else if (OutputLevel >= 2)
            {
                OutputLevel -= 2;
            }
        }

        _shiftRegister >>= 1;
        _bitsRemaining--;

        if (_bitsRemaining > 0)
        {
            return;
        }

        _bitsRemaining = 8;
        _silence = _bufferEmpty;
        if (!_bufferEmpty)
        {
            _shiftRegister = _sampleBuffer;
            _bufferEmpty = true;
            // Reload DMAs attempt to halt on the next PUT cycle.
            if (Active) _dmaDelay = _getCycle ? 1 : 2;
        }
    }

    internal void CompleteDma(byte value)
    {
        _sampleBuffer = value;
        _bufferEmpty = false;
        _dmaDelay = -1;

        // Sample memory is the top half of the address space and wraps within it.
        _currentAddress = _currentAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(_currentAddress + 1);
        _bytesRemaining--;
        // Finish the memory reader when its last byte is fetched. Waiting until
        // the output requests another byte inserts eight silent bits per loop
        // and can incorrectly restart a channel disabled through $4015.
        if (_bytesRemaining == 0)
        {
            if (_loop) Restart();
            else if (_irqEnabled) IrqPending = true;
        }
    }

    public int Output() => OutputLevel;

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_timer);
        writer.Write(_timerPeriod);
        writer.Write(_irqEnabled);
        writer.Write(_loop);
        writer.Write(_sampleAddress);
        writer.Write(_sampleLength);
        writer.Write(_currentAddress);
        writer.Write(_bytesRemaining);
        writer.Write(_shiftRegister);
        writer.Write(_bitsRemaining);
        writer.Write(_silence);
        writer.Write(OutputLevel);
        writer.Write(IrqPending);
        writer.Write(_sampleBuffer);
        writer.Write(_bufferEmpty);
        writer.Write(_dmaDelay);
        writer.Write(_getCycle);
    }

    internal void LoadState(BinaryReader reader, bool legacy = false)
    {
        _timer = reader.ReadInt32();
        _timerPeriod = reader.ReadInt32();
        _irqEnabled = reader.ReadBoolean();
        _loop = reader.ReadBoolean();
        _sampleAddress = reader.ReadUInt16();
        _sampleLength = reader.ReadInt32();
        _currentAddress = reader.ReadUInt16();
        _bytesRemaining = reader.ReadInt32();
        _shiftRegister = reader.ReadByte();
        _bitsRemaining = reader.ReadInt32();
        _silence = reader.ReadBoolean();
        OutputLevel = reader.ReadInt32();
        IrqPending = reader.ReadBoolean();
        _sampleBuffer = legacy ? (byte)0 : reader.ReadByte();
        _bufferEmpty = legacy || reader.ReadBoolean();
        _dmaDelay = legacy ? -1 : reader.ReadInt32();
        _getCycle = legacy || reader.ReadBoolean();
    }

    internal void RestoreLegacyDmaPhase(long cycle)
    {
        _getCycle = (cycle & 1) == 0;
        if (Active) _dmaDelay = _getCycle ? 1 : 2;
    }
}
