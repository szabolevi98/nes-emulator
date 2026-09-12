using System.Diagnostics;
using NesEmulator.Audio;
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
public sealed class MainForm : Form, IMessageFilter
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

    /// <summary>The second port, on the left of the keyboard so two can share one.</summary>
    private static readonly Dictionary<Keys, NesButton> KeyMap2 = new()
    {
        [Keys.W] = NesButton.Up,
        [Keys.S] = NesButton.Down,
        [Keys.A] = NesButton.Left,
        [Keys.D] = NesButton.Right,
        [Keys.G] = NesButton.A,
        [Keys.F] = NesButton.B,
        [Keys.R] = NesButton.Start,
        [Keys.T] = NesButton.Select,
    };

    private readonly ScreenControl _screen;
    private readonly Panel _debugger;
    private readonly Label _summary;
    private readonly Label _registers;
    private readonly TextBox _trace;
    private readonly System.Windows.Forms.Timer _clock;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _soundItem;
    private readonly float[] _audioBuffer = new float[4096];
    private WaveOutPlayer? _audio;

    private Nes? _nes;
    private RewindBuffer? _rewind;
    private string? _romPath;
    private bool _rewinding;
    private int _rewindTicks;
    private bool _running;
    private bool _menuActive;
    private int _framesSinceCount;
    private double _lastFpsReport;
    private double _fps;
    private readonly FramePacer _pacer = new();
    private readonly Icon _windowIcon;

    public MainForm(string? romPath)
    {
        using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("NesEmulator.app.ico")
            ?? throw new InvalidOperationException("The application icon resource is missing."))
        {
            using Icon resourceIcon = new(stream, 32, 32);
            _windowIcon = (Icon)resourceIcon.Clone();
            Icon = _windowIcon;
        }
        Text = "NES Emulator";
        BackColor = Background;
        ForeColor = Foreground;
        MinimumSize = new Size(640, 480);
        Size = new Size(1024, 720);
        StartPosition = FormStartPosition.CenterScreen;

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
        file.DropDownItems.Add(new ToolStripMenuItem("&Save State", null, (_, _) => SaveState())
        {
            ShortcutKeys = Keys.F1,
        });
        file.DropDownItems.Add(new ToolStripMenuItem("&Load State", null, (_, _) => LoadState())
        {
            ShortcutKeys = Keys.F4,
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        _pauseItem = new ToolStripMenuItem("&Pause", null, (_, _) => TogglePause())
        {
            ShortcutKeys = Keys.F5,
            Enabled = false,
        };

        // A machine with no output device is not an error worth stopping for; the
        // emulator simply runs silently and the menu entry stays off.
        try
        {
            _audio = new WaveOutPlayer();
        }
        catch (InvalidOperationException)
        {
            _audio = null;
        }

        _soundItem = new ToolStripMenuItem("&Sound", null, (sender, _) =>
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender!;
            item.Checked = !item.Checked;
            _nes?.Apu.DiscardSamples();
            _pacer.Reset(_stopwatch.Elapsed.TotalSeconds);
        })
        {
            Checked = _audio is not null,
            Enabled = _audio is not null,
            ShortcutKeyDisplayString = "M",
        };

        ToolStripMenuItem emulation = new("&Emulation");
        emulation.DropDownItems.Add(_pauseItem);
        emulation.DropDownItems.Add(new ToolStripMenuItem("&Reset", null, (_, _) => ResetConsole())
        {
            ShortcutKeys = Keys.Control | Keys.R,
        });
        emulation.DropDownItems.Add(new ToolStripSeparator());
        emulation.DropDownItems.Add(_soundItem);

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

        ToolStripMenuItem help = new("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&Controls...", null, (_, _) =>
            ShowHelpDialog(() => new ControlsForm(KeyMap, KeyMap2, MainMenuStrip!))));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(new ToolStripMenuItem("&About NES Emulator...", null, (_, _) => ShowAbout()));

        menu.Items.AddRange([file, emulation, view, help]);

        Controls.Add(_screen);
        Controls.Add(_debugger);
        Controls.Add(_summary);
        Controls.Add(menu);
        MainMenuStrip = menu;
        menu.MenuActivate += (_, _) =>
        {
            _menuActive = true;
            ReleaseControllerInput();
        };
        menu.MenuDeactivate += (_, _) => _menuActive = false;

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

        // The timer only asks how much work is due; the sound card decides how
        // much that is. See OnClockTick.
        _clock = new System.Windows.Forms.Timer { Interval = 8 };
        _clock.Tick += (_, _) => OnClockTick();

        if (romPath is not null && File.Exists(romPath))
        {
            LoadRom(romPath);
        }

        Application.AddMessageFilter(this);
    }

    private void ShowAbout() => ShowHelpDialog(() => new AboutForm());

    private void ShowHelpDialog(Func<Form> createDialog)
    {
        bool resume = _running;
        if (resume) Stop();
        ReleaseControllerInput();

        try
        {
            using Form dialog = createDialog();
            dialog.ShowDialog(this);
        }
        finally
        {
            if (resume && !IsDisposed) Start();
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
            _rewind = new RewindBuffer(_nes);
            _romPath = path;
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
            _rewind = null;
            _romPath = null;
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
        _pacer.Reset(_stopwatch.Elapsed.TotalSeconds);
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

    /// <summary>
    /// Decides how many frames are due. With sound on, that is however many
    /// buffers the card has finished with, so the emulator runs at the speed the
    /// audio is being consumed and never drifts away from it. With sound off
    /// the elapsed wall clock decides when the next NTSC frame is due.
    /// </summary>
    private void OnClockTick()
    {
        if (_nes is null)
        {
            return;
        }

        if (_rewinding)
        {
            StepBack();
            return;
        }

        if (_audio is null || !_soundItem.Checked)
        {
            int frames = _pacer.FramesDue(_stopwatch.Elapsed.TotalSeconds);
            for (int i = 0; i < frames; i++) RunOneFrame();
            return;
        }

        // Two at most, so a long stall catches up gradually instead of lurching.
        int due = Math.Min(_audio.FreeBuffers, 2);
        for (int i = 0; i < due; i++)
        {
            RunOneFrame();
        }
    }

    private void RunOneFrame()
    {
        if (_nes is null)
        {
            return;
        }

        _nes.RunFrame();
        _rewind?.OnFrame();
        _screen.Present(_nes.Ppu.FrameBuffer);
        PlayAudio();

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

    /// <summary>Winds one snapshot back and shows it. Each is a few frames of play.</summary>
    private void StepBack()
    {
        // One snapshot per few ticks, so winding back reads as fast rewind rather
        // than an instant jump to the start.
        if (++_rewindTicks < 4)
        {
            return;
        }

        _rewindTicks = 0;

        if (_nes is null || _rewind is null || !_rewind.StepBack())
        {
            return;
        }

        _screen.Present(_nes.Ppu.FrameBuffer);
        RefreshState();
    }

    private string StatePath() =>
        Path.ChangeExtension(_romPath ?? string.Empty, ".state");

    private void SaveState()
    {
        if (_nes is null || _romPath is null)
        {
            return;
        }

        try
        {
            using FileStream file = File.Create(StatePath());
            _nes.SaveState(file);
            Announce($"State saved to {Path.GetFileName(StatePath())}");
        }
        catch (IOException exception)
        {
            Announce(exception.Message, warning: true);
        }
    }

    private void LoadState()
    {
        if (_nes is null || _romPath is null)
        {
            return;
        }

        string path = StatePath();
        if (!File.Exists(path))
        {
            Announce("No saved state for this cartridge yet.", warning: true);
            return;
        }

        try
        {
            using FileStream file = File.OpenRead(path);
            _nes.LoadState(file);

            // What was recorded before this jump no longer leads here.
            _rewind?.Clear();
            _screen.Present(_nes.Ppu.FrameBuffer);
            RefreshState();
            Announce($"State loaded from {Path.GetFileName(path)}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
        {
            Announce(exception.Message, warning: true);
        }
    }

    private void Announce(string message, bool warning = false)
    {
        _summary.ForeColor = warning ? Warning : Muted;
        _summary.Text = message;
    }

    private void PlayAudio()
    {
        if (_nes is null || _audio is null)
        {
            return;
        }

        if (!_soundItem.Checked)
        {
            // Keep the ring from filling up while muted, so unmuting does not play
            // a second of stale sound.
            _nes.Apu.DiscardSamples();
            return;
        }

        while (_audio.FreeBuffers > 0 && _nes.Apu.AvailableSamples >= _audio.SamplesPerBuffer)
        {
            int taken = _nes.Apu.ReadSamples(_audioBuffer, _audio.SamplesPerBuffer);
            if (!_audio.Submit(_audioBuffer, taken))
            {
                break; // nothing free; the rest stays buffered for the next frame
            }
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
            (cpu.Jammed ? "  — JAMMED"
                : _rewinding ? $"  — rewinding, {_rewind?.Count ?? 0} left"
                : _running ? string.Empty : "  — paused");
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

    /// <summary>
    /// Capture controller keys before WinForms treats arrows and Enter as focus
    /// navigation or a button click. KeyPreview runs too late for those messages.
    /// Only this window participates; menus and modal dialogs keep their keys.
    /// </summary>
    public bool PreFilterMessage(ref Message m)
    {
        const int KeyDown = 0x0100, KeyUp = 0x0101;
        const int SysKeyDown = 0x0104, SysKeyUp = 0x0105;
        if (m.Msg is not (KeyDown or KeyUp or SysKeyDown or SysKeyUp)
            || !Enabled || _menuActive
            || Control.FromChildHandle(m.HWnd)?.FindForm() != this)
        {
            return false;
        }

        bool pressed = m.Msg is KeyDown or SysKeyDown;
        // Shift is also Select, so Shift + an arrow must still reach the game.
        // Ctrl/Alt combinations belong to the application (Ctrl+R, Alt+F4, ...).
        if (pressed && ((ModifierKeys & (Keys.Control | Keys.Alt)) != 0 || m.Msg == SysKeyDown))
        {
            return false;
        }

        Keys key = (Keys)(int)m.WParam & Keys.KeyCode;
        if (key == Keys.M)
        {
            // Toggle once per press; holding M must not repeatedly flip the sound.
            if (pressed && ((long)m.LParam & (1L << 30)) == 0 && _soundItem.Enabled)
            {
                _soundItem.PerformClick();
            }
            return true;
        }

        if (_nes is null) return false;

        if (key == Keys.Back && _rewind is not null)
        {
            if (pressed) _rewinding = true;
            else EndRewind();
            return true;
        }

        bool handled = false;
        if (KeyMap.TryGetValue(key, out NesButton button))
        {
            if (pressed) _nes.Port1.Buttons |= button;
            else _nes.Port1.Buttons &= ~button;
            handled = true;
        }

        if (KeyMap2.TryGetValue(key, out NesButton second))
        {
            if (pressed) _nes.Port2.Buttons |= second;
            else _nes.Port2.Buttons &= ~second;
            handled = true;
        }

        return handled;
    }

    private void EndRewind()
    {
        if (!_rewinding) return;
        _rewinding = false;
        _rewindTicks = 0;
        _nes?.Apu.DiscardSamples();
        _pacer.Reset(_stopwatch.Elapsed.TotalSeconds);
    }

    private void ReleaseControllerInput()
    {
        EndRewind();
        if (_nes is not null)
        {
            _nes.Port1.Buttons = 0;
            _nes.Port2.Buttons = 0;
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        ReleaseControllerInput();
        base.OnDeactivate(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
            _clock.Dispose();
            _audio?.Dispose();
            _windowIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}
