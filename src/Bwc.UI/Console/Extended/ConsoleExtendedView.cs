using Bwc.UI.Theme;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Bwc.UI.Console.Extended;

/// <summary>
/// Hosts any result content with an optional description pane on its left and the Extended and Standard tabs at the
/// bottom, as the classic result pane does. Extended shows the pane (about 36 percent of the width, as measured in the
/// classic window) and the content; Standard shows the content alone. The content element is never moved or rebuilt by a
/// tab change, so its selection, focus and scroll position stay. It knows no snap-in: the caller supplies the content
/// and a <see cref="ConsoleDescriptionModel"/> and answers <see cref="DescriptionLinkInvoked"/>. The failure modes are in
/// docs/failure-modes/console-extended.md.
/// </summary>
public sealed class ConsoleExtendedView : UserControl
{
    /// <summary>The share of the view width that the description pane takes (212 of 589 logical pixels in the probe).</summary>
    public const double DescriptionRatio = ConsoleExtendedMetrics.DescriptionRatio;

    /// <summary>The narrowest the description pane is made, in DIPs. A BWC choice, not measured from classic.</summary>
    public const double MinDescriptionWidth = ConsoleExtendedMetrics.MinPaneWidth;

    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly ColumnDefinition _paneColumn = new();
    private readonly ContentControl _contentHost = new();
    private readonly ConsoleDescriptionPane _pane = new();
    private readonly ConsoleTabStrip _tabs = new();
    private bool _descriptionBarShown = true;

    public ConsoleExtendedView()
    {
        AutomationProperties.SetAutomationId(this, "ConsoleExtendedView");
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _paneColumn.Width = new GridLength(ConsoleExtendedMetrics.MinPaneWidth);
        _body.ColumnDefinitions.Add(_paneColumn);
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_pane, 0);
        _contentHost.IsTabStop = false;
        _contentHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _contentHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(_contentHost, 1);
        _body.Children.Add(_pane);
        _body.Children.Add(_contentHost);

        Grid.SetRow(_body, 0);
        Grid.SetRow(_tabs, 1);
        _root.Children.Add(_body);
        _root.Children.Add(_tabs);
        Content = _root;

