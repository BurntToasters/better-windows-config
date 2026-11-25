using Bwc.UI.Icons;
using Bwc.UI.Theme;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.Console.List;

/// <summary>
/// One realized item of the console list, the element of the items repeater. Its size and arrangement follow the view
/// mode of its owner: a full row with a cell per visible column in Detail, an icon and a label in Small Icons and
/// List, a large icon over a two line label in Large Icons. Selection and focus are drawn here from the state the
/// owner pushes; the element holds no model.
/// </summary>
public sealed class ConsoleListItemElement : Panel
{
    private readonly Rectangle _back;
    private readonly Rectangle _focusRect;
    private readonly Image _icon;
    private TextBlock[] _cells = [];
    private ConsoleListViewMode _cellMode = ConsoleListViewMode.Detail;
    private int _index = -1;
    private bool _selected;
    private bool _focused;
    private double _fontSize = -1;
    private Rect _labelBox;

    public ConsoleListItemElement()
    {
        // A transparent background makes the whole element a hit target, selected or not.
        Background = DenseTheme.Transparent;
        _back = new Rectangle { IsHitTestVisible = false, Fill = DenseTheme.Transparent };
        _focusRect = BwcFocusRect.Create(DenseTheme.Transparent);
        _icon = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
        RightTapped += OnRightTapped;
        Loaded += (_, _) => ApplyIcon();
    }

    internal ConsoleList? Owner { get; private set; }

    internal ConsoleListRow? Row { get; private set; }

    internal int Index => _index;

    internal void Attach(ConsoleList owner, int index, ConsoleListRow row, bool selected, bool focused)
    {
        Owner = owner;
        _index = index;
        Row = row;
        _selected = selected;
        _focused = focused;
        int cellCount = Math.Max(1, owner.VisibleColumns.Count);
        if (_cells.Length != cellCount || _cellMode != owner.ViewMode)
        {
            BuildCells(cellCount, owner.ViewMode);
        }

        ApplyMetricsCore();
        ApplyTexts();
        ApplyIcon();
        ApplyTheme();
        InvalidateMeasure();
    }

    internal void Detach()
    {
        Owner = null;
        Row = null;
    }

    internal void ApplyMetrics()
    {
        ApplyMetricsCore();
        InvalidateMeasure();
        InvalidateArrange();
    }

    /// <summary>Re-reads the texts after a column change.</summary>
    internal void Refresh()
    {
        if (Owner is null || Row is null)
        {
            return;
        }

        int cellCount = Math.Max(1, Owner.VisibleColumns.Count);
        if (_cells.Length != cellCount)
        {
            BuildCells(cellCount, Owner.ViewMode);
            ApplyMetricsCore();
            ApplyTheme();
        }

        ApplyTexts();
        InvalidateMeasure();
    }

    internal void ApplyTheme()
    {
        _back.Fill = _selected ? DenseTheme.ListSelectionBackground : DenseTheme.Transparent;
        Brush foreground = _selected ? DenseTheme.ListSelectionForeground : DenseTheme.Text;
        foreach (TextBlock cell in _cells)
        {
            cell.Foreground = foreground;
        }

        _focusRect.Stroke = _focused
            ? (_selected ? DenseTheme.ListSelectionForeground : DenseTheme.Text)
            : DenseTheme.Transparent;
    }

    internal void SetState(bool selected, bool focused)
    {
        if (_selected == selected && _focused == focused)
        {
            return;
        }

        _selected = selected;
        _focused = focused;
        ApplyTheme();
    }

