using NesEmulator.Core.Input;

namespace NesEmulator;

/// <summary>Keyboard reference built from the bindings the application actually uses.</summary>
public sealed class ControlsForm : Form
{
    private readonly Font _uiFont = new("Segoe UI", 9f);
    private readonly Font _titleFont = new("Segoe UI", 20f);
    private readonly Font _headingFont = new("Segoe UI", 10f, FontStyle.Bold);
    private static readonly Color Muted = Color.FromArgb(148, 148, 158);
    private static readonly Color Accent = Color.FromArgb(45, 212, 191);

    public ControlsForm(IReadOnlyDictionary<Keys, NesButton> playerOne,
        IReadOnlyDictionary<Keys, NesButton> playerTwo, MenuStrip menu)
    {
        Text = "Controls — NES Emulator";
        Font = _uiFont;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 430);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.FromArgb(228, 228, 231);

        AddLabel("Controls", 28, 20, _titleFont);
        AddLabel("Keyboard controls for both players and the emulator.", 30, 65, color: Muted);
        AddLabel("Controllers", 28, 104, _headingFont);
        AddLabel("Emulator shortcuts", 370, 104, _headingFont);

        AddLabel("NES button", 28, 138, color: Accent);
        AddLabel("Player 1", 140, 138, color: Accent);
        AddLabel("Player 2", 236, 138, color: Accent);
        NesButton[] buttons = [NesButton.Up, NesButton.Down, NesButton.Left, NesButton.Right,
            NesButton.A, NesButton.B, NesButton.Start, NesButton.Select];
        for (int i = 0; i < buttons.Length; i++)
        {
            int y = 166 + i * 24;
            AddLabel(buttons[i].ToString(), 28, y, color: Muted);
            AddLabel(ControllerKey(playerOne, buttons[i]), 140, y);
            AddLabel(ControllerKey(playerTwo, buttons[i]), 236, y);
        }

        AddLabel("Key", 370, 138, color: Accent);
        AddLabel("Action", 520, 138, color: Accent);
        int shortcutRow = 0;
        foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>()
            .SelectMany(section => section.DropDownItems.OfType<ToolStripMenuItem>())
            .Where(item => item.ShortcutKeys != Keys.None || !string.IsNullOrEmpty(item.ShortcutKeyDisplayString)))
        {
            int y = 166 + shortcutRow++ * 24;
            string action = item.ShortcutKeys == Keys.F5
                ? "Pause / resume"
                : (item.Text ?? string.Empty).Replace("&", string.Empty).Replace("...", string.Empty);
            AddLabel(string.IsNullOrEmpty(item.ShortcutKeyDisplayString)
                ? FormatKey(item.ShortcutKeys) : item.ShortcutKeyDisplayString, 370, y);
            AddLabel(action, 520, y, color: Muted);
        }
        AddLabel("Backspace (hold)", 370, 166 + shortcutRow * 24);
        AddLabel("Rewind", 520, 166 + shortcutRow * 24, color: Muted);

        Panel divider = new()
        {
            Location = new Point(338, 106), Size = new Size(1, 247),
            BackColor = Color.FromArgb(63, 63, 70),
        };
        Button close = new()
        {
            Text = "Close", DialogResult = DialogResult.OK,
            Location = new Point(608, 370), Size = new Size(84, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(32, 32, 36),
            ForeColor = ForeColor, TabIndex = 0,
        };
        close.FlatAppearance.BorderColor = divider.BackColor;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 54);
        AcceptButton = close;
        CancelButton = close;
        Controls.AddRange([divider, close]);
    }

    private void AddLabel(string text, int x, int y, Font? font = null, Color? color = null)
    {
        Controls.Add(new Label
        {
            Text = text, Location = new Point(x, y), AutoSize = true,
            Font = font ?? _uiFont, ForeColor = color ?? ForeColor,
        });
    }

    private static string ControllerKey(IReadOnlyDictionary<Keys, NesButton> bindings, NesButton button) =>
        FormatKey(bindings.First(binding => binding.Value == button).Key);

    private static string FormatKey(Keys key) => key switch
    {
        Keys.ShiftKey => "Shift",
        Keys.Enter => "Enter",
        Keys.Up => "↑",
        Keys.Down => "↓",
        Keys.Left => "←",
        Keys.Right => "→",
        _ => new KeysConverter().ConvertToInvariantString(key)?.Replace("Control", "Ctrl") ?? key.ToString(),
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _headingFont.Dispose();
            _titleFont.Dispose();
            _uiFont.Dispose();
        }
    }
}
