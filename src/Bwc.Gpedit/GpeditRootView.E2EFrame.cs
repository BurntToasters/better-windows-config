#if BWC_E2E
using System.Globalization;
using Bwc.UI.Console.Extended;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Icons;
using Bwc.UI.Menu;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit;

/// <summary>
/// E2E flavor only. The placeholder frame host that the Console_* tests drive (BWC_E2E_GPEDIT_HOST=console-frame): the
/// two configuration roots, a result list with synthetic rows and the command echo in the status bar. It moved here
/// unchanged from the scaffold host when piece G1 gave the editor view its real tree and list. Nothing in this file
/// exists in the ship flavor (docs/failure-modes/gpedit-tree-list.md, section 10).
/// </summary>
public sealed partial class GpeditRootView
{
    private int _contextRequests;
    private int _activations;
    private int _tabChanges;

    /// <summary>The frame host setup that used to be the constructor tail.</summary>
    private void InitializeFrameHost()
    {
        ConfigureList();

        var computer = new TreeNode("Computer Configuration") { Icon = IconKind.ClosedFolder };
        var user = new TreeNode("User Configuration") { Icon = IconKind.ClosedFolder };
        Shell.TreeContent.SetRoots([computer, user]);
        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        Shell.TreeContent.SelectNode(computer);
        SyncViewChecks();

        if (string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_TAB"), "standard", StringComparison.Ordinal))
        {
            _extended.SelectedTab = ConsoleViewTab.Standard;
            _tabChanges = 0;
            Shell.StatusText = string.Empty;
        }

        string? seededStatus = Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_STATUS_TEXT");
        if (!string.IsNullOrEmpty(seededStatus))
        {
            Shell.StatusText = seededStatus;
        }

        SeedE2ERows();
    }

    /// <summary>
    /// The placeholder result list: the shared list in the result pane with three neutral columns. The rows come from
    /// the G1 piece; the E2E flavor fills synthetic ones.
    /// </summary>
    private void ConfigureList()
    {
        _list.SetColumns(
        [
            new ConsoleListColumn("name", "Name", 350),
            new ConsoleListColumn("state", "State", 100),
            new ConsoleListColumn("comment", "Comment", 200),
        ]);

        // The classic list names a row by its text plus a word the host adds; the list control adds nothing itself.
        _list.ItemNameFormatter = row => row.Text + " setting";
        _list.ItemActivated += OnListItemActivated;
        _list.ContextMenuRequested += OnListContextMenuRequested;
        _list.ViewModeChanged += (_, _) => SyncViewChecks();
        _list.SelectionChanged += (_, _) => UpdateDescription();

        // The result pane is the Extended view around the list: a description pane on the left and the two tabs under
        // it. Tab inside the result pane first walks the stops of the view (list, links), then the shell cycles panes.
        _extended.ViewContent = _list;
        _extended.ContentFocusTarget = _list;
        _extended.DescriptionLinkInvoked += OnDescriptionLinkInvoked;
        _extended.SelectedTabChanged += OnSelectedTabChanged;
        Shell.ResultContent = _extended;
        Shell.ResultFocusTarget = _list;
        Shell.ResultPaneTabHandler = _extended.TryMoveFocusWithin;
    }

    /// <summary>
    /// The placeholder description of the selected item. A single selected row gives a policy style model (title, the edit
    /// link, a Requirements and a Description section); anything else gives the selected tree node as a category with the
    /// hint. The G pieces replace the text with the template store's.
    /// </summary>
    private void UpdateDescription()
    {
        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        if (selected.Count == 1)
        {
            ConsoleListRow row = selected[0];
            _extended.Description = new ConsoleDescriptionModel(
                row.Text,
                [
                    new ConsoleDescriptionSection("requirements", ConsoleStrings.Get("ConsoleDescriptionRequirements"), "Placeholder requirements."),
                    new ConsoleDescriptionSection("description", ConsoleStrings.Get("ConsoleDescriptionHelpHeading"), HelpTextFor(row)),
                ],
                [new ConsoleDescriptionLink("edit", ConsoleStrings.Get("ConsoleExtendedEditPolicyLink"))]);
            return;
        }

        _extended.Description = new ConsoleDescriptionModel(
            Shell.TreeContent.SelectedNode?.Name ?? string.Empty,
            [new ConsoleDescriptionSection("hint", string.Empty, ConsoleStrings.Get("ConsoleDescriptionNoSelectionHint"))]);
    }

    private static string HelpTextFor(ConsoleListRow row)
    {
#if BWC_E2E
        switch (Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_HELP"))
        {
            case "empty":
                return string.Empty;
            case "long":
                var text = new System.Text.StringBuilder();
                for (int i = 0; text.Length < 60_000; i++)
                {
                    _ = text.Append('w').Append(i.ToString("D5", CultureInfo.InvariantCulture)).Append(' ');
                }

                return text.ToString(0, 60_000);
            case "markup":
                return "Tom & Jerry <b>x</b>\r\nline two\rline three\nline four";
            case "wide":
                return "Start " + new string('W', 300) + " end";
        }
#endif
        return $"Placeholder help text of {row.Text}. The policy text comes from the template store in a later piece.";
    }

    private void OnDescriptionLinkInvoked(object? sender, ConsoleDescriptionLinkEventArgs e)
    {
        _linkInvocations++;
        Shell.StatusText = $"Link: {e.Link.Id} n={_linkInvocations}";
    }

    private void OnSelectedTabChanged(object? sender, EventArgs e)
    {
        _tabChanges++;
        Shell.StatusText = $"Tab: {_extended.SelectedTab} n={_tabChanges}";
    }

    private void OnListItemActivated(object? sender, ConsoleListItemEventArgs e)
    {
        _activations++;
        Shell.StatusText = $"Activated: {e.Row.Text} n={_activations}";
    }

    private void OnListContextMenuRequested(object? sender, ConsoleListContextEventArgs e)
    {
        string source = e.FromKeyboard ? "key" : "pointer";
        string target = e.Area switch
        {
            ConsoleListContextArea.Item => $" row={e.Row?.Text}",
            ConsoleListContextArea.Header => " header",
            _ => string.Empty,
        };
        _contextRequests++;
        Shell.StatusText = $"Context menu: pane=Result source={source} n={_contextRequests}{target}";
    }

    private static string CommandName(int id) => id switch
    {
        ConsoleToolbarIds.Back => "Back",
        ConsoleToolbarIds.Forward => "Forward",
        ConsoleToolbarIds.UpOneLevel => "UpOneLevel",
        ConsoleToolbarIds.ShowHideConsoleTree => "ShowHideConsoleTree",
        ConsoleToolbarIds.Properties => "Properties",
        ConsoleToolbarIds.Refresh => "Refresh",
        ConsoleToolbarIds.ExportList => "ExportList",
        ConsoleToolbarIds.Help => "Help",
        _ => "Extension" + id,
    };

    private const int DefaultE2ERows = 8;
    private const int MaxE2ERows = 100_000;
    private static readonly string[] E2EStates = ["Not configured", "Enabled", "Disabled"];
    private static readonly string[] E2ETypeAheadNames =
        ["Alpha", "Beta", "Bravo", "Charlie", "Delta", "Écho", "Foxtrot", "Zulu", "Ünder", "日本語"];

    private int _e2eRowCount = DefaultE2ERows;
    private int _e2eGeneration;
    private bool _e2eRowsRequested;

    /// <summary>
    /// E2E only. BWC_E2E_CONSOLE_ROWS is the number of synthetic rows (default 8; a missing, malformed or negative value
    /// gives the default, more than 100,000 is limited). Row i is named "Setting NNNNN" with a number that is a
    /// permutation of the rows, has the state i % 3 and the comment i % 2, and its tag is i. BWC_E2E_CONSOLE_NAMES set
    /// to typeahead names the first rows from a fixed set that includes non ASCII names, and BWC_E2E_CONSOLE_LONGTEXT
    /// gives row 3 a comment of 5,000 characters. With BWC_E2E_CONSOLE_ROWS set, the toolbar Refresh replaces the rows.
    /// </summary>
    private void SeedE2ERows()
    {
        string? requested = Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_ROWS");
        _e2eRowsRequested = !string.IsNullOrEmpty(requested);
        if (int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count >= 0)
        {
            _e2eRowCount = Math.Min(count, MaxE2ERows);
        }

        _list.SetRows(CreateE2ERows(_e2eRowCount, 0));
    }

    private static List<ConsoleListRow> CreateE2ERows(int count, int generation)
    {
        bool typeAhead = string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_NAMES"), "typeahead", StringComparison.Ordinal);
        bool longText = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BWC_E2E_CONSOLE_LONGTEXT"));
        var rows = new List<ConsoleListRow>(count);
        for (int i = 0; i < count; i++)
        {
            long key = ((i * 37L) + 11) % Math.Max(1, count);
            string name = typeAhead && i < E2ETypeAheadNames.Length
                ? E2ETypeAheadNames[i]
                : (generation > 0 ? "Refreshed " : "Setting ") + key.ToString("D5", CultureInfo.InvariantCulture);
            string comment = longText && i == 3 ? new string('W', 5000) : (i % 2 == 0 ? "No" : "Yes");

            // Every fourth row names a module icon that does not exist, to exercise the fallback glyph.
            IconReference icon = i % 4 == 3 ? ModuleIcon.ById("bwc-e2e-missing-module.dll", 1) : IconKind.StringValue;
            rows.Add(new ConsoleListRow(name, E2EStates[i % 3], comment) { SmallIcon = icon, LargeIcon = icon, Tag = i });
        }

        return rows;
    }

    /// <summary>E2E only: Refresh replaces the rows by 100 of a new set, then by none, then by the full set again.</summary>
    private void RefreshE2ERows()
    {
        if (!_e2eRowsRequested)
        {
            return;
        }

        _e2eGeneration++;
        int phase = _e2eGeneration % 3;
        int count = phase switch
        {
            1 => Math.Min(100, _e2eRowCount),
            2 => 0,
            _ => _e2eRowCount,
        };
        _list.SetRows(CreateE2ERows(count, phase == 0 ? 0 : _e2eGeneration), keepSelectionByTag: true);
    }

    private string _resolvedBefore = string.Empty;
    private MenuFlyoutItemBase? _removedItem;
    private IList<MenuFlyoutItemBase>? _removedFrom;
    private int _removedAt;

    /// <summary>
    /// E2E only. Removes the List radio now, inserts the same item again 400 ms later (a removed item stays the child of
    /// its popup until the popup has closed), then collects garbage three times and reports in the status bar how many of
    /// the wrappers that registered a menu action were collected (MenuItemActions counts them).
    /// </summary>
    private void CollectMenuWrappers()
    {
        try
        {
            (int foundBefore, int leavesBefore) = CountResolvedActions();
            _resolvedBefore = $"resolvedBefore={foundBefore} of {leavesBefore}";
            RemoveListRadio();
        }
        catch (Exception ex)
        {
            Shell.StatusText = $"Wrapper GC failed: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => FinishCollectMenuWrappers();
        timer.Start();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RemoveListRadio()
    {
        foreach (MenuBarItem top in Shell.MenuBar.Items)
        {
            for (int i = 0; i < top.Items.Count; i++)
            {
                MenuFlyoutItemBase item = top.Items[i];
                if (item.Tag is int id && id == ViewListCommand)
                {
                    _removedItem = item;
                    _removedFrom = top.Items;
                    _removedAt = i;
                    top.Items.RemoveAt(i);
                    return;
                }
            }
        }
    }

    private void FinishCollectMenuWrappers()
    {
        try
        {
            ReinsertListRadio();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        catch (Exception ex)
        {
            Shell.StatusText = $"Wrapper GC failed: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        (int registered, int collected) = MenuItemActions.E2EWrapperCounts();
        (int found, int leaves) = CountResolvedActions();
        Shell.StatusText = $"Wrapper GC: collected={collected} of {registered}; {_resolvedBefore}; resolved={found} of {leaves}";
    }

    /// <summary>E2E only. How many menu leaves with a command still resolve their action by the keyboard lookup.</summary>
    private (int Found, int Leaves) CountResolvedActions()
    {
        int found = 0;
        int leaves = 0;
        foreach (MenuBarItem top in Shell.MenuBar.Items)
        {
            foreach (MenuFlyoutItemBase entry in top.Items)
            {
                // Leaves of the frame without a handler (disabled skeleton entries) carry no action and are not counted.
                if (entry is MenuFlyoutItem leaf && leaf.IsEnabled)
                {
                    leaves++;
                    if (MenuItemActions.TryGet(leaf, out _))
                    {
                        found++;
                    }
                }
            }
        }

        return (found, leaves);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ReinsertListRadio()
    {
        if (_removedItem is not null && _removedFrom is not null)
        {
            _removedFrom.Insert(_removedAt, _removedItem);
        }

        _removedItem = null;
        _removedFrom = null;
    }

    private void SaveListState()
    {
        ConsoleListState state = _list.GetState();
        string columns = string.Join(
            ",",
            state.Columns.Select(column => column.Id + "=" + column.Width.ToString("0.##", CultureInfo.InvariantCulture) + (column.Visible ? string.Empty : "!")));
        string sort = state.SortColumnId is null ? "none" : $"{state.SortColumnId}:{state.SortDirection}";
        Shell.StatusText = $"State: {columns};sort={sort};mode={state.ViewMode}";
    }

    private void RestoreListState()
    {
        _list.ApplyState(new ConsoleListState(
            [
                new ConsoleListColumnState("comment", 150, true),
                new ConsoleListColumnState("name", 222, true),
                new ConsoleListColumnState("state", 100, false),
            ],
            "name",
            ConsoleListSortDirection.Ascending,
            ConsoleListViewMode.Detail));
    }
}
#endif