        _pane.LinkInvoked += (_, e) => DescriptionLinkInvoked?.Invoke(this, e);
        _pane.LayoutChanged += (_, _) => UpdateHelpText();
        _tabs.SelectedTabChanged += OnSelectedTabChanged;
        _body.SizeChanged += (_, _) => ApplyPaneWidth();
        PreviewKeyDown += OnPreviewKeyDown;
        ApplyPaneWidth();
    }

    /// <summary>Raised after the selected tab changed. The caller persists <see cref="SelectedTab"/>.</summary>
    public event EventHandler? SelectedTabChanged;

    /// <summary>Raised when a link of the description pane is invoked.</summary>
    public event EventHandler<ConsoleDescriptionLinkEventArgs>? DescriptionLinkInvoked;

    /// <summary>The content next to the description pane: the list, a page, or nothing. It is never re-parented.</summary>
    public UIElement? ViewContent
    {
        get => _contentHost.Content as UIElement;
        set => _contentHost.Content = value;
    }

    /// <summary>
    /// The element of the content that takes the keyboard focus (the list). It is the first keyboard stop of the view, the
    /// place the focus goes when a tab change would collapse the focused description pane.
    /// </summary>
    public Control? ContentFocusTarget { get; set; }

    /// <summary>The selected tab, for the caller to persist and restore. Setting the selected tab again does nothing.</summary>
    public ConsoleViewTab SelectedTab
    {
        get => _tabs.SelectedTab;
        set => _tabs.SelectedTab = value;
    }

    /// <summary>
    /// What the description pane shows. Null shows an empty pane. A model set while Standard is selected is kept and shown
    /// when Extended returns.
    /// </summary>
    public ConsoleDescriptionModel? Description
    {
        get => _pane.Model;
        set
        {
            _pane.Model = value;
            UpdateHelpText();
        }
    }

    /// <summary>
    /// The Description bar switch of Customize View: false hides the description pane on the Extended tab too, and the content
    /// takes its room. The Standard tab shows no pane either way. Default true.
    /// </summary>
    public bool IsDescriptionBarShown
    {
        get => _descriptionBarShown;
        set
        {
            if (_descriptionBarShown == value)
            {
                return;
            }

            _descriptionBarShown = value;
            ApplyPaneVisibility();
        }
    }

    /// <summary>True while the description pane shows (Extended is selected and the Description bar switch is on).</summary>
    public bool IsDescriptionVisible => _tabs.SelectedTab == ConsoleViewTab.Extended && _descriptionBarShown;

    /// <summary>The width of the description pane in DIPs; zero while Standard is selected.</summary>
    public double DescriptionPaneWidth => IsDescriptionVisible ? _paneColumn.Width.Value : 0;

    /// <summary>The description pane, for a host that needs its brushes or text.</summary>
    public ConsoleDescriptionPane DescriptionPane => _pane;

    /// <summary>The tab strip, for a host that needs it.</summary>
    public ConsoleTabStrip Tabs => _tabs;

    /// <summary>
    /// The hook for <c>ConsoleShell.ResultPaneTabHandler</c>: moves the focus to the next keyboard stop of the view for Tab
    /// (the previous one for Shift+Tab) and returns true, or returns false when the focus is not in the view or already at
    /// the last (first) stop, and the shell then cycles to the next pane. The stops are the content focus target, the
    /// links of the description pane in order, and the description scroller when its text does not fit.
    /// </summary>
    public bool TryMoveFocusWithin(bool forward)
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var stops = new List<Control>();
        if (ContentFocusTarget is { } target)
        {
            stops.Add(target);
        }

        if (IsDescriptionVisible)
        {
            stops.AddRange(_pane.FocusStops());
        }

        object? focused = FocusManager.GetFocusedElement(XamlRoot);
        int current = stops.FindIndex(stop => ReferenceEquals(stop, focused));
        if (current < 0 && focused is DependencyObject focusedElement)
        {
            current = stops.FindLastIndex(stop => IsWithin(focusedElement, stop));
        }

        if (current < 0)
        {
            return false;
        }

        int next = current + (forward ? 1 : -1);
        return next >= 0 && next < stops.Count && stops[next].Focus(FocusState.Keyboard);
    }

    private void OnSelectedTabChanged(object? sender, EventArgs e)
    {
        ApplyPaneVisibility();
        SelectedTabChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows or hides the pane by the selected tab and the Description bar switch. Collapsing the pane while the focus is
    /// inside it would leave the focus on nothing: the content takes it first.
    /// </summary>
    private void ApplyPaneVisibility()
    {
        bool hide = !IsDescriptionVisible;
        if (hide && XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsWithin(focused, _pane))
        {
            _ = ContentFocusTarget?.Focus(FocusState.Programmatic);
        }

        _pane.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        ApplyPaneWidth();
        UpdateHelpText();
    }

    private void ApplyPaneWidth()
    {
        _paneColumn.Width = new GridLength(IsDescriptionVisible ? ConsoleExtendedMetrics.PaneWidthFor(_body.ActualWidth) : 0);
        UpdateHelpText();
    }

    /// <summary>Ctrl+Tab and Ctrl+Shift+Tab switch the tab. Classic has no key for the tabs (declared choice).</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != VirtualKey.Tab || !IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu))
        {
            return;
        }

        e.Handled = true;
        _tabs.SelectedTab = _tabs.SelectedTab == ConsoleViewTab.Extended ? ConsoleViewTab.Standard : ConsoleViewTab.Extended;
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static bool IsWithin(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateHelpText()
    {
#if BWC_E2E
        // E2E only: the state of the view as UIA help text, because counts would be announced by screen readers.
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
        string text = string.Create(
            c,
            $"Tab={_tabs.SelectedTab};PaneVisible={(IsDescriptionVisible ? 1 : 0)};PaneWidth={DescriptionPaneWidth:0.##};" +
            $"ViewWidth={_body.ActualWidth:0.##};Paragraphs={_pane.ParagraphCount};ParagraphChars={_pane.ParagraphChars};" +
            $"Links={_pane.FocusStops().Count(stop => stop is HyperlinkButton)};Stops={_pane.FocusStops().Count}");
        AutomationProperties.SetHelpText(this, text);
#endif
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleExtendedViewPeer(this);

    private sealed class ConsoleExtendedViewPeer : FrameworkElementAutomationPeer
    {
        public ConsoleExtendedViewPeer(ConsoleExtendedView owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(ConsoleExtendedView);

        protected override bool IsControlElementCore() => true;
    }
}
