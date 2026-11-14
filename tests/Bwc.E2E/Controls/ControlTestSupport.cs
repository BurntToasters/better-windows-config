using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Bwc.E2E.Controls;

/// <summary>Small UIA helpers shared by the tree and list control tests.</summary>
internal static class ControlTestSupport
{
    internal const string TreeControl = "TreeControl";
    internal const string ListControl = "ListControl";
    internal const string TreeRowPrefix = "TreeRow_";
    internal const string ListRowPrefix = "ListRow_";

    internal static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ExpandTimeout = TimeSpan.FromSeconds(10);

    private static readonly IntPtr PerMonitorV2 = new(-4);

    internal static void PrepareDpi()
    {
        _ = SetProcessDpiAwarenessContext(PerMonitorV2);
        _ = SetThreadDpiAwarenessContext(PerMonitorV2);
    }

    /// <summary>
    /// Brings the target window to the foreground before synthetic input and returns true when it is foreground.
    /// Callers check the result before they send input. The routine is <see cref="AppSession.EnsureForeground"/>,
    /// which never clicks into the window: a click on a window another application covers would land on that
    /// application.
    /// </summary>
    internal static bool EnsureForeground(AppSession session) => session.EnsureForeground(TimeSpan.FromSeconds(3));

    internal static void SendKeys(AppSession session, params string[] keys)
    {
        _ = EnsureForeground(session);
        session.SendKeys(keys);
    }

    /// <summary>
    /// Types a type-ahead prefix after the tree's 500 ms buffer window has expired. Without the
    /// pause a second prefix appends to the first and selects nothing.
    /// </summary>
    internal static void TypeAhead(AppSession session, string text)
    {
        Thread.Sleep(600);
        SendKeys(session, text);
    }

    internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // A stale element is retried on the next pass.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Finds a tree or list row by its name. Registry key and value names are case-insensitive, so
    /// the comparison ignores case.
    /// </summary>
    internal static AutomationElement? FindRowByName(AppSession session, string rootId, ControlType controlType, string name)
    {
        var rows = Rows(session, rootId, controlType);
        return rows.FirstOrDefault(row => string.Equals(SafeName(row), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>DPI of the session's main window, as reported by GetDpiForWindow. Zero when unavailable.</summary>
    internal static uint WindowDpi(AppSession session) => GetDpiForWindow(session.MainHwnd);

    internal static AutomationElement? FindRowById(AppSession session, string rowId)
    {
        return session.Find(rowId);
    }

    internal static AutomationElement[] Rows(AppSession session, string rootId, ControlType controlType)
    {
        var root = session.Find(rootId);
        if (root is null)
        {
            return [];
        }

        try
        {
            return root.FindAllDescendants(cf => cf.ByControlType(controlType));
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static string SafeName(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
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

    internal static string SafeId(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.AutomationId ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool IsSelected(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Patterns.SelectionItem.IsSupported &&
                element.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Select(AutomationElement? element)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
            }
        }
        catch (Exception)
        {
            // The caller records the resulting selection state.
        }
    }

    internal static bool Expand(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            if (!element.Patterns.ExpandCollapse.IsSupported)
            {
                return false;
            }

            element.Patterns.ExpandCollapse.Pattern.Expand();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool Collapse(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            if (!element.Patterns.ExpandCollapse.IsSupported)
            {
                return false;
            }

            element.Patterns.ExpandCollapse.Pattern.Collapse();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string StateOf(AutomationElement? element)
    {
        if (element is null)
        {
            return "missing";
        }

        try
        {
            return element.Patterns.ExpandCollapse.IsSupported
                ? element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString()
                : "unsupported";
        }
        catch (Exception)
        {
            return "stale";
        }
    }

    internal static int RowCountFromHelpText(AppSession session, string rootId)
    {
        var root = session.Find(rootId);
        if (root is null)
        {
            return -1;
        }

        try
        {
            string help = root.Properties.HelpText.Value ?? string.Empty;
            const string marker = "Rows=";
            int start = help.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return -1;
            }

            start += marker.Length;
            int end = help.IndexOf(';', start);
            string text = end < 0 ? help[start..] : help[start..end];
            return int.TryParse(text, out var count) ? count : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static double HeightOf(AutomationElement? element)
    {
        if (element is null)
        {
            return 0;
        }

        try
        {
            return element.BoundingRectangle.Height;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Seeds the run subtree with one value of every type used by the list assertions and an
    /// optional Wide key with many children for the chunked tree expand. Returns the sandbox so
    /// the caller can clean it up in a finally block.
    /// </summary>
    internal static SandboxRegistry SeedRegistry(E2EConfig config, int wideChildCount)
    {
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();

        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue(string.Empty, "Default value text", RegistryValueKind.String);
            root.SetValue("AlphaString", "alpha", RegistryValueKind.String);
            root.SetValue("ZebraString", "zebra", RegistryValueKind.String);
            root.SetValue("SampleDword", 42, RegistryValueKind.DWord);
            root.SetValue("SampleBinary", new byte[] { 0x01, 0x02, 0xAB }, RegistryValueKind.Binary);
            root.SetValue("MultiString", new[] { "first", "second" }, RegistryValueKind.MultiString);
            root.SetValue("EmptyString", string.Empty, RegistryValueKind.String);
        }

        if (wideChildCount > 0)
        {
            using var wide = sandbox.CreateSubKey("Wide");
            for (var index = 1; index <= wideChildCount; index++)
            {
                string name = "Wide" + index.ToString("D4", CultureInfo.InvariantCulture);
                using var child = wide.CreateSubKey(name, writable: true)
                    ?? throw new InvalidOperationException($"Could not create registry key '{name}'.");
            }
        }

        return sandbox;
    }

    /// <summary>
    /// Deletes this run's key (HKCU\Software\BWC-E2E\&lt;run-id&gt;) and nothing else. The BWC-E2E parent
    /// is removed only when it is empty. The parent delete is non-recursive, so a key that another
    /// run creates between the check and the delete makes the delete fail instead of being removed.
    /// </summary>
    internal static void CleanupRegistry(E2EConfig config)
    {
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();

        try
        {
            bool empty;
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\BWC-E2E", writable: false))
            {
                empty = parent is null || (parent.SubKeyCount == 0 && parent.ValueCount == 0);
            }

            if (empty)
            {
                // Non-recursive: the registry refuses the delete when a child key appears in the meantime.
                Registry.CurrentUser.DeleteSubKey(@"Software\BWC-E2E", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            // The parent still holds another run's key (or a handle is open). That is expected
            // while runs overlap, and the next run's cleanup retries the same delete.
        }
    }

    internal static bool WaitForTreeSelection(AppSession session, string name, TimeSpan? timeout = null)
    {
        return WaitUntil(
            () => IsSelected(FindRowByName(session, TreeControl, ControlType.TreeItem, name)),
            timeout ?? TimeSpan.FromSeconds(5));
    }

    private const string AddressBarId = "AddressBar";
    private const int NavigationAttempts = 2;
    private const int MaxMenuCloses = 4;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly TimeSpan KeyboardFocusTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypeAheadGap = TimeSpan.FromMilliseconds(600);

    // Shift, Ctrl and Alt, both sides. A stuck one of these turns typed letters into shortcuts or menu keys.
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    /// <summary>
    /// Selects the run key, or a child of it, in the tree. The address bar is the primary route: the
    /// full path is typed and Enter is pressed. Type-ahead through the tree is the fallback. Before
    /// each route, stuck Shift, Ctrl and Alt are released and open menus are closed, so typed letters
    /// are not read as menu keys. Each route checks the address bar before it reports success. When
    /// both routes fail, the whole navigation is retried once from the root. Returns false when no
    /// attempt ends with the target selected.
    /// </summary>
    internal static bool NavigateToSandboxKey(AppSession session, E2EConfig config, string? childKey = null)
    {
        string leaf = childKey ?? config.RunId;
        string typedPath = @"HKEY_CURRENT_USER\Software\BWC-E2E\" + config.RunId;
        if (childKey is not null)
        {
            typedPath += @"\" + childKey;
        }

        for (var attempt = 1; attempt <= NavigationAttempts; attempt++)
        {
            if (attempt > 1)
            {
                E2EResults.Note("navigate.retry", $"Retrying navigation to '{typedPath}' from the root (attempt {attempt}).");
                _ = NavigateViaAddressBar(session, "Computer", "Computer");
            }

            if (NavigateViaAddressBar(session, typedPath, leaf))
            {
                E2EResults.Note("navigate.route", $"'{typedPath}' selected through the address bar (attempt {attempt}).");
                return true;
            }

            if (NavigateViaTypeAhead(session, config, childKey, typedPath))
            {
                E2EResults.Note("navigate.route", $"'{typedPath}' selected through type-ahead (attempt {attempt}).");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Types a path into the address bar and presses Enter. Succeeds when the address bar shows the
    /// path and the expected row is selected with keyboard focus back in the tree.
    /// </summary>
    private static bool NavigateViaAddressBar(AppSession session, string typedPath, string selectedRow)
    {
        if (!ClearInput(session) || !FocusAddressBar(session))
        {
            return false;
        }

        AutomationElement? address = session.Find(AddressBarId);
        if (address is null)
        {
            return false;
        }

        try
        {
            address.AsTextBox().Text = string.Empty;
            if (!EnsureForeground(session))
            {
                return false;
            }

            Keyboard.Type(typedPath);
        }
        catch (Exception)
        {
            return false;
        }

        if (!WaitUntil(
                () => string.Equals(ReadAddress(session), typedPath, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        try
        {
            session.SendKeys("Enter");
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        bool arrived = WaitUntil(
            () => string.Equals(
                    E2EGuards.NormalizeKeyPath(ReadAddress(session)),
                    E2EGuards.NormalizeKeyPath(typedPath),
                    StringComparison.OrdinalIgnoreCase) &&
                IsSelected(FindRowByName(session, TreeControl, ControlType.TreeItem, selectedRow)),
            TimeSpan.FromSeconds(5));
        return arrived && WaitForTreeKeyboardFocus(session, selectedRow, KeyboardFocusTimeout);
    }

    /// <summary>
    /// Expands HKCU, Software, BWC-E2E and the run key with Right, then selects each row by type-ahead.
    /// Every key goes through PrepareTreeKeyboard. Succeeds when the address bar shows the target.
    /// </summary>
    private static bool NavigateViaTypeAhead(AppSession session, E2EConfig config, string? childKey, string typedPath)
    {
        AutomationElement? hkcu = FindRowByName(session, TreeControl, ControlType.TreeItem, "HKEY_CURRENT_USER");
        if (hkcu is null)
        {
            return false;
        }

        Select(hkcu);
        _ = WaitForTreeSelection(session, "HKEY_CURRENT_USER");

        var path = new List<string> { "Software", "BWC-E2E", config.RunId };
        if (childKey is not null)
        {
            path.Add(childKey);
        }

        string focused = "HKEY_CURRENT_USER";
        foreach (string next in path)
        {
            if (!TreeKeys(session, focused, "Right") || !WaitForTreeRow(session, next))
            {
                return false;
            }

            Thread.Sleep(TypeAheadGap);
            if (!TypeText(session, focused, next) || !WaitForTreeSelection(session, next))
            {
                return false;
            }

            focused = next;
        }

        return WaitUntil(
            () => string.Equals(
                E2EGuards.NormalizeKeyPath(ReadAddress(session)),
                E2EGuards.NormalizeKeyPath(typedPath),
                StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Clears stuck modifiers and open menus, then makes the named tree row the keyboard target.</summary>
    private static bool PrepareTreeKeyboard(AppSession session, string rowName)
    {
        return ClearInput(session) && WaitForTreeKeyboardFocus(session, rowName, KeyboardFocusTimeout);
    }

    private static bool TreeKeys(AppSession session, string focusRow, string key)
    {
        if (!PrepareTreeKeyboard(session, focusRow))
        {
            return false;
        }

        try
        {
            session.SendKeys(key);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TypeText(AppSession session, string focusRow, string text)
    {
        if (!PrepareTreeKeyboard(session, focusRow))
        {
            return false;
        }

        try
        {
            Keyboard.Type(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Releases stuck modifiers and closes open menus. Returns false when a menu stays open.</summary>
    private static bool ClearInput(AppSession session)
    {
        _ = ReleaseStuckModifiers();
        bool clear = CloseOpenMenus(session);
        _ = ReleaseStuckModifiers();
        return clear;
    }

    /// <summary>
    /// Sends a key-up for one key that the system reports as still down, such as an Enter an earlier step left held.
    /// A held key makes the next key-down read as a repeat, which some controls ignore. Returns true when it was released.
    /// </summary>
    internal static bool ReleaseStuckKey(int virtualKey)
    {
        if ((unchecked((ushort)GetAsyncKeyState(virtualKey)) & 0x8000) == 0)
        {
            return false;
        }

        keybd_event(unchecked((byte)virtualKey), 0, KeyEventKeyUp, UIntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Sends a key-up for each of Shift, Ctrl and Alt that the system reports as down. Returns true
    /// when any key was released.
    /// </summary>
    internal static bool ReleaseStuckModifiers()
    {
        bool released = false;
        foreach (int key in ModifierKeys)
        {
            if ((unchecked((ushort)GetAsyncKeyState(key)) & 0x8000) == 0)
            {
                continue;
            }

            keybd_event(unchecked((byte)key), 0, KeyEventKeyUp, UIntPtr.Zero);
            released = true;
        }

        if (released)
        {
            E2EResults.Note("navigate.modifiers", "Released a stuck Shift, Ctrl or Alt before keyboard input.");
        }

        return released;
    }

    /// <summary>
    /// Escape until no menu or flyout owned by the session is visible, at most MaxMenuCloses times.
    /// Escape is sent only while a menu is visible, so an idle tree never receives it.
    /// </summary>
    internal static bool CloseOpenMenus(AppSession session)
    {
        for (var attempt = 0; attempt < MaxMenuCloses; attempt++)
        {
            if (!MenuOpen(session))
            {
                return true;
            }

            E2EResults.Note("navigate.menu", $"Closing an open menu before input (escape {attempt + 1}).");
            try
            {
                session.SendKeys("Escape");
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            _ = WaitUntil(() => !MenuOpen(session), TimeSpan.FromMilliseconds(400));
        }

        return !MenuOpen(session);
    }

    /// <summary>
    /// True when a visible top-level window of the session's process is a Win32 menu (#32768) or holds
    /// a menu item, such as a MenuFlyout. Tooltips hold no menu items, so they do not count.
    /// </summary>
    internal static bool MenuOpen(AppSession session)
    {
        var owned = new List<IntPtr>();
        uint pid = unchecked((uint)session.ProcessId);
        _ = EnumWindows(
            (hwnd, lParam) =>
            {
                if (hwnd != session.MainHwnd && IsWindowVisible(hwnd))
                {
                    _ = GetWindowThreadProcessId(hwnd, out uint owner);
                    if (owner == pid)
                    {
                        owned.Add(hwnd);
                    }
                }

                return true;
            },
            IntPtr.Zero);

        if (owned.Count == 0)
        {
            return false;
        }

        using var automation = new UIA3Automation();
        foreach (IntPtr hwnd in owned)
        {
            if (string.Equals(ClassNameOf(hwnd), "#32768", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                AutomationElement? element = automation.FromHandle(hwnd);
                if (element?.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem)) is not null)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // The window closed while it was being inspected.
            }
        }

        return false;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        int length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool FocusAddressBar(AppSession session)
    {
        if (!EnsureForeground(session))
        {
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            AutomationElement? address = session.Find(AddressBarId);
            if (address is null)
            {
                continue;
            }

            try
            {
                address.Focus();
            }
            catch (Exception)
            {
                try
                {
                    address.Click();
                }
                catch (Exception)
                {
                    // The next attempt reacquires the element.
                }
            }

            if (WaitUntil(() => HasKeyboardFocus(session.Find(AddressBarId)), TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadAddress(AppSession session)
    {
        try
        {
            AutomationElement? address = session.Find(AddressBarId);
            return address is null ? string.Empty : session.TextOf(address);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool WaitForTreeRow(AppSession session, string name, TimeSpan? timeout = null)
    {
        return WaitUntil(
            () => FindRowByName(session, TreeControl, ControlType.TreeItem, name) is not null,
            timeout ?? ExpandTimeout);
    }

    /// <summary>
    /// Waits until the main window is foreground and keyboard focus is on the named tree row or the
    /// tree control. Each pass brings the window forward and focuses the row again, so focus that a
    /// closed dialog took is handed back. Returns false when that does not hold before the timeout,
    /// and the caller must not send keyboard input in that case.
    /// </summary>
    internal static bool WaitForTreeKeyboardFocus(AppSession session, string rowName, TimeSpan timeout)
    {
        return WaitUntil(
            () =>
            {
                _ = EnsureForeground(session);
                FocusRow(session, rowName);
                return IsTreeKeyboardFocused(session, rowName);
            },
            timeout);
    }

    /// <summary>
    /// True when the main window is foreground and the tree control or the named row has keyboard focus.
    /// </summary>
    internal static bool IsTreeKeyboardFocused(AppSession session, string rowName)
    {
        if (!Win32.IsForeground(session.MainHwnd))
        {
            return false;
        }

        return HasKeyboardFocus(session.Find(TreeControl)) ||
            HasKeyboardFocus(FindRowByName(session, TreeControl, ControlType.TreeItem, rowName));
    }

    private static void FocusRow(AppSession session, string rowName)
    {
        AutomationElement? row = FindRowByName(session, TreeControl, ControlType.TreeItem, rowName);
        try
        {
            row?.Focus();
        }
        catch (Exception)
        {
            // IsTreeKeyboardFocused reports a focus that did not stick.
        }
    }

    private static bool HasKeyboardFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string[] ListRowNames(AppSession session)
    {
        return Rows(session, ListControl, ControlType.DataItem)
            .Select(SafeName)
            .Where(name => name.Length > 0)
            .ToArray();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
