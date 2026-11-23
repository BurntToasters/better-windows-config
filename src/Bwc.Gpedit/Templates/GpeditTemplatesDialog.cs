using Bwc.Core;
using Bwc.Gpedit.Policy;
using Bwc.UI.Dialogs;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.Gpedit.Templates;

/// <summary>
/// The Add/Remove Templates dialog of the Local Group Policy Editor: the imported templates of the BWC store with their
/// namespace and status, Add (a file picker, then the engine's import), Remove (the selected row), Close, and the list of
/// the template problems of the last load. The window has the automation id <c>GpeditTemplates</c>; its parts have the ids
/// <c>GpeditTemplates.&lt;Part&gt;</c>. Add and Remove run off the UI thread, keep no copy of the engine's checks and
/// show a refusal in the details box (<see cref="GpeditTemplateDetailsDialog"/>). The dialog reports whether the store
/// changed, and the window reloads once when it closes after a change (docs/failure-modes/gpedit-templates.md, sections
/// 2 to 9).
/// </summary>
internal sealed class GpeditTemplatesDialog : ClassicDialog
{
    internal const string RootId = "GpeditTemplates";

    private const string Prefix = RootId + ".";
    private const int StaticId = 65535;
    private const int AddId = 303;
    private const int RemoveId = 304;
    private const int CloseId = 2;
    private const double NameColumnDip = 150;
    private const double NamespaceColumnDip = 170;

    private readonly GpeditTemplateStore _store;
    private readonly IReadOnlyList<GpeditProblem> _problems;
    private readonly ListBox _list;
    private readonly ListBox _problemList;
    private readonly Control _add;
    private readonly Control _remove;
    private readonly List<TemplateRowInfo> _rows = [];
    private Task _pending = Task.CompletedTask;
    private bool _busy;
    private bool _closed;

    internal GpeditTemplatesDialog(GpeditTemplateStore store, IReadOnlyList<TemplateRowInfo> rows, IReadOnlyList<GpeditProblem> problems)
        : base(BuildTemplate())
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(problems);
        _store = store;
        _problems = problems;

