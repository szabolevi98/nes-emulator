using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Controls;

/// <summary>
/// Shows what the picture unit produced. The frame arrives as one palette index
/// per pixel, which is turned into colour here rather than in the emulator, so
/// the core stays free of any drawing library.
///
/// Scaling is nearest neighbour and, where it fits, a whole number of pixels per
/// pixel: anything smoother turns the art into mush, because these tiles were
/// drawn for a screen where one pixel was one pixel.
/// </summary>
public sealed class ScreenControl : Control
{
    private readonly Bitmap _bitmap = new(
        Ppu2C02.ScreenWidth,
        Ppu2C02.ScreenHeight,
        PixelFormat.Format32bppRgb);

    private readonly int[] _pixels = new int[Ppu2C02.ScreenWidth * Ppu2C02.ScreenHeight];

    /// <summary>
    /// How much a television of the period hid behind its bezel. Games counted on
    /// it: the edges of the picture are where a scrolling game's half-written tile
    /// column and its sprites appearing out of nowhere were meant to be invisible.
    /// </summary>
    private const int Overscan = 8;

    private bool _cropOverscan;

    /// <summary>
    /// Whether to show only what a television would have shown. Off by default,
    /// because the emulator's job is to say what the console produced; this is a
    /// display choice, and it throws real pixels away.
    /// </summary>
    [DefaultValue(false)]
    public bool CropOverscan
    {
        get => _cropOverscan;
        set
        {
            if (_cropOverscan == value)
            {
                return;
            }

            _cropOverscan = value;
            Invalidate();
        }
    }

    private Rectangle VisibleArea => _cropOverscan
        ? new Rectangle(Overscan, Overscan, Ppu2C02.ScreenWidth - (2 * Overscan), Ppu2C02.ScreenHeight - (2 * Overscan))
        : new Rectangle(0, 0, Ppu2C02.ScreenWidth, Ppu2C02.ScreenHeight);

    public ScreenControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        BackColor = Color.Black;
    }

    /// <summary>Converts a frame of palette indices and asks for a repaint.</summary>
    public void Present(ushort[] frame)
    {
        for (int i = 0; i < _pixels.Length; i++)
        {
            // Colour and emphasis travel together in each entry.
            _pixels[i] = NesPalette.Emphasized[frame[i] & 0x1FF];
        }

        BitmapData data = _bitmap.LockBits(
            new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.Clear(BackColor);

        Rectangle source = VisibleArea;
        Rectangle target = FitInside(ClientSize);
        if (target.Width > 0 && target.Height > 0)
        {
            e.Graphics.DrawImage(_bitmap, target, source, GraphicsUnit.Pixel);
        }
    }

    /// <summary>Largest centred rectangle of the right shape that fits the control.</summary>
    private Rectangle FitInside(Size available)
    {
        if (available.Width <= 0 || available.Height <= 0)
        {
            return Rectangle.Empty;
        }

        Rectangle visible = VisibleArea;
        int scale = Math.Min(
            available.Width / visible.Width,
            available.Height / visible.Height);

        int width;
        int height;

        if (scale >= 1)
        {
            width = visible.Width * scale;
            height = visible.Height * scale;
        }
        else
        {
            // Smaller than one to one: keep the shape and accept the resampling.
            double factor = Math.Min(
                (double)available.Width / visible.Width,
                (double)available.Height / visible.Height);
            width = (int)(visible.Width * factor);
            height = (int)(visible.Height * factor);
        }

        return new Rectangle(
            (available.Width - width) / 2,
            (available.Height - height) / 2,
            width,
            height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bitmap.Dispose();
        }

        base.Dispose(disposing);
    }
}
