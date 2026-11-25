using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Console.Frame;

/// <summary>
/// The Add/Remove Columns dialog of the console frame (mmcndmgr.dll RT_DIALOG 1070, DLG-061, DLG-118): the columns that are
/// not shown on the left, the columns that are shown on the right, Add and Remove between them, Move Up, Move Down and
/// Restore Defaults beside the shown columns, OK and Cancel. Nothing reaches the list before OK. The first column of the
/// list is fixed: it cannot be removed or moved, and no column can move above it (docs/failure-modes/console-frame.md,
/// section 3). The window has the automation id <c>ConsoleColumns</c> and its parts <c>ConsoleColumns.&lt;Part&gt;</c>.
/// </summary>
public sealed class ConsoleColumnsDialog : ClassicDialog
{
    internal const string RootId = "ConsoleColumns";

    private const string Prefix = RootId + ".";
    private const int StaticId = 65535;
    private const int AvailableListId = 4084;
    private const int AddId = 4085;
    private const int RemoveId = 4086;
    private const int DisplayedListId = 4088;
    private const int MoveUpId = 4089;
    private const int MoveDownId = 4090;
    private const int RestoreId = 4087;

    private readonly record struct Entry(string Id, string Header, int Index);

    private readonly List<Entry> _definition;
    private readonly List<Entry> _displayed = [];
    private readonly List<Entry> _available = [];
    private readonly ListBox _availableList;
    private readonly ListBox _displayedList;
    private readonly Control _add;
    private readonly Control _remove;
    private readonly Control _moveUp;
    private readonly Control _moveDown;
    private bool _filling;

