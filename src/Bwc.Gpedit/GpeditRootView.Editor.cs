using System.Globalization;
using System.Text;
using Bwc.Core;
using Bwc.Gpedit.Editor.Dialog;
using Bwc.Gpedit.List;
using Bwc.Gpedit.Policy;
using Bwc.Gpedit.Save;
using Bwc.Gpedit.Tree;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.UI.Console.Extended;
using Bwc.UI.Console.Frame;
using Bwc.UI.Console.List;
using Bwc.UI.Tree;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;

namespace Bwc.Gpedit;

/// <summary>
/// The editor wiring of the view: the console tree over the template categories, the result list with State and Comment
/// read from the side's policy file, the description pane, the status text, the setting dialog, the persisted view state
/// and the E2E readout (docs/failure-modes/gpedit-tree-list.md). The commands are in GpeditRootView.Commands.cs and the
/// model load in GpeditRootView.Reload.cs. The window takes its saved place from the head before it is shown; the view
/// keeps the settings and writes them back.
/// </summary>
public sealed partial class GpeditRootView
{
    private const string ToolId = "gpedit";
    private const int SettingsSaveDelayMs = 500;
    private const double SettingColumnWidth = 350;
    private const double StateColumnWidth = 100;
    private const double CommentColumnWidth = 139;
    private const double PathColumnWidth = 250;
    private const double NameColumnWidth = 180;
    private const double MinSavedColumnWidth = ConsoleList.MinColumnWidth;
    private const double MaxSavedColumnWidth = ConsoleList.MaxColumnWidth;
    private const double MaxSavedTreeWidth = 4096;
    private const int PolicyEditSteps = 3;

    private readonly SettingsStore<GpeditSettings> _settingsStore =
        new(AppPaths.SettingsFile(ToolId), GpeditSettingsContext.Default.GpeditSettings);

    private readonly Dictionary<string, double> _columnWidths = new(StringComparer.Ordinal);
    private readonly GpeditPlaceholderPage _placeholder = new();

    private GpeditSettings _settings = new();
    private GpeditTreeSource _source = null!;
    private GpeditModel? _model;
    private GpeditListLayout? _layout;
    private ConsoleDescriptionModel _nodeDescription = new(string.Empty);
    private bool _placeholderShown;
    private DispatcherQueueTimer? _saveTimer;
    private AppWindow? _appWindow;
    private RectInt32? _normalBounds;
    private int _editRequests;
    private string _lastEdit = string.Empty;
    private int _linkInvocations;
    private PolicySettingDialog? _policyDialog;
    private GpoStore? _gpoStore;

    /// <summary>
    /// Raised when the user asks to edit a policy: the Edit policy setting link, Enter or a double click on a policy row.
    /// The view itself subscribes and opens the setting dialog (piece G3a, <see cref="OpenPolicyDialog"/>).
    /// </summary>
    public event EventHandler<GpeditPolicyEditEventArgs>? PolicyEditRequested;

    /// <summary>
    /// The window rectangle saved at the last close, for the head to apply before the first show. Null when none was saved.
    /// Whether the rectangle is usable is for the head to decide.
    /// </summary>
    public GpeditSavedPlacement? SavedPlacement =>
        _settings.WindowX is { } x && _settings.WindowY is { } y && _settings.WindowWidth is { } width && _settings.WindowHeight is { } height
            ? new GpeditSavedPlacement(x, y, width, height, _settings.WindowMaximized)
            : null;

    /// <summary>Tells the view the normal (not maximized) rectangle the head gave the window; it is saved at close.</summary>
    public void NoteNormalBounds(int x, int y, int width, int height) => _normalBounds = new RectInt32(x, y, width, height);

