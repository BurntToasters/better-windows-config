using System.Diagnostics;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Bwc.Oracle.Capture.Gpedit.Probe;
using Bwc.Oracle.Capture.Gpedit.Session;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

internal enum DialogKind
{
    Setting,
    ShowContents,
    FilterOptions,
    MessageBox,
    Other,
}

// Drives the classic gpedit.msc window of one mmc.exe the session started. The driver never starts anything itself: GpeditContext
// starts mmc.exe and records its pid in the manifest, so the session restore closes it. Partial files: Tree (nodes), List (result list,
// status bar, tabs, sort), Policy (open a policy), Menu, Filter, Dialogs (message boxes and cleanup).
// Failure modes: docs/failure-modes/oracle-gpedit-driver.md.
internal sealed partial class GpeditDriver
{
    internal const string SettingDialogId = "Editor";
    internal const string ShowContentsDialogId = "ListBoxDialog";
    internal const int FilterManagedComboId = 3501;

    private readonly GpeditContext ctx;
    private readonly Process mmc;
    private readonly string shotDir;
    private readonly List<string> notes = new();
    private IntPtr main;
    private AutomationElement? mainElement;
    private IntPtr lastListHwnd;

    private GpeditDriver(GpeditContext ctx, Process mmc, string shotDir)
    {
        this.ctx = ctx;
        this.mmc = mmc;
        this.shotDir = shotDir;
        ProbeClickGuard.TargetPid = mmc.Id;
    }

    internal GpeditContext Context => ctx;

    internal int Pid => mmc.Id;

    internal IntPtr MainWindow => main;

    // The file snapshot after the last write the driver saw. Each FileWatch starts from it.
    internal JsonObject? LastSnapshot { get; set; }

    // The folder screenshots go to.
    internal string ShotDir => shotDir;

    // The phase times of the operation that runs now. The runner puts a new recorder here before each operation.
    internal PhaseRecorder Phases { get; set; } = new();

    // Milliseconds from the process start to the main window, and for the console tree to show an item. Zero until measured.
    internal int ClassicStartMs { get; private set; }

    internal int TreeReadyMs { get; private set; }

    // Starts classic gpedit through the session context (inherits elevation, pid recorded for the restore) and waits for its window.
    internal static GpeditDriver Start(GpeditContext ctx, string shotDir)
    {
        Directory.CreateDirectory(shotDir);
        Stopwatch sinceStart = Stopwatch.StartNew();
        Process process = ctx.StartMmc(ctx.Paths.GpeditMsc);
        return Attach(ctx, process, shotDir, sinceStart);
    }

    // Takes over an mmc.exe that the session started (the Process returned by GpeditContext.StartMmc). The stopwatch, when given, started
    // before the process did.
    internal static GpeditDriver Attach(GpeditContext ctx, Process process, string shotDir, Stopwatch? sinceStart = null)
    {
        Directory.CreateDirectory(shotDir);
        sinceStart ??= Stopwatch.StartNew();
        var driver = new GpeditDriver(ctx, process, shotDir);
        driver.WaitForMainWindow();
        driver.ClassicStartMs = (int)sinceStart.ElapsedMilliseconds;
        Stopwatch populated = Stopwatch.StartNew();
        driver.WaitPopulated();
        driver.TreeReadyMs = (int)populated.ElapsedMilliseconds;
        return driver;
    }

    internal bool HasExited => mmc.HasExited;

    internal AutomationElement Main()
    {
        if (mainElement is null)
        {
            throw new DriverException("the gpedit window was not found", fatal: true);
        }

        ThrowIfDead();
        return mainElement;
    }

    internal void ThrowIfDead()
    {
        if (mmc.HasExited)
        {
            throw new DriverException($"mmc.exe exited with code {mmc.ExitCode}", fatal: true);
        }
    }

    internal void Note(string text) => notes.Add(text);

