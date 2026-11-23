using System.Drawing;
using System.Globalization;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>
/// The ids of the U3b features that the tests use: the File and Help popups of the console frame (MENU-002 to MENU-005,
/// MENU-031 to MENU-036) and the parts of the Options dialog (DLG-119). The contract is
/// docs/failure-modes/console-options.md.
/// </summary>
internal static class OptionsIds
{
    internal const int FileMenu = 59001;
    internal const int HelpMenu = 59004;

    internal const int OptionsCommand = 13220;
    internal const int ExitCommand = 57665;
    internal const int HelpTopicsCommand = 12804;
    internal const int WebSiteCommand = 13245;
    internal const int AboutCommand = 57664;
    internal const int SnapInAboutCommand = 13242;

    internal const string Dialog = "ConsoleOptions";
    internal const string Name = "ConsoleOptions.Name";
    internal const string ChangeIcon = "ConsoleOptions.ChangeIcon";
    internal const string Mode = "ConsoleOptions.Mode";
    internal const string ModeDescription = "ConsoleOptions.ModeDescription";
    internal const string NoSave = "ConsoleOptions.NoSave";
    internal const string CustomizeViews = "ConsoleOptions.CustomizeViews";
    internal const string Ok = "ConsoleOptions.Ok";
    internal const string Cancel = "ConsoleOptions.Cancel";

    /// <summary>The parts of the dialog in the reading order of the classic page (DLG-119), then OK and Cancel.</summary>
    internal static readonly string[] Parts = [Name, ChangeIcon, Mode, ModeDescription, NoSave, CustomizeViews, Ok, Cancel];

    /// <summary>The settings key of the customize-views choice (absent means allowed).</summary>
    internal const string SettingsKey = "AllowCustomizeViews";

    /// <summary>The web site the Help entry names (the project page that the update feed already names).</summary>
    internal const string WebSiteUrl = "https://github.com/BurntToasters/better-windows-config";

    /// <summary>The name the snap-in About entry gives the native box (caption is "About " plus it).</summary>
    internal const string SnapInAboutName = "Local Group Policy Editor snap-in";

    internal const ushort VkP = 0x50;
    internal const ushort VkH = 0x48;
}

/// <summary>One entry of an open popup with its rectangle in physical pixels and its access key.</summary>
internal sealed record ShapeEntry(int Id, string Name, bool Enabled, int Top, int Bottom, string AccessKey);

/// <summary>
/// Reads an open popup of the menu bar as entries and the groups the separators make. Separators have no UI Automation
/// peer, so a gap of two physical pixels or more between two entries is a separator (the rule of the Menus popup test).
/// This works for popups of two entries too, which the pitch rule of <see cref="FrameMenu.GroupSizes"/> cannot read.
/// </summary>
internal static class MenuShape
{
    private static readonly int[] TopLevelIds = [59001, 59002, 59003, 59004];

    /// <summary>The command entries of every open popup of the process, top to bottom.</summary>
    internal static List<ShapeEntry> Read(ShellRun run)
    {
        var found = new List<ShapeEntry>();
        AutomationElement[] windows;
        try
        {
            windows = ProcessWindows.Of(run.Automation, run.Session.ProcessId);
        }
        catch (Exception)
        {
            return found;
        }

        foreach (AutomationElement window in windows)
        {
            AutomationElement[] items;
            try
            {
                items = window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
            }
            catch (Exception)
            {
                continue;
            }

            foreach (AutomationElement item in items)
            {
                try
                {
                    string id = ShellRun.SafeId(item);
                    if (!id.StartsWith(GpeditCommandIds.MenuPrefix, StringComparison.Ordinal) ||
                        !int.TryParse(id.AsSpan(GpeditCommandIds.MenuPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int commandId) ||
                        Array.IndexOf(TopLevelIds, commandId) >= 0 ||
                        found.Any(e => e.Id == commandId) ||
                        item.Properties.IsOffscreen.Value)
                    {
                        continue;
                    }

                    Rectangle bounds = item.BoundingRectangle;
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        string key = item.Properties.AccessKey.ValueOrDefault ?? string.Empty;
                        found.Add(new ShapeEntry(commandId, item.Name ?? string.Empty, item.Properties.IsEnabled.Value, bounds.Top, bounds.Bottom, key));
                    }
                }
                catch (Exception)
                {
                    // A stale element is skipped; the next poll reads the tree again.
                }
            }
        }

        found.Sort((a, b) => a.Top.CompareTo(b.Top));
        return found;
    }