    private void InitializeEditor()
    {
        _settings = LoadSettings();
        foreach (KeyValuePair<string, double> pair in _settings.ColumnWidths)
        {
            if (IsUsableWidth(pair.Value))
            {
                _columnWidths[pair.Key] = pair.Value;
            }
        }

        LoadSortStacks();
        _columnChoice = ConsoleColumnChoice.From(_settings.ColumnOrder, _settings.HiddenColumns);
        ConfigureEditorResultPane();
        StartModelLoad();
        PolicyEditRequested += OnPolicyEditRequested;

        Shell.TreeContent.SetRoots([_source.Root]);
        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        ApplySavedView();
        ApplySavedSwitches();
        ApplySavedOptions();
        Shell.TreeContent.SelectNode(_source.Root);
        SyncViewChecks();
        Shell.ViewChanged += (_, _) => ScheduleSettingsSave();

        Loaded += OnEditorLoaded;
        Unloaded += OnEditorUnloaded;
    }

    private void ConfigureEditorResultPane()
    {
        // The classic list names a row by its text plus a word for a policy; the list control adds nothing itself.
        _list.ItemNameFormatter = GpeditListBuilder.RowName;
        // A click on a header sorts the order shown now, so the list keeps the clicks of each node (gpedit-filter.md).
        _list.CumulativeSort = true;
        _list.SortChanged += OnListSortChanged;
        _list.ItemActivated += OnEditorItemActivated;
        _list.ContextMenuRequested += OnEditorListContext;
        _list.ViewModeChanged += (_, _) =>
        {
            SyncViewChecks();
            ScheduleSettingsSave();
        };
        _list.SelectionChanged += (_, _) => UpdateRowDescription();
        _list.ColumnStateChanged += (_, _) =>
        {
            foreach (ConsoleListColumn column in _list.Columns)
            {
                _columnWidths[column.Id] = column.Width;
            }

            ScheduleSettingsSave();
        };

        _extended.ViewContent = _list;
        _extended.ContentFocusTarget = _list;
        _extended.DescriptionLinkInvoked += OnEditorDescriptionLink;
        _extended.SelectedTabChanged += (_, _) => ScheduleSettingsSave();
        Shell.ResultContent = _extended;
        Shell.ResultFocusTarget = _list;
        Shell.ResultPaneTabHandler = _extended.TryMoveFocusWithin;
        Shell.TreePaneWidthChanged += (_, _) => ScheduleSettingsSave();
    }

    // ---- state --------------------------------------------------------------------------------------

    /// <summary>The state that the commands and the readout show has changed.</summary>
    private void StateChanged()
    {
        UpdateCommandStates();
        UpdateReadout();
    }

    // ---- showing a node -----------------------------------------------------------------------------

    private void ShowNode(TreeNode node)
    {
        try
        {
            GpeditListContent content = GpeditListBuilder.Build(node, _model, _source, _filter);
            ApplyContent(content, node);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never throw to the UI thread: the previous view stays.
            Log.Warn($"The node '{node.Name}' could not be shown: {ex.Message}");
        }
    }

    private void ApplyContent(GpeditListContent content, TreeNode node)
    {
        _nodeDescription = content.Description;
        if (content.IsPlaceholder)
        {
            ShowPlaceholder(node);
        }
        else
        {
            ShowList();
            ApplyLayout(content.Layout);
            ApplySortOf(node);
            _list.SetRows(content.Rows);
        }

        _extended.Description = content.Description;
        Shell.StatusText = content.StatusText;
        NoteContent(content);
        StateChanged();
    }

    private void ShowPlaceholder(TreeNode node)
    {
        _list.SetRows([]);
        _placeholder.Show(node.Name);
        if (_placeholderShown)
        {
            return;
        }

        // A list that held the focus is leaving: the result pane takes it so the keyboard is not lost.
        bool listHadFocus = _list.FocusState != FocusState.Unfocused;
        _extended.ContentFocusTarget = null;
        Shell.ResultFocusTarget = null;
        _extended.ViewContent = _placeholder;
        _placeholderShown = true;
        if (listHadFocus)
        {
            Shell.FocusResultPane();
        }
    }

    private void ShowList()
    {
        if (!_placeholderShown)
        {
            return;
        }

        _extended.ViewContent = _list;
        _extended.ContentFocusTarget = _list;
        Shell.ResultFocusTarget = _list;
        _placeholderShown = false;
    }

