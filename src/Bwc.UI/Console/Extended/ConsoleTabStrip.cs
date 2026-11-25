using Bwc.UI.Console.Shell;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Bwc.UI.Console.Extended;

/// <summary>
/// The Extended and Standard tabs at the bottom of the view. Two small bottom tabs, not focusable (as in classic): a click,
/// UI Automation Select or Invoke, and the view's Ctrl+Tab change the selection. UI Automation sees a Tab control with two
/// TabItems named by their labels.
/// </summary>
public sealed class ConsoleTabStrip : UserControl
{
    private readonly Border _frame = new();
    private readonly StackPanel _row = new();
    private readonly ConsoleTabItem _extended;
    private readonly ConsoleTabItem _standard;
    private ConsoleViewTab _selected = ConsoleViewTab.Extended;
    private bool _subscribed;

    public ConsoleTabStrip()
    {
        AutomationProperties.SetAutomationId(this, "ConsoleExtendedTabs");
        AutomationProperties.SetName(this, ConsoleStrings.Get("ConsoleExtendedTabsName"));
        IsTabStop = false;

        _extended = new ConsoleTabItem(this, ConsoleViewTab.Extended, ConsoleStrings.Get("ConsoleExtendedTabExtended"), ConsoleExtendedMetrics.ExtendedTabWidth);
        _standard = new ConsoleTabItem(this, ConsoleViewTab.Standard, ConsoleStrings.Get("ConsoleExtendedTabStandard"), ConsoleExtendedMetrics.StandardTabWidth);
        _row.Orientation = Orientation.Horizontal;
        _row.HorizontalAlignment = HorizontalAlignment.Left;
        _row.Children.Add(_extended);
        _row.Children.Add(_standard);

        _frame.BorderThickness = new Thickness(0, 1, 0, 0);
        _frame.Child = _row;
        Content = _frame;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyMetrics();
        ApplyBrushes();
    }

    /// <summary>Raised after the selected tab changed (once per real change).</summary>
    public event EventHandler? SelectedTabChanged;

    /// <summary>The selected tab. Setting the selected tab again raises nothing.</summary>
    public ConsoleViewTab SelectedTab
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            _extended.Refresh();
            _standard.Refresh();
            RaiseSelectionAutomation(value);
            SelectedTabChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Control? _focusBeforePress;

    /// <summary>
    /// A press on a tab takes the keyboard focus away from the element that had it (the tab is not a focus stop and the
    /// window falls back to nothing). The strip remembers the focused control at the press and puts the focus back after
    /// the tap, so a mouse switch leaves the list where it was.
    /// </summary>
    internal void NotePress()
    {
        _focusBeforePress = XamlRoot is not null ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Control : null;
    }

    internal void RestoreFocusAfterPress()
    {
        Control? target = _focusBeforePress;
        _focusBeforePress = null;
        if (target is not null)
        {
            _ = DispatcherQueue.TryEnqueue(() => _ = target.Focus(FocusState.Programmatic));
        }
    }

    internal ConsoleTabItem ItemFor(ConsoleViewTab tab) => tab == ConsoleViewTab.Extended ? _extended : _standard;

    /// <summary>Takes the colors from the current palette.</summary>
    public void ApplyBrushes()
    {
        _frame.Background = BwcBrushes.Face;
        _frame.BorderBrush = BwcBrushes.Shadow;
        _extended.Refresh();
        _standard.Refresh();
    }

