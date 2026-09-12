using NesEmulator.Core.Apu;

namespace NesEmulator.Core;

/// <summary>Wall-clock frame scheduling when there is no audio device to pace against.</summary>
public sealed class FramePacer
{
    // Average of a full NTSC frame and the one-dot-short odd frame.
    public const double FramesPerSecond = Apu2A03.ClockRate * 3 / (341 * 262 - 0.5);
    private double _lastTime;
    private double _fraction;

    public void Reset(double seconds)
    {
        _lastTime = seconds;
        _fraction = 0;
    }

    public int FramesDue(double seconds)
    {
        double elapsed = Math.Max(0, seconds - _lastTime);
        _lastTime = seconds;
        // Drop a long stall instead of blocking the UI trying to replay it.
        _fraction = Math.Min(2, _fraction + elapsed * FramesPerSecond);
        int due = (int)(_fraction + 1e-9);
        _fraction = Math.Max(0, _fraction - due);
        return due;
    }
}
