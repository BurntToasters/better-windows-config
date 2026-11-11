using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Print;

/// <summary>
/// Drives File > Print and Ctrl+P against a seeded sandbox key. The print dialog is always
/// cancelled, so no spooler job or PDF file is produced. Windows 11 25H2 presents the system
/// PrintDialog (an ApplicationFrameWindow hosted by PrintDialog.exe) for owned Win32 print
/// requests; older builds present the classic #32770 dialog. Both shapes are accepted, and the
/// main window must be disabled in both cases.
/// </summary>
[TestClass]
public sealed class PrintTests
{
    private const int MenuFile = 512;
    private const int CommandPrint = 660;
    private const string ClassicDialogClass = "#32770";
    private const string SystemDialogClass = "ApplicationFrameWindow";
    private const string ErrorDialogTitle = "Registry Editor";
    private const string SeedValueName = "PrintValue";
    private const string SeedDwordName = "PrintDword";
    private const string ChildKeyName = "PrintChild";
    private const string ChildValueName = "ChildValue";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Print")]
    public void Print_OwnedDialogCancelsAndCommandStateFollowsSelection()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("print.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "print");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            int staleDialogs = CloseStalePrintDialogs();
            E2EResults.Record(
                "print.stale-dialogs",
                staleDialogs == 0,
                $"closed={staleDialogs}.");

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            CheckComputerDisablesPrint(session, driver);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seeded = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeedValueName, StringComparer.Ordinal),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "print.navigate",
                navigated && seeded,
                $"navigated={navigated}, seededRows={seeded}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            if (!navigated || !seeded)
            {
                FailIfNeeded();
                return;
            }

            if (!CheckPrintMenuEnabled(session, driver))
            {
                FailIfNeeded();
                return;
            }

            bool menuDialog = InvokePrintFromFileMenu(session, driver, out PrintDialogInfo menuInfo);
            if (menuDialog)
            {
                CancelAndRecord(session, "print", menuInfo);
            }

            bool ctrlPDialog = FocusTreeAndPressCtrlP(session, config, out PrintDialogInfo ctrlPInfo);
            if (ctrlPDialog)
            {
                CancelAndRecord(session, "print.ctrlp", ctrlPInfo);
            }

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckComputerDisablesPrint(AppSession session, EditingDriver driver)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        bool opened = TryOpenFileMenu(driver);
        AutomationElement? item = opened ? driver.WaitForElement("MenuCommand_" + CommandPrint, TimeSpan.FromSeconds(5)) : null;
        bool enabled = SafeIsEnabled(item);

        E2EResults.Record(
            "print.menu.computer-disabled",
            opened && item is not null && !enabled,
            $"fileMenu={opened}, itemPresent={item is not null}, enabled={enabled}.");