    /// <summary>Opens a top level menu through UI Automation and returns its entries once any show.</summary>
    internal static List<ShapeEntry> OpenTop(ShellRun run, int topId)
    {
        using (var driver = new EditingDriver(run.Session))
        {
            driver.OpenMenu(topId);
        }

        var entries = new List<ShapeEntry>();
        _ = ShellRun.WaitUntil(() => (entries = Read(run)).Count > 0, TimeSpan.FromSeconds(6));
        return entries;
    }

    /// <summary>The sizes of the runs of entries between separators. Empty for no entry.</summary>
    internal static int[] Groups(IReadOnlyList<ShapeEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var sizes = new List<int> { 1 };
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Top - entries[i - 1].Bottom >= 2)
            {
                sizes.Add(1);
            }
            else
            {
                sizes[^1]++;
            }
        }

        return sizes.ToArray();
    }

    /// <summary>
    /// The names of the items that sit inside the rectangle of the menu bar (no popup open), left to right. A popup entry is
    /// outside that rectangle, so a menu with another top level entry (Favorites) would show up here.
    /// </summary>
    internal static string[] TopLevelNames(ShellRun run)
    {
        try
        {
            AutomationElement? bar = run.Find(ShellRun.MenuBarId);
            if (bar is null)
            {
                return [];
            }

            Rectangle barRect = bar.BoundingRectangle;
            return bar.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .Where(item => !item.Properties.IsOffscreen.Value && item.BoundingRectangle is { Width: > 0 } r && r.Top >= barRect.Top - 1 && r.Bottom <= barRect.Bottom + 1)
                .OrderBy(item => item.BoundingRectangle.Left)
                .Select(ShellRun.SafeName)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static int[] Ids(IReadOnlyList<ShapeEntry> entries) => entries.Select(e => e.Id).ToArray();

    internal static ShapeEntry? Entry(IReadOnlyList<ShapeEntry> entries, int id) => entries.FirstOrDefault(e => e.Id == id);

    internal static string Describe(IReadOnlyList<ShapeEntry> entries) =>
        string.Join("|", entries.Select(e => $"{e.Id}:{e.Name}:{(e.Enabled ? "on" : "off")}:{e.AccessKey}")) +
        $" groups [{string.Join(",", Groups(entries))}]";

    internal static bool Close(ShellRun run)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Read(run).Count == 0)
            {
                return true;
            }

            run.Tap(ShellRun.VkEscape);
        }

        return ShellRun.WaitUntil(() => Read(run).Count == 0, TimeSpan.FromSeconds(3));
    }
}

/// <summary>Reading of the Options dialog that the U3b tests share.</summary>
internal static class OptionsDialogRead
{
    /// <summary>The text of an edit box, a combo box or a label: the value when it has one, else the name.</summary>
    internal static string TextOf(FrameDialog dialog, string id)
    {
        try
        {
            AutomationElement? element = dialog.Find(id, 1);
            if (element is null)
            {
                return string.Empty;
            }

            string? value = element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
            return !string.IsNullOrEmpty(value) ? value : ShellRun.SafeName(element);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>True when the part exposes the Value pattern and it is read-only.</summary>
    internal static bool IsReadOnly(FrameDialog dialog, string id)
    {
        try
        {
            return dialog.Find(id, 1)?.Patterns.Value.PatternOrDefault?.IsReadOnly.ValueOrDefault == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The caption of the dialog window.</summary>
    internal static string Caption(FrameDialog dialog) =>
        dialog.Root is { } root ? ShellRun.SafeName(ClassicDialogTestsBridge.WindowOf(root)) : string.Empty;

    /// <summary>The title of the main window of the run.</summary>
    internal static string WindowTitle(ShellRun run)
    {
        try
        {
            return run.Session.Window.Title ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
