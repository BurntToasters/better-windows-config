using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `metrics`: points classic regedit at the Mixed key, measures its main window with Win32 messages,
// captures a PrintWindow screenshot, closes regedit, and restores LastKey/View.
internal static class MetricsCommand
{
    private const int RequiredDpi = 96;
    private const string WindowClass = "RegEdit_RegEdit";
    private const string MixedKeyName = "Mixed";
    private const string LastKeyValue = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle\Mixed";
    private const string SeededKeyPath = @"Software\BWC-Oracle\Mixed";
    private const int ExpectedListRows = 8; // seven values in Mixed plus the (Default) row

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        Win32.SetProcessDpiAwarenessContext(Msg.DpiAwarePerMonitorV2);
        uint systemDpi = Win32.GetDpiForSystem();
        if (systemDpi != RequiredDpi && !allowDpi)
        {
            throw new InvalidOperationException(
                $"metrics refused: system DPI is {systemDpi}, but classic-regedit parity is measured at {RequiredDpi} DPI (100%). " +
                "Set the display scale to 100%, or pass --allow-dpi to record metrics at this DPI.");
        }

        using (RegistryKey? seeded = Registry.CurrentUser.OpenSubKey(SeededKeyPath, writable: false))
        {
            if (seeded is null)
            {
                throw new InvalidOperationException($"metrics needs the seeded tree: HKCU\\{SeededKeyPath} is missing. Run `seed` first.");
            }
        }