    /// <summary>Fetches the icon again, for example after the window moved to a monitor of another scale.</summary>
    internal void ApplyIcon()
    {
        if (Row is null || Owner is null)
        {
            return;
        }

        bool large = Owner.ViewMode == ConsoleListViewMode.LargeIcons;
        IconReference? reference = large ? Row.LargeIcon ?? Row.SmallIcon : Row.SmallIcon;
        if (reference is null)
        {
            _icon.Source = null;
            return;
        }

        try
        {
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            _icon.Source = IconProvider.Get(reference.Value, large ? scale * 2 : scale);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _icon.Source = null;
            UiDiagnostics.Warn?.Invoke($"The console list could not load an icon: {ex.Message}");
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Owner is null)
        {
            return new Size(0, ConsoleListMetrics.RowHeight);
        }

        switch (Owner.ViewMode)
        {
            case ConsoleListViewMode.Detail:
                return MeasureDetail(availableSize);
            case ConsoleListViewMode.LargeIcons:
                return MeasureLarge();
            default:
                return MeasureLabel();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Owner is null)
        {
            return finalSize;
        }

        switch (Owner.ViewMode)
        {
            case ConsoleListViewMode.Detail:
                ArrangeDetail(finalSize);
                break;
            case ConsoleListViewMode.LargeIcons:
                ArrangeLarge(finalSize);
                break;
            default:
                ArrangeLabel(finalSize);
                break;
        }

        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleListItemAutomationPeer(this);

    // ---- Detail -----------------------------------------------------------------------------------

    private Size MeasureDetail(Size availableSize)
    {
        double height = ConsoleListMetrics.RowHeight;
        IReadOnlyList<ConsoleListColumn> columns = Owner!.VisibleColumns;
        double total = Owner.ColumnsTotalWidth;
        _icon.Measure(new Size(ConsoleListMetrics.SmallIconSize, ConsoleListMetrics.SmallIconSize));
        for (int i = 0; i < _cells.Length; i++)
        {
            double width = i < columns.Count ? columns[i].Width : 0;
            double inset = i == 0 ? ConsoleListMetrics.LabelLeft : ConsoleListMetrics.CellPad;
            _cells[i].Visibility = i < columns.Count ? Visibility.Visible : Visibility.Collapsed;
            _cells[i].Measure(new Size(Math.Max(0, width - inset - ConsoleListMetrics.CellPad), height));
        }

        double rowWidth = double.IsInfinity(availableSize.Width) ? total : Math.Max(total, availableSize.Width);
        _back.Measure(new Size(rowWidth, height));
        BwcFocusRect.Measure(_focusRect, this, new Size(rowWidth, height));
        return new Size(rowWidth, height);
    }

    private void ArrangeDetail(Size finalSize)
    {
        double height = Math.Min(finalSize.Height, ConsoleListMetrics.RowHeight);
        IReadOnlyList<ConsoleListColumn> columns = Owner!.VisibleColumns;
        double total = Owner.ColumnsTotalWidth;
        double extra = Math.Max(0, finalSize.Width - total);
        double iconSize = ConsoleListMetrics.SmallIconSize;
        _icon.Arrange(new Rect(ConsoleListMetrics.IconLeft, (height - iconSize) / 2, iconSize, iconSize));
        _back.Arrange(new Rect(0, 0, finalSize.Width, height));

        double left = 0;
        for (int i = 0; i < columns.Count && i < _cells.Length; i++)
        {
            double width = columns[i].Width + (i == columns.Count - 1 ? extra : 0);
            double inset = i == 0 ? ConsoleListMetrics.LabelLeft : ConsoleListMetrics.CellPad;
            double textHeight = Math.Min(height, _cells[i].DesiredSize.Height);
            _cells[i].Arrange(new Rect(left + inset, (height - textHeight) / 2, Math.Max(0, width - inset - ConsoleListMetrics.CellPad), textHeight));
            left += columns[i].Width;
        }

        ArrangeFocus(new Rect(0, 0, finalSize.Width, height));
    }

    // ---- Small Icons and List ---------------------------------------------------------------------

    private Size MeasureLabel()
    {
        double height = ConsoleListMetrics.RowHeight;
        double width = Owner!.LabelItemWidth;
        _icon.Measure(new Size(ConsoleListMetrics.SmallIconSize, ConsoleListMetrics.SmallIconSize));
        _cells[0].Visibility = Visibility.Visible;
        for (int i = 1; i < _cells.Length; i++)
        {
            _cells[i].Visibility = Visibility.Collapsed;
        }

        _cells[0].Measure(new Size(Math.Max(0, width - ConsoleListMetrics.LabelLeft - 2), height));
        double textWidth = Math.Min(_cells[0].DesiredSize.Width + 4, width - ConsoleListMetrics.LabelLeft + 2);
        _labelBox = new Rect(ConsoleListMetrics.LabelLeft - 2, 0, Math.Max(0, textWidth), height);
        _back.Measure(new Size(_labelBox.Width, height));
        BwcFocusRect.Measure(_focusRect, this, new Size(_labelBox.Width, height));
        return new Size(width, height);
    }

    private void ArrangeLabel(Size finalSize)
    {
        double height = Math.Min(finalSize.Height, ConsoleListMetrics.RowHeight);
        double iconSize = ConsoleListMetrics.SmallIconSize;
        _icon.Arrange(new Rect(ConsoleListMetrics.IconLeft, (height - iconSize) / 2, iconSize, iconSize));
        _back.Arrange(_labelBox);
        double textHeight = Math.Min(height, _cells[0].DesiredSize.Height);
        _cells[0].Arrange(new Rect(ConsoleListMetrics.LabelLeft, (height - textHeight) / 2, Math.Max(0, finalSize.Width - ConsoleListMetrics.LabelLeft - 2), textHeight));
        ArrangeFocus(_labelBox);
    }

    // ---- Large Icons ------------------------------------------------------------------------------

    private Size MeasureLarge()
    {
        const double width = ConsoleListMetrics.LargeCellWidth;
        const double height = ConsoleListMetrics.LargeCellHeight;
        _icon.Measure(new Size(ConsoleListMetrics.LargeIconSize, ConsoleListMetrics.LargeIconSize));
        _cells[0].Visibility = Visibility.Visible;
        for (int i = 1; i < _cells.Length; i++)
        {
            _cells[i].Visibility = Visibility.Collapsed;
        }

        double textTop = 4 + ConsoleListMetrics.LargeIconSize + 3;
        _cells[0].Measure(new Size(width - 4, height - textTop));
        double textWidth = Math.Min(_cells[0].DesiredSize.Width + 4, width);
        double textHeight = Math.Min(_cells[0].DesiredSize.Height + 2, height - textTop);
        _labelBox = new Rect((width - textWidth) / 2, textTop - 1, textWidth, textHeight);
        _back.Measure(new Size(_labelBox.Width, _labelBox.Height));
        BwcFocusRect.Measure(_focusRect, this, new Size(_labelBox.Width, _labelBox.Height));
        return new Size(width, height);
    }

    private void ArrangeLarge(Size finalSize)
    {
        double iconSize = ConsoleListMetrics.LargeIconSize;
        _icon.Arrange(new Rect((finalSize.Width - iconSize) / 2, 4, iconSize, iconSize));
        _back.Arrange(_labelBox);
        double textTop = 4 + iconSize + 3;
        double textWidth = Math.Min(_cells[0].DesiredSize.Width, finalSize.Width - 4);
        _cells[0].Arrange(new Rect((finalSize.Width - textWidth) / 2, textTop, textWidth, Math.Min(_cells[0].DesiredSize.Height, finalSize.Height - textTop)));
        ArrangeFocus(_labelBox);
    }

    private void ArrangeFocus(Rect box)
    {
        double pixel = BwcFocusRect.DevicePixel(this);
        _focusRect.Arrange(new Rect(box.X + (pixel * 0.5), box.Y + (pixel * 0.5), Math.Max(0, box.Width - pixel), Math.Max(0, box.Height - pixel)));
    }

    // ---- build and state --------------------------------------------------------------------------

    private void BuildCells(int count, ConsoleListViewMode mode)
    {
        Children.Clear();
        _cellMode = mode;
        _cells = new TextBlock[count];
        Children.Add(_back);
        for (int i = 0; i < count; i++)
        {
            var cell = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                FontFamily = DenseMetrics.FontFamily,
                FontSize = DenseMetrics.FontSize,
                Foreground = DenseTheme.Text,
            };
            _cells[i] = cell;
            Children.Add(cell);
        }

        Children.Add(_icon);
        Children.Add(_focusRect);
        _fontSize = -1;
    }