    private void ApplyLayout(GpeditListLayout layout)
    {
        if (_layout == layout)
        {
            return;
        }

        _layout = layout;
        _list.SetColumns(layout switch
        {
            GpeditListLayout.Navigation => [Column("name", "GpeditColumnName", NameColumnWidth)],
            GpeditListLayout.AllSettings =>
            [
                Column("setting", "GpeditColumnSetting", SettingColumnWidth),
                Column("state", "GpeditColumnState", StateColumnWidth),
                Column("comment", "GpeditColumnComment", CommentColumnWidth),
                Column("path", "GpeditColumnPath", PathColumnWidth),
            ],
            _ =>
            [
                Column("setting", "GpeditColumnSetting", SettingColumnWidth),
                Column("state", "GpeditColumnState", StateColumnWidth),
                Column("comment", "GpeditColumnComment", CommentColumnWidth),
            ],
        });

        // The column choice is one for the list: every layout applies it (Add/Remove Columns).
        ApplyColumnChoice();
    }

    private ConsoleListColumn Column(string id, string headerKey, double defaultWidth) =>
        new(id, GpeditStrings.Tree(headerKey), _columnWidths.TryGetValue(id, out double saved) ? saved : defaultWidth);

    private void UpdateRowDescription()
    {
        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        ConsoleDescriptionModel? row = selected.Count == 1 ? GpeditListBuilder.DescribeRow(selected[0]) : null;
        _extended.Description = row ?? _nodeDescription;
        StateChanged();
    }

    // ---- activation and edit requests ---------------------------------------------------------------

    private void OnEditorItemActivated(object? sender, ConsoleListItemEventArgs e)
    {
        if (e.Row.Tag is not GpeditRowTag tag)
        {
            return;
        }

        if (tag is { Kind: GpeditRowKind.Policy, Policy: { } policy })
        {
            RaisePolicyEdit(policy, tag.Side);
        }
        else if (tag.Target is { } target)
        {
            SelectWhenVisible(target, 20);
        }
    }

