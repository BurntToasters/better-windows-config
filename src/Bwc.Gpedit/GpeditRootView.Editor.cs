using System.Globalization;
using System.Text;
using Bwc.Core;
using Bwc.Gpedit.List;
using Bwc.Gpedit.Policy;
using Bwc.Gpedit.Tree;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.UI.Console.Extended;
using Bwc.UI.Console.List;
using Bwc.UI.Tree;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics;

namespace Bwc.Gpedit;

/// <summary>
/// The editor wiring of the view (piece G1): the console tree over the template categories, the result list with State and
/// Comment read from the side's policy file, the description pane, the status text, the persisted view state and the E2E
/// readout. Reading only: no save and no commands (docs/failure-modes/gpedit-tree-list.md).
/// </summary>
public sealed partial class GpeditRootView
{
    private const string ToolId = "gpedit";
    private const int SettingsSaveDelayMs = 500;
    private const double SettingColumnWidth = 350;
    private const double StateColumnWidth = 100;
    private const double CommentColumnWidth = 139;
    private const double NameColumnWidth = 180;
    private const double MinSavedColumnWidth = ConsoleList.MinColumnWidth;
    private const double MaxSavedColumnWidth = ConsoleList.MaxColumnWidth;
    private const double MaxSavedTreeWidth = 4096;
    private const int PolicyEditSteps = 3;

    private readonly SettingsStore<GpeditSettings> _settingsStore =
        new(AppPaths.SettingsFile(ToolId), GpeditSettingsContext.Default.GpeditSettings);

    private readonly Dictionary<string, double> _columnWidths = new(StringComparer.Ordinal);
    private readonly GpeditPlaceholderPage _placeholder = new();
    private readonly CancellationTokenSource _loadCancel = new();

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

    /// <summary>
    /// Raised when the user asks to edit a policy: the Edit policy setting link, Enter or a double click on a policy row.
    /// The setting dialog (piece G3a) handles it; until then nothing subscribes.
    /// </summary>
    public event EventHandler<GpeditPolicyEditEventArgs>? PolicyEditRequested;

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

        ConfigureEditorResultPane();
        StartModelLoad();

        Shell.TreeContent.SetRoots([_source.Root]);
        Shell.TreeContent.SelectionChanged += OnTreeSelectionChanged;
        ApplySavedView();
        Shell.TreeContent.SelectNode(_source.Root);
        SyncViewChecks();

        Loaded += OnEditorLoaded;
        Unloaded += OnEditorUnloaded;
    }

    private void ConfigureEditorResultPane()
    {
        // The classic list names a row by its text plus a word for a policy; the list control adds nothing itself.
        _list.ItemNameFormatter = GpeditListBuilder.RowName;
        _list.ItemActivated += OnEditorItemActivated;
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

    // ---- model --------------------------------------------------------------------------------------

    private void StartModelLoad()
    {
        CancellationToken token = _loadCancel.Token;
        Task<GpeditModel> task = Task.Run(() => GpeditModel.Load(token), token);
        _source = new GpeditTreeSource(task);
        _ = AwaitModelAsync(task);
    }

    private async Task AwaitModelAsync(Task<GpeditModel> task)
    {
        try
        {
            GpeditModel model = await task;
            _model = model;
            Log.Info(
                $"Local Group Policy Editor model loaded in {model.LoadTime.TotalMilliseconds:0} ms: " +
                $"{model.Templates.Files.Count} templates, {model.Templates.Categories.Count} categories, " +
                $"{model.Templates.Policies.Count} policies, {model.Problems.Count} problems.");
            if (Shell.TreeContent.SelectedNode is { } selected)
            {
                ShowNode(selected);
            }
            else
            {
                UpdateReadout();
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed while the templates were loading.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("Local Group Policy Editor model could not be applied: " + ex.Message);
        }
    }

    // ---- showing a node -----------------------------------------------------------------------------

    private void ShowNode(TreeNode node)
    {
        try
        {
            GpeditListContent content = GpeditListBuilder.Build(node, _model, _source);
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
            _list.SetRows(content.Rows);
        }

        _extended.Description = content.Description;
        Shell.StatusText = content.StatusText;
        UpdateReadout();
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
        _list.SetColumns(layout == GpeditListLayout.Navigation
            ? [Column("name", "GpeditColumnName", NameColumnWidth)]
            :
            [
                Column("setting", "GpeditColumnSetting", SettingColumnWidth),
                Column("state", "GpeditColumnState", StateColumnWidth),
                Column("comment", "GpeditColumnComment", CommentColumnWidth),
            ]);
    }

    private ConsoleListColumn Column(string id, string headerKey, double defaultWidth) =>
        new(id, GpeditStrings.Tree(headerKey), _columnWidths.TryGetValue(id, out double saved) ? saved : defaultWidth);

    private void UpdateRowDescription()
    {
        IReadOnlyList<ConsoleListRow> selected = _list.SelectedRows;
        ConsoleDescriptionModel? row = selected.Count == 1 ? GpeditListBuilder.DescribeRow(selected[0]) : null;
        _extended.Description = row ?? _nodeDescription;
        UpdateReadout();
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
        try
        {
            PolicyEditRequested?.Invoke(this, new GpeditPolicyEditEventArgs(policy, side));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"The edit request of '{policy.FullName}' failed: {ex.Message}");
        }

        UpdateReadout();
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
        };
        foreach (KeyValuePair<string, double> pair in _columnWidths)
        {
            snapshot.ColumnWidths[pair.Key] = pair.Value;
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

            // The head resizes the window in its own Loaded handler, which runs after this one. The saved placement is
            // applied once the dispatcher is idle again.
            _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplySavedPlacement);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The window of the Local Group Policy Editor could not be tracked: " + ex.Message);
        }
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCancel.Cancel();
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
        _loadCancel.Cancel();
    }

    /// <summary>
    /// Moves and sizes the window from the saved rectangle, fitted into the work area of the display it overlaps (or the
    /// nearest one). Nothing is changed when no usable rectangle was saved.
    /// </summary>
    private void ApplySavedPlacement()
    {
        if (_appWindow is not { } window
            || _settings.WindowX is not { } x || _settings.WindowY is not { } y
            || _settings.WindowWidth is not { } width || _settings.WindowHeight is not { } height
            || width <= 0 || height <= 0 || width > 32767 || height > 32767)
        {
            return;
        }

        try
        {
            var saved = new RectInt32(x, y, width, height);
            RectInt32 work = DisplayArea.GetFromRect(saved, DisplayAreaFallback.Nearest).WorkArea;
            int fittedWidth = Math.Min(width, work.Width);
            int fittedHeight = Math.Min(height, work.Height);
            int fittedX = Math.Clamp(x, work.X, work.X + work.Width - fittedWidth);
            int fittedY = Math.Clamp(y, work.Y, work.Y + work.Height - fittedHeight);
            var fitted = new RectInt32(fittedX, fittedY, fittedWidth, fittedHeight);
            window.MoveAndResize(fitted);
            _normalBounds = fitted;
            if (_settings.WindowMaximized && window.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The saved window placement could not be applied: " + ex.Message);
        }
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
        Add("Links", _linkInvocations);
        Add("Edits", _editRequests);
        Add("LastEdit", _lastEdit);
        AutomationProperties.SetHelpText(this, text.ToString());

        if (model is not null && Environment.GetEnvironmentVariable("BWC_E2E_GPEDIT_SHAPE") == "1")
        {
            AutomationProperties.SetFullDescription(this, _source.DescribeShape(model));
        }
#endif
    }
}
