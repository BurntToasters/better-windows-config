using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.E2E.Find;

/// <summary>
/// Drives the classic Find dialog against an isolated sandbox subtree. The run root is the only
/// search root because the process receives BWC_E2E_SANDBOX_ROOT, so the checks do not depend on
/// unrelated machine registry contents and the no-hit search finishes in bounded time.
/// </summary>
[TestClass]
public sealed class FindTests
{
    private const string RunRootDataValue = "AlphaValue";
    private const string ValueHitName = "DataNeedle";
    private const string DataHitName = "DataNeedle";
    private const string DataSearchText = "needle";
    private const string MissingText = "NoSuchText";
    private const string FinishedMessage = "Finished searching through the registry.";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Find")]
    [TestCategory("E2EOnly")]
    public void Find_SearchesKeysValuesAndData()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = SeedSandbox(config);
        string dataRoot = CreateFreshDataRoot(config, "find");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        try
        {
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new FindDialogDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            Record("find.navigate.runRoot", navigated, $"navigated to run root={navigated}.");

            // F3 with no prior query opens Find instead of starting an invisible search.
            bool f3Opened = SendAccelerator(session, "F3") && driver.WaitForOpen();
            Record("find.f3.noPriorOpens", f3Opened, $"Find dialog after F3 opened={f3Opened}.");
            if (f3Opened)
            {
                bool cancelled = driver.Invoke("DialogSecondary") && driver.WaitForClosed();
                Record("find.f3.cancelCloses", cancelled, $"Cancel closed={cancelled}.");
            }

            // The Edit > Find menu command is wired to the same dialog.
            bool menuOpened = driver.OpenMenu(513, 662) && driver.WaitForOpen();
            Record("find.menu.open", menuOpened, $"Edit > Find opened={menuOpened}.");
            if (menuOpened)
            {
                bool menuCancelled = driver.Invoke("DialogSecondary") && driver.WaitForClosed();
                Record("find.menu.cancelCloses", menuCancelled, $"Cancel closed={menuCancelled}.");
            }

            bool controlFOpened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForOpen();
            Record("find.ctrlF.open", controlFOpened, $"Ctrl+F opened={controlFOpened}.");
            if (!controlFOpened)
            {
                FailIfNeeded();
                return;
            }

            // Keys only: the first hit is the Alpha key, the next hit is Alphabet.
            bool preparedKeys = driver.SetText("FindText", "Alpha") &&
                driver.SetChecked("FindKeys", true) &&
                driver.SetChecked("FindValues", false) &&
                driver.SetChecked("FindData", false) &&
                driver.SetChecked("FindWholeString", false);
            Record("find.keys.prepare", preparedKeys, "FindText=Alpha, Keys only.");

            bool entered = preparedKeys && driver.Focus("FindText") && driver.SendEnter();
            Record("find.keys.enter", entered, $"Enter sent={entered}.");
            bool alphaStatus = driver.WaitForSearchIdle(TimeSpan.FromSeconds(30));
            bool alphaSelected = ControlTestSupport.WaitForTreeSelection(
                session,
                "Alpha",
                TimeSpan.FromSeconds(30));
            Record(
                "find.keys.first",
                alphaStatus && alphaSelected,
                $"status='{driver.StatusText()}', Alpha selected={alphaSelected}.");

            bool nextClicked = driver.Invoke("DialogPrimary");
            bool alphabetStatus = nextClicked && driver.WaitForSearchIdle(TimeSpan.FromSeconds(30));
            bool alphabetSelected = ControlTestSupport.WaitForTreeSelection(
                session,
                "Alphabet",
                TimeSpan.FromSeconds(30));
            Record(
                "find.keys.second",
                alphabetStatus && alphabetSelected,
                $"status='{driver.StatusText()}', Alphabet selected={alphabetSelected}.");

            // Values only: DataNeedle's value name matches and its list row is selected.
            bool resetForValue = SelectRunRoot(session, config);
            Record("find.values.reset", resetForValue, $"run root reselected={resetForValue}.");

            bool preparedValue = driver.SetText("FindText", ValueHitName) &&
                driver.SetChecked("FindKeys", false) &&
                driver.SetChecked("FindValues", true) &&
                driver.SetChecked("FindData", false);
            bool valueClicked = preparedValue && driver.Invoke("DialogPrimary");
            bool valueStatus = valueClicked && driver.WaitForSearchIdle(TimeSpan.FromSeconds(30));
            bool valueRowSelected = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.ListControl,
                        ControlType.DataItem,
                        ValueHitName)),
                TimeSpan.FromSeconds(30));
            Record(
                "find.values.hit",
                valueStatus && valueRowSelected,
                $"status='{driver.StatusText()}', row '{ValueHitName}' selected={valueRowSelected}.");

            // Data only: the value data contains the needle text and maps back to its row.
            bool resetForData = SelectRunRoot(session, config);
            Record("find.data.reset", resetForData, $"run root reselected={resetForData}.");

            bool preparedData = driver.SetText("FindText", DataSearchText) &&
                driver.SetChecked("FindKeys", false) &&
                driver.SetChecked("FindValues", false) &&
                driver.SetChecked("FindData", true);
            bool dataClicked = preparedData && driver.Invoke("DialogPrimary");
            bool dataStatus = dataClicked && driver.WaitForSearchIdle(TimeSpan.FromSeconds(30));
            bool dataRowSelected = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.ListControl,
                        ControlType.DataItem,
                        DataHitName)),
                TimeSpan.FromSeconds(30));
            Record(
                "find.data.hit",
                dataStatus && dataRowSelected,
                $"status='{driver.StatusText()}', row '{DataHitName}' selected={dataRowSelected}.");

            // No hit: the oracle finished message is shown and the dialog stays open.
            bool resetForMiss = SelectRunRoot(session, config);
            Record("find.miss.reset", resetForMiss, $"run root reselected={resetForMiss}.");

            bool preparedMiss = driver.SetText("FindText", MissingText) &&
                driver.SetChecked("FindKeys", true) &&
                driver.SetChecked("FindValues", false) &&
                driver.SetChecked("FindData", false);
            bool missClicked = preparedMiss && driver.Invoke("DialogPrimary");
            bool finished = missClicked && driver.WaitForStatusText(
                FinishedMessage,
                TimeSpan.FromSeconds(30));
            bool staysOpen = driver.IsOpen;
            Record(
                "find.miss.finished",
                finished && staysOpen,
                $"status='{driver.StatusText()}', dialog open={staysOpen}.");

            // The finished message is modal to the Find dialog, so it is dismissed with OK before Find Next again.
            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
            bool dismissed = ControlTestSupport.WaitUntil(() => driver.StatusText() != FinishedMessage, TimeSpan.FromSeconds(10));
            bool missAgain = dismissed && driver.Invoke("DialogPrimary") && driver.WaitForStatusText(
                FinishedMessage,
                TimeSpan.FromSeconds(10));
            Record(
                "find.miss.noWrap",
                missAgain,
                $"second Find Next status='{driver.StatusText()}'.");

            bool cancelClosed = driver.Invoke("DialogSecondary") && driver.WaitForClosed();
            Record("find.miss.cancelCloses", cancelClosed, $"Cancel closed={cancelClosed}.");

            // Reopening restores the text and flags from the in-memory settings.
            bool reopened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForOpen();
            string restoredText = reopened ? driver.TextOf("FindText") : "(missing)";
            bool restoredFlags = reopened &&
                driver.IsChecked("FindKeys") &&
                !driver.IsChecked("FindValues") &&
                !driver.IsChecked("FindData");
            Record(
                "find.reopen.restored",
                reopened && string.Equals(restoredText, MissingText, StringComparison.Ordinal) && restoredFlags,
                $"text='{restoredText}', keys/values/data restored={restoredFlags}.");

            bool escapeSent = reopened && driver.Focus("FindText") && driver.SendEscape();
            bool escapeClosed = escapeSent && driver.WaitForClosed();
            Record(
                "find.escape.closes",
                escapeClosed,
                $"Escape sent={escapeSent}, closed={escapeClosed}.");

            // F3 repeats the last completed query from the last hit.
            bool f3Reopened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForOpen();
            bool f3Prepared = f3Reopened &&
                driver.SetText("FindText", "Alpha") &&
                driver.SetChecked("FindKeys", true) &&
                driver.SetChecked("FindValues", false) &&
                driver.SetChecked("FindData", false);
            bool f3First = f3Prepared && driver.Invoke("DialogPrimary") &&
                ControlTestSupport.WaitForTreeSelection(session, "Alpha", TimeSpan.FromSeconds(30));
            Record("find.f3.prepare", f3First, $"Alpha selected={f3First}.");
            bool f3Cancelled = driver.Invoke("DialogSecondary") && driver.WaitForClosed();
            Record("find.f3.cancel", f3Cancelled, $"Cancel closed={f3Cancelled}.");

            bool f3Sent = f3Cancelled && FocusRunRootRow(session, config) &&
                SendAccelerator(session, "F3");
            bool f3Next = f3Sent && ControlTestSupport.WaitForTreeSelection(
                session,
                "Alphabet",
                TimeSpan.FromSeconds(30));
            Record(
                "find.f3.next",
                f3Next,
                $"F3 sent={f3Sent}, Alphabet selected={f3Next}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static SandboxRegistry SeedSandbox(E2EConfig config)
    {
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();

        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(RunRootDataValue, "alpha value", RegistryValueKind.String);
            root.SetValue(ValueHitName, "needle", RegistryValueKind.String);
            root.SetValue(
                "MultiNeedle",
                new[] { "haystack", "needle in haystack" },
                RegistryValueKind.MultiString);
            root.SetValue("Dword42", 42, RegistryValueKind.DWord);
        }

        using (var alpha = sandbox.CreateSubKey("Alpha"))
        {
            alpha.SetValue("Inside", "alpha child", RegistryValueKind.String);
        }

        using (sandbox.CreateSubKey("Alphabet"))
        {
        }

        using (sandbox.CreateSubKey("Beta"))
        {
        }

        return sandbox;
    }

    /// <summary>
    /// Selects Beta and then the run root. The two selections force DetailsList.SetRows, which
    /// clears any selected value row so the next search starts at the run key name.
    /// </summary>
    private static bool SelectRunRoot(AppSession session, E2EConfig config)
    {
        if (!ControlTestSupport.WaitForTreeSelection(session, "Beta", TimeSpan.FromMilliseconds(250)))
        {
            var beta = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                "Beta");
            ControlTestSupport.Select(beta);
            _ = ControlTestSupport.WaitForTreeSelection(session, "Beta");
        }

        var runRoot = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            config.RunId);
        ControlTestSupport.Select(runRoot);
        return ControlTestSupport.WaitForTreeSelection(session, config.RunId);
    }

    /// <summary>Gives the main window keyboard focus before an accelerator is sent.</summary>
    private static bool FocusRunRootRow(AppSession session, E2EConfig config)
    {
        var row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            config.RunId);
        if (row is null)
        {
            return false;
        }

        try
        {
            row.Click();
        }
        catch (Exception)
        {
            return false;
        }

        return ControlTestSupport.WaitForTreeSelection(session, config.RunId);
    }

    private static bool SendAccelerator(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.accelerator", false, $"'{key}' failed: {ex.Message}");
            return false;
        }
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

    private static void Record(string id, bool pass, string notes) =>
        _ = E2EResults.Record(id, pass, notes);

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Find checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    /// Finds the modal Find controls and the real dialog HWND. UIA reports the owned dialog's
    /// contents under the main window element and reports the main HWND as the window handle, so
    /// keyboard input must use the Win32 Find window found by title. The UIA root stays the main
    /// window handle that carries DialogRoot and the dialog controls.
    /// </summary>
    private sealed class FindDialogDriver : IDisposable
    {
        private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

        private readonly AppSession _session;
        private readonly UIA3Automation _automation;
        private nint _uiHwnd;
        private nint _dialogHwnd;

        internal FindDialogDriver(AppSession session)
        {
            _session = session;
            _automation = new UIA3Automation();
        }

        internal bool IsOpen => FindWin32DialogWindow() != 0;

        internal bool WaitForOpen(TimeSpan? timeout = null)
        {
            Window? found = null;
            _ = ControlTestSupport.WaitUntil(
                () => (found = FindDialogWindow()) is not null,
                timeout ?? UiTimeout);

            if (found is null)
            {
                return false;
            }

            _uiHwnd = found.Properties.NativeWindowHandle.Value;
            _dialogHwnd = FindWin32DialogWindow();
            if (_dialogHwnd == 0)
            {
                _ = ControlTestSupport.WaitUntil(
                    () => (_dialogHwnd = FindWin32DialogWindow()) != 0,
                    timeout ?? UiTimeout);
            }

            if (_dialogHwnd == 0)
            {
                _dialogHwnd = _uiHwnd;
            }

            _ = WaitForElement("FindText", UiTimeout);
            return _uiHwnd != 0;
        }

        internal bool WaitForClosed(TimeSpan? timeout = null) =>
            ControlTestSupport.WaitUntil(
                () => FindDialogWindow() is null && FindWin32DialogWindow() == 0,
                timeout ?? TimeSpan.FromSeconds(10));

        internal bool OpenMenu(params int[] commandIds)
        {
            if (!ControlTestSupport.EnsureForeground(_session))
            {
                return false;
            }

            foreach (int commandId in commandIds)
            {
                AutomationElement? element = WaitForElementAcrossWindows(
                    "MenuCommand_" + commandId,
                    UiTimeout);
                if (element is null)
                {
                    return false;
                }

                try
                {
                    if (element.Patterns.Invoke.IsSupported)
                    {
                        element.Patterns.Invoke.Pattern.Invoke();
                    }
                    else
                    {
                        element.Click();
                    }
                }
                catch (Exception)
                {
                    try
                    {
                        element.Click();
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal bool SetText(string automationId, string text)
        {
            AutomationElement? element = WaitForElement(automationId, UiTimeout);
            if (element is null)
            {
                return false;
            }

            try
            {
                element.AsTextBox().Text = text;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal bool Focus(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, UiTimeout);
            if (element is null)
            {
                return false;
            }

            try
            {
                element.Focus();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal bool SetChecked(string automationId, bool isChecked)
        {
            AutomationElement? element = WaitForElement(automationId, UiTimeout);
            if (element is null)
            {
                return false;
            }

            try
            {
                if (element.Patterns.Toggle.IsSupported &&
                    element.Patterns.Toggle.Pattern.ToggleState.Value is { } state)
                {
                    bool current = state == ToggleState.On;
                    if (current != isChecked)
                    {
                        element.Patterns.Toggle.Pattern.Toggle();
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                // Fall back to a click.
            }

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

        internal bool IsChecked(string automationId)
        {
            AutomationElement? element = FindElement(automationId);
            if (element is null)
            {
                return false;
            }

            try
            {
                if (element.Patterns.Toggle.IsSupported &&
                    element.Patterns.Toggle.Pattern.ToggleState.Value is { } state)
                {
                    return state == ToggleState.On;
                }
            }
            catch (Exception)
            {
                // Fall through to invalid.
            }

            return false;
        }

        internal bool Invoke(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, UiTimeout);
            if (element is null)
            {
                return false;
            }

            try
            {
                if (element.Patterns.Invoke.IsSupported)
                {
                    element.Patterns.Invoke.Pattern.Invoke();
                    return true;
                }
            }
            catch (Exception)
            {
                // Fall back to a click.
            }

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

        internal string TextOf(string automationId)
        {
            AutomationElement? element = FindElement(automationId);
            if (element is null)
            {
                return string.Empty;
            }

            try
            {
                if (element.Patterns.Value.IsSupported)
                {
                    return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // TextBlock exposes its text as Name.
            }

            try
            {
                return element.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The classic Find dialog has no status line. A finished search is a message box, so its text is
        /// the status. A status line, when present, is read as before.
        /// </summary>
        internal string StatusText()
        {
            AutomationElement? message = FindAcrossWindows("DialogMessage");
            if (message is not null)
            {
                return ControlTestSupport.SafeName(message);
            }

            return TextOf("FindStatus");
        }

        /// <summary>A hit leaves the dialog open with Find Next enabled again; the search is idle when it is.</summary>
        internal bool WaitForSearchIdle(TimeSpan timeout) =>
            ControlTestSupport.WaitUntil(
                () => FindElement("DialogPrimary") is { IsEnabled: true },
                timeout);

        internal bool WaitForStatus(string fragment, TimeSpan timeout) =>
            ControlTestSupport.WaitUntil(
                () => StatusText().Contains(fragment, StringComparison.Ordinal),
                timeout);

        internal bool WaitForStatusText(string expected, TimeSpan timeout) =>
            ControlTestSupport.WaitUntil(
                () => string.Equals(StatusText(), expected, StringComparison.Ordinal),
                timeout);

        internal bool SendEnter() => SendVirtualKey(VirtualKeyShort.RETURN);

        internal bool SendEscape() => SendVirtualKey(VirtualKeyShort.ESCAPE);

        public void Dispose()
        {
            _automation.Dispose();
        }

        private bool SendVirtualKey(VirtualKeyShort key)
        {
            if (_dialogHwnd == 0 || !Win32.IsWindow(_dialogHwnd))
            {
                return false;
            }

            if (!Win32.IsForeground(_dialogHwnd))
            {
                _ = Win32.ShowWindow(_dialogHwnd, Win32.SwRestore);
                _ = Win32.SetForegroundWindow(_dialogHwnd);
            }

            if (!ControlTestSupport.WaitUntil(
                () => Win32.IsForeground(_dialogHwnd),
                TimeSpan.FromSeconds(2)))
            {
                // SetForegroundWindow can be refused while the owner is disabled. A click on the
                // edit is the activation fallback that always works on the interactive desktop.
                AutomationElement? edit = FindElement("FindText");
                if (edit is null)
                {
                    return false;
                }

                try
                {
                    edit.Click();
                }
                catch (Exception)
                {
                    return false;
                }

                if (!ControlTestSupport.WaitUntil(
                    () => Win32.IsForeground(_dialogHwnd),
                    TimeSpan.FromSeconds(2)))
                {
                    return false;
                }
            }

            Keyboard.Press(key);
            return true;
        }

        private AutomationElement? WaitForElement(string automationId, TimeSpan timeout)
        {
            AutomationElement? result = null;
            _ = ControlTestSupport.WaitUntil(
                () => (result = FindElement(automationId)) is not null,
                timeout);
            return result;
        }

        private AutomationElement? FindElement(string automationId)
        {
            if (_uiHwnd == 0 || !Win32.IsWindow(_uiHwnd))
            {
                return null;
            }

            try
            {
                var window = _automation.FromHandle(_uiHwnd).AsWindow();
                return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private AutomationElement? WaitForElementAcrossWindows(string automationId, TimeSpan timeout)
        {
            AutomationElement? result = null;
            _ = ControlTestSupport.WaitUntil(
                () => (result = FindAcrossWindows(automationId)) is not null,
                timeout);
            return result;
        }

        private AutomationElement? FindAcrossWindows(string automationId)
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();
                foreach (AutomationElement window in desktop.FindAllChildren(
                    cf => cf.ByControlType(ControlType.Window)))
                {
                    try
                    {
                        if (window.Properties.ProcessId.Value != _session.ProcessId)
                        {
                            continue;
                        }

                        AutomationElement? found = window.FindFirstDescendant(
                            cf => cf.ByAutomationId(automationId));
                        if (found is not null)
                        {
                            return found;
                        }
                    }
                    catch (Exception)
                    {
                        // A window that disappeared during enumeration is skipped.
                    }
                }
            }
            catch (Exception)
            {
                // UIA can throw while a window is created or destroyed.
            }

            return null;
        }

        private nint FindWin32DialogWindow()
        {
            nint found = 0;
            try
            {
                _ = EnumWindows(
                    (hwnd, lParam) =>
                    {
                        _ = lParam;
                        _ = GetWindowThreadProcessId(hwnd, out uint processId);
                        if (processId != (uint)_session.ProcessId || !IsWindowVisible(hwnd))
                        {
                            return true;
                        }

                        var title = new StringBuilder(128);
                        _ = GetWindowText(hwnd, title, title.Capacity);
                        if (!string.Equals(title.ToString(), "Find", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        found = hwnd;
                        return false;
                    },
                    IntPtr.Zero);
            }
            catch (Exception)
            {
                return 0;
            }

            return found;
        }

        private Window? FindDialogWindow()
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();
                foreach (AutomationElement window in desktop.FindAllChildren(
                    cf => cf.ByControlType(ControlType.Window)))
                {
                    try
                    {
                        if (window.Properties.ProcessId.Value != _session.ProcessId)
                        {
                            continue;
                        }

                        AutomationElement? root = window.FindFirstDescendant(
                            cf => cf.ByAutomationId("DialogRoot"));
                        if (root is not null)
                        {
                            return window.AsWindow();
                        }
                    }
                    catch (Exception)
                    {
                        // A window that disappeared during enumeration is skipped.
                    }
                }
            }
            catch (Exception)
            {
                // UIA can throw while a window is created or destroyed.
            }

            return null;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);
#pragma warning restore SYSLIB1054
    }
}