    internal JsonArray TakeNotes()
    {
        var array = new JsonArray(notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        notes.Clear();
        return array;
    }

    private void WaitForMainWindow()
    {
        int pid = mmc.Id;
        WinInfo? window = ProbeUia.Wait(() =>
        {
            if (mmc.HasExited)
            {
                throw new DriverException($"mmc.exe exited with code {mmc.ExitCode} before its window appeared", fatal: true);
            }

            ctx.ThrowIfCancelled();
            List<WinInfo> visible = ProbeWindows.TopLevel(pid).Where(w => w.Visible && w.Rect.Width > 0).ToList();
            return visible.FirstOrDefault(w => w.Class == "MMCMainFrame")
                ?? visible.FirstOrDefault(w => w.Title.Contains("Group Policy", StringComparison.OrdinalIgnoreCase));
        }, TimeSpan.FromSeconds(90));

        if (window is null)
        {
            var seen = new JsonArray(ProbeWindows.TopLevel(pid).Where(w => w.Visible).Select(w => (JsonNode?)ProbeWindows.ToJson(w)).ToArray());
            throw new DriverException("no gpedit main window within 90 seconds", fatal: true, evidence: seen);
        }

        main = window.Hwnd;
        mainElement = AutomationElement.FromHandle(main);
        ProbeInput.Foreground(main);
    }

    private void WaitPopulated()
    {
        AutomationElement? tree = ProbeUia.Wait(
            () => ProbeUia.OfType(Main(), ControlType.Tree).FirstOrDefault() ?? ProbeUia.OfClass(Main(), "SysTreeView32").FirstOrDefault(),
            TimeSpan.FromSeconds(30));
        if (tree is null)
        {
            throw new DriverException("no console tree within 30 seconds", fatal: true, evidence: CollectStartEvidence("noConsoleTree"));
        }

        List<AutomationElement>? items = ProbeUia.Wait(
            () =>
            {
                List<AutomationElement> found = ProbeUia.OfType(tree, ControlType.TreeItem);
                return found.Count > 0 ? found : null;
            },
            TimeSpan.FromSeconds(30));
        if (items is null)
        {
            throw new DriverException("the console tree stayed empty for 30 seconds", fatal: true, evidence: CollectStartEvidence("emptyConsoleTree"));
        }
    }

    // What classic showed when the console tree did not come: the visible top-level windows of mmc, the class names of the direct children
    // of the main window and a screenshot name. Every part has its own try, so a failure here (window gone, UIA error) is recorded as text
    // and the start error that called this is the one that is thrown. Failure mode 32 in docs/failure-modes/oracle-gpedit-driver.md.
    private JsonObject CollectStartEvidence(string stage)
    {
        var evidence = new JsonObject { ["stage"] = stage };
        try
        {
            evidence["mmcExited"] = mmc.HasExited;
        }
        catch (Exception ex)
        {
            evidence["mmcExitedError"] = ex.GetType().Name + ": " + ex.Message;
        }

        try
        {
            evidence["windows"] = new JsonArray(ProbeWindows.TopLevel(mmc.Id).Where(w => w.Visible).Select(w => (JsonNode?)ProbeWindows.ToJson(w)).ToArray());
        }
        catch (Exception ex)
        {
            evidence["windowsError"] = ex.GetType().Name + ": " + ex.Message;
        }

        try
        {
            evidence["mainChildClasses"] = new JsonArray(
                ProbeWindows.Children(main).Where(c => Win32.GetParent(c.Hwnd) == main).Select(c => (JsonNode?)JsonValue.Create(c.Class)).ToArray());
        }
        catch (Exception ex)
        {
            evidence["mainChildClassesError"] = ex.GetType().Name + ": " + ex.Message;
        }

        try
        {
            evidence["screenshot"] = Shot(main, "start-" + stage);
        }
        catch (Exception ex)
        {
            evidence["screenshotError"] = ex.GetType().Name + ": " + ex.Message;
        }

        return evidence;
    }

    internal static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    internal static string IdOf(IntPtr hwnd)
    {
        try
        {
            return AutomationElement.FromHandle(hwnd).Current.AutomationId ?? string.Empty;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    internal List<WinInfo> Dialogs() => ProbeWindows.Dialogs(Pid, main);

    internal static bool IsOpen(IntPtr hwnd) => Win32Ext.IsWindow(hwnd) && Win32.IsWindowVisible(hwnd);

    internal static bool WaitClosed(IntPtr hwnd, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (IsOpen(hwnd))
        {
            if (DateTime.UtcNow >= end)
            {
                return false;
            }

            Thread.Sleep(100);
        }

        return true;
    }

    internal DialogKind Classify(WinInfo window)
    {
        if (window.Class.StartsWith("WindowsForms10", StringComparison.Ordinal))
        {
            string id = IdOf(window.Hwnd);
            return id == SettingDialogId ? DialogKind.Setting : id == ShowContentsDialogId ? DialogKind.ShowContents : DialogKind.Other;
        }

        if (window.Class == "#32770")
        {
            return Win32Ext.GetDlgItem(window.Hwnd, FilterManagedComboId) != IntPtr.Zero ? DialogKind.FilterOptions : DialogKind.MessageBox;
        }

        return DialogKind.Other;
    }

    // Foreground check, then a key. Used for menus, the context menu key and the grid.
    internal void Key(IntPtr window, ushort key)
    {
        DriverInput.RequireForeground(window, Pid);
        DriverInput.Key(key);
    }

    // Two screenshots as the probe takes them: the window's own paint and a copy of its screen area. Best effort, never throws.
    internal string Shot(IntPtr hwnd, string name)
    {
        var parts = new List<string>();
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        try
        {
            Report.SaveWindow(hwnd, Path.Combine(shotDir, safe + ".png"));
            parts.Add(safe + ".png");
        }
        catch (Exception ex)
        {
            parts.Add("printwindow failed: " + ex.Message);
        }

        try
        {
            if (!Win32.GetWindowRect(hwnd, out NativeRect rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new InvalidOperationException("empty window rectangle");
            }

            using var bitmap = new Bitmap(rect.Width, rect.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height));
            }

            bitmap.Save(Path.Combine(shotDir, safe + "-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
            parts.Add(safe + "-screen.png");
        }
        catch (Exception ex)
        {
            parts.Add("screen copy failed: " + ex.Message);
        }

        return string.Join("; ", parts);
    }

    // File > Exit through the window close message, for a caller that wants mmc closed before the session ends. The restore does it anyway.
    internal JsonObject Close()
    {
        if (mmc.HasExited)
        {
            return new JsonObject { ["alreadyExited"] = true, ["exitCode"] = mmc.ExitCode };
        }

        JsonArray handled = EnsureNoDialogs();
        Win32.PostMessageW(main, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        bool exited = mmc.WaitForExit(15000);
        return new JsonObject { ["exited"] = exited, ["dialogsHandled"] = handled };
    }
}
