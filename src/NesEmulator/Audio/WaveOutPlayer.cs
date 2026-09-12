using System.Runtime.InteropServices;

namespace NesEmulator.Audio;

/// <summary>
/// Plays the sound unit's output through the Windows wave API, which is the one
/// audio path that needs no package and no COM.
///
/// A handful of buffers are prepared once and then cycled: a finished buffer is
/// refilled and queued again. Nothing here polls or sleeps — the sound card
/// finishing a buffer is what frees it, so the number of free buffers is also a
/// clock. The emulator paces itself against that, which keeps the audio
/// continuous even when the frame timer drifts.
/// </summary>
public sealed class WaveOutPlayer : IDisposable
{
    private const int BufferCount = 8;
    private const uint WhdrDone = 0x00000001;
    private const uint WhdrPrepared = 0x00000002;
    private const uint WhdrInQueue = 0x00000010;

    private readonly int _samplesPerBuffer;
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly short[] _staging;

    private IntPtr _device;
    private bool _disposed;

    public WaveOutPlayer(int sampleRate = 44100, int samplesPerBuffer = 735)
    {
        _samplesPerBuffer = samplesPerBuffer;
        _staging = new short[samplesPerBuffer];

        WaveFormatEx format = new()
        {
            FormatTag = 1, // uncompressed
            Channels = 1,
            SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16,
            BlockAlign = 2,
            AverageBytesPerSecond = (uint)(sampleRate * 2),
            Size = 0,
        };

        int result = waveOutOpen(out _device, -1, format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            _device = IntPtr.Zero;
            throw new InvalidOperationException($"No audio output available (waveOut error {result}).");
        }

        int bytes = samplesPerBuffer * 2;
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(bytes);
            _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());

            WaveHdr header = new()
            {
                Data = _buffers[i],
                BufferLength = (uint)bytes,
            };

            Marshal.StructureToPtr(header, _headers[i], false);
            waveOutPrepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHdr>());
        }
    }

    /// <summary>How many buffers the card has finished with and are waiting to be filled.</summary>
    public int FreeBuffers
    {
        get
        {
            int free = 0;
            for (int i = 0; i < BufferCount; i++)
            {
                if (!IsQueued(i))
                {
                    free++;
                }
            }

            return free;
        }
    }

    public int SamplesPerBuffer => _samplesPerBuffer;

    /// <summary>
    /// How many times the card has been left with nothing queued. Every one of
    /// these is a gap in the sound, so this is the number that says whether the
    /// emulator is feeding the card fast enough.
    /// </summary>
    public long Underruns { get; private set; }

    /// <summary>Checks for a dry queue. Call once per pass, before deciding what to do.</summary>
    public void PollUnderrun()
    {
        if (FreeBuffers == BufferCount)
        {
            Underruns++;
        }
    }

    private bool IsQueued(int index)
    {
        WaveHdr header = Marshal.PtrToStructure<WaveHdr>(_headers[index]);
        return (header.Flags & WhdrInQueue) != 0 && (header.Flags & WhdrDone) == 0;
    }

    /// <summary>
    /// Queues one buffer of signed, filtered PCM samples in the -1 to 1 range. Returns
    /// false when the card has nothing free, in which case the caller is ahead and
    /// should simply not produce more.
    /// </summary>
    public bool Submit(float[] samples, int count)
    {
        if (_disposed || _device == IntPtr.Zero)
        {
            return false;
        }

        int index = -1;
        for (int i = 0; i < BufferCount && index < 0; i++)
        {
            if (!IsQueued(i))
            {
                index = i;
            }
        }

        if (index < 0)
        {
            return false;
        }

        int length = Math.Min(count, _samplesPerBuffer);
        for (int i = 0; i < length; i++)
        {
            _staging[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * 28000f);
        }

        // A short last block would otherwise play the previous contents.
        for (int i = length; i < _samplesPerBuffer; i++)
        {
            _staging[i] = 0;
        }

        Marshal.Copy(_staging, 0, _buffers[index], _samplesPerBuffer);

        WaveHdr header = Marshal.PtrToStructure<WaveHdr>(_headers[index]);
        header.BufferLength = (uint)(_samplesPerBuffer * 2);
        header.Flags = WhdrPrepared;
        Marshal.StructureToPtr(header, _headers[index], false);

        return waveOutWrite(_device, _headers[index], Marshal.SizeOf<WaveHdr>()) == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);

            for (int i = 0; i < BufferCount; i++)
            {
                if (_headers[i] != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHdr>());
                }
            }

            waveOutClose(_device);
            _device = IntPtr.Zero;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            if (_headers[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headers[i]);
                _headers[i] = IntPtr.Zero;
            }

            if (_buffers[i] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffers[i]);
                _buffers[i] = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private sealed class WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr device, int deviceId, WaveFormatEx format,
        IntPtr callback, IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