        // The window is found by this id; the base names the root of every classic dialog DialogRoot.
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, GpeditStrings.Templates("TemplatesCaption"));
        }

        _list = RequireControl<ListBox>(Prefix + "List");
        _problemList = RequireControl<ListBox>(Prefix + "Problems");
        _add = RequireControl<Control>(Prefix + "Add");
        _remove = RequireControl<Control>(Prefix + "Remove");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        Closed += (_, _) => _closed = true;

        ShowRows(rows);
        ShowProblems();
        UpdateButtons();
    }

    /// <summary>True once an Add imported a template or a Remove removed one. The window reloads when it closes then.</summary>
    internal bool Changed { get; private set; }

    /// <summary>
    /// Completes when the Add or Remove that is running has finished. The window waits for it after the dialog closed, so a
    /// change that finished late is still counted.
    /// </summary>
    internal Task PendingAsync() => _pending;

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case AddId:
                if (!_busy)
                {
                    // The click returns first; the modal picker opens from the dispatcher, so a UI Automation Invoke is not held.
                    _ = DispatcherQueue.TryEnqueue(StartAdd);
                }

                break;
            case RemoveId:
                if (!_busy)
                {
                    StartRemove();
                }

                break;
            default:
                // Close, Escape and Enter do nothing while an Add or Remove is running: the window never closes under it.
                if (!_busy)
                {
                    base.OnCommand(id, automationId);
                }

                break;
        }
    }

    // ---- Add ----------------------------------------------------------------------------------------

    private void StartAdd()
    {
        if (_busy || _closed)
        {
            return;
        }

        string? path;
        try
        {
            _busy = true;
            UpdateButtons();
            path = TemplateFilePicker.Pick(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The file picker of the Add/Remove Templates dialog failed: " + ex.Message);
            path = null;
        }

        if (string.IsNullOrEmpty(path))
        {
            // A cancelled picker changes nothing.
            _busy = false;
            UpdateButtons();
            return;
        }

        _pending = RunAddAsync(path);
    }

    private async Task RunAddAsync(string path)
    {
        try
        {
            TemplateOperationResult result = await Task.Run(() => _store.Add(path));
            await FinishAsync(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("An Add of the Add/Remove Templates dialog failed: " + ex.Message);
            _busy = false;
        }
    }

    // ---- Remove -------------------------------------------------------------------------------------

    private void StartRemove()
    {
        if (_busy || _closed || _list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count)
        {
            return;
        }

        TemplateRowInfo row = _rows[_list.SelectedIndex];
        _busy = true;
        UpdateButtons();
        _pending = RunRemoveAsync(row);
    }

    private async Task RunRemoveAsync(TemplateRowInfo row)
    {
        try
        {
            TemplateOperationResult result = await Task.Run(() => _store.Remove(row));
            await FinishAsync(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("A Remove of the Add/Remove Templates dialog failed: " + ex.Message);
            _busy = false;
        }
    }

    // ---- after an Add or a Remove -------------------------------------------------------------------

    /// <summary>
    /// The list is read again from disk, so it is what the store holds now, and the details box shows the problems of a
    /// refusal. The dialog stays open either way.
    /// </summary>
    private async Task FinishAsync(TemplateOperationResult result)
    {
        if (result.Changed)
        {
            Changed = true;
        }

        IReadOnlyList<TemplateRowInfo>? rows = null;
        try
        {
            rows = await Task.Run(_store.ReadRows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Log.Warn("The list of the Add/Remove Templates dialog could not be read again: " + ex.Message);
        }

        if (_closed)
        {
            _busy = false;
            return;
        }

        if (rows is not null)
        {
            ShowRows(rows);
        }

        _busy = false;
        UpdateButtons();
        if (result.Problems.Count > 0)
        {
            await ShowDetailsAsync(GpeditTemplateStore.FormatProblems(result.Problems));
        }
    }

    private async Task ShowDetailsAsync(string text)
    {
        try
        {
            var details = new GpeditTemplateDetailsDialog(text);
            _ = await details.ShowAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The details box of the Add/Remove Templates dialog could not be shown: " + ex.Message);
        }
    }

    // ---- the lists ----------------------------------------------------------------------------------

    private void ShowRows(IReadOnlyList<TemplateRowInfo> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        _list.Items.Clear();
        foreach (TemplateRowInfo row in rows)
        {
            string status = StatusWord(row.Status);
            _list.Items.Add(MakeItem(
                _list,
                row.FileName,
                $"Namespace={Plain(row.Namespace)};Status={row.Status};",
                [(row.FileName, NameColumnDip), (row.Namespace, NamespaceColumnDip), (status, double.NaN)]));
        }

        UpdateButtons();
    }

    private void ShowProblems()
    {
        _problemList.Items.Clear();
        foreach (GpeditProblem problem in _problems)
        {
            if (problem.Kind is not (GpeditProblemKind.TemplateFailure or GpeditProblemKind.TemplateStore or GpeditProblemKind.LoadException))
            {
                continue;
            }

            string name = GpeditTemplateStore.FileNameOf(problem.Path);
            _problemList.Items.Add(MakeItem(
                _problemList,
                name,
                $"Code={Plain(problem.Detail)};Detail={Plain(problem.Message)};Kind={problem.Kind};",
                [(name, NameColumnDip), (problem.Detail, NamespaceColumnDip), (problem.Message, double.NaN)]));
        }
    }

    /// <summary>One line of a list: the parts in columns, the file name as the name and the values as the help text.</summary>
    private static ListBoxItem MakeItem(ListBox list, string name, string help, (string Text, double Width)[] parts)
    {
        var grid = new Grid();
        int column = 0;
        foreach ((string text, double width) in parts)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width),
            });
            var cell = new TextBlock
            {
                Text = text,
                Foreground = BwcPaint.WindowText,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cell, column++);
            grid.Children.Add(cell);
        }

        var item = new ListBoxItem
        {
            Content = grid,
            Style = list.ItemContainerStyle,
        };
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetHelpText(item, help);
        return item;
    }

    /// <summary>A value of the help text: a semicolon would end it early.</summary>
    private static string Plain(string value) => value.Replace(';', ',');

    private static string StatusWord(TemplateRowStatus status) => status switch
    {
        TemplateRowStatus.Loaded => GpeditStrings.Templates("TemplatesStatusLoaded"),
        TemplateRowStatus.Shadowed => GpeditStrings.Templates("TemplatesStatusShadowed"),
        _ => GpeditStrings.Templates("TemplatesStatusNotLoaded"),
    };

    /// <summary>Remove needs a selected row. Nothing is enabled while an Add or Remove runs.</summary>
    private void UpdateButtons()
    {
        _add.IsEnabled = !_busy;
        _remove.IsEnabled = !_busy && _list.SelectedIndex >= 0;
    }

    // ---- the template -------------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate()
    {
        // 288 x 196 dialog units: the five parts of the classic dialog (label, list, Add, Remove, Close) in reading order, and
        // under them the list of template problems, which classic shows in a box at start.
        ClassicControlDefinition[] controls =
        [
            Label("LabelList", "TemplatesLabelList", 7, 7, 274, 8),
            ListDef("List", "TemplatesNameList", 7, 17, 274, 64),
            Button(AddId, "Add", "TemplatesAdd", 7, 86, 50, 14),
            Button(RemoveId, "Remove", "TemplatesRemove", 61, 86, 50, 14),
            Button(CloseId, "Close", "TemplatesClose", 231, 86, 50, 14, isDefault: true),
            Label("LabelProblems", "TemplatesLabelProblems", 7, 108, 274, 8),
            ListDef("Problems", "TemplatesNameProblems", 7, 118, 274, 70),
        ];
        return new ClassicDialogDefinition(
            GpeditStrings.Templates("TemplatesCaption"),
            "Add/Remove Templates dialog of the Local Group Policy Editor",
            288,
            196,
            controls);
    }

    private static ClassicControlDefinition Label(string part, string textKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.Label, StaticId, Prefix + part, GpeditStrings.Templates(textKey), x, y, cx, cy);

    private static ClassicControlDefinition ListDef(string part, string nameKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.ListBox, StaticId, Prefix + part, GpeditStrings.Templates(nameKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition Button(int id, string part, string textKey, int x, int y, int cx, int cy, bool isDefault = false) =>
        new(ClassicControlKind.Button, id, Prefix + part, GpeditStrings.Templates(textKey), x, y, cx, cy) { TabStop = true, IsDefault = isDefault };
}
