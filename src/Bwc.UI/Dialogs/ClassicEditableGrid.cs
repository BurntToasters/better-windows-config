using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The classic editable list of the policy "Show Contents" dialog: one column (Value) or two (Value name, Value). The grid
/// always ends with one empty row. Typing into it makes it a real row and adds a new empty row. A cell is read-only until
/// F2, a double click, or a printable key starts an edit. Enter commits and moves to the next column, Escape reverts,
/// Delete on a cell that is not being edited removes the row, the arrows move between cells, and Tab leaves the grid.
/// Every cell is an edit with its own automation id (<c>id.R0C1</c>) and name ("Value name, row 1")
/// (docs/failure-modes/classic-dialog.md, sections 14.14 to 14.19).
/// </summary>
public sealed class ClassicEditableGrid : ContentControl, IClassicFocusHost
{
    private readonly IReadOnlyList<string> _columns;
    private readonly ClassicFontSpec _font;
    private readonly ResourceDictionary _styles;
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroller;
    private readonly List<Row> _rows = [];
    private string _id = string.Empty;
    private int _currentRow;
    private int _currentColumn;
    private TextBox? _editing;
    private string _editOriginal = string.Empty;
    private bool _suspend;
    private int _lastRemoved = -1;

    /// <param name="columns">The column titles. One or two.</param>
    /// <param name="font">The dialog font.</param>
    /// <param name="styles">The classic dialog styles.</param>
    public ClassicEditableGrid(IReadOnlyList<string> columns, ClassicFontSpec font, ResourceDictionary styles)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(styles);
        if (columns.Count is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns.Count, "An editable grid has one or two columns.");
        }

        _columns = columns;
        _font = font;
        _styles = styles;

        var header = new Grid { Background = BwcBrushes.Face };
        for (int i = 0; i < columns.Count; i++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var title = new TextBlock
            {
                Text = columns[i],
                Margin = new Thickness(3, 1, 3, 1),
                FontFamily = new FontFamily(font.Face),
                FontSize = font.DipSize,
                Foreground = BwcBrushes.WindowText,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(title, i);
            header.Children.Add(title);
        }

        var headerLine = new Border { BorderBrush = BwcBrushes.Shadow, BorderThickness = new Thickness(0, 0, 0, 1), Child = header };

        _scroller = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsTabStop = false,
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(headerLine, 0);
        Grid.SetRow(_scroller, 1);
        layout.Children.Add(headerLine);
        layout.Children.Add(_scroller);

        Content = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = BwcBrushes.WindowText,
            Background = BwcBrushes.Window,
            Child = layout,
        };
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;

        AddRow(isNew: true, []);
        Renumber();
    }

    /// <summary>Number of columns, 1 or 2.</summary>
    public int ColumnCount => _columns.Count;

    /// <summary>Number of real rows. The trailing empty row is not counted.</summary>
    public int RowCount => _rows.Count - 1;

    /// <summary>The cell texts of the real rows, as they are now (an edit in progress is included).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows =>
        _rows.Take(_rows.Count - 1).Select(row => (IReadOnlyList<string>)row.Cells.Select(c => c.Text ?? string.Empty).ToArray()).ToArray();

    /// <summary>Raised when a row is added, removed, or a cell edit is committed.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>The cell that takes focus: the one that had it last, or the first.</summary>
    FrameworkElement IClassicFocusHost.FocusTarget => CellAt(_currentRow, _currentColumn);

    /// <summary>The text box of one cell (0 based; the empty row is at <see cref="RowCount"/>).</summary>
    public TextBox CellAt(int row, int column)
    {
        int r = Math.Clamp(row, 0, _rows.Count - 1);
        int c = Math.Clamp(column, 0, _columns.Count - 1);
        return _rows[r].Cells[c];
    }

    /// <summary>Replaces the rows. Missing texts are empty. The empty row stays last.</summary>
    public void SetRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        EndEdit(commit: false);
        _body.Children.Clear();
        _rows.Clear();
        foreach (IReadOnlyList<string> row in rows)
        {
            AddRow(isNew: false, row);
        }

        AddRow(isNew: true, []);
        _currentRow = 0;
        _currentColumn = 0;
        Renumber();
    }

    /// <summary>Sets the automation id and name of the grid. The cells take ids from the grid id.</summary>
    internal void SetIdentity(string automationId, string name)
    {
        _id = automationId;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, name);
        Renumber();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private void AddRow(bool isNew, IReadOnlyList<string> texts)
    {
        var panel = new Grid();
        var cells = new TextBox[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBox cell = CreateCell(isNew, i < texts.Count ? texts[i] : string.Empty);
            Grid.SetColumn(cell, i);
            panel.Children.Add(cell);
            cells[i] = cell;
        }

        _rows.Add(new Row(panel, cells, isNew));
        _body.Children.Add(panel);
    }

    private TextBox CreateCell(bool isNew, string text)
    {
        var cell = new TextBox
        {
            Style = ClassicControlFactory.StyleOf(_styles, "BwcClassicBareTextBoxStyle"),
            FontFamily = new FontFamily(_font.Face),
            FontSize = _font.DipSize,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            Text = text,
            // The empty row takes typing directly. A real row is read-only until an edit starts.
            IsReadOnly = !isNew,
            IsTabStop = true,
        };
        ApplyVisual(cell, focused: false);
        Bwc.UI.Menu.TextEditMenu.Attach(cell);
        cell.PreviewKeyDown += OnCellKeyDown;
        cell.GotFocus += OnCellGotFocus;
        cell.LostFocus += OnCellLostFocus;
        cell.TextChanged += OnCellTextChanged;
        cell.DoubleTapped += OnCellDoubleTapped;
        return cell;
    }

    private void OnCellGotFocus(object sender, RoutedEventArgs e)
    {
        var cell = (TextBox)sender;
        if (TryLocate(cell, out int row, out int column))
        {
            _currentRow = row;
            _currentColumn = column;
        }

        ApplyVisual(cell, focused: true);
        cell.StartBringIntoView();
    }

    private void OnCellLostFocus(object sender, RoutedEventArgs e)
    {
        var cell = (TextBox)sender;
        if (ReferenceEquals(_editing, cell))
        {
            EndEdit(commit: true);
        }

        ApplyVisual(cell, focused: false);
    }

    private void OnCellDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var cell = (TextBox)sender;
        if (cell.IsReadOnly)
        {
            BeginEdit(cell, selectAll: true);
        }
    }

    /// <summary>The first character in the empty row makes it a real row and adds a new empty row.</summary>
    private void OnCellTextChanged(object sender, TextChangedEventArgs e)
    {
        var cell = (TextBox)sender;
        if (_suspend || !TryLocate(cell, out int index, out _))
        {
            return;
        }

        Row row = _rows[index];
        if (!row.IsNew || string.IsNullOrEmpty(cell.Text))
        {
            return;
        }

        row.IsNew = false;
        foreach (TextBox other in row.Cells)
        {
            // Only the cell being typed in stays editable. The other cells of the new real row are read-only like any row.
            other.IsReadOnly = !ReferenceEquals(other, cell);
        }

        _editing = cell;
        _editOriginal = string.Empty;
        AddRow(isNew: true, []);
        Renumber();
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var cell = (TextBox)sender;
        if (!TryLocate(cell, out int rowIndex, out int column))
        {
            return;
        }

        Row row = _rows[rowIndex];
        bool activeEdit = ReferenceEquals(_editing, cell);
        bool editing = activeEdit || row.IsNew; // The trailing row accepts text without an active edit.
        bool ctrlOrAlt = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu);

        switch (e.Key)
        {
            case VirtualKey.Up when !ctrlOrAlt:
                e.Handled = true;
                MoveTo(rowIndex - 1, column);
                return;

            case VirtualKey.Down when !ctrlOrAlt:
                e.Handled = true;
                MoveTo(rowIndex + 1, column);
                return;

            case VirtualKey.Left when !ctrlOrAlt && (!editing || (cell.SelectionStart == 0 && cell.SelectionLength == 0)):
                if (column > 0)
                {
                    e.Handled = true;
                    MoveTo(rowIndex, column - 1);
                }

                return;

            case VirtualKey.Right when !ctrlOrAlt &&
                (!editing || (cell.SelectionStart == cell.Text.Length && cell.SelectionLength == 0)):
                if (column < _columns.Count - 1)
                {
                    e.Handled = true;
                    MoveTo(rowIndex, column + 1);
                }

                return;

            case VirtualKey.Delete when !editing:
                // A real row that is not being edited goes. In an edit and on the empty row Delete deletes a character.
                e.Handled = true;
                RemoveRow(rowIndex, column);
                return;

            case VirtualKey.F2:
                e.Handled = true;
                if (!editing)
                {
                    BeginEdit(cell, selectAll: true);
                }

                return;

            case VirtualKey.Enter when activeEdit:
                e.Handled = true;
                EndEdit(commit: true);
                if (column < _columns.Count - 1 && TryLocate(cell, out _, out _))
                {
                    MoveTo(rowIndex, column + 1);
                }

                return;

            case VirtualKey.Escape when activeEdit:
                // Consumed only when it ends an edit. Otherwise it reaches the dialog and cancels it.
                e.Handled = true;
                EndEdit(commit: false);
                return;
        }

        if (!editing && !ctrlOrAlt && IsPrintable(e.Key))
        {
            // The key that starts the edit is also the first character: the text is selected, so it replaces the old text.
            BeginEdit(cell, selectAll: true);
        }
    }

    private void BeginEdit(TextBox cell, bool selectAll)
    {
        if (ReferenceEquals(_editing, cell))
        {
            return;
        }

        EndEdit(commit: true);
        _editing = cell;
        _editOriginal = cell.Text ?? string.Empty;
        cell.IsReadOnly = false;
        ApplyVisual(cell, focused: true);
        if (selectAll)
        {
            cell.SelectAll();
        }
    }

    /// <summary>
    /// Ends the edit in progress. A commit keeps the text; a revert restores the text from before the edit. A real row with
    /// no text left in any cell is removed.
    /// </summary>
    private void EndEdit(bool commit)
    {
        TextBox? cell = _editing;
        if (cell is null)
        {
            return;
        }

        _editing = null;
        if (!commit)
        {
            _suspend = true;
            cell.Text = _editOriginal;
            _suspend = false;
        }

        cell.IsReadOnly = true;
        ApplyVisual(cell, focused: cell.FocusState != FocusState.Unfocused);

        if (TryLocate(cell, out int index, out int column) && !_rows[index].IsNew && _rows[index].Cells.All(c => string.IsNullOrEmpty(c.Text)))
        {
            // The focus follows only when this cell still has it (a click elsewhere must not pull it back).
            RemoveRow(index, column, refocus: cell.FocusState != FocusState.Unfocused);
            return;
        }

        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveTo(int row, int column)
    {
        int before = _rows.Count;
        _lastRemoved = -1;
        EndEdit(commit: true);
        if (_rows.Count < before && _lastRemoved >= 0 && _lastRemoved < row)
        {
            // The edited row was removed (all cells empty), so the rows below it moved up by one.
            row--;
        }

        int r = Math.Clamp(row, 0, _rows.Count - 1);
        int c = Math.Clamp(column, 0, _columns.Count - 1);
        _ = CellAt(r, c).Focus(FocusState.Keyboard);
    }

    private void RemoveRow(int index, int column, bool refocus = true)
    {
        if (index < 0 || index >= _rows.Count - 1)
        {
            return;
        }

        if (_editing is { } editing && _rows[index].Cells.Contains(editing))
        {
            _editing = null;
        }

        _body.Children.Remove(_rows[index].Panel);
        _rows.RemoveAt(index);
        _lastRemoved = index;
        Renumber();
        RowsChanged?.Invoke(this, EventArgs.Empty);

        // The focus goes to the same index: the next row, or the empty row when the last real row was removed.
        if (refocus)
        {
            _ = CellAt(index, column).Focus(FocusState.Keyboard);
        }
    }

    private bool TryLocate(TextBox cell, out int row, out int column)
    {
        for (int r = 0; r < _rows.Count; r++)
        {
            int c = Array.IndexOf(_rows[r].Cells, cell);
            if (c >= 0)
            {
                row = r;
                column = c;
                return true;
            }
        }

        row = -1;
        column = -1;
        return false;
    }

    /// <summary>Gives every cell its id and name after rows were added or removed.</summary>
    private void Renumber()
    {
        for (int r = 0; r < _rows.Count; r++)
        {
            for (int c = 0; c < _columns.Count; c++)
            {
                TextBox cell = _rows[r].Cells[c];
                AutomationProperties.SetAutomationId(cell, $"{_id}.R{r}C{c}");
                AutomationProperties.SetName(cell, _rows[r].IsNew ? $"{_columns[c]}, new row" : $"{_columns[c]}, row {r + 1}");
            }
        }
    }

    /// <summary>A read-only cell with focus is drawn selected, like a list row. Other cells use the window colors.</summary>
    private void ApplyVisual(TextBox cell, bool focused)
    {
        bool selected = focused && cell.IsReadOnly;
        cell.Background = selected ? BwcBrushes.Selection : BwcBrushes.Window;
        cell.Foreground = selected ? BwcBrushes.SelectionText : BwcBrushes.WindowText;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsPrintable(VirtualKey key)
    {
        int code = (int)key;
        return key == VirtualKey.Space ||
            code is >= 0x30 and <= 0x39 ||
            code is >= 0x41 and <= 0x5A ||
            code is >= 0x60 and <= 0x6F ||
            code is >= 0xBA and <= 0xC0 ||
            code is >= 0xDB and <= 0xDF;
    }

    private sealed class Row
    {
        internal Row(Grid panel, TextBox[] cells, bool isNew)
        {
            Panel = panel;
            Cells = cells;
            IsNew = isNew;
        }

        internal Grid Panel { get; }

        internal TextBox[] Cells { get; }

        /// <summary>True for the trailing empty row.</summary>
        internal bool IsNew { get; set; }
    }
}