        bool written = GuiLock.Run(() => Measure(paths, systemDpi, allowDpi));
        if (!written)
        {
            throw new InvalidOperationException("metrics did not complete.");
        }
    }

    private static bool Measure(RepoPaths paths, uint systemDpi, bool allowDpi)
    {
        AppletSnapshot before = AppletSnapshot.Capture();
        Console.WriteLine($"metrics: applet state before: {before.Describe()}");
        Process? proc = null;
        try
        {
            RegistryGuard.SetAppletValue("LastKey", RegType.Sz, SeedSpec.Sz(LastKeyValue));
            proc = RegeditProcess.Start(Array.Empty<string>());
            proc.WaitForInputIdle(15000);
            IntPtr hwnd = WaitForMainWindow(proc);
            Console.WriteLine($"metrics: regedit main window 0x{hwnd:X} pid {proc.Id}");

            // The status bar is off by default in this regedit; turn it on for measurement, then put it back.
            bool? statusBarWasVisible = SetStatusBar(hwnd, visible: true);
            Console.WriteLine(statusBarWasVisible is null
                ? "metrics: this regedit has no View > Status Bar item"
                : $"metrics: status bar visible at launch: {statusBarWasVisible}");

            JsonObject root = Collect(paths, hwnd, proc, systemDpi, allowDpi, statusBarWasVisible);

            Screenshot(paths, hwnd);
            if (statusBarWasVisible is bool wasVisible)
            {
                SetStatusBar(hwnd, visible: wasVisible);
            }

            Close(proc, hwnd);

            string json = root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }).Replace("\r\n", "\n") + "\n";
            Util.WriteUtf8NoBom(paths.MetricsJson, json);
            Console.WriteLine($"metrics: wrote {paths.MetricsJson} and {paths.ScreenshotPng}");
            return true;
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
            {
                proc.Kill();
                proc.WaitForExit(5000);
                Console.WriteLine("metrics: killed the regedit process this run started");
            }

            before.Restore();
            Console.WriteLine($"metrics: applet state restored: {before.Describe()}");
        }
    }

    private static IntPtr WaitForMainWindow(Process proc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited)
            {
                throw new InvalidOperationException($"regedit exited with code {proc.ExitCode} before its window appeared.");
            }

            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((h, _) =>
            {
                Win32.GetWindowThreadProcessId(h, out uint pid);
                if (pid == proc.Id && ClassOf(h) == WindowClass)
                {
                    found = h;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"regedit pid {proc.Id} showed no {WindowClass} window within 30s.");
    }

    // Waits until the tree and list show the Mixed key (selected, eight rows), then lets the first paint settle.
    private static void WaitForLoadedKey(RegeditMeasure measure, RegeditMeasure.ChildInfo tree, RegeditMeasure.ChildInfo list)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            string? selected = measure.Tree(tree)["selectedItem"]?.GetValue<string>();
            int rows = measure.ListView(list)["itemCount"]?.GetValue<int>() ?? -1;
            if (selected == MixedKeyName && rows == ExpectedListRows)
            {
                Thread.Sleep(1000);
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"regedit did not show the {MixedKeyName} key with {ExpectedListRows} rows within 20s.");
    }

    // Sets View > Status Bar through the menu's own command id and returns the state it was in before.
    // Returns null when this regedit build has no View > Status Bar item (Windows 11 25H2 does not).
    private static bool? SetStatusBar(IntPtr hwnd, bool visible)
    {
        IntPtr menu = Win32.GetMenu(hwnd);
        if (menu == IntPtr.Zero)
        {
            throw new InvalidOperationException("regedit has no menu bar to read View > Status Bar from.");
        }

        IntPtr view = Win32.GetSubMenu(menu, FindMenuPosition(menu, "View"));
        int statusPos = TryFindMenuPosition(view, "Status Bar");
        if (statusPos < 0)
        {
            return null;
        }

        uint id = Win32.GetMenuItemID(view, statusPos);
        bool wasChecked = (Win32.GetMenuState(view, (uint)statusPos, Msg.MF_BYPOSITION) & Msg.MF_CHECKED) != 0;
        if (wasChecked == visible)
        {
            return wasChecked;
        }

        if (!Win32.PostMessageW(hwnd, Msg.WM_COMMAND, new IntPtr(id), IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PostMessage(WM_COMMAND, Status Bar) failed.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (HasStatusBarChild(hwnd) != visible && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        if (HasStatusBarChild(hwnd) != visible)
        {
            throw new TimeoutException($"status bar did not become {(visible ? "visible" : "hidden")} within 5s.");
        }

        return wasChecked;
    }

    private static int FindMenuPosition(IntPtr menu, string label)
    {
        int pos = TryFindMenuPosition(menu, label);
        if (pos < 0)
        {
            throw new InvalidOperationException($"menu item '{label}' not found; items: {string.Join(" | ", MenuItemTexts(menu))}");
        }

        return pos;
    }

    private static int TryFindMenuPosition(IntPtr menu, string label)
    {
        List<string> items = MenuItemTexts(menu);
        return items.IndexOf(label);
    }

    private static List<string> MenuItemTexts(IntPtr menu)
    {
        int count = Win32.GetMenuItemCount(menu);
        var text = new System.Text.StringBuilder(256);
        var items = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            text.Clear();
            Win32.GetMenuStringW(menu, (uint)i, text, text.Capacity, Msg.MF_BYPOSITION);
            items.Add(text.ToString().Replace("&", string.Empty));
        }

        return items;
    }

    private static bool HasStatusBarChild(IntPtr hwnd)
    {
        bool found = false;
        Win32.EnumChildWindows(hwnd, (h, _) =>
        {
            if (ClassOf(h) == "msctls_statusbar32")
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static JsonObject Collect(RepoPaths paths, IntPtr hwnd, Process proc, uint systemDpi, bool allowDpi, bool? statusBarWasVisible)
    {
        var measure = new RegeditMeasure(hwnd, proc.Handle);
        List<RegeditMeasure.ChildInfo> children = measure.Children();
        RegeditMeasure.ChildInfo tree = Single(children, "SysTreeView32");
        RegeditMeasure.ChildInfo list = Single(children, "SysListView32");
        WaitForLoadedKey(measure, tree, list);
        children = measure.Children();
        tree = Single(children, "SysTreeView32");
        list = Single(children, "SysListView32");
        RegeditMeasure.ChildInfo? status = children.FirstOrDefault(c => c.Class == "msctls_statusbar32");

        if (!Win32.GetWindowRect(hwnd, out NativeRect origin))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }

        JsonObject window = measure.MainWindow(out int windowDpi);
        if (windowDpi != RequiredDpi && !allowDpi)
        {
            throw new InvalidOperationException($"metrics refused: regedit window DPI is {windowDpi}, expected {RequiredDpi}.");
        }

        int treeRightRel = tree.Rect.Right - origin.Left;
        int listLeftRel = list.Rect.Left - origin.Left;

        RegeditMeasure.ChildInfo addressBar = children
            .Where(c => c.Class == "Edit")
            .OrderBy(c => c.Rect.Top)
            .First();

        var childArray = new JsonArray();
        foreach (RegeditMeasure.ChildInfo c in children.OrderBy(c => c.Class, StringComparer.Ordinal)
                     .ThenBy(c => c.Id)
                     .ThenBy(c => c.Rect.Top)
                     .ThenBy(c => c.Rect.Left))
        {
            childArray.Add(new JsonObject
            {
                ["className"] = c.Class,
                ["id"] = c.Id,
                ["text"] = c.Text,
                ["visible"] = c.Visible,
                ["parentClass"] = c.ParentClass,
                ["rect"] = new JsonObject
                {
                    ["x"] = c.Rect.Left - origin.Left,
                    ["y"] = c.Rect.Top - origin.Top,
                    ["width"] = c.Rect.Width,
                    ["height"] = c.Rect.Height,
                },
            });
        }

        return new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture metrics",
            ["os"] = OsInfo(),
            ["regedit"] = new JsonObject
            {
                ["path"] = RegeditProcess.RegeditPath,
                ["fileVersion"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(RegeditProcess.RegeditPath).FileVersion,
                ["windowClass"] = WindowClass,
                ["compatLayer"] = "RunAsInvoker",
                ["lastKey"] = LastKeyValue,
                ["selectedTreeItem"] = MixedKeyName,
                ["statusBarMenuItemPresent"] = statusBarWasVisible is not null,
                ["statusBarVisibleAtLaunch"] = JsonValue.Create(statusBarWasVisible),
            },
            ["dpi"] = new JsonObject
            {
                ["system"] = systemDpi,
                ["window"] = windowDpi,
                ["requiredForParity"] = RequiredDpi,
                ["overrideAllowed"] = allowDpi,
            },
            ["window"] = window,
            ["menuBar"] = measure.MenuBar(),
            ["systemMetrics"] = measure.SystemMetrics(),
            ["nonClientFonts"] = RegeditMeasure.NonClientFonts(),
            ["tree"] = measure.Tree(tree),
            ["list"] = measure.ListView(list),
            ["statusBar"] = status is null
                ? new JsonObject { ["present"] = false, ["reason"] = "this regedit build has no status bar; View menu has no Status Bar item" }
                : measure.StatusBar(status),
            ["addressBar"] = new JsonObject
            {
                ["className"] = addressBar.Class,
                ["text"] = addressBar.Text,
                ["rect"] = new JsonObject
                {
                    ["x"] = addressBar.Rect.Left - origin.Left,
                    ["y"] = addressBar.Rect.Top - origin.Top,
                    ["width"] = addressBar.Rect.Width,
                    ["height"] = addressBar.Rect.Height,
                },
            },
            ["splitter"] = new JsonObject
            {
                ["treeRight"] = treeRightRel,
                ["listLeft"] = listLeftRel,
                ["gapPx"] = listLeftRel - treeRightRel,
            },
            ["children"] = childArray,
        };
    }

    private static RegeditMeasure.ChildInfo Single(List<RegeditMeasure.ChildInfo> children, string className)
    {
        var matches = children.Where(c => c.Class == className).ToList();
        if (matches.Count != 1)
        {
            string seen = string.Join(", ", children.Select(c => $"{c.Class}(id {c.Id}, visible {c.Visible}, h {c.Rect.Height})"));
            throw new InvalidOperationException($"expected one {className} child in regedit, found {matches.Count}. Children: {seen}");
        }

        return matches[0];
    }

    private static JsonObject OsInfo()
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false)
            ?? throw new InvalidOperationException("Cannot read the Windows NT version key.");
        return new JsonObject
        {
            ["productName"] = key.GetValue("ProductName")?.ToString(),
            ["currentBuild"] = key.GetValue("CurrentBuild")?.ToString(),
            ["ubr"] = Convert.ToInt32(key.GetValue("UBR") ?? 0),
            ["displayVersion"] = key.GetValue("DisplayVersion")?.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private static void Screenshot(RepoPaths paths, IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out NativeRect r))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        }

        using var bitmap = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!Win32.PrintWindow(hwnd, hdc, Msg.PW_RENDERFULLCONTENT))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow(PW_RENDERFULLCONTENT) failed.");
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ScreenshotPng)!);
        bitmap.Save(paths.ScreenshotPng, ImageFormat.Png);
    }

    // Asks regedit to close with WM_CLOSE and waits for it. Any failure falls through to the caller's kill.
    private static void Close(Process proc, IntPtr hwnd)
    {
        if (!Win32.PostMessageW(hwnd, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "PostMessage(WM_CLOSE) failed.");
        }

        if (!proc.WaitForExit(15000))
        {
            throw new TimeoutException($"regedit pid {proc.Id} did not exit within 15s of WM_CLOSE.");
        }

        Console.WriteLine($"metrics: regedit exited with code {proc.ExitCode} after WM_CLOSE");
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