    /// <summary>Takes the height and font from the dense metrics.</summary>
    public void ApplyMetrics()
    {
        Height = ConsoleExtendedMetrics.TabHeight;
        _extended.ApplyMetrics();
        _standard.ApplyMetrics();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleTabStripPeer(this);

    private void RaiseSelectionAutomation(ConsoleViewTab current)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
        {
            return;
        }

        try
        {
            if (FrameworkElementAutomationPeer.FromElement(ItemFor(current)) is ConsoleTabItemPeer peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            UiDiagnostics.Warn?.Invoke($"The tab strip could not raise its selection event: {ex.Message}");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        BwcTheme.Changed += OnThemeChanged;
        DenseMetrics.Changed += OnMetricsChanged;
        ApplyMetrics();
        ApplyBrushes();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        BwcTheme.Changed -= OnThemeChanged;
        DenseMetrics.Changed -= OnMetricsChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyBrushes();

    private void OnMetricsChanged(object? sender, EventArgs e) => ApplyMetrics();
}

/// <summary>One tab: a label in a box with a border on its left, right and bottom, none on top when selected.</summary>
public sealed class ConsoleTabItem : UserControl
{
    private readonly ConsoleTabStrip _owner;
    private readonly Border _box = new();
    private readonly TextBlock _label = new();

    internal ConsoleTabItem(ConsoleTabStrip owner, ConsoleViewTab tab, string label, double width)
    {
        _owner = owner;
        Tab = tab;
        Label = label;
        IsTabStop = false;
        Width = width;
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(this, "ConsoleExtendedTab_" + tab);
        AutomationProperties.SetName(this, label);

        _label.Text = label;
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        _box.Child = _label;
        _box.BorderThickness = new Thickness(1, 0, 1, 1);
        Content = _box;
        Tapped += OnTapped;
        PointerPressed += (_, _) => _owner.NotePress();
    }

    internal ConsoleViewTab Tab { get; }

    internal string Label { get; }

    internal bool IsSelected => _owner.SelectedTab == Tab;

    internal void Select() => _owner.SelectedTab = Tab;

    internal ConsoleTabStrip Owner => _owner;

    internal void Refresh()
    {
        bool selected = IsSelected;
        _box.Background = selected ? BwcBrushes.Window : BwcBrushes.Face;
        _box.BorderBrush = BwcBrushes.Shadow;
        _label.Foreground = BwcBrushes.WindowText;
        _label.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
    }

    internal void ApplyMetrics()
    {
        _label.FontSize = DenseMetrics.FontSize;
        _label.FontFamily = DenseMetrics.FontFamily;
        Height = ConsoleExtendedMetrics.TabHeight;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleTabItemPeer(this);

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        Select();
        _owner.RestoreFocusAfterPress();
    }
}

/// <summary>UI Automation peer of the strip: a Tab control with the Selection pattern (always one selected).</summary>
internal sealed class ConsoleTabStripPeer : FrameworkElementAutomationPeer, ISelectionProvider
{
    private readonly ConsoleTabStrip _strip;

    public ConsoleTabStripPeer(ConsoleTabStrip strip)
        : base(strip)
    {
        _strip = strip;
    }

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => true;

    public IRawElementProviderSimple[] GetSelection()
    {
        AutomationPeer? peer = PeerOf(_strip.ItemFor(_strip.SelectedTab));
        return peer is null ? [] : [ProviderFromPeer(peer)];
    }

    internal IRawElementProviderSimple StripProvider() => ProviderFromPeer(this);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

    protected override string GetClassNameCore() => nameof(ConsoleTabStrip);

    protected override bool IsControlElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

    private static AutomationPeer? PeerOf(FrameworkElement element) =>
        FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
}

/// <summary>UI Automation peer of one tab: a TabItem named by its label, with SelectionItem and Invoke.</summary>
internal sealed class ConsoleTabItemPeer : FrameworkElementAutomationPeer, ISelectionItemProvider, IInvokeProvider
{
    private readonly ConsoleTabItem _item;

    public ConsoleTabItemPeer(ConsoleTabItem item)
        : base(item)
    {
        _item = item;
    }

    public bool IsSelected => _item.IsSelected;

    public IRawElementProviderSimple SelectionContainer =>
        FrameworkElementAutomationPeer.FromElement(_item.Owner) is ConsoleTabStripPeer peer
            ? peer.StripProvider()
            : ProviderFromPeer(this);

    public void Select() => _item.Select();

    public void AddToSelection() => _item.Select();

    public void RemoveFromSelection()
    {
        // A tab strip always has one selected tab; removing it is not possible.
    }

    public void Invoke() => _item.Select();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

    protected override string GetClassNameCore() => nameof(ConsoleTabItem);

    protected override string GetNameCore() => _item.Label;

    protected override bool IsControlElementCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.SelectionItem => this,
        PatternInterface.Invoke => this,
        _ => base.GetPatternCore(patternInterface),
    };
}