    private void ApplyTexts()
    {
        if (Owner is null || Row is null)
        {
            return;
        }

        IReadOnlyList<ConsoleListColumn> columns = Owner.VisibleColumns;
        bool detail = Owner.ViewMode == ConsoleListViewMode.Detail;
        for (int i = 0; i < _cells.Length; i++)
        {
            string text;
            ConsoleListAlignment alignment = ConsoleListAlignment.Left;
            if (detail && i < columns.Count)
            {
                text = Row.CellText(columns[i].Index);
                alignment = columns[i].Alignment;
            }
            else
            {
                text = i == 0 ? Row.Text : string.Empty;
            }

            if (!string.Equals(_cells[i].Text, text, StringComparison.Ordinal))
            {
                _cells[i].Text = text;
            }

            _cells[i].TextAlignment = alignment switch
            {
                ConsoleListAlignment.Right => TextAlignment.Right,
                ConsoleListAlignment.Center => TextAlignment.Center,
                _ => TextAlignment.Left,
            };
        }

        if (Owner.ViewMode == ConsoleListViewMode.LargeIcons)
        {
            _cells[0].TextWrapping = TextWrapping.Wrap;
            _cells[0].MaxLines = 2;
            _cells[0].TextAlignment = TextAlignment.Center;
            _icon.Width = ConsoleListMetrics.LargeIconSize;
            _icon.Height = ConsoleListMetrics.LargeIconSize;
        }
        else
        {
            _cells[0].TextWrapping = TextWrapping.NoWrap;
            _cells[0].MaxLines = 0;
            _icon.Width = ConsoleListMetrics.SmallIconSize;
            _icon.Height = ConsoleListMetrics.SmallIconSize;
        }
    }

