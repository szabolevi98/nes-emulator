using System.Reflection;
using NesEmulator;
using NesEmulator.Controls;
using NesEmulator.Core;
using NesEmulator.Core.Cartridges.Mappers;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;

internal static class Program
{
    private static int _failures;
    private static int _total;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        using MainForm form = new(null);
        // Keep the window hidden and the timer stopped; only exercise the real
        // WinForms message filter and controller ports, without touching user focus.
        byte[] rom = new byte[16 + 16384 + 8192];
        "NES\u001a"u8.CopyTo(rom);
        rom[4] = 1;
        rom[5] = 1;
        Nes nes = new(NesEmulator.Core.Cartridges.Cartridge.FromBytes(rom));
        typeof(MainForm).GetField("_nes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(form, nes);

        Control screen = form.Controls.Cast<Control>().Single(c => c.GetType().Name == "ScreenControl");
        using TextBox trace = new() { ReadOnly = true, Multiline = true };
        using Button button = new();
        form.Controls.Add(trace);
        form.Controls.Add(button);

        foreach (Control target in new Control[] { form, screen, trace, button })
        {
            foreach ((Keys key, NesButton expected) in new[]
            {
                (Keys.Up, NesButton.Up), (Keys.Down, NesButton.Down),
                (Keys.Left, NesButton.Left), (Keys.Right, NesButton.Right),
                (Keys.Enter, NesButton.Start), (Keys.X, NesButton.A),
                (Keys.Z, NesButton.B), (Keys.ShiftKey, NesButton.Select),
            })
            {
                bool consumed = Send(target, key, true);
                Check($"{target.GetType().Name}: {key} down reaches port 1 before control navigation",
                    consumed && nes.Port1.Buttons == expected);
                Check($"{target.GetType().Name}: {key} up releases port 1",
                    Send(target, key, false) && nes.Port1.Buttons == 0);
            }
        }

        Send(screen, Keys.X, true);
        Send(trace, Keys.Right, true);
        Check("simultaneous A and Right", nes.Port1.Buttons == (NesButton.A | NesButton.Right));
        Send(button, Keys.X, false);
        Check("releasing A preserves Right", nes.Port1.Buttons == NesButton.Right);
        Send(screen, Keys.G, true);
        Check("port 2 remains independent", nes.Port2.Buttons == NesButton.A && nes.Port1.Buttons == NesButton.Right);
        Invoke(form, "OnDeactivate", EventArgs.Empty);
        Check("focus loss releases both controllers", nes.Port1.Buttons == 0 && nes.Port2.Buttons == 0);

        Send(screen, Keys.ShiftKey, true);
        Send(screen, Keys.Right, true);
        Check("Select and a direction can be held together", nes.Port1.Buttons == (NesButton.Select | NesButton.Right));
        Invoke(form, "OnDeactivate", EventArgs.Empty);
        Message altR = Message.Create(screen.Handle, 0x0104, (nint)Keys.R, (nint)(1 << 29));
        Check("Alt+R does not press player 2 Start", !Application.FilterMessage(ref altR) && nes.Port2.Buttons == 0);
        Send(screen, Keys.R, true);
        Message altRelease = Message.Create(screen.Handle, 0x0105, (nint)Keys.R, (nint)(1 << 29));
        Check("a key released after Alt still releases its button", Application.FilterMessage(ref altRelease) && nes.Port2.Buttons == 0);

        typeof(MainForm).GetField("_rewind", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(form, new RewindBuffer(nes));
        Check("Backspace starts rewind", Send(screen, Keys.Back, true)
            && (bool)typeof(MainForm).GetField("_rewinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!);
        Invoke(form, "OnDeactivate", EventArgs.Empty);
        Check("focus loss also stops rewind",
            !(bool)typeof(MainForm).GetField("_rewinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!);

        using Form dialog = new();
        Check("other windows retain their keys", !Send(dialog, Keys.Enter, true) && nes.Port1.Buttons == 0);
        form.Enabled = false;
        Check("disabled owner does not capture dialog input", !Send(screen, Keys.Enter, true));
        form.Enabled = true;

        Send(screen, Keys.Right, true);
        typeof(MenuStrip).GetMethod("OnMenuActivate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form.MainMenuStrip, new object[] { EventArgs.Empty });
        Check("opening a menu releases held controls", nes.Port1.Buttons == 0);
        Check("menu navigation retains arrows and Enter", !Send(screen, Keys.Down, true) && !Send(screen, Keys.Enter, true));
        typeof(MenuStrip).GetMethod("OnMenuDeactivate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form.MainMenuStrip, new object[] { EventArgs.Empty });
        Check("closing menu restores game input", Send(screen, Keys.Enter, true) && nes.Port1.Buttons == NesButton.Start);
        Send(screen, Keys.Enter, false);
        Check("F1 remains available to Save State", !Send(screen, Keys.F1, true));
        CheckMapperMenu(form, rom);
        CheckAbProfileMenu(form, rom);
        CheckOverscanMenu(form);
        form.Dispose();
        using Form survivor = new();
        Check("disposing emulator removes its filter", !Send(survivor, Keys.Enter, true));

        Console.WriteLine($"{_total - _failures}/{_total} UI input checks passed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckOverscanMenu(MainForm form)
    {
        object Field(string name) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        ToolStripMenuItem item = (ToolStripMenuItem)Field("_overscanItem");
        Control screen = (Control)Field("_screen");
        PropertyInfo crop = screen.GetType().GetProperty("CropOverscan")!;

        Check("overscan cropping is off until asked for", !item.Checked && !(bool)crop.GetValue(screen)!);
        item.PerformClick();
        Check("the View item turns cropping on", item.Checked && (bool)crop.GetValue(screen)!);
        item.PerformClick();
        Check("and turns it off again", !item.Checked && !(bool)crop.GetValue(screen)!);

        // The flag is only half of it: check what actually reaches the bitmap. A
        // separate control is used rather than the form's, which is docked and
        // would fight over its own size. The frame is built so the eight pixels a
        // television would have hidden are white and everything else is black, so
        // one pixel says which of the two views is being drawn.
        ushort[] frame = new ushort[256 * 240];
        for (int y = 0; y < 240; y++)
        {
            for (int index = 0; index < 256; index++)
            {
                bool edge = index < 8 || index >= 248 || y < 8 || y >= 232;
                frame[(y * 256) + index] = (ushort)(edge ? 0x30 : 0x0F); // white edge, black middle
            }
        }

        using ScreenControl painter = new();
        painter.Present(frame);

        painter.Size = new Size(256, 240);
        using (Bitmap whole = new(painter.Width, painter.Height))
        {
            painter.DrawToBitmap(whole, new Rectangle(0, 0, painter.Width, painter.Height));
            Check("the uncropped view starts at the console's first pixel",
                whole.GetPixel(0, 0).R > 0xC0 && whole.GetPixel(128, 120).R < 0x40);
        }

        painter.CropOverscan = true;
        painter.Size = new Size(240, 224);
        using (Bitmap cropped = new(painter.Width, painter.Height))
        {
            painter.DrawToBitmap(cropped, new Rectangle(0, 0, painter.Width, painter.Height));
            Check("cropping drops the eight pixels at every edge",
                cropped.GetPixel(0, 0).R < 0x40 && cropped.GetPixel(239, 223).R < 0x40);
        }
    }

    private static void CheckMapperMenu(MainForm form, byte[] rom)
    {
        object Field(string name) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        ToolStripMenuItem menu = (ToolStripMenuItem)Field("_mmc3Item");
        ToolStripMenuItem standard = (ToolStripMenuItem)Field("_mmc3StandardItem");
        ToolStripMenuItem alternate = (ToolStripMenuItem)Field("_mmc3AlternateItem");
        string path = Path.Combine(Path.GetTempPath(), $"nes-mmc3-ui-{Guid.NewGuid():N}.nes");
        try
        {
            File.WriteAllBytes(path, rom);
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            Check("MMC3 menu is disabled for a different mapper", !menu.Enabled);
            byte[] mmc3Rom = (byte[])rom.Clone();
            mmc3Rom[6] = 0x40;
            File.WriteAllBytes(path, mmc3Rom);
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            Check("MMC3 ROM opens with standard revision selected", menu.Enabled && standard.Checked && !alternate.Checked
                && ((Mmc3)((Nes)Field("_nes")).Mapper).IrqRevision == Mmc3IrqRevision.Standard);
            Nes old = (Nes)Field("_nes");
            object rewind = Field("_rewind");
            alternate.PerformClick();
            Nes changed = (Nes)Field("_nes");
            Check("MMC3 alternate selection recreates the console and rewind history", !ReferenceEquals(old, changed)
                && !ReferenceEquals(rewind, Field("_rewind")) && ((Mmc3)changed.Mapper).IrqRevision == Mmc3IrqRevision.Alternate);
            Check("MMC3 selection preserves pause and updates checks", !(bool)Field("_running") && alternate.Checked && !standard.Checked);
            alternate.PerformClick();
            Check("selecting the active MMC3 revision does not restart", ReferenceEquals(changed, Field("_nes")));
            Invoke(form, "Start");
            standard.PerformClick();
            Check("MMC3 standard selection preserves running state", (bool)Field("_running") && standard.Checked && !alternate.Checked
                && ((Mmc3)((Nes)Field("_nes")).Mapper).IrqRevision == Mmc3IrqRevision.Standard);
            Invoke(form, "Stop");
            alternate.PerformClick();
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            Check("opening a ROM again restores its default MMC3 profile", standard.Checked && !alternate.Checked);
        }
        finally { File.Delete(path); }
    }

    private static void CheckAbProfileMenu(MainForm form, byte[] rom)
    {
        object Field(string name) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        ToolStripMenuItem menu = (ToolStripMenuItem)Field("_abItem");
        ToolStripMenuItem ee = (ToolStripMenuItem)Field("_abEeItem");
        ToolStripMenuItem ff = (ToolStripMenuItem)Field("_abFfItem");
        string path = Path.Combine(Path.GetTempPath(), $"nes-ab-ui-{Guid.NewGuid():N}.nes");
        try
        {
            File.WriteAllBytes(path, rom);
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            Check("$AB menu is available for any cartridge", menu.Enabled && ee.Checked && !ff.Checked
                && ((Nes)Field("_nes")).Cpu.AbProfile == AbOpcodeProfile.MaskEE);
            Nes old = (Nes)Field("_nes");
            object rewind = Field("_rewind");
            ff.PerformClick();
            Nes changed = (Nes)Field("_nes");
            Check("$FF selection recreates the console and rewind history", !ReferenceEquals(old, changed)
                && !ReferenceEquals(rewind, Field("_rewind")) && changed.Cpu.AbProfile == AbOpcodeProfile.MaskFF);
            Check("$AB selection preserves pause and updates checks", !(bool)Field("_running") && ff.Checked && !ee.Checked);
            ff.PerformClick();
            Check("selecting the active $AB profile does not restart", ReferenceEquals(changed, Field("_nes")));
            Invoke(form, "Start");
            ee.PerformClick();
            Check("$EE selection preserves running state", (bool)Field("_running") && ee.Checked && !ff.Checked
                && ((Nes)Field("_nes")).Cpu.AbProfile == AbOpcodeProfile.MaskEE);
            Invoke(form, "Stop");
            ff.PerformClick();
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            Check("opening a ROM again restores the default $AB profile", ee.Checked && !ff.Checked
                && ((Nes)Field("_nes")).Cpu.AbProfile == AbOpcodeProfile.MaskEE);

            // An MMC3 cartridge has to keep both selections independently.
            byte[] mmc3Rom = (byte[])rom.Clone();
            mmc3Rom[6] = 0x40;
            File.WriteAllBytes(path, mmc3Rom);
            Invoke(form, "LoadRom", path);
            Invoke(form, "Stop");
            ((ToolStripMenuItem)Field("_mmc3AlternateItem")).PerformClick();
            ff.PerformClick();
            Nes both = (Nes)Field("_nes");
            Check("the $AB profile and the MMC3 revision survive together",
                both.Cpu.AbProfile == AbOpcodeProfile.MaskFF
                && ((Mmc3)both.Mapper).IrqRevision == Mmc3IrqRevision.Alternate
                && ff.Checked && ((ToolStripMenuItem)Field("_mmc3AlternateItem")).Checked);
            ((ToolStripMenuItem)Field("_mmc3StandardItem")).PerformClick();
            Check("changing the MMC3 revision keeps the $AB profile",
                ((Nes)Field("_nes")).Cpu.AbProfile == AbOpcodeProfile.MaskFF && ff.Checked);
        }
        finally { File.Delete(path); }
    }

    private static bool Send(Control target, Keys key, bool down)
    {
        Message message = Message.Create(target.Handle, down ? 0x0100 : 0x0101, (nint)key, 0);
        return Application.FilterMessage(ref message);
    }

    private static void Invoke(MainForm form, string method, params object[] args) =>
        typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);

    private static void Check(string name, bool passed)
    {
        _total++;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}");
        if (!passed) _failures++;
    }
}
