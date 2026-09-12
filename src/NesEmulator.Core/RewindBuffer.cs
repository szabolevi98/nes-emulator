using System.IO.Compression;

namespace NesEmulator.Core;

/// <summary>
/// A ring of recent save states, so the last stretch of play can be wound back.
///
/// Snapshots are taken every few frames rather than every frame: a console state
/// is around seventy kilobytes, most of it the finished picture, and keeping one
/// per frame would cost far more memory than the feature is worth. They are
/// deflated on the way in, which takes them to a few kilobytes each, because a
/// console's memory is mostly repeated bytes and long runs of zero.
///
/// Winding back is therefore stepwise: each press moves back one snapshot, not
/// one frame. At the default settings that is a sixth of a second at a time over
/// roughly the last minute.
/// </summary>
public sealed class RewindBuffer(Nes nes, int capacity = 360, int framesBetween = 10)
{
    private readonly Nes _nes = nes;
    private readonly byte[]?[] _snapshots = new byte[capacity][];
    private readonly MemoryStream _scratch = new();

    private int _next;
    private int _framesSinceCapture;

    public int Count { get; private set; }

    public int Capacity => capacity;

    /// <summary>Total bytes the ring is holding, after compression.</summary>
    public long BytesHeld
    {
        get
        {
            long total = 0;
            foreach (byte[]? snapshot in _snapshots)
            {
                total += snapshot?.Length ?? 0;
            }

            return total;
        }
    }

    /// <summary>
    /// Offer a frame to the ring. Only every <c>framesBetween</c>-th one is kept,
    /// so this can be called after every frame without thinking about it.
    /// </summary>
    public void OnFrame()
    {
        if (_framesSinceCapture > 0 && _framesSinceCapture < framesBetween)
        {
            _framesSinceCapture++;
            return;
        }

        _framesSinceCapture = 1;
        Capture();
    }

    public void Capture()
    {
        _scratch.SetLength(0);
        _nes.SaveState(_scratch);

        MemoryStream packed = new();
        using (DeflateStream deflate = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            _scratch.Position = 0;
            _scratch.CopyTo(deflate);
        }

        _snapshots[_next] = packed.ToArray();
        _next = (_next + 1) % capacity;
        Count = Math.Min(Count + 1, capacity);
    }

    /// <summary>
    /// Restores the most recent snapshot and drops it, so repeated calls walk
    /// backwards. Returns false once there is nothing left to wind back to.
    /// </summary>
    public bool StepBack()
    {
        if (Count == 0)
        {
            return false;
        }

        _next = (_next - 1 + capacity) % capacity;
        byte[]? packed = _snapshots[_next];
        _snapshots[_next] = null;
        Count--;

        if (packed is null)
        {
            return false;
        }

        _scratch.SetLength(0);
        using (DeflateStream inflate = new(new MemoryStream(packed), CompressionMode.Decompress))
        {
            inflate.CopyTo(_scratch);
        }

        _scratch.Position = 0;
        _nes.LoadState(_scratch);

        // The frame just restored should not immediately be captured again.
        _framesSinceCapture = 1;
        return true;
    }

    public void Clear()
    {
        Array.Clear(_snapshots);
        _next = 0;
        Count = 0;
        _framesSinceCapture = 0;
    }
}