    /// <summary>
    /// Selects a child of the shown node, expanding the node first when it is collapsed. The expansion inserts its rows in
    /// chunks, so the selection retries a few times on the dispatcher until the child is visible.
    /// </summary>
    private void SelectWhenVisible(TreeNode target, int attempts)
    {
        DenseTree tree = Shell.TreeContent;
        if (target.Parent is { IsExpanded: false, HasChildren: true } parent && ReferenceEquals(tree.SelectedNode, parent))
        {
            tree.ExpandSelected();
        }

        tree.SelectNode(target);
        if (ReferenceEquals(tree.SelectedNode, target) || attempts <= 0)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SelectWhenVisible(target, attempts - 1));
    }

    private void OnEditorDescriptionLink(object? sender, ConsoleDescriptionLinkEventArgs e)
    {
        _linkInvocations++;
        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        if (e.Link.Id == "edit" && selected.Count == 1 && selected[0].Tag is GpeditRowTag { Kind: GpeditRowKind.Policy, Policy: { } policy } tag)
        {
            RaisePolicyEdit(policy, tag.Side);
        }
        else
        {
            UpdateReadout();
        }
    }

    private void RaisePolicyEdit(PolicyDefinition policy, GpoSide side)
    {
        _editRequests++;
        _lastEdit = policy.FullName;
        RecordCommand(CommandEdit);
        try
        {
            PolicyEditRequested?.Invoke(this, new GpeditPolicyEditEventArgs(policy, side));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The edit request of '{policy.FullName}' failed: {ex.Message}");
        }

        StateChanged();
    }

    // ---- the setting dialog -------------------------------------------------------------------------

    private void OnPolicyEditRequested(object? sender, GpeditPolicyEditEventArgs e) => OpenPolicyDialog(e.Policy, e.Side);

    /// <summary>
    /// Opens the setting dialog for a policy (docs/failure-modes/gpedit-dialog.md). One dialog at a time. Previous Setting and
    /// Next Setting move through the policy rows of the shown list in display order. Apply and OK save the edit into the policy
    /// object through the seam <see cref="IPolicyEditStore"/> (<see cref="PolicySaveStore"/>), the model is replaced by what
    /// was saved, and the State and Comment cells are rebuilt.
    /// </summary>
    private void OpenPolicyDialog(PolicyDefinition policy, GpoSide side)
    {
#if BWC_E2E
        // The list tests that only count the request keep the main window enabled.
        if (string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_NO_DIALOG"), "1", StringComparison.Ordinal))
        {
            return;
        }
#endif
        if (_policyDialog is not null || _model is not { } model || XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return;
        }

        var order = new List<PolicyRef>();
        foreach (ConsoleListRow row in _list.Rows)
        {
            if (row.Tag is GpeditRowTag { Kind: GpeditRowKind.Policy, Policy: { } rowPolicy } tag)
            {
                order.Add(new PolicyRef(rowPolicy, tag.Side));
            }
        }

        int index = order.FindIndex(r => ReferenceEquals(r.Policy, policy) && r.Side == side);
        if (index < 0)
        {
            order = [new PolicyRef(policy, side)];
            index = 0;
        }

        var store = new PolicySaveStore(SaveStoreOf(model), model.Snapshot, model.IsReadOnly);
        PolicySettingDialog dialog;
        try
        {
            dialog = new PolicySettingDialog(order, index, store);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The setting dialog of '{policy.FullName}' could not be created: {ex.Message}");
            return;
        }

        dialog.Applied += (_, _) => OnPolicySaved(store);
        _policyDialog = dialog;
        _ = RunPolicyDialogAsync(dialog, Win32Interop.GetWindowFromWindowId(environment.AppWindowId));
    }

    private async Task RunPolicyDialogAsync(PolicySettingDialog dialog, nint owner)
    {
        try
        {
            _ = await dialog.ShowAsync(owner);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The setting dialog failed: " + ex.Message);
        }
        finally
        {
            // Nothing here may throw: the task is not awaited, so an exception would end up unobserved.
            _policyDialog = null;
            try
            {
                SelectPolicyRow(dialog.Shown);
                StateChanged();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn("The window could not be updated after the setting dialog closed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// The one store of the window, made when the first dialog opens. It keeps the backup of the session, so the first save
    /// of a window makes one backup folder and the later saves make none. Null when the model chose no policy object.
    /// </summary>
    private GpoStore? SaveStoreOf(GpeditModel model)
    {
        if (model.StorePolicy is not { } policy)
        {
            return null;
        }

        if (_gpoStore is null || !string.Equals(_gpoStore.Policy.GpoRoot, policy.GpoRoot, StringComparison.OrdinalIgnoreCase))
        {
            _gpoStore = new GpoStore(policy, new GpoStoreOptions { Access = model.Access });
        }

        return _gpoStore;
    }

    /// <summary>
    /// A save wrote files: the loaded model becomes the files as read back after the write, so the list shows the saved state
    /// and every later dialog opens on it.
    /// </summary>
    private void OnPolicySaved(PolicySaveStore store)
    {
        if (store.Snapshot is { } snapshot && _model is { } model && !ReferenceEquals(model.Snapshot, snapshot))
        {
            _model = model.WithSnapshot(snapshot);
        }

        RefreshPolicyRows();
    }

    /// <summary>
    /// Rebuilds the rows of the shown node after an Apply, keeping the
    /// selection, so State and Comment are current.
    /// </summary>
    private void RefreshPolicyRows()
    {
        if (_model is null || Shell.TreeContent.SelectedNode is not { } node)
        {
            return;
        }

        try
        {
            GpeditListContent content = GpeditListBuilder.Build(node, _model, _source, _filter);
            if (!content.IsPlaceholder)
            {
                _list.SetRows(content.Rows, keepSelectionByTag: true);
                NoteContent(content);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The rows of '{node.Name}' could not be refreshed: {ex.Message}");
        }
    }

    /// <summary>After the dialog closes the list selects the last policy shown and takes the focus back.</summary>
    private void SelectPolicyRow(PolicyRef shown)
    {
        IReadOnlyList<ConsoleListRow> rows = _list.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Tag is GpeditRowTag { Kind: GpeditRowKind.Policy, Policy: { } rowPolicy } tag
                && ReferenceEquals(rowPolicy, shown.Policy) && tag.Side == shown.Side)
            {
                _list.SelectIndex(i, focus: true);
                return;
            }
        }
    }

    // ---- persistence --------------------------------------------------------------------------------

    private GpeditSettings LoadSettings()
    {
        try
        {
            return _settingsStore.Load();
        }
        catch (IOException ex)
        {
            Log.Warn("Local Group Policy Editor settings could not be loaded: " + ex.Message);
            return new GpeditSettings();
        }
    }

    private static bool IsUsableWidth(double width) =>
        !double.IsNaN(width) && !double.IsInfinity(width) && width >= MinSavedColumnWidth && width <= MaxSavedColumnWidth;

    /// <summary>Applies the saved tree width, view mode and tab. A value that is out of range or unknown is ignored.</summary>
    private void ApplySavedView()
    {
        if (_settings.TreePaneWidth is { } width && !double.IsNaN(width) && width > 0 && width <= MaxSavedTreeWidth)
        {
            Shell.TreePaneWidth = width;
        }

        if (Enum.TryParse(_settings.ViewMode, ignoreCase: false, out ConsoleListViewMode mode) && Enum.IsDefined(mode))
        {
            _list.ViewMode = mode;
        }

        if (Enum.TryParse(_settings.ExtendedTab, ignoreCase: false, out ConsoleViewTab tab) && Enum.IsDefined(tab))
        {
            _extended.SelectedTab = tab;
        }
    }

    private GpeditSettings CaptureSettings()
    {
        var snapshot = new GpeditSettings
        {
            TreePaneWidth = Shell.TreePaneWidth,
            ViewMode = _list.ViewMode.ToString(),
            ExtendedTab = _extended.SelectedTab.ToString(),
            WindowX = _settings.WindowX,
            WindowY = _settings.WindowY,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight,
            WindowMaximized = _settings.WindowMaximized,
            ColumnOrder = [.. _columnChoice.Order],
            HiddenColumns = [.. _columnChoice.Hidden],
            ViewConsoleTree = Shell.IsTreeVisible,
            ViewStandardToolbar = Shell.IsToolbarVisible,
            ViewSnapInToolbars = Shell.AreSnapInToolbarsVisible,
            ViewStatusBar = Shell.IsStatusBarVisible,
            ViewDescriptionBar = _extended.IsDescriptionBarShown,
            AllowCustomizeViews = _options.AllowCustomizeViews,
        };
        foreach (KeyValuePair<string, double> pair in _columnWidths)
        {
            snapshot.ColumnWidths[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, List<ConsoleListSortKey>> pair in _sortStacks)
        {
            snapshot.Sorts[pair.Key] = pair.Value.Select(key => $"{key.ColumnId}:{key.Direction}").ToList();
        }

        if (_appWindow is { } window)
        {
            bool restored = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };
            RectInt32 bounds = restored
                ? new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height)
                : _normalBounds ?? new RectInt32(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height);
            snapshot.WindowX = bounds.X;
            snapshot.WindowY = bounds.Y;
            snapshot.WindowWidth = bounds.Width;
            snapshot.WindowHeight = bounds.Height;
            snapshot.WindowMaximized = window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        }

        return snapshot;
    }

    /// <summary>Saves a short time after the last change, so a drag writes the file once. Closing saves at once.</summary>
    private void ScheduleSettingsSave()
    {
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private DispatcherQueueTimer CreateSaveTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SettingsSaveDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SaveSettings();
        return timer;
    }

    private void SaveSettings()
    {
        try
        {
            GpeditSettings snapshot = CaptureSettings();
            _settingsStore.Save(snapshot);
            _settings = snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Local Group Policy Editor settings could not be saved: " + ex.Message);
        }
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (_appWindow is not null || XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return;
        }

        try
        {
            _appWindow = AppWindow.GetFromWindowId(environment.AppWindowId);
            if (_appWindow is null)
            {
                return;
            }

            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += OnAppWindowChanged;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The window of the Local Group Policy Editor could not be tracked: " + ex.Message);
        }
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        _saveTimer?.Stop();
        CloseLoads();
        DetachWindow();
    }

    private void DetachWindow()
    {
        if (_appWindow is { } window)
        {
            window.Closing -= OnAppWindowClosing;
            window.Changed -= OnAppWindowChanged;
            _appWindow = null;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if ((args.DidPositionChange || args.DidSizeChange) && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _normalBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _saveTimer?.Stop();
        SaveSettings();
        CloseLoads();
        DetachWindow();
    }

    // ---- E2E readout --------------------------------------------------------------------------------

    /// <summary>
    /// E2E flavor only: publishes the state of the view as the UIA help text of GpeditRoot (docs/failure-modes/
    /// gpedit-tree-list.md, section 13) and, with BWC_E2E_GPEDIT_SHAPE=1, the whole tree as its full description. A
    /// screen reader would announce these, so the ship flavor never sets them.
    /// </summary>
    private void UpdateReadout()
    {
#if BWC_E2E
        GpeditModel? model = _model;
        var text = new StringBuilder();
        void Add(string key, object? value) =>
            text.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace(';', ',')).Append(';');

        Add("Mode", "gpedit");
        Add("Loaded", model is null ? 0 : 1);
        Add("ReadOnly", model is { IsReadOnly: true } ? 1 : 0);
        if (model is not null)
        {
            Add("LoadMs", (int)model.LoadTime.TotalMilliseconds);
            Add("Templates", model.Templates.Files.Count);
            Add("Categories", model.Templates.Categories.Count);
            Add("PolicyCount", model.Templates.Policies.Count);
            Add("Failures", model.Templates.Failures.Count);
            Add("Problems", model.Problems.Count);
            Add(
                "ProblemList",
                string.Join('|', model.Problems.Select(p => p.Kind + "/" + p.Detail + "/" + System.IO.Path.GetFileName(p.Path))));
            Add("Store", model.StoreDescription.StartsWith("Sandbox", StringComparison.Ordinal) ? "sandbox" : model.StoreDescription.StartsWith("DenyAll", StringComparison.Ordinal) ? "denyall" : "system");
        }

        Add("Node", GpeditTreeSource.PathOf(Shell.TreeContent.SelectedNode));
        Add("Page", _placeholderShown ? "placeholder" : "list");
        Add("Layout", _layout?.ToString() ?? "none");
        Add("Rows", _list.Count);
        Add("ShownFolders", _list.Rows.Count(r => r.Tag is GpeditRowTag { Kind: GpeditRowKind.Folder }));
        Add("ShownPolicies", _list.Rows.Count(r => r.Tag is GpeditRowTag { Kind: GpeditRowKind.Policy }));
        Add("Status", Shell.StatusText);
        Add("FilterOn", _filter.IsOn ? 1 : 0);
        Add("FilterShown", _filter.IsOn ? _list.Rows.Count(r => r.Tag is GpeditRowTag { Kind: GpeditRowKind.Policy }) : _filterTotal);
        Add("FilterTotal", _filterTotal);
        Add("Links", _linkInvocations);
        Add("Edits", _editRequests);
        Add("LastEdit", _lastEdit);
        Add("Refreshes", _refreshes);
        Add("TemplateReloads", _templateReloads);
        Add("Reloading", _loadInFlight && !_loadIsInitial ? 1 : 0);
        Add("HelpRequests", _helpRequests);
        Add("WebSiteRequests", _webSiteRequests);
        Add("WebSiteUrl", _webSiteUrl);
        Add("LastCommand", _lastCommand);
        Add("LineUps", _lineUps);
        Add("AutoArrange", _autoArrange ? 1 : 0);
        AutomationProperties.SetHelpText(this, text.ToString());

        if (model is not null && Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_SHAPE") == "1")
        {
            AutomationProperties.SetFullDescription(this, _source.DescribeShape(model));
        }

        if (model is not null && Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_ICONS") == "1")
        {
            AutomationProperties.SetFullDescription(this, _source.DescribeIcons(model));
        }
#endif
    }
}