        CloseMenu(session);
    }

    private static bool CheckPrintMenuEnabled(AppSession session, EditingDriver driver)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        bool opened = TryOpenFileMenu(driver);
        AutomationElement? item = opened ? driver.WaitForElement("MenuCommand_" + CommandPrint, TimeSpan.FromSeconds(5)) : null;
        bool enabled = SafeIsEnabled(item);

        bool recorded = E2EResults.Record(
            "print.menu.key-enabled",
            opened && item is not null && enabled,
            $"fileMenu={opened}, itemPresent={item is not null}, enabled={enabled}.");
        return recorded;
    }

    private static bool InvokePrintFromFileMenu(
        AppSession session,
        EditingDriver driver,
        out PrintDialogInfo dialog)
    {
        dialog = default;
        AutomationElement? item = driver.WaitForElement("MenuCommand_" + CommandPrint, TimeSpan.FromSeconds(5));
        bool activated = Activate(item);
        E2EResults.Record(
            "print.menu.invoked",
            activated,
            $"activated={activated}, itemPresent={item is not null}.");

        return activated && WaitForPrintDialogCheck(session, "print", out dialog);
    }

    private static bool FocusTreeAndPressCtrlP(
        AppSession session,
        E2EConfig config,
        out PrintDialogInfo dialog)
    {
        dialog = default;
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            config.RunId);

        try
        {
            row?.Focus();
        }
        catch (Exception)
        {
            // The send below records a failed foreground check when focus is unusable.
        }

        Thread.Sleep(200);
        bool sent;
        try
        {
            ControlTestSupport.SendKeys(session, "CONTROL+KEY_P");
            sent = true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("print.ctrlp.send", false, ex.Message);
            sent = false;
        }

        if (!sent)
        {
            return false;
        }

        return WaitForPrintDialogCheck(session, "print.ctrlp", out dialog);
    }

    /// <summary>
    /// Waits for the print dialog and records the shared dialog, owner, and main-disabled checks.
    /// Returns true when the caller should cancel it. A missing dialog is reported as an
    /// informational pass only when the machine has no default printer.
    /// </summary>
    private static bool WaitForPrintDialogCheck(
        AppSession session,
        string prefix,
        out PrintDialogInfo dialog)
    {
        dialog = WaitForPrintDialog(session.ProcessId, session.MainHwnd, TimeSpan.FromSeconds(10));
        if (dialog.Hwnd == 0)
        {
            nint errorDialog = FindAnyDialog(session.ProcessId);
            bool errorClosed = errorDialog != 0 && TryCancelDialog(errorDialog, out _);
            bool hasDefaultPrinter = HasDefaultPrinter();
            bool alive = !session.IsEnabled || ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(5));

            string note =
                $"no print dialog appeared, defaultPrinter={hasDefaultPrinter}, " +
                $"errorDialog=0x{errorDialog.ToInt64():X}, errorClosed={errorClosed}, mainEnabled={session.IsEnabled}. " +
                "visible dialogs: " + DescribeDialogs();
            bool pass = !hasDefaultPrinter && alive;
            E2EResults.Record(prefix + ".dialog", pass, pass ? "informational skip: " + note : note);
            E2EResults.Record(prefix + ".owner", pass, pass ? "informational skip: no dialog." : "no dialog.");
            E2EResults.Record(prefix + ".cancel", pass, pass ? "informational skip: no dialog." : "no dialog.");
            E2EResults.Record(prefix + ".reenabled", pass, pass ? "informational skip: no dialog." : "no dialog.");
            return false;
        }

        string host = dialog.IsSystemDialog
            ? "Windows 11 system PrintDialog host"
            : "classic common dialog";
        E2EResults.Record(
            prefix + ".dialog",
            true,
            $"{host}, class='{dialog.ClassName}', hwnd=0x{dialog.Hwnd.ToInt64():X}, " +
            $"pid={dialog.ProcessId}, title='{dialog.Title}'.");

        bool mainDisabled = !Win32.IsWindowEnabled(session.MainHwnd);
        bool ownerPass;
        string ownerNote;
        if (dialog.IsSystemDialog)
        {
            // The Windows 11 host is a separate UWP frame and exposes no GW_OWNER. PrintDlgW
            // still disables the owner HWND for the lifetime of the request, which is the
            // observable ownership contract on this build.
            ownerPass = mainDisabled;
            ownerNote =
                $"system host owner=0x{dialog.Owner.ToInt64():X}; modal contract uses disabled main={mainDisabled}.";
        }
        else
        {
            ownerPass = dialog.Owner == session.MainHwnd;
            ownerNote = $"owner=0x{dialog.Owner.ToInt64():X}, main=0x{session.MainHwnd.ToInt64():X}.";
        }

        E2EResults.Record(prefix + ".owner", ownerPass, ownerNote);
        E2EResults.Record(
            prefix + ".main-disabled",
            mainDisabled,
            $"main enabled while dialog open={!mainDisabled}.");

        return true;
    }

    private static void CancelAndRecord(AppSession session, string prefix, PrintDialogInfo dialog)
    {
        bool cancelled = TryCancelDialog(dialog.Hwnd, out string method);
        bool gone = ControlTestSupport.WaitUntil(
            () => !IsDialogVisible(dialog.Hwnd),
            TimeSpan.FromSeconds(8));
        E2EResults.Record(
            prefix + ".cancel",
            cancelled && gone,
            $"method={method}, cancelled={cancelled}, gone={gone}, hwnd=0x{dialog.Hwnd.ToInt64():X}.");

        bool enabledAgain = ControlTestSupport.WaitUntil(
            () => Win32.IsWindowEnabled(session.MainHwnd),
            TimeSpan.FromSeconds(8));
        bool alive = session.Find(ControlTestSupport.TreeControl) is not null;
        E2EResults.Record(
            prefix + ".reenabled",
            enabledAgain && alive,
            $"mainEnabledAfter={enabledAgain}, treePresent={alive}.");
    }

    private static bool TryOpenFileMenu(EditingDriver driver)
    {
        try
        {
            driver.OpenMenu(MenuFile);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("print.file-menu", false, ex.Message);
            return false;
        }
    }

    private static void CloseMenu(AppSession session)
    {
        try
        {
            ControlTestSupport.SendKeys(session, "ESCAPE");
            Thread.Sleep(300);
        }
        catch (Exception)
        {
            // The next menu invocation also dismisses a stale flyout if necessary.
        }
    }

    private static bool Activate(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            element.Focus();
            Thread.Sleep(150);
            Keyboard.Type(VirtualKeyShort.RETURN);
            return true;
        }
        catch (Exception)
        {
            try
            {
                element.Click();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static bool SafeIsEnabled(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.IsEnabled.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static PrintDialogInfo WaitForPrintDialog(
        int processId,
        nint mainHwnd,
        TimeSpan timeout)
    {
        PrintDialogInfo found = default;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                found = FindPrintDialog(processId, mainHwnd);
                return found.Hwnd != 0;
            },
            timeout);
        return found;
    }

    private static PrintDialogInfo FindPrintDialog(int processId, nint mainHwnd)
    {
        PrintDialogInfo found = default;
        _ = EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint windowProcessId);
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                string className = ClassOf(hwnd);
                string title = TitleOf(hwnd);

                if (windowProcessId == (uint)processId &&
                    string.Equals(className, ClassicDialogClass, StringComparison.Ordinal) &&
                    !string.Equals(title, ErrorDialogTitle, StringComparison.Ordinal) &&
                    Win32.GetWindow(hwnd, Win32.GvOwner) == mainHwnd)
                {
                    found = new PrintDialogInfo(
                        hwnd,
                        IsSystemDialog: false,
                        (int)windowProcessId,
                        className,
                        title,
                        Win32.GetWindow(hwnd, Win32.GvOwner));
                    return false;
                }

                if (string.Equals(className, SystemDialogClass, StringComparison.Ordinal) &&
                    title.EndsWith(" - Print", StringComparison.Ordinal))
                {
                    found = new PrintDialogInfo(
                        hwnd,
                        IsSystemDialog: true,
                        (int)windowProcessId,
                        className,
                        title,
                        Win32.GetWindow(hwnd, Win32.GvOwner));
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    private static int CloseStalePrintDialogs()
    {
        var handles = new List<nint>();
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (IsWindowVisible(hwnd) &&
                    string.Equals(ClassOf(hwnd), SystemDialogClass, StringComparison.Ordinal) &&
                    TitleOf(hwnd).EndsWith(" - Print", StringComparison.Ordinal))
                {
                    handles.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);

        foreach (nint hwnd in handles)
        {
            _ = TryCancelDialog(hwnd, out _);
            _ = ControlTestSupport.WaitUntil(
                () => !IsDialogVisible(hwnd),
                TimeSpan.FromSeconds(4));
        }

        return handles.Count;
    }

    private static nint FindAnyDialog(int processId)
    {
        nint found = 0;
        _ = EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint windowProcessId);
                if (windowProcessId != (uint)processId ||
                    !IsWindowVisible(hwnd) ||
                    !string.Equals(ClassOf(hwnd), ClassicDialogClass, StringComparison.Ordinal))
                {
                    return true;
                }

                found = hwnd;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private static bool TryCancelDialog(nint dialog, out string method)
    {
        method = "none";

        try
        {
            using var automation = new UIA3Automation();
            AutomationElement root = automation.FromHandle(dialog);
            AutomationElement? cancel = root.FindFirstDescendant(
                cf => cf.ByName("Cancel").And(cf.ByControlType(ControlType.Button)));
            if (cancel is not null)
            {
                try
                {
                    if (cancel.Patterns.Invoke.IsSupported)
                    {
                        cancel.Patterns.Invoke.Pattern.Invoke();
                    }
                    else
                    {
                        cancel.Click();
                    }
                }
                catch (Exception)
                {
                    cancel.Click();
                }

                method = "cancel-button";
                if (ControlTestSupport.WaitUntil(
                        () => !IsDialogVisible(dialog),
                        TimeSpan.FromSeconds(4)))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // The dialog may be a classic one whose UIA tree is not ready; Escape follows.
        }

        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            if (!EnsureForeground(dialog))
            {
                return false;
            }

            Thread.Sleep(250);
            try
            {
                Keyboard.Type(VirtualKeyShort.ESCAPE);
            }
            catch (Exception)
            {
                return false;
            }

            method = method == "none" ? "escape" : method + "-then-escape";
            if (ControlTestSupport.WaitUntil(
                    () => !IsDialogVisible(dialog),
                    TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }

        return !IsDialogVisible(dialog);
    }

    private static bool IsDialogVisible(nint hwnd) =>
        Win32.IsWindow(hwnd) && IsWindowVisible(hwnd);

    private static bool EnsureForeground(nint hwnd)
    {
        if (Win32.IsForeground(hwnd))
        {
            return true;
        }

        Win32.ShowWindow(hwnd, Win32.SwRestore);
        _ = Win32.SetForegroundWindow(hwnd);
        if (ControlTestSupport.WaitUntil(
                () => Win32.IsForeground(hwnd),
                TimeSpan.FromMilliseconds(600)))
        {
            return true;
        }

        nint foreground = Win32.GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 &&
            foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            _ = Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return ControlTestSupport.WaitUntil(
            () => Win32.IsForeground(hwnd),
            TimeSpan.FromSeconds(2));
    }

    private static bool HasDefaultPrinter()
    {
        uint size = 0;
        _ = GetDefaultPrinter(null, ref size);
        if (size == 0)
        {
            return false;
        }

        var buffer = new StringBuilder((int)size);
        return GetDefaultPrinter(buffer, ref size) && buffer.Length > 0;
    }

    private static string DescribeDialogs()
    {
        var builder = new StringBuilder();
        _ = EnumWindows(
            (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                string className = ClassOf(hwnd);
                if (!string.Equals(className, ClassicDialogClass, StringComparison.Ordinal) &&
                    !string.Equals(className, SystemDialogClass, StringComparison.Ordinal))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out uint processId);
                builder.Append(
                    $"[pid={processId}, proc='{ProcessNameOf(processId)}', class='{className}', " +
                    $"title='{TitleOf(hwnd)}', owner=0x{Win32.GetWindow(hwnd, Win32.GvOwner).ToInt64():X}]");
                return true;
            },
            IntPtr.Zero);

        return builder.Length == 0 ? "<none>" : builder.ToString();
    }

    private static string ProcessNameOf(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string ClassOf(nint hwnd)
    {
        var className = new StringBuilder(64);
        _ = GetClassName(hwnd, className, className.Capacity);
        return className.ToString();
    }

    private static string TitleOf(nint hwnd)
    {
        var title = new StringBuilder(256);
        _ = GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(SeedValueName, "printed", RegistryValueKind.String);
            root.SetValue(SeedDwordName, 7, RegistryValueKind.DWord);
        }

        using var child = sandbox.CreateSubKey(ChildKeyName);
        child.SetValue(ChildValueName, "child", RegistryValueKind.String);
    }

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDataRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The application exit path may still hold a settings file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Print checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private readonly record struct PrintDialogInfo(
        nint Hwnd,
        bool IsSystemDialog,
        int ProcessId,
        string ClassName,
        string Title,
        nint Owner);

#pragma warning disable SYSLIB1054 // The test-only P/Invoke surface does not need LibraryImport plumbing.
    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, EntryPoint = "GetDefaultPrinterW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(StringBuilder? name, ref uint size);
#pragma warning restore SYSLIB1054
}
