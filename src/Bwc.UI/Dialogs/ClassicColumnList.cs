using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The classic column list of the resource viewers: a header row of column titles over a single-select list. Each row
/// is one line of cells with equal column widths. Double-click and Enter raise <see cref="RowActivated"/>.
/// </summary>
public sealed class ClassicColumnList : Grid
{
    private readonly ListView _list;
    private readonly Grid _header;
    private IReadOnlyList<string> _columns = [];

    public ClassicColumnList(ClassicFontSpec font)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _header = new Grid { Background = BwcBrushes.Window, Padding = new Thickness(0) };
        Grid.SetRow(_header, 0);
        Children.Add(_header);

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            IsTabStop = true,
            Background = BwcBrushes.Window,
            Foreground = BwcBrushes.WindowText,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(font.Face),
            FontSize = font.DipSize,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            MinHeight = 0,
        };
        _list.DoubleTapped += OnDoubleTapped;
        _list.KeyDown += OnKeyDown;
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        Grid.SetRow(_list, 1);
        Children.Add(_list);
    }

    /// <summary>The list control, for tests that select rows through UI Automation.</summary>
    public ListView List => _list;

    /// <summary>
    /// Sets the UI Automation id on the list. A Grid has no automation peer, so an id on this control never reaches
    /// UI Automation; the list is the control with the rows and the selection.
    /// </summary>
    internal void SetListAutomationId(string automationId) => AutomationProperties.SetAutomationId(_list, automationId);

    /// <summary>Raised when the selected row changes, by keyboard, mouse, or <see cref="SelectRow"/>.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised on double-click or Enter for the selected row.</summary>
    public event EventHandler? RowActivated;

    /// <summary>Number of rows.</summary>
    public int RowCount => _list.Items.Count;

    /// <summary>The selected row, or -1.</summary>
    public int SelectedIndex => _list.SelectedIndex;

    /// <summary>Sets the column titles. Each title sits over one equal-width column.</summary>
    public void SetColumns(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns = columns;
        _header.ColumnDefinitions.Clear();
        _header.Children.Clear();
        for (int i = 0; i < columns.Count; i++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var title = new TextBlock { Text = columns[i], Margin = new Thickness(2, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, i);
            _header.Children.Add(title);
        }
    }

    /// <summary>Replaces the rows. Each row has one text per column.</summary>
    public void SetRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _list.Items.Clear();
        foreach (IReadOnlyList<string> row in rows)
        {
            var line = new Grid();
            for (int i = 0; i < _columns.Count; i++)
            {
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var cell = new TextBlock
                {
                    Text = i < row.Count ? row[i] : string.Empty,
                    Margin = new Thickness(2, 0, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(cell, i);
                line.Children.Add(cell);
            }

            // The item name carries the cells, so UI Automation reads a row without its child text.
            var item = new ListViewItem { Content = line };
            AutomationProperties.SetName(item, string.Join("|", row));
            _list.Items.Add(item);
        }
    }

    /// <summary>Returns the cell texts of one row, for tests and for the values of the row.</summary>
    public IReadOnlyList<string> CellsOf(int row)
    {
        if (row < 0 || row >= _list.Items.Count || _list.Items[row] is not ListViewItem { Content: Grid line })
        {
            return [];
        }

        return line.Children.OfType<TextBlock>().OrderBy(Grid.GetColumn).Select(t => t.Text).ToArray();
    }

    /// <summary>Selects one row (or none when index is -1).</summary>
    public void SelectRow(int index) => _list.SelectedIndex = index;

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_list.SelectedIndex >= 0)
        {
            RowActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _list.SelectedIndex >= 0)
        {
            e.Handled = true;
            RowActivated?.Invoke(this, EventArgs.Empty);
        }
    }
}
