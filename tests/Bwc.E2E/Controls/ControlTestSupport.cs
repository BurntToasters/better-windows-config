using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
    /// Brings the target window to the foreground before synthetic input. SetForegroundWindow can
    /// be refused by the foreground lock, so a click on the window is the fallback that always
    /// activates it.
    /// </summary>
    internal static bool EnsureForeground(AppSession session)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.IsForeground(session.MainHwnd))
            {
                return true;
            }

            try
            {
                session.Window.SetForeground();
            }
            catch (Exception)
            {
                // The click below may still succeed.
            }

            if (WaitUntil(() => Win32.IsForeground(session.MainHwnd), TimeSpan.FromMilliseconds(400)))
            {
                return true;
            }

            try
            {
                session.Window.Click();
            }
            catch (Exception)
            {
                // The next attempt retries.
            }

            if (WaitUntil(() => Win32.IsForeground(session.MainHwnd), TimeSpan.FromMilliseconds(1200)))
            {
                return true;
            }
        }

        return Win32.IsForeground(session.MainHwnd);
    }

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

    /// <summary>
    /// Expands HKCU, Software, BWC-E2E, and the run key using keyboard Right and type-ahead. An
    /// optional child is selected under the run key. Returns false at the first missing row.
    /// </summary>
    internal static bool NavigateToSandboxKey(AppSession session, E2EConfig config, string? childKey = null)
    {
        var hkcu = FindRowByName(session, TreeControl, ControlType.TreeItem, "HKEY_CURRENT_USER");
        if (hkcu is null)
        {
            return false;
        }

        Select(hkcu);
        _ = WaitForTreeSelection(session, "HKEY_CURRENT_USER");
        SendKeys(session, "Right");
        if (!WaitForTreeRow(session, "Software"))
        {
            return false;
        }

        SendKeys(session, "Software");
        if (!WaitForTreeSelection(session, "Software"))
        {
            return false;
        }

        SendKeys(session, "Right");
        if (!WaitForTreeRow(session, "BWC-E2E"))
        {
            return false;
        }

        TypeAhead(session, "BWC-E2E");
        if (!WaitForTreeSelection(session, "BWC-E2E"))
        {
            return false;
        }

        SendKeys(session, "Right");
        if (!WaitForTreeRow(session, config.RunId))
        {
            return false;
        }

        TypeAhead(session, config.RunId);
        if (!WaitForTreeSelection(session, config.RunId))
        {
            return false;
        }

        if (childKey is null)
        {
            return true;
        }

        SendKeys(session, "Right");
        if (!WaitForTreeRow(session, childKey))
        {
            return false;
        }

        TypeAhead(session, childKey);
        return WaitForTreeSelection(session, childKey);
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

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
