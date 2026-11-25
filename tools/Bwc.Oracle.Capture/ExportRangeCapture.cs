using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture;

// `export-range`: the classic Export dialog's range panel, controls 1280 (All), 1281 (Selected branch), and 1282 (the
// branch path edit), for DLG-001 and OQ-15. Writes go only under HKCU\Software\BWC-Oracle, which is created if missing.
// The step opens File > Export, reads the three controls, clicks All and reads again, clicks Selected branch, types a
// key under BWC-Oracle that does not exist, and presses Save into artifacts/oracle/export-range. It then reads any new
// message box, dismisses it, and cancels. Edit text is read with WM_GETTEXT (GetWindowText cannot read another
// process's edit). Numbers and states go to regedit-export-range.json (tracked). Box texts are Microsoft strings, so
// they go to the untracked local/export-range-text.json.
internal static class ExportRangeCapture
{
    private const string OracleLastKey = @"Computer\HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string ClassicOracleKey = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string MissingKey = ClassicOracleKey + @"\DoesNotExist";
    private const int AllId = 1280;
    private const int SelectedBranchId = 1281;
    private const int BranchEditId = 1282;
    private const int OkId = 1;
    private const int FileNameComboId = 1148;
    private const int FileNameEditId = 1152;
    private const int BstChecked = 1;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmCommand = 0x0111;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan BoxTimeout = TimeSpan.FromSeconds(10);

    internal static void Run(RepoPaths paths, bool allowDpi)
    {
        uint systemDpi = OracleSession.RequireDpi(allowDpi);
        RegistryGuard.CreateOracleKey(string.Empty).Dispose();

        string saveDir = Path.Combine(paths.Root, "artifacts", "oracle", "export-range");
        Directory.CreateDirectory(saveDir);
        string savePath = Path.Combine(saveDir, "missing-branch.reg");
        if (File.Exists(savePath))
        {
            File.Delete(savePath);
        }

        RegistryGuard.SetAppletValue(string.Empty, "LastKey", RegType.Sz, SeedSpec.Sz(OracleLastKey));
        using RegeditWindow w = RegeditWindow.Start();
        var before = new HashSet<IntPtr>(w.OwnedDialogs());
        w.PostMenuCommand("&File", "&Export...");
        IntPtr dlg = w.WaitForNewDialog(before, OpenTimeout);
        Thread.Sleep(800);
        uint dialogDpi = Win32.GetDpiForWindow(dlg);

        var steps = new JsonArray
        {
            Snapshot(w, dlg, "initial"),
        };

        IntPtr all = FindById(w, dlg, AllId);
        IntPtr branch = FindById(w, dlg, SelectedBranchId);
        IntPtr edit = FindById(w, dlg, BranchEditId);

        Click(all);
        Thread.Sleep(600);
        steps.Add(Snapshot(w, dlg, "after-all"));

        Click(branch);
        Thread.Sleep(600);
        steps.Add(Snapshot(w, dlg, "after-selected-branch"));

        IntPtr fileEdit = FindById(w, dlg, FileNameEditId);
        IntPtr fileCombo = FindById(w, dlg, FileNameComboId);
        Win32Ext.SendMessageText(edit, Wm.WM_SETTEXT, IntPtr.Zero, MissingKey);
        Win32Ext.SendMessageText(fileEdit != IntPtr.Zero ? fileEdit : fileCombo, Wm.WM_SETTEXT, IntPtr.Zero, savePath);
        Thread.Sleep(300);
        string typedBack = ReadEdit(w, edit);
        string fileBack = ReadEdit(w, fileEdit != IntPtr.Zero ? fileEdit : fileCombo);

        // Every dialog of the process that exists now is excluded. A new one after Save is the message box.
        var dialogsBeforeSave = new HashSet<IntPtr>(RegeditWindow.TopLevel(w.ProcessId, RegeditWindow.DialogClass));
        IntPtr okButton = FindById(w, dlg, OkId);
        bool okFound = okButton != IntPtr.Zero;
        // WM_COMMAND with IDOK (wParam = id, notification 0) to the dialog, the same message the button sends.
        bool saveSent = okFound && Win32.PostMessageW(dlg, WmCommand, new IntPtr(OkId), okButton);

        IntPtr box = WaitForNewDialog(w, dialogsBeforeSave, dlg, BoxTimeout);
        string boxCaption = box == IntPtr.Zero ? string.Empty : WindowText(box);
        string boxText = box == IntPtr.Zero ? string.Empty : StaticText(w, box);
        bool fileWritten = File.Exists(savePath);
        if (box != IntPtr.Zero)
        {
            w.CloseDialog(box);
            Thread.Sleep(500);
        }

        bool dialogOpenAfterBox = Win32Ext.IsWindow(dlg);
        string dialogCaption = WindowText(dlg);
        w.CloseDialog(dlg);

        var root = new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture export-range",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["dpi"] = Report.DpiInfo(systemDpi, dialogDpi, allowDpi),
            ["controls"] = new JsonObject
            {
                ["all"] = AllId,
                ["selectedBranch"] = SelectedBranchId,
                ["branchEdit"] = BranchEditId,
                ["ok"] = OkId,
            },
            ["typedKey"] = MissingKey,
            ["typedKeyReadBack"] = typedBack,
            ["fileNameReadBack"] = fileBack.Replace(paths.Root, "<repo>", StringComparison.OrdinalIgnoreCase),
            ["steps"] = steps,
            ["save"] = new JsonObject
            {
                ["okControlFound"] = okFound,
                ["saveMessage"] = "WM_COMMAND IDOK to the dialog",
                ["saveMessagePosted"] = saveSent,
                ["savePathFolderExists"] = Directory.Exists(saveDir),
            },
            ["missingKey"] = new JsonObject
            {
                ["messageBoxShown"] = box != IntPtr.Zero,
                ["fileWritten"] = fileWritten,
                ["dialogOpenAfterSave"] = dialogOpenAfterBox,
            },
            ["notes"] = "Classic Export is the old common dialog with a child template. Its controls are found by id " +
                "under the dialog. Edit text is read with WM_GETTEXT. A missing-key box appears after Save; its caption " +
                "is in local/export-range-text.json. Its text is not measured: the static text read returns empty " +
                "(two runs), so the RT_STRING 148 text is not confirmed from classic.",
        };

