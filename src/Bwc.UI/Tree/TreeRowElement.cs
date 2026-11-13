using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Bwc.UI.Tree;

/// <summary>
/// One realized tree row. It is the ItemsRepeater element itself, not a wrapper, so the height is
/// exactly the dense tree row height and the selection background covers the whole row width.
/// </summary>
public sealed class TreeRowElement : Panel
{
    // Offsets from the start of the row's indent, in DIPs. Measured on classic regedit at 200 percent
    // (docs/parity/oracle/regedit-row-metrics.json): the expander is centered at indent + 10.5, the
    // icon starts at indent + 18, and the first text ink is at indent + 37.5 (the glyph bearing adds
    // about half a DIP to the layout origin, so the layout origin is indent + 37).
    private const double ChevronSize = 16;
    private const double ChevronInset = 2.5;
    private const double IconInset = 18;
    private const double TextInset = 37;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly Grid _chevronHost;
    private readonly FontIcon _chevron;
    private readonly Image _icon;
    private readonly TextBlock _label;
    private readonly Rectangle _focusRect;
    private readonly Rectangle _selectionBorder;
    private bool _selected;
    private double _indentWidth;
    private double _rowHeight = -1;
    private double _fontSize = -1;
    private double _overlayPixel = -1;
    private double _overlayRowHeight = -1;
    private ExpandCollapseState _expandState = ExpandCollapseState.LeafNode;

    // The peer UI Automation holds for this row. Expand and collapse notifications go to this instance
    // directly, instead of a FromElement lookup, which misses while the framework has not yet handed the
    // peer back to the element (the first ExpandCollapse call on a row was dropped that way).
    private TreeRowAutomationPeer? _peer;

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
        _chevronHost.DoubleTapped += OnChevronDoubleTapped;

        _icon = new Image
        {
            Width = Icons.IconProvider.IconDip,
            Height = Icons.IconProvider.IconDip,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

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

        // The focus rectangle is visible only on the selected row, and its brush is transparent
        // unless the tree has focus. It is the last child so it draws over the selection fill.
        _focusRect = BwcFocusRect.Create(DenseTheme.TreeFocusRect);
        _selectionBorder = BwcFocusRect.CreateBorder(DenseTheme.TreeSelectionBorder);

        Children.Add(_chevronHost);
        Children.Add(_icon);
        Children.Add(_label);
        Children.Add(_selectionBorder);
        Children.Add(_focusRect);
        Tapped += OnRowTapped;
        DoubleTapped += OnRowDoubleTapped;
        RightTapped += OnRowRightTapped;
        PointerPressed += OnRowPointerPressed;
        Loaded += (_, _) =>
        {
            ApplyIcon();
            ApplyOverlayMetrics(DenseMetrics.TreeRowHeight);
        };

        // The overlays stretch across the row and take their height from the metric. Their margin is the
        // device pixel inset, so no overlay is resized inside a layout pass.
        _focusRect.HorizontalAlignment = HorizontalAlignment.Stretch;
        _focusRect.VerticalAlignment = VerticalAlignment.Top;
        _selectionBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        _selectionBorder.VerticalAlignment = VerticalAlignment.Top;
        ApplyOverlayMetrics(DenseMetrics.TreeRowHeight);
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
        if (MetricsStale())
        {
            // Attach runs inside the ItemsRepeater layout pass, so a metric change is queued instead of
            // written here. The queued ApplyMetrics runs outside layout.
            _ = DispatcherQueue.TryEnqueue(ApplyMetrics);
        }

        ApplyChevron();
        ApplyIcon();
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
        Background = _selected ? DenseTheme.TreeSelectionBackground : DenseTheme.Transparent;
        Brush foreground = _selected ? DenseTheme.TreeSelectionForeground : DenseTheme.Text;
        _label.Foreground = foreground;
        _chevron.Foreground = foreground;
        _focusRect.Stroke = _selected ? DenseTheme.TreeFocusRect : DenseTheme.Transparent;
        _selectionBorder.Stroke = _selected ? DenseTheme.TreeSelectionBorder : DenseTheme.Transparent;
    }

    internal void SetSelected(bool selected)
    {
        if (_selected == selected)
        {
            return;
        }

        _selected = selected;
        ApplyTheme();
        NotifyPeer(peer =>
        {
            peer.RaiseSelectionChanged(!selected, selected);
            if (selected)
            {
                peer.RaiseElementSelected();
            }
        });
    }

    /// <summary>
    /// The x of the label start for a node, in content coordinates (the indent plus the text inset).
    /// The tree scrolls horizontally only when this start is off screen.
    /// </summary>
    internal static double LabelStartFor(TreeNode node) =>
        (Math.Max(0, node.Depth) * DenseMetrics.TreeIndent) + TextInset;

    /// <summary>The UIA expand state of a node. A node without children is a leaf.</summary>
    internal static ExpandCollapseState StateOf(TreeNode? node)
    {
        if (node is null || !node.HasChildren)
        {
            return ExpandCollapseState.LeafNode;
        }

        return node.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    }

    internal void RefreshExpansion()
    {
        ApplyChevron();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = DenseMetrics.TreeRowHeight;
        double textX = _indentWidth + TextInset;
        _chevronHost.Measure(new Size(ChevronSize, ChevronSize));
        _icon.Measure(new Size(Icons.IconProvider.IconDip, Icons.IconProvider.IconDip));

        double availableText = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - textX);
        _label.Measure(new Size(availableText, height));

