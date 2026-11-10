using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Bwc.UI.Tree;

/// <summary>
/// One realized tree row. It is the ItemsRepeater element itself, not a wrapper, so the height is
/// exactly the dense tree row height and the selection background covers the whole row width.
/// </summary>
public sealed class TreeRowElement : Panel
{
    private const double ChevronSize = 16;
    private const double ChevronGap = 2;
    private const double TextGap = 3;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly Grid _chevronHost;
    private readonly FontIcon _chevron;
    private readonly TextBlock _label;
    private bool _selected;
    private double _indentWidth;
    private double _rowHeight = -1;
    private double _fontSize = -1;

    public TreeRowElement()
    {
        Background = DenseTheme.Transparent;

        _chevron = new FontIcon
        {
            FontFamily = IconFont,
            FontSize = 8,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _chevronHost = new Grid
        {
            Width = ChevronSize,
            Height = ChevronSize,
            Background = DenseTheme.Transparent,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _chevronHost.Children.Add(_chevron);
        _chevronHost.Tapped += OnChevronTapped;

        _label = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = DenseMetrics.FontFamily,
            FontSize = DenseMetrics.FontSize,
            Foreground = DenseTheme.Text,
        };

        Children.Add(_chevronHost);
        Children.Add(_label);
        Tapped += OnRowTapped;
        Height = DenseMetrics.TreeRowHeight;
    }

    internal DenseTree? Owner { get; private set; }

    internal TreeNode? Node { get; private set; }

    internal void Attach(DenseTree owner, TreeNode node)
    {
        Owner = owner;
        Node = node;

        if (!string.Equals(_label.Text, node.Name, StringComparison.Ordinal))
        {
            _label.Text = node.Name;
        }

        double indent = Math.Max(0, node.Depth) * DenseMetrics.TreeIndent;
        bool layoutChanged = indent != _indentWidth || _rowHeight != DenseMetrics.TreeRowHeight;
        _indentWidth = indent;
        ApplyMetricsCore();
        ApplyChevron();
        ApplyTheme();

        if (layoutChanged)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    internal void Detach()
    {
        Owner = null;
        Node = null;
    }

    internal void ApplyMetrics()
    {
        if (Node is null)
        {
            return;
        }

        double indent = Math.Max(0, Node.Depth) * DenseMetrics.TreeIndent;
        bool layoutChanged = indent != _indentWidth || _rowHeight != DenseMetrics.TreeRowHeight;
        _indentWidth = indent;
        ApplyMetricsCore();
        ApplyChevron();

        if (layoutChanged)
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    internal void ApplyTheme()
    {
        Background = _selected ? DenseTheme.SelectionBackground : DenseTheme.Transparent;
        Brush foreground = _selected ? DenseTheme.SelectionForeground : DenseTheme.Text;
        _label.Foreground = foreground;
        _chevron.Foreground = foreground;
    }

    internal void SetSelected(bool selected)
    {
        if (_selected == selected)
        {
            return;
        }

        _selected = selected;
        ApplyTheme();
    }

    internal void RefreshExpansion()
    {
        ApplyChevron();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = DenseMetrics.TreeRowHeight;
        double textX = _indentWidth + ChevronSize + TextGap;
        _chevronHost.Measure(new Size(ChevronSize, ChevronSize));

        double availableText = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - textX);
        _label.Measure(new Size(availableText, height));

        double width = double.IsInfinity(availableSize.Width)
            ? textX + _label.DesiredSize.Width
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double height = Math.Min(finalSize.Height, DenseMetrics.TreeRowHeight);
        _chevronHost.Arrange(new Rect(_indentWidth, (height - ChevronSize) / 2, ChevronSize, ChevronSize));

        double textX = _indentWidth + ChevronSize + TextGap;
        double textWidth = Math.Max(0, finalSize.Width - textX - 2);
        double labelHeight = Math.Min(height, _label.DesiredSize.Height);
        _label.Arrange(new Rect(textX, (height - labelHeight) / 2, textWidth, labelHeight));
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreeRowAutomationPeer(this);

    private void ApplyMetricsCore()
    {
        double fontSize = DenseMetrics.FontSize;
        double rowHeight = DenseMetrics.TreeRowHeight;
        bool fontChanged = fontSize != _fontSize;
        bool heightChanged = rowHeight != _rowHeight;

        _fontSize = fontSize;
        _rowHeight = rowHeight;
        if (fontChanged)
        {
            _label.FontSize = fontSize;
        }

        if (heightChanged)
        {
            Height = rowHeight;
        }
    }

    private void ApplyChevron()
    {
        bool hasChildren = Node?.HasChildren == true;
        _chevronHost.Visibility = hasChildren ? Visibility.Visible : Visibility.Collapsed;
        if (!hasChildren)
        {
            return;
        }

        _chevron.Glyph = Node?.IsExpanded == true ? "\uE70D" : "\uE76C";
    }

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        Owner?.RowTapped(this);
        e.Handled = true;
    }

    private void OnChevronTapped(object sender, TappedRoutedEventArgs e)
    {
        Owner?.ChevronTapped(this);
        e.Handled = true;
    }
}

/// <summary>
/// UIA peer for one tree row. The visible index is read from the owning tree on every call so a
/// recycled row never keeps the index it had before an insert, collapse, or scroll.
/// </summary>
internal sealed class TreeRowAutomationPeer :
    FrameworkElementAutomationPeer,
    IExpandCollapseProvider,
    ISelectionItemProvider,
    IScrollItemProvider
{
    private readonly TreeRowElement _row;

    public TreeRowAutomationPeer(TreeRowElement row)
        : base(row)
    {
        _row = row;
    }

    public ExpandCollapseState ExpandCollapseState
    {
        get
        {
            var node = _row.Node;
            if (node is null || !node.HasChildren)
            {
                return ExpandCollapseState.LeafNode;
            }

            return node.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        }
    }

    public bool IsSelected =>
        _row.Owner?.SelectedNode is { } selected && ReferenceEquals(selected, _row.Node);

    public IRawElementProviderSimple SelectionContainer => null!;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    protected override string GetClassNameCore() => "DenseTreeRow";

    protected override string GetNameCore() => _row.Node?.Name ?? string.Empty;

    protected override string GetAutomationIdCore()
    {
        var index = _row.Owner?.IndexOfVisibleNode(_row.Node) ?? -1;
        return index >= 0 ? $"TreeRow_{index}" : "TreeRow";
    }

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        return patternInterface switch
        {
            PatternInterface.ExpandCollapse => this,
            PatternInterface.SelectionItem => this,
            PatternInterface.ScrollItem => this,
            _ => base.GetPatternCore(patternInterface),
        };
    }

    public void Expand()
    {
        if (_row.Node is { HasChildren: true } node)
        {
            _row.Owner?.ExpandFromAutomation(node);
        }
    }

    public void Collapse()
    {
        if (_row.Node is { IsExpanded: true } node)
        {
            _row.Owner?.CollapseNode(node);
        }
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
        // A dense tree has exactly one selected row. Removing that selection would leave no anchor
        // for the keyboard, so UIA removal is a no-op.
    }

    public void Select()
    {
        if (_row.Node is { } node)
        {
            _row.Owner?.SelectNode(node);
            _row.Owner?.FocusSelected();
        }
    }

    public void ScrollIntoView()
    {
        if (_row.Node is { } node)
        {
            _row.Owner?.RevealNode(node);
        }
    }
}