        Report.WriteJson(Path.Combine(paths.OracleDocs, "regedit-export-range.json"), root);
        Report.WriteJson(paths.LocalText("export-range-text.json"), new JsonObject
        {
            ["dialogCaption"] = dialogCaption,
            ["messageCaption"] = boxCaption,
            ["messageText"] = boxText,
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageBuffer(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    // Reads an edit's text in this process's address space through WM_GETTEXT, which works across processes.
    private static string ReadEdit(RegeditWindow w, IntPtr edit)
    {
        if (edit == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = (int)w.Send(edit, WmGetTextLength, IntPtr.Zero, IntPtr.Zero).ToInt64();
        var sb = new StringBuilder(length + 1);
        _ = SendMessageBuffer(edit, WmGetText, new IntPtr(sb.Capacity), sb);
        return sb.ToString();
    }

    private static bool Click(IntPtr control) =>
        control != IntPtr.Zero && Win32.PostMessageW(control, Wm.BM_CLICK, IntPtr.Zero, IntPtr.Zero);

    private static JsonObject Snapshot(RegeditWindow w, IntPtr dlg, string name)
    {
        IntPtr all = FindById(w, dlg, AllId);
        IntPtr branch = FindById(w, dlg, SelectedBranchId);
        IntPtr edit = FindById(w, dlg, BranchEditId);
        return new JsonObject
        {
            ["name"] = name,
            ["all"] = new JsonObject { ["present"] = all != IntPtr.Zero, ["checked"] = IsChecked(w, all) },
            ["selectedBranch"] = new JsonObject { ["present"] = branch != IntPtr.Zero, ["checked"] = IsChecked(w, branch) },
            ["branchEdit"] = new JsonObject
            {
                ["present"] = edit != IntPtr.Zero,
                ["enabled"] = edit != IntPtr.Zero && Win32Ext.IsWindowEnabled(edit),
                ["text"] = ReadEdit(w, edit),
            },
        };
    }

    private static bool IsChecked(RegeditWindow w, IntPtr radio) =>
        radio != IntPtr.Zero && w.Send(radio, Wm.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == BstChecked;

    // The template's child controls are children of a child window of the common dialog, so the search goes down.
    private static IntPtr FindById(RegeditWindow w, IntPtr parent, int id)
    {
        foreach (IntPtr child in w.ChildrenOf(parent))
        {
            if (Win32.GetDlgCtrlID(child) == id)
            {
                return child;
            }

            IntPtr nested = FindById(w, child, id);
            if (nested != IntPtr.Zero)
            {
                return nested;
            }
        }

        return IntPtr.Zero;
    }

    // A dialog of the process that was not on screen before Save. The owner's own dialog is not a candidate.
    private static IntPtr WaitForNewDialog(RegeditWindow w, HashSet<IntPtr> existing, IntPtr owner, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            foreach (IntPtr h in RegeditWindow.TopLevel(w.ProcessId, RegeditWindow.DialogClass))
            {
                if (h != owner && !existing.Contains(h))
                {
                    return h;
                }
            }

            Thread.Sleep(150);
        }

        return IntPtr.Zero;
    }

    // Every static control of the box, at any depth, read with WM_GETTEXT. The box text is one or more statics.
    private static string StaticText(RegeditWindow w, IntPtr box)
    {
        var parts = new List<string>();
        CollectStatics(w, box, parts);
        return string.Join("\n", parts.Where(p => p.Length > 0));
    }

    private static void CollectStatics(RegeditWindow w, IntPtr parent, List<string> parts)
    {
        foreach (IntPtr child in w.ChildrenOf(parent))
        {
            if (string.Equals(RegeditWindow.ClassOf(child), "Static", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(ReadEdit(w, child));
            }

            CollectStatics(w, child, parts);
        }
    }

    private static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        _ = Win32.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