    public ConsoleColumnsDialog(IReadOnlyList<ConsoleListColumn> columns)
        : base(BuildTemplate())
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("The list has no columns.", nameof(columns));
        }

        // The window is found by this id; the base names the root of every classic dialog DialogRoot.
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, ConsoleStrings.Get("ConsoleColumnsCaption"));
        }

        _definition = [.. columns.OrderBy(column => column.Index).Select(column => new Entry(column.Id, column.Header, column.Index))];
        Entry first = _definition[0];
        _displayed.Add(first);
        _displayed.AddRange(columns.Where(column => column.Visible && column.Id != first.Id).Select(column => new Entry(column.Id, column.Header, column.Index)));
        _available.AddRange(_definition.Where(entry => _displayed.All(shown => shown.Id != entry.Id)));

        _availableList = RequireControl<ListBox>(Prefix + "Available");
        _displayedList = RequireControl<ListBox>(Prefix + "Displayed");
        _add = RequireControl<Control>(Prefix + "Add");
        _remove = RequireControl<Control>(Prefix + "Remove");
        _moveUp = RequireControl<Control>(Prefix + "MoveUp");
        _moveDown = RequireControl<Control>(Prefix + "MoveDown");
        _availableList.SelectionChanged += OnSelectionChanged;
        _displayedList.SelectionChanged += OnSelectionChanged;
        Fill(-1, -1);
    }

    /// <summary>The choice that OK approved: the shown columns in order, then the others; the others are hidden.</summary>
    public ConsoleColumnChoice? Result { get; private set; }

    protected override bool TryAccept()
    {
        Result = new ConsoleColumnChoice(
            [.. _displayed.Select(entry => entry.Id), .. _available.Select(entry => entry.Id)],
            [.. _available.Select(entry => entry.Id)]);
        return true;
    }

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case AddId:
                Add();
                break;
            case RemoveId:
                Remove();
                break;
            case MoveUpId:
                Move(-1);
                break;
            case MoveDownId:
                Move(1);
                break;
            case RestoreId:
                Restore();
                break;
            default:
                base.OnCommand(id, automationId);
                break;
        }
    }

    // ---- the operations ---------------------------------------------------------------------------

    /// <summary>Add: the selected available column goes to the end of the shown columns.</summary>
    private void Add()
    {
        int at = _availableList.SelectedIndex;
        if (at < 0 || at >= _available.Count)
        {
            return;
        }

        Entry entry = _available[at];
        _available.RemoveAt(at);
        _displayed.Add(entry);
        Fill(Math.Min(at, _available.Count - 1), _displayed.Count - 1);
    }

    /// <summary>Remove: the selected shown column goes to the available columns, which stay in the definition order.</summary>
    private void Remove()
    {
        int at = _displayedList.SelectedIndex;
        if (!CanRemove(at))
        {
            return;
        }

        Entry entry = _displayed[at];
        _displayed.RemoveAt(at);
        _available.Add(entry);
        _available.Sort((a, b) => a.Index.CompareTo(b.Index));
        int nextShown = Math.Min(at, _displayed.Count - 1);
        Fill(_available.FindIndex(e => e.Id == entry.Id), nextShown >= 1 ? nextShown : -1);
    }

    /// <summary>Move Up and Move Down: the selected shown column changes places with its neighbor and stays selected.</summary>
    private void Move(int step)
    {
        int at = _displayedList.SelectedIndex;
        if (step < 0 ? !CanMoveUp(at) : !CanMoveDown(at))
        {
            return;
        }

        (_displayed[at], _displayed[at + step]) = (_displayed[at + step], _displayed[at]);
        Fill(_availableList.SelectedIndex, at + step);
    }

    /// <summary>Restore Defaults: every column shown, in definition order. It changes the dialog only; OK applies it.</summary>
    private void Restore()
    {
        _displayed.Clear();
        _displayed.AddRange(_definition);
        _available.Clear();
        Fill(-1, -1);
    }

    private bool CanRemove(int at) => at >= 1 && at < _displayed.Count;

    private bool CanMoveUp(int at) => at >= 2 && at < _displayed.Count;

    private bool CanMoveDown(int at) => at >= 1 && at < _displayed.Count - 1;

    // ---- the lists --------------------------------------------------------------------------------

    private void Fill(int selectAvailable, int selectDisplayed)
    {
        _filling = true;
        try
        {
            FillList(_availableList, _available);
            FillList(_displayedList, _displayed);
            _availableList.SelectedIndex = selectAvailable >= 0 && selectAvailable < _available.Count ? selectAvailable : -1;
            _displayedList.SelectedIndex = selectDisplayed >= 0 && selectDisplayed < _displayed.Count ? selectDisplayed : -1;
        }
        finally
        {
            _filling = false;
        }

        UpdateButtons();
    }

    private static void FillList(ListBox list, List<Entry> entries)
    {
        list.Items.Clear();
        foreach (Entry entry in entries)
        {
            list.Items.Add(entry.Header);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling)
        {
            UpdateButtons();
        }
    }

    /// <summary>
    /// Add needs a selected available column. Remove, Move Up and Move Down need a selected shown column that is not the
    /// first, and a move needs room: the second column cannot go above the first and the last cannot go down.
    /// </summary>
    private void UpdateButtons()
    {
        int shown = _displayedList.SelectedIndex;
        _add.IsEnabled = _availableList.SelectedIndex >= 0 && _availableList.SelectedIndex < _available.Count;
        _remove.IsEnabled = CanRemove(shown);
        _moveUp.IsEnabled = CanMoveUp(shown);
        _moveDown.IsEnabled = CanMoveDown(shown);
    }

    // ---- the template -----------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate()
    {
        // The tab order of the classic dialog: Available, Add, Remove, Displayed, Move Up, Move Down, Restore Defaults,
        // OK, Cancel.
        ClassicControlDefinition[] controls =
        [
            Label("LabelAvailable", "ConsoleColumnsLabelAvailable", 7, 7, 93, 8),
            ListDef(AvailableListId, "Available", "ConsoleColumnsNameAvailable", 7, 17, 90, 118),
            Button(AddId, "Add", "ConsoleColumnsAdd", 101, 44, 60, 14),
            Button(RemoveId, "Remove", "ConsoleColumnsRemove", 101, 62, 60, 14),
            Label("LabelDisplayed", "ConsoleColumnsLabelDisplayed", 165, 7, 93, 8),
            ListDef(DisplayedListId, "Displayed", "ConsoleColumnsNameDisplayed", 165, 17, 90, 118),
            Button(MoveUpId, "MoveUp", "ConsoleColumnsMoveUp", 259, 17, 80, 14),
            Button(MoveDownId, "MoveDown", "ConsoleColumnsMoveDown", 259, 35, 80, 14),
            Button(RestoreId, "Restore", "ConsoleColumnsRestore", 259, 59, 80, 14),
            Button(1, "Ok", "ConsoleDialogOk", 235, 151, 50, 14, isDefault: true),
            Button(2, "Cancel", "ConsoleDialogCancel", 289, 151, 50, 14),
        ];
        return new ClassicDialogDefinition(
            ConsoleStrings.Get("ConsoleColumnsCaption"),
            "Add/Remove Columns dialog of the console frame",
            346,
            172,
            controls);
    }

    private static ClassicControlDefinition Label(string part, string textKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.Label, StaticId, Prefix + part, ConsoleStrings.Get(textKey), x, y, cx, cy);

    private static ClassicControlDefinition ListDef(int id, string part, string nameKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.ListBox, id, Prefix + part, ConsoleStrings.Get(nameKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition Button(int id, string part, string textKey, int x, int y, int cx, int cy, bool isDefault = false) =>
        new(ClassicControlKind.Button, id, Prefix + part, ConsoleStrings.Get(textKey), x, y, cx, cy) { TabStop = true, IsDefault = isDefault };
}
