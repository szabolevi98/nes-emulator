using System.Diagnostics;
using NesEmulator.Controls;
using NesEmulator.Core;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;

namespace NesEmulator;

/// <summary>
/// The emulator window: the screen, and a debugger panel that can be folded out
/// beside it. The debugger is the same trace view the processor was built
/// against, which is the window you want open the moment a game misbehaves.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 24, 27);
    private static readonly Color Surface = Color.FromArgb(32, 32, 36);
    private static readonly Color Foreground = Color.FromArgb(228, 228, 231);
    private static readonly Color Muted = Color.FromArgb(148, 148, 158);
    private static readonly Color Accent = Color.FromArgb(96, 165, 250);
    private static readonly Color Warning = Color.FromArgb(248, 113, 113);

    /// <summary>Keyboard to controller, in the layout most emulators settled on.</summary>
    private static readonly Dictionary<Keys, NesButton> KeyMap = new()
    {
        [Keys.Up] = NesButton.Up,
        [Keys.Down] = NesButton.Down,
        [Keys.Left] = NesButton.Left,
        [Keys.Right] = NesButton.Right,
        [Keys.X] = NesButton.A,
        [Keys.Z] = NesButton.B,
        [Keys.Enter] = NesButton.Start,
        [Keys.ShiftKey] = NesButton.Select,
    };

    private readonly ScreenControl _screen;
    private readonly Panel _debugger;
    private readonly Label _summary;
    private readonly Label _registers;
    private readonly TextBox _trace;
    private readonly System.Windows.Forms.Timer _clock;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ToolStripMenuItem _pauseItem;

    private Nes? _nes;
    private bool _running;
    private int _framesSinceCount;
    private double _lastFpsReport;
    private double _fps;

    public MainForm(string? romPath)
    {
        Text = "NES Emulator";
        BackColor = Background;
        ForeColor = Foreground;
        MinimumSize = new Size(640, 480);
        Size = new Size(1024, 720);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _screen = new ScreenControl { Dock = DockStyle.Fill };

        _registers = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(10, 5, 10, 0),
            ForeColor = Accent,
            Font = new Font(FontFamily.GenericMonospace, 9f),
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
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
        };

        FlowLayoutPanel debugButtons = new()
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(6, 5, 6, 5),
            BackColor = Background,
        };

        debugButtons.Controls.AddRange(
        [
            MakeButton("Step", () => Trace(1)),
            MakeButton("Trace 500", () => Trace(500)),
            MakeButton("Clear", () => _trace.Clear()),
        ]);

        _debugger = new Panel
        {
            Dock = DockStyle.Right,
            Width = 430,
            BackColor = Background,
            Visible = false,
        };

        _debugger.Controls.Add(_trace);
        _debugger.Controls.Add(debugButtons);
        _debugger.Controls.Add(_registers);

        _summary = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(10, 5, 10, 0),
            ForeColor = Muted,
            Text = "No cartridge loaded. File -> Open ROM, or drop one on the window.",
        };

        MenuStrip menu = new()
        {
            BackColor = Surface,
            ForeColor = Foreground,
        };

        ToolStripMenuItem file = new("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open ROM...", null, (_, _) => OpenRom())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        _pauseItem = new ToolStripMenuItem("&Pause", null, (_, _) => TogglePause())
        {
            ShortcutKeys = Keys.F5,
            Enabled = false,
        };

        ToolStripMenuItem emulation = new("&Emulation");
        emulation.DropDownItems.Add(_pauseItem);
        emulation.DropDownItems.Add(new ToolStripMenuItem("&Reset", null, (_, _) => ResetConsole())
        {
            ShortcutKeys = Keys.Control | Keys.R,
        });

        ToolStripMenuItem view = new("&View");
        ToolStripMenuItem debuggerItem = new("&Debugger", null, (sender, _) =>
        {
            _debugger.Visible = !_debugger.Visible;
            ((ToolStripMenuItem)sender!).Checked = _debugger.Visible;
            RefreshState();
        })
        {
            ShortcutKeys = Keys.F12,
            CheckOnClick = false,
        };

        view.DropDownItems.Add(debuggerItem);

        menu.Items.AddRange([file, emulation, view]);

        Controls.Add(_screen);
        Controls.Add(_debugger);
        Controls.Add(_summary);
        Controls.Add(menu);
        MainMenuStrip = menu;

        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                LoadRom(files[0]);
            }
        };

        // A sixtieth of a second is the closest a form timer gets to the real
        // 60.1 frames a second; the measured rate is shown in the status line.
        _clock = new System.Windows.Forms.Timer { Interval = 16 };
        _clock.Tick += (_, _) => RunOneFrame();

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
            TabStop = false,
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
            _pauseItem.Enabled = true;
            Start();
        }
        catch (Exception exception)
            when (exception is InvalidDataException or NotSupportedException or IOException)
        {
            _nes = null;
            Stop();
            _pauseItem.Enabled = false;
            _summary.ForeColor = Warning;
            _summary.Text = exception.Message;
            _registers.Text = string.Empty;
            _trace.Clear();
            Text = "NES Emulator";
        }
    }

    private void Start()
    {
        _running = true;
        _pauseItem.Text = "&Pause";
        _clock.Start();
    }

    private void Stop()
    {
        _running = false;
        _pauseItem.Text = "&Resume";
        _clock.Stop();
        RefreshState();
    }

    private void TogglePause()
    {
        if (_nes is null)
        {
            return;
        }

        if (_running)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    private void ResetConsole()
    {
        _nes?.Reset();
        _trace.Clear();
        RefreshState();
    }

    private void RunOneFrame()
    {
        if (_nes is null)
        {
            return;
        }

        _nes.RunFrame();
        _screen.Present(_nes.Ppu.FrameBuffer);

        _framesSinceCount++;
        double now = _stopwatch.Elapsed.TotalSeconds;
        if (now - _lastFpsReport >= 1.0)
        {
            _fps = _framesSinceCount / (now - _lastFpsReport);
            _framesSinceCount = 0;
            _lastFpsReport = now;
        }

        if (_debugger.Visible)
        {
            RefreshState();
        }
    }

    /// <summary>Runs instructions one at a time, writing each into the trace view.</summary>
    private void Trace(int instructions)
    {
        if (_nes is null)
        {
            return;
        }

        if (_running)
        {
            Stop();
        }

        List<string> lines = new(instructions);
        for (int i = 0; i < instructions && !_nes.Cpu.Jammed; i++)
        {
            lines.Add(Disassembler.TraceLine(_nes.Cpu, _nes.Bus));
            _nes.StepInstruction();
        }

        _trace.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        _screen.Present(_nes.Ppu.FrameBuffer);
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
            $"PC:{cpu.PC:X4} A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} " +
            $"P:{cpu.P:X2}[{Flags(cpu.P)}] SP:{cpu.S:X2}  " +
            $"line {_nes.Ppu.Scanline,4} dot {_nes.Ppu.Cycle,3}  {_fps:0.0} fps" +
            (cpu.Jammed ? "  — JAMMED" : _running ? string.Empty : "  — paused");
    }

    private static string Flags(byte p)
    {
        // Upper case where the flag is set, lower case where it is clear.
        const string names = "NV-BDIZC";
        char[] text = new char[8];
        for (int i = 0; i < 8; i++)
        {
            text[i] = (p & (0x80 >> i)) != 0 ? names[i] : char.ToLowerInvariant(names[i]);
        }

        return new string(text);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_nes is not null && KeyMap.TryGetValue(e.KeyCode, out NesButton button))
        {
            _nes.Port1.Buttons |= button;
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_nes is not null && KeyMap.TryGetValue(e.KeyCode, out NesButton button))
        {
            _nes.Port1.Buttons &= ~button;
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    /// <summary>The arrow keys would otherwise move focus between controls.</summary>
    protected override bool IsInputKey(Keys keyData) =>
        KeyMap.ContainsKey(keyData) || base.IsInputKey(keyData);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
        }

        base.Dispose(disposing);
    }
}
