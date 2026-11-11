using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace Bwc.E2E.ContextMenus;

/// <summary>
/// Finds the context menu popup elements and drives right clicks. MenuFlyout content can live in a
/// separate HWND, so the lookup starts at the main window and falls back to the desktop tree. Only
/// elements whose automation id carries the context prefix and whose process id matches the
/// session are accepted, which keeps the main menu bar out of the snapshot.
/// </summary>
internal sealed class ContextMenuDriver : IDisposable
{
    internal const string ContextPrefix = "ContextMenuCommand_";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly AppSession _session;
    private readonly UIA3Automation _automation;

    internal ContextMenuDriver(AppSession session)
    {
        _session = session;
        _automation = new UIA3Automation();
    }

    internal bool RightClick(AutomationElement? element)
    {
        if (element is null || !ControlTestSupport.EnsureForeground(_session))
        {
            return false;
        }

        try
        {
            element.RightClick();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool RightClick(int x, int y)
    {
        if (!ControlTestSupport.EnsureForeground(_session))
        {
            return false;
        }

        try
        {
            Mouse.RightClick(new System.Drawing.Point(x, y));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until the visible context menu names match the expected sequence. The latest
    /// snapshot is returned even when the names do not match so the caller can report the actual
    /// content instead of an opaque timeout.
    /// </summary>
    internal MenuSnapshot? WaitForMenu(IReadOnlyList<string> expectedNames, TimeSpan? timeout = null)
    {
        MenuSnapshot? latest = null;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                latest = Snapshot();
                return latest is not null && NamesEqual(latest.Names, expectedNames);
            },
            timeout ?? UiTimeout);

        return latest;
    }

    internal MenuSnapshot? WaitForMenu(TimeSpan? timeout = null)
    {
        MenuSnapshot? latest = null;
        _ = ControlTestSupport.WaitUntil(
            () => (latest = Snapshot()) is not null,
            timeout ?? UiTimeout);
        return latest;
    }

    internal MenuEntry? WaitForItem(int commandId, TimeSpan? timeout = null)
    {
        MenuEntry? found = null;
        _ = ControlTestSupport.WaitUntil(
            () => (found = Snapshot()?.Find(commandId)) is not null,
            timeout ?? UiTimeout);
        return found;
    }

    internal bool Invoke(int commandId)
    {
        MenuEntry? entry = WaitForItem(commandId);
        if (entry is null)
        {
            return false;
        }

        try
        {
            if (entry.Element.Patterns.Invoke.IsSupported)
            {
                entry.Element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }
        }
        catch (Exception)
        {
            // Fall back to a click below.
        }

        try
        {
            entry.Element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a MenuFlyoutSubItem. WinUI exposes it as a menu item with an expand collapse pattern
    /// in some builds and as a plain click target in others.
    /// </summary>
    internal bool OpenSubmenu(int commandId)
    {
        MenuEntry? entry = WaitForItem(commandId);
        if (entry is null)
        {
            return false;
        }

        try
        {
            if (entry.Element.Patterns.ExpandCollapse.IsSupported)
            {
                entry.Element.Patterns.ExpandCollapse.Pattern.Expand();
                return true;
            }
        }
        catch (Exception)
        {
            // Fall back to a click below.
        }

        try
        {
            entry.Element.Click();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool WaitForNoMenu(TimeSpan? timeout = null)
    {
        return ControlTestSupport.WaitUntil(
            () => Snapshot() is null,
            timeout ?? TimeSpan.FromSeconds(5));
    }

    private MenuSnapshot? Snapshot()
    {
        var entries = new List<MenuEntry>();

        try
        {
            AutomationElement main = _automation.FromHandle(_session.MainHwnd);
            Collect(main.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)), entries);
        }
        catch (Exception)
        {
            // The main window may be between layouts while a dialog opens.
        }

        if (entries.Count == 0)
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();
                Collect(
                    desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)),
                    entries);
            }
            catch (Exception)
            {
                // UIA can throw while a window is being created or destroyed.
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        entries.Sort((left, right) =>
        {
            int top = left.Top.CompareTo(right.Top);
            return top != 0 ? top : left.Left.CompareTo(right.Left);
        });

        return new MenuSnapshot(entries);
    }

    private void Collect(AutomationElement[] elements, List<MenuEntry> entries)
    {
        foreach (AutomationElement element in elements)
        {
            string automationId;
            try
            {
                if (element.Properties.ProcessId.Value != _session.ProcessId)
                {
                    continue;
                }

                automationId = element.AutomationId ?? string.Empty;
            }
            catch (Exception)
            {
                continue;
            }

            if (!automationId.StartsWith(ContextPrefix, StringComparison.Ordinal) ||
                !int.TryParse(automationId.AsSpan(ContextPrefix.Length), out int commandId))
            {
                continue;
            }

            try
            {
                if (element.Properties.IsOffscreen.Value)
                {
                    continue;
                }

                System.Drawing.Rectangle bounds = element.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                entries.Add(new MenuEntry(
                    commandId,
                    element.Name ?? string.Empty,
                    bounds.Left,
                    bounds.Top,
                    element.Properties.IsEnabled.Value,
                    element));
            }
            catch (Exception)
            {
                // A stale element is skipped; the next snapshot pass re-reads the tree.
            }
        }
    }

    private static bool NamesEqual(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose() => _automation.Dispose();
}

internal sealed record MenuEntry(
    int Id,
    string Name,
    double Left,
    double Top,
    bool Enabled,
    AutomationElement Element);

internal sealed class MenuSnapshot
{
    private readonly IReadOnlyList<MenuEntry> _entries;

    internal MenuSnapshot(IReadOnlyList<MenuEntry> entries)
    {
        _entries = entries;
        Names = entries.Select(entry => entry.Name).ToArray();
    }

    internal IReadOnlyList<MenuEntry> Entries => _entries;

    internal IReadOnlyList<string> Names { get; }

    internal MenuEntry? Find(int commandId) =>
        _entries.FirstOrDefault(entry => entry.Id == commandId);
}
