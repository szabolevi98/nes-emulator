using NesEmulator.Core;
using NesEmulator.Core.Cpu;

namespace NesEmulator;

/// <summary>
/// The debugger shell. There is no picture unit yet, so what the emulator can
/// show is what it actually knows: the cartridge header, the processor state and
/// a running trace of the instructions it is executing. Every one of those stays
/// useful once the video output arrives, because this is the window you keep open
/// when a game misbehaves.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 24, 27);
    private static readonly Color Surface = Color.FromArgb(32, 32, 36);
    private static readonly Color Foreground = Color.FromArgb(228, 228, 231);
    private static readonly Color Muted = Color.FromArgb(148, 148, 158);
    private static readonly Color Accent = Color.FromArgb(96, 165, 250);

    private readonly Label _summary;
    private readonly TextBox _trace;
    private readonly Label _registers;
    private readonly Button _stepButton;
    private readonly Button _runButton;
    private readonly Button _resetButton;

    private Nes? _nes;

    public MainForm(string? romPath)
    {
        Text = "NES Emulator";
        BackColor = Background;
        ForeColor = Foreground;
        MinimumSize = new Size(720, 480);
        Size = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;

        MenuStrip menu = new()
        {
            BackColor = Surface,
            ForeColor = Foreground,
            Renderer = new ToolStripProfessionalRenderer(),
        };

        ToolStripMenuItem file = new("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open ROM...", null, (_, _) => OpenRom())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        menu.Items.Add(file);

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 6, 10, 0),
            ForeColor = Muted,
            Text = "No cartridge loaded.",
        };

        _registers = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 4, 10, 0),
            ForeColor = Accent,
            Font = new Font(FontFamily.GenericMonospace, 9.75f),
            Text = string.Empty,
        };

        _trace = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Surface,
            ForeColor = Foreground,
            BorderStyle = BorderStyle.None,
            Font = new Font(FontFamily.GenericMonospace, 9.75f),
        };

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Background,
        };

        _stepButton = MakeButton("Step", () => Run(1));
        _runButton = MakeButton("Run 1000", () => Run(1000));
        _resetButton = MakeButton("Reset", () =>
        {
            _nes?.Reset();
            _trace.Clear();
            RefreshState();
        });

        buttons.Controls.AddRange([_stepButton, _runButton, _resetButton]);

        Controls.Add(_trace);
        Controls.Add(buttons);
        Controls.Add(_registers);
        Controls.Add(_summary);
        Controls.Add(menu);
        MainMenuStrip = menu;

        SetEnabled(false);

        if (romPath is not null && File.Exists(romPath))
        {
            LoadRom(romPath);
        }
    }

    private Button MakeButton(string text, Action action)
    {
        Button button = new()
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Foreground,
            Padding = new Padding(10, 4, 10, 4),
        };

        button.FlatAppearance.BorderColor = Color.FromArgb(63, 63, 70);
        button.Click += (_, _) => action();
        return button;
    }

    private void OpenRom()
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "NES ROM images (*.nes)|*.nes|All files (*.*)|*.*",
            Title = "Open a cartridge image",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadRom(dialog.FileName);
        }
    }

    private void LoadRom(string path)
    {
        try
        {
            _nes = Nes.FromFile(path);
            _trace.Clear();
            _summary.ForeColor = Muted;
            _summary.Text = $"{Path.GetFileName(path)} — {_nes.Cartridge}";
            Text = $"{Path.GetFileName(path)} — NES Emulator";
            SetEnabled(true);
            RefreshState();
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException)
        {
            _nes = null;
            SetEnabled(false);
            _summary.ForeColor = Color.FromArgb(248, 113, 113);
            _summary.Text = exception.Message;
            _registers.Text = string.Empty;
            _trace.Clear();
        }
    }

    private void Run(int instructions)
    {
        if (_nes is null)
        {
            return;
        }

        // Keep the trace bounded; a thousand lines is plenty to see a loop.
        List<string> lines = new(instructions);
        for (int i = 0; i < instructions && !_nes.Cpu.Jammed; i++)
        {
            lines.Add(Disassembler.TraceLine(_nes.Cpu, _nes.Bus));
            _nes.StepInstruction();
        }

        _trace.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        RefreshState();
    }

    private void RefreshState()
    {
        if (_nes is null)
        {
            _registers.Text = string.Empty;
            return;
        }

        Cpu6502 cpu = _nes.Cpu;
        _registers.Text =
            $"PC:{cpu.PC:X4}  A:{cpu.A:X2}  X:{cpu.X:X2}  Y:{cpu.Y:X2}  " +
            $"P:{cpu.P:X2} [{Flags(cpu.P)}]  SP:{cpu.S:X2}  CYC:{cpu.Cycles:N0}" +
            (cpu.Jammed ? "  — JAMMED" : string.Empty);
    }

    private static string Flags(byte p)
    {
        // Upper case where the flag is set, lower case where it is clear.
        const string names = "NV-BDIZC";
        char[] text = new char[8];
        for (int i = 0; i < 8; i++)
        {
            bool set = (p & (0x80 >> i)) != 0;
            text[i] = set ? names[i] : char.ToLowerInvariant(names[i]);
        }

        return new string(text);
    }

    private void SetEnabled(bool enabled)
    {
        _stepButton.Enabled = enabled;
        _runButton.Enabled = enabled;
        _resetButton.Enabled = enabled;
    }
}
