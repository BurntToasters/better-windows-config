using System.Diagnostics;
using System.Drawing.Printing;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `print`: reads whether File > Print is enabled with the Computer root, a hive root and a key selected. Then it opens the
// Print command on the seeded key and looks for the Print dialog anywhere on the desktop for up to 60 seconds: a dialog
// owned by regedit, or a window of a print host process (Windows 11 may show a separate window). It never presses Print or
// OK. The host is read with UI Automation (read-only, UiaProbe). No printer is selected and no job is sent. The dialog
// is cancelled with Escape (or IDCANCEL for a regedit dialog). The PDF export is not measured (see failure modes, section 21).
// Tracked: regedit-print.json. Text: local/print-text.json.
internal static class PrintCapture
{
    private const int PrintCommand = 660;
    private const string MoreSettingsName = "More settings";
    private const int RangePanelTemplate = 108;
    private const string PdfPrinter = "Microsoft Print to PDF";
    private const uint VkEscape = 0x1B;
    private const uint WmKeyDownMessage = 0x0100;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(60);

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        ResourceDump dump = ResourceDump.Load(paths);
        var targets = new JsonArray();
        var targetText = new JsonArray();
        var notMeasured = new JsonArray();

        var selections = new (string Name, string LastKey)[]
        {
            ("computer", "Computer"),
            ("hive-hkcu", @"Computer\HKEY_CURRENT_USER"),
            ("key-mixed", UiProbe.LastKeyFor("Mixed")),
        };
        foreach ((string name, string lastKey) in selections)
        {
            try
            {
                (JsonObject tracked, JsonObject text) = PrintEnabled(name, lastKey);
                targets.Add(tracked);
                targetText.Add(text);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"print: {name} not measured: {ex.Message}");
                notMeasured.Add(new JsonObject { ["name"] = name, ["reason"] = Report.StableReason(ex.Message) });
            }
        }

        JsonObject dialogTracked;
        JsonObject dialogText;
        try
        {
            (dialogTracked, dialogText) = PrintDialog(dump);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"print: dialog not measured: {ex.Message}");
            dialogTracked = new JsonObject { ["measured"] = false, ["printed"] = false, ["reason"] = Report.StableReason(ex.Message) };
            dialogText = new JsonObject { ["reason"] = ex.Message };
            notMeasured.Add(new JsonObject { ["name"] = "print-dialog", ["reason"] = Report.StableReason(ex.Message) });
        }

        bool pdfInstalled = PrinterInstalled(PdfPrinter);
        var pdf = new JsonObject
        {
            ["measured"] = false,
            ["reason"] = pdfInstalled
                ? "Printing to the PDF needs the save-path dialog, which this capture does not drive (failure modes, section 22). Nothing was printed."
                : "Microsoft Print to PDF is not installed on this machine. Nothing was printed.",
        };

        Report.WriteJson(paths.PrintJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture print",
            ["os"] = Report.OsInfo(),
            ["classicRegedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, systemDpi, allowDpi),
            ["note"] = "File > Print is read from the File menu popup with each selection. The Print dialog is cancelled. No print job is sent.",
            ["fileMenuPrint"] = targets,
            ["printDialog"] = dialogTracked,
            ["microsoftPrintToPdfInstalled"] = pdfInstalled,
            ["pdfExport"] = pdf,
            ["notMeasured"] = notMeasured,
        });

        Report.WriteJson(paths.LocalText("print-text.json"), new JsonObject
        {
            ["fileMenuPrint"] = targetText,
            ["printDialog"] = dialogText,
        });
    }

    private static (JsonObject Tracked, JsonObject Text) PrintEnabled(string name, string lastKey)
    {
        using RegeditWindow w = UiProbe.StartAt(lastKey, waitForList: false);
        (IntPtr popup, List<UiProbe.MenuRow> rows) = UiProbe.OpenTopMenu(w, 'F');
        try
        {
            UiProbe.MenuRow print = rows.FirstOrDefault(r => r.Id == PrintCommand)
                ?? throw new InvalidOperationException("File menu has no Print item.");
            return (
                new JsonObject { ["target"] = name, ["measured"] = true, ["printEnabled"] = !print.Grayed },
                new JsonObject { ["target"] = name, ["items"] = UiProbe.MenuLocal(rows) });
        }
        finally
        {
            w.ClosePopup(popup);
        }
    }

    private static (JsonObject Tracked, JsonObject Text) PrintDialog(ResourceDump dump)
    {
        using RegeditWindow w = UiProbe.StartAt(UiProbe.LastKeyFor("Mixed"), waitForList: true);
        HashSet<IntPtr> before = AllVisibleTopLevels();
        w.PostMenuCommand("&File", "&Print...\tCtrl+P");

        IntPtr dialog = IntPtr.Zero;
        var deadline = DateTime.UtcNow + DialogTimeout;
        while (DateTime.UtcNow < deadline && dialog == IntPtr.Zero)
        {
            foreach (IntPtr h in AllVisibleTopLevels().OrderBy(x => x.ToInt64()))
            {
                if (!before.Contains(h) && IsPrintHost(w, h))
                {
                    dialog = h;
                    break;
                }
            }

            Thread.Sleep(250);
        }

        if (dialog == IntPtr.Zero)
        {
            List<string> seen = AllVisibleTopLevels().Where(h => !before.Contains(h)).Select(h => Describe(w, h)).ToList();
            throw new InvalidOperationException(
                $"no Print dialog appeared within 60 seconds of File > Print. New visible windows: {(seen.Count == 0 ? "none" : string.Join("; ", seen))}");
        }

        Thread.Sleep(1500);
        bool regeditOwned = WindowPid(dialog) == w.ProcessId;
        var tracked = new JsonObject
        {
            ["measured"] = true,
            ["printed"] = false,
            ["printerVerified"] = false,
            ["regeditOwned"] = regeditOwned,
        };
        // The host differs between runs (a CoreWindow of PrintDialog, or an application frame of the shell), so its class and
        // process name go to the untracked text only. The tracked record keeps what does not vary.
        var text = new JsonObject
        {
            ["title"] = UiProbe.TextOf(dialog),
            ["windowClass"] = RegeditWindow.ClassOf(dialog),
            ["processName"] = ProcessNameOf(dialog),
        };
        try
        {
            if (regeditOwned)
            {
                Observed top = UiProbe.Describe(w, dialog, "print-dialog");
                tracked["controls"] = top.Tracked["controls"]?.DeepClone();
                text["controls"] = top.Text["controls"]?.DeepClone();
                IntPtr panel = FindRangePanel(w, dialog, dump);
                if (panel == IntPtr.Zero)
                {
                    tracked["rangePanelTemplate"] = null;
                    tracked["rangePanel"] = "not found";
                }
                else
                {
                    List<UiProbe.Ctl> controls = UiProbe.Controls(w, panel);
                    tracked["rangePanelTemplate"] = UiProbe.Num(dump.MatchDialog(UiProbe.SortedControlIds(controls)));
                    tracked["rangePanel"] = UiProbe.ControlsTracked(controls);
                    text["rangePanel"] = UiProbe.ControlsLocal(controls);
                }
            }
            else
            {
                // Read-only UI Automation walk of the host. Nothing is invoked, selected or set (section 22).
                try
                {
                    // The host remembers whether More settings was open, and Invoke toggles it. So the page controls are looked
                    // for first, and the link is invoked only when they are not there.
                    UiaProbe.Tree plain = UiaProbe.Walk(dialog);
                    bool alreadyOpen = UiaProbe.Summary(plain)["hasPagesControl"]!.GetValue<bool>();
                    bool expanded = alreadyOpen;
                    UiaProbe.Tree tree = plain;
                    if (!alreadyOpen)
                    {
                        expanded = UiaProbe.ExpandNamed(dialog, MoreSettingsName);
                        if (expanded)
                        {
                            // The page controls can appear after a delay, so the tree is polled for up to 10 seconds.
                            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                            do
                            {
                                Thread.Sleep(500);
                                tree = UiaProbe.Walk(dialog);
                            }
                            while (DateTime.UtcNow < until && !UiaProbe.Summary(tree)["hasPagesControl"]!.GetValue<bool>());
                        }
                    }

                    JsonObject summary = UiaProbe.Summary(tree);
                    summary["moreSettingsFound"] = expanded;
                    tracked["uia"] = summary;
                    text["uia"] = UiaProbe.Local(tree);
                }
                catch (Exception ex)
                {
                    tracked["uia"] = new JsonObject { ["measured"] = false, ["reason"] = Report.StableReason(ex.Message) };
                }

                tracked["rangePanel"] = "not readable as Win32 controls; the UI Automation summary is under uia.";
            }
        }
        finally
        {
            tracked["cancelled"] = Cancel(w, dialog, regeditOwned);
        }

        return (tracked, text);
    }

    // A window that the print of this run can have opened: a regedit dialog, or a window of a process whose name contains
    // "Print", or an application frame window titled "Print". Other new windows are recorded and not touched.
    private static bool IsPrintHost(RegeditWindow w, IntPtr h)
    {
        if (WindowPid(h) == w.ProcessId)
        {
            return RegeditWindow.ClassOf(h) == RegeditWindow.DialogClass;
        }

        string process = ProcessNameOf(h);
        if (process.Contains("print", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RegeditWindow.ClassOf(h) == "ApplicationFrameWindow" && UiProbe.TextOf(h).Contains("Print", StringComparison.OrdinalIgnoreCase);
    }

    // Cancels without confirming: IDCANCEL for a regedit dialog, Escape then WM_CLOSE for a window of another process.
    private static bool Cancel(RegeditWindow w, IntPtr dialog, bool regeditOwned)
    {
        if (regeditOwned)
        {
            w.CloseDialog(dialog);
            return !Win32Ext.IsWindow(dialog) || !Win32.IsWindowVisible(dialog);
        }

        Win32.PostMessageW(dialog, WmKeyDownMessage, new IntPtr((int)VkEscape), IntPtr.Zero);
        if (!WaitGone(dialog, TimeSpan.FromSeconds(10)))
        {
            Win32.PostMessageW(dialog, Wm.CLOSE, IntPtr.Zero, IntPtr.Zero);
            WaitGone(dialog, TimeSpan.FromSeconds(10));
        }

        return !Win32Ext.IsWindow(dialog) || !Win32.IsWindowVisible(dialog);
    }

    private static bool WaitGone(IntPtr h, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Win32Ext.IsWindow(h) || !Win32.IsWindowVisible(h))
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return !Win32Ext.IsWindow(h) || !Win32.IsWindowVisible(h);
    }

    private static HashSet<IntPtr> AllVisibleTopLevels()
    {
        var set = new HashSet<IntPtr>();
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.IsWindowVisible(h))
            {
                set.Add(h);
            }

            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static int WindowPid(IntPtr h)
    {
        Win32.GetWindowThreadProcessId(h, out uint pid);
        return (int)pid;
    }

    private static string ProcessNameOf(IntPtr h)
    {
        try
        {
            return Process.GetProcessById(WindowPid(h)).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string Describe(RegeditWindow w, IntPtr h)
    {
        string owner = WindowPid(h) == w.ProcessId ? "this run regedit" : ProcessNameOf(h);
        return $"class '{RegeditWindow.ClassOf(h)}' process '{owner}'";
    }

    // The range panel is a child window of the Print dialog whose control ids equal template 108. Returns the first such child.
    private static IntPtr FindRangePanel(RegeditWindow w, IntPtr dlg, ResourceDump dump)
    {
        foreach (IntPtr child in w.ChildrenOf(dlg).Where(c => Win32.GetParent(c) == dlg))
        {
            List<UiProbe.Ctl> controls = UiProbe.Controls(w, child);
            if (controls.Count > 0 && dump.MatchDialog(UiProbe.SortedControlIds(controls)) == RangePanelTemplate)
            {
                return child;
            }
        }

        return IntPtr.Zero;
    }

    private static bool PrinterInstalled(string name)
    {
        foreach (string printer in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(printer, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