    private void ApplyMetricsCore()
    {
        double fontSize = DenseMetrics.FontSize;
        if (fontSize == _fontSize)
        {
            return;
        }

        _fontSize = fontSize;
        foreach (TextBlock cell in _cells)
        {
            cell.FontSize = fontSize;
            cell.FontFamily = DenseMetrics.FontFamily;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => Owner?.ItemPressed(this, e);

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Owner?.ItemDoubleTapped(this, e);

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        Owner?.ItemRightTapped(this, e);
        e.Handled = true;
    }
}

/// <summary>
/// UIA peer of one item: a ListItem named by the label (or by the host's formatter), with the Invoke, SelectionItem,
/// ScrollItem and GridItem patterns, as the classic rows have. The index is read from the element, which the repeater
/// updates on recycling, so a sort or a scroll cannot leave a stale number.
/// </summary>
internal sealed class ConsoleListItemAutomationPeer :
    FrameworkElementAutomationPeer,
    IInvokeProvider,
    ISelectionItemProvider,
    IScrollItemProvider,
    IGridItemProvider
{
    private readonly ConsoleListItemElement _item;

    public ConsoleListItemAutomationPeer(ConsoleListItemElement item)
        : base(item)
    {
        _item = item;
    }

    public bool IsSelected => _item.Owner?.IsRowSelected(_item.Index) == true;

    public IRawElementProviderSimple SelectionContainer => OwnerProvider();

    public int Column => 0;

    public int ColumnSpan => _item.Owner?.VisibleColumns.Count ?? 0;

    public int Row => _item.Index;

    public int RowSpan => 1;

    public IRawElementProviderSimple ContainingGrid => OwnerProvider();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => "ConsoleListItem";

    protected override string GetNameCore() => _item.Owner is { } owner && _item.Row is { } row ? owner.ItemAutomationName(row) : string.Empty;

    protected override string GetAutomationIdCore() => _item.Index >= 0 ? $"ConsoleListRow_{_item.Index}" : "ConsoleListRow";

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Invoke => this,
        PatternInterface.SelectionItem => this,
        PatternInterface.ScrollItem => this,
        PatternInterface.GridItem => this,
        _ => base.GetPatternCore(patternInterface),
    };

    public void Invoke() => _item.Owner?.ActivateIndex(_item.Index);

    public void Select() => _item.Owner?.SelectIndex(_item.Index, focus: true);

    public void AddToSelection() => _item.Owner?.AddIndexToSelection(_item.Index);

    public void RemoveFromSelection() => _item.Owner?.RemoveIndexFromSelection(_item.Index);

    public void ScrollIntoView() => _item.Owner?.RevealIndex(_item.Index);

    private IRawElementProviderSimple OwnerProvider()
    {
        ConsoleList? owner = _item.Owner;
        if (owner is null)
        {
            return ProviderFromPeer(this);
        }

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(owner)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(owner);
        return peer is null ? ProviderFromPeer(this) : ProviderFromPeer(peer);
    }
}
