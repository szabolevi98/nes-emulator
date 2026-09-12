using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace NesEmulator;

/// <summary>Product details, authorship and project links.</summary>
public sealed class AboutForm : Form
{
    private readonly Bitmap _iconImage;
    private readonly Font _titleFont = new("Segoe UI", 20f, FontStyle.Regular);
    private readonly Font _uiFont = new("Segoe UI", 9f);

    public AboutForm()
    {
        Text = "About NES Emulator";
        Font = _uiFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.FromArgb(228, 228, 231);

        using (Stream stream = typeof(AboutForm).Assembly.GetManifestResourceStream("NesEmulator.app.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing."))
        {
            using Icon icon = new(stream, 64, 64);
            _iconImage = icon.ToBitmap();
        }

        PictureBox iconBox = new()
        {
            Location = new Point(28, 26), Size = new Size(64, 64),
            Image = _iconImage, SizeMode = PictureBoxSizeMode.Zoom, TabStop = false,
        };
        Label title = MakeLabel("NES Emulator", 108, 26);
        title.Font = _titleFont;
        title.ForeColor = ForeColor;
        Label version = MakeLabel($"Version {ReadVersion()}", 110, 67);
        Label description = MakeLabel("An 8-bit NES emulator with sound, save states, rewind and a built-in debugger.", 28, 108);
        description.AutoSize = false;
        description.Size = new Size(444, 42);

        Panel separator = new()
        {
            Location = new Point(28, 163), Size = new Size(444, 1),
            BackColor = Color.FromArgb(63, 63, 70),
        };
        string copyright = typeof(AboutForm).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright (c) 2026 szabolevi98";
        Label copyrightLabel = MakeLabel(copyright.Replace("(c)", "©"), 28, 181);
        Label license = MakeLabel("MIT License", 28, 204);
        LinkLabel profile = MakeLink("https://github.com/szabolevi98", 28, 230, 0);
        LinkLabel repository = MakeLink("Source code on GitHub", 28, 253, 1,
            "https://github.com/szabolevi98/nes-emulator");

        Button close = new()
        {
            Text = "Close", DialogResult = DialogResult.OK,
            Location = new Point(388, 247), Size = new Size(84, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(32, 32, 36),
            ForeColor = ForeColor, TabIndex = 2,
        };
        close.FlatAppearance.BorderColor = separator.BackColor;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 54);
        AcceptButton = close;
        CancelButton = close;
        Controls.AddRange([iconBox, title, version, description, separator, copyrightLabel, license, profile, repository, close]);
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, Location = new Point(x, y), AutoSize = true,
        ForeColor = Color.FromArgb(148, 148, 158),
    };

    private LinkLabel MakeLink(string text, int x, int y, int tabIndex, string? url = null)
    {
        Color accent = Color.FromArgb(45, 212, 191);
        LinkLabel link = new()
        {
            Text = text, Location = new Point(x, y), AutoSize = true,
            LinkColor = accent, VisitedLinkColor = accent,
            ActiveLinkColor = Color.FromArgb(96, 165, 250),
            LinkBehavior = LinkBehavior.HoverUnderline, TabIndex = tabIndex,
        };
        string target = url ?? text;
        link.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, $"Could not open the browser.\n\n{target}",
                    "Open GitHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        return link;
    }

    private static string ReadVersion()
    {
        Assembly assembly = typeof(AboutForm).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return !string.IsNullOrWhiteSpace(informational)
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _iconImage.Dispose();
            _titleFont.Dispose();
            _uiFont.Dispose();
        }
    }
}