        // A row measured without a bound (horizontal scrolling) measures to its label. The overlays
        // are measured to the same width, so the row, its label and its overlays agree.
        double width = double.IsInfinity(availableSize.Width)
            ? textX + _label.DesiredSize.Width
            : availableSize.Width;
        _selectionBorder.Measure(new Size(width, height));
        _focusRect.Measure(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double height = Math.Min(finalSize.Height, DenseMetrics.TreeRowHeight);
        _chevronHost.Arrange(new Rect(_indentWidth + ChevronInset, (height - ChevronSize) / 2, ChevronSize, ChevronSize));

        double iconSize = Icons.IconProvider.IconDip;
        _icon.Arrange(new Rect(_indentWidth + IconInset, (height - iconSize) / 2, iconSize, iconSize));

        double textX = _indentWidth + TextInset;
        double textWidth = Math.Max(0, finalSize.Width - textX - 2);
        double labelHeight = Math.Min(height, _label.DesiredSize.Height);
        _label.Arrange(new Rect(textX, (height - labelHeight) / 2, textWidth, labelHeight));

        // The overlays were sized by their margin and stretch, so arrange only places them in the row.
        _selectionBorder.Arrange(new Rect(0, 0, finalSize.Width, height));
        _focusRect.Arrange(new Rect(0, 0, finalSize.Width, height));
        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => _peer ??= new TreeRowAutomationPeer(this);

    private bool MetricsStale() =>
        DenseMetrics.FontSize != _fontSize || DenseMetrics.TreeRowHeight != _rowHeight;

    /// <summary>
    /// Applies the font and the overlay geometry. Only call this outside a layout pass. The row's height
    /// is not a property: <see cref="MeasureOverride"/> returns the metric height.
    /// </summary>
    private void ApplyMetricsCore()
    {
        double fontSize = DenseMetrics.FontSize;
        double rowHeight = DenseMetrics.TreeRowHeight;
        if (fontSize != _fontSize)
        {
            _label.FontSize = fontSize;
        }

        _fontSize = fontSize;
        _rowHeight = rowHeight;
        ApplyOverlayMetrics(rowHeight);
    }

    /// <summary>
    /// The overlay stroke is one device pixel, inset by half a pixel from the row edge, the same inset
    /// <see cref="BwcFocusRect"/> uses. Its width comes from the stretch, so only the height is set here.
    /// </summary>
    private void ApplyOverlayMetrics(double rowHeight)
    {
        double pixel = BwcFocusRect.DevicePixel(this);
        if (pixel == _overlayPixel && rowHeight == _overlayRowHeight)
        {
            return;
        }

        _overlayPixel = pixel;
        _overlayRowHeight = rowHeight;
        double inset = pixel * 0.5;
        var margin = new Thickness(inset);
        double overlayHeight = Math.Max(0, rowHeight - (inset * 2));
        foreach (Rectangle overlay in new[] { _selectionBorder, _focusRect })
        {
            overlay.StrokeThickness = pixel;
            overlay.Margin = margin;
            overlay.Height = overlayHeight;
        }
    }

    /// <summary>
    /// The root is the computer icon. Every key uses the closed folder, which classic regedit shows
    /// for the selected and expanded keys too, so the icon never changes with expansion.
    /// </summary>
    private void ApplyIcon()
    {
        if (Node is null)
        {
            return;
        }

        var kind = Node.Parent is null ? Icons.IconKind.Computer : Icons.IconKind.ClosedFolder;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        _icon.Source = Icons.IconProvider.Get(kind, scale);
    }

    private void ApplyChevron()
    {
        bool hasChildren = Node?.HasChildren == true;
        _chevronHost.Visibility = hasChildren ? Visibility.Visible : Visibility.Collapsed;
        if (hasChildren)
        {
            _chevron.Glyph = Node?.IsExpanded == true ? "\uE70D" : "\uE76C";
        }

        var state = StateOf(Node);
        if (state != _expandState)
        {
            var before = _expandState;
            _expandState = state;
            NotifyPeer(peer => peer.RaiseExpandStateChanged(before, state));
        }
    }

    private void NotifyPeer(Action<TreeRowAutomationPeer> action)
    {
        if (_peer is { } peer)
        {
            action(peer);
        }
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

    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        Owner?.RowDoubleTapped(this);
        e.Handled = true;
    }

    private void OnChevronTapped(object sender, TappedRoutedEventArgs e)
    {
        Owner?.ChevronTapped(this);
        e.Handled = true;
    }

    /// <summary>
    /// The expander toggles on each click through its own Tapped handler. A double-tap on the expander
    /// must not reach the row, or the row would toggle a second time.
    /// </summary>
    private static void OnChevronDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    /// <summary>
    /// A right button press selects the row and takes the focus before the menu opens. The flyout is shown on
    /// the release (RightTapped); a focus change after it opens dismisses it, so the focus must already be here.
    /// </summary>
    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        Owner?.RowRightPressed(this);
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || IsChevronSource(e.OriginalSource))
        {
            return;
        }

        Owner?.RowRightTapped(this, e);
        e.Handled = true;
    }

    private bool IsChevronSource(object? source)
    {
        for (var current = source as DependencyObject;
            current is not null;
            current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _chevronHost))
            {
                return true;
            }
        }

        return false;
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

    public ExpandCollapseState ExpandCollapseState => TreeRowElement.StateOf(_row.Node);

    /// <summary>Raised by the row when its expand state changes, so UIA clients re-read the state.</summary>
    internal void RaiseExpandStateChanged(ExpandCollapseState before, ExpandCollapseState after) =>
        RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, before, after);

    /// <summary>Raised by the row when its selection changes.</summary>
    internal void RaiseSelectionChanged(bool before, bool after) =>
        RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, before, after);

    /// <summary>Raised by the row when it becomes the selected item of its tree.</summary>
    internal void RaiseElementSelected() =>
        RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);

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
            // The owner sets IsExpanded and raises the change through the row, which notifies this peer.
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
