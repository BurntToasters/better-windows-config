using System.Text;
using Bwc.UI.Console.Shell;
using Bwc.UI.Theme;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace Bwc.UI.Console.Extended;

/// <summary>
/// The description pane of the Extended view: a title, links and labeled sections of plain, selectable text in one
/// vertical scroller. It knows no snap-in; the caller supplies a <see cref="ConsoleDescriptionModel"/>. Text is shown with
/// TextBlocks only, so an ampersand is a character, nothing is parsed, and a very long text is split over several blocks
/// (docs/failure-modes/console-extended.md, sections 1 to 3).
/// </summary>
public sealed class ConsoleDescriptionPane : UserControl
{
    private readonly Border _frame = new();
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _stack = new();
    private readonly List<(HyperlinkButton Button, ConsoleDescriptionLink Link)> _links = [];
    private readonly SolidColorBrush _linkBrush = new(Color.FromArgb(0xFF, 0x00, 0x00, 0xFF));
    private ConsoleDescriptionModel? _model;
    private int _paragraphCount;
    private int _paragraphChars;
    private bool _subscribed;

    public ConsoleDescriptionPane()
    {
        AutomationProperties.SetAutomationId(this, "ConsoleDescriptionPane");
        AutomationProperties.SetName(this, ConsoleStrings.Get("ConsoleDescriptionPaneName"));
        IsTabStop = false;

        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollMode = ScrollMode.Enabled;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroller.HorizontalScrollMode = ScrollMode.Disabled;
        _scroller.ZoomMode = ZoomMode.Disabled;
        _scroller.IsTabStop = true;
        _scroller.TabFocusNavigation = KeyboardNavigationMode.Once;
        AutomationProperties.SetAutomationId(_scroller, "ConsoleDescriptionScroll");
        AutomationProperties.SetName(_scroller, ConsoleStrings.Get("ConsoleDescriptionScrollName"));
        _scroller.KeyDown += OnScrollerKeyDown;
        _scroller.Content = _stack;

        _stack.Orientation = Orientation.Vertical;
        _stack.Margin = new Thickness(5, 4, 12, 4);
        _stack.SizeChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        _scroller.SizeChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);

        _frame.Child = _scroller;
        Content = _frame;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyBrushes();
    }

    /// <summary>Raised when a link is invoked by a click, Enter, Space or UI Automation.</summary>
    public event EventHandler<ConsoleDescriptionLinkEventArgs>? LinkInvoked;

    /// <summary>Raised after the scroller or its content changed size, so the keyboard stops are current.</summary>
    internal event EventHandler? LayoutChanged;

    /// <summary>
    /// What the pane shows. Null shows an empty pane. The pane is rebuilt on every set, shown or not, so a model set while
    /// the pane is collapsed is the one shown when it returns.
    /// </summary>
    public ConsoleDescriptionModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            Rebuild();
        }
    }

    /// <summary>The scroller of the pane, a keyboard stop when its content does not fit.</summary>
    internal ScrollViewer Scroller => _scroller;

    /// <summary>The number of text blocks that hold section text (E2E readout).</summary>
    internal int ParagraphCount => _paragraphCount;

    /// <summary>The number of characters in those blocks (E2E readout).</summary>
    internal int ParagraphChars => _paragraphChars;

    /// <summary>The links that are focus stops, in model order, then the scroller when it can scroll.</summary>
    internal IReadOnlyList<Control> FocusStops()
    {
        var stops = new List<Control>();
        if (Visibility != Visibility.Visible)
        {
            return stops;
        }

        foreach ((HyperlinkButton button, _) in _links)
        {
            stops.Add(button);
        }

        if (_scroller.ScrollableHeight > 0)
        {
            stops.Add(_scroller);
        }

        return stops;
    }

    /// <summary>The title, the links and the sections as plain text lines, for a copy of the whole pane.</summary>
    public string AllText()
    {
        if (_model is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine(_model.Title);
        foreach (ConsoleDescriptionSection section in _model.Sections)
        {
            if (section.Label.Length > 0)
            {
                text.AppendLine(section.Label);
            }

            if (section.Text.Length > 0)
            {
                text.AppendLine(Normalize(section.Text));
            }
        }

        return text.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>Takes the colors from the current palette.</summary>
    public void ApplyBrushes()
    {
        _frame.Background = BwcBrushes.Window;
        _linkBrush.Color = LinkColor();
    }

    /// <summary>Takes the font from the dense metrics.</summary>
    public void ApplyMetrics()
    {
        foreach (UIElement child in _stack.Children)
        {
            ApplyFont(child);
        }
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>
    /// Splits text into blocks of at most <see cref="ConsoleExtendedMetrics.MaxParagraphLength"/> characters. Whole lines
    /// are kept together (joined by one line break) until the limit; a longer line is cut after a space when it has one in
    /// the second half of the limit, else at the limit. Nothing is dropped except the line break at a block boundary.
    /// </summary>
    internal static List<string> SplitParagraphs(string text)
    {
        string normalized = Normalize(text);
        var chunks = new List<string>();
        if (normalized.Length == 0)
        {
            return chunks;
        }

        const int max = ConsoleExtendedMetrics.MaxParagraphLength;
        var current = new StringBuilder();
        bool open = false;

        void Close()
        {
            if (open)
            {
                chunks.Add(current.ToString());
                _ = current.Clear();
                open = false;
            }
        }

        foreach (string line in normalized.Split('\n'))
        {
            string rest = line;
            while (rest.Length > max)
            {
                int cut = rest.LastIndexOf(' ', max - 1);
                if (cut < max / 2)
                {
                    cut = max - 1;
                }

                Close();
                chunks.Add(rest[..(cut + 1)]);
                rest = rest[(cut + 1)..];
            }

            if (open && current.Length + 1 + rest.Length > max)
            {
                Close();
            }

            if (open)
            {
                _ = current.Append('\n');
            }

            _ = current.Append(rest);
            open = true;
        }

        Close();
        return chunks;
    }

    private void Rebuild()
    {
        _stack.Children.Clear();
        _links.Clear();
        _paragraphCount = 0;
        _paragraphChars = 0;
        if (_model is null)
        {
            return;
        }

        var title = new TextBlock
        {
            Text = _model.Title,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontWeight = FontWeights.Bold,
            Foreground = BwcBrushes.WindowText,
        };
        AutomationProperties.SetAutomationId(title, "ConsoleDescriptionTitle");
        AutomationProperties.SetName(title, _model.Title);
        _stack.Children.Add(title);

        foreach (ConsoleDescriptionLink link in _model.Links)
        {
            _stack.Children.Add(CreateLink(link));
        }

        foreach (ConsoleDescriptionSection section in _model.Sections)
        {
            AddSection(section);
        }

        ApplyMetrics();
    }

    private HyperlinkButton CreateLink(ConsoleDescriptionLink link)
    {
        var button = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Margin = new Thickness(0, 6, 0, 0),
            MinHeight = 0,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new TextBlock
            {
                Text = link.Text,
                TextWrapping = TextWrapping.Wrap,
                TextDecorations = Windows.UI.Text.TextDecorations.Underline,
                Foreground = _linkBrush,
            },
        };
        AutomationProperties.SetAutomationId(button, "ConsoleDescriptionLink_" + link.Id);
        AutomationProperties.SetName(button, link.Text);
        button.Click += (_, _) => LinkInvoked?.Invoke(this, new ConsoleDescriptionLinkEventArgs(link));
        _links.Add((button, link));
        return button;
    }

    private void AddSection(ConsoleDescriptionSection section)
    {
        if (section.Label.Length > 0)
        {
            var label = new TextBlock
            {
                Text = section.Label,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 0),
                Foreground = BwcBrushes.WindowText,
            };
            AutomationProperties.SetAutomationId(label, "ConsoleDescriptionLabel_" + section.Id);
            AutomationProperties.SetName(label, section.Label);
            _stack.Children.Add(label);
        }

        List<string> blocks = SplitParagraphs(section.Text);
        for (int i = 0; i < blocks.Count; i++)
        {
            var body = new TextBlock
            {
                Text = blocks[i],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, section.Label.Length > 0 || i > 0 ? 2 : 10, 0, 0),
                Foreground = BwcBrushes.WindowText,
            };
            AutomationProperties.SetAutomationId(body, i == 0 ? "ConsoleDescriptionText_" + section.Id : $"ConsoleDescriptionText_{section.Id}_{i}");
            _stack.Children.Add(body);
            _paragraphCount++;
            _paragraphChars += blocks[i].Length;
        }
    }

    private static void ApplyFont(UIElement element)
    {
        switch (element)
        {
            case TextBlock text:
                text.FontSize = DenseMetrics.FontSize;
                text.FontFamily = DenseMetrics.FontFamily;
                break;
            case HyperlinkButton { Content: TextBlock content }:
                content.FontSize = DenseMetrics.FontSize;
                content.FontFamily = DenseMetrics.FontFamily;
                break;
        }
    }

    /// <summary>
    /// The link color: classic's blue on the light palette (lighter on dark), the system hotlight color in high contrast, with
    /// the window text color when the system has none.
    /// </summary>
    private static Color LinkColor()
    {
        BwcPalette palette = BwcTheme.Current;
        switch (palette.Kind)
        {
            case BwcThemeKind.HighContrast:
                // COLOR_HOTLIGHT is the system's link color in a contrast theme; a zero value means the system has none.
                uint colorRef = PInvoke.GetSysColor(SYS_COLOR_INDEX.COLOR_HOTLIGHT);
                return colorRef == 0
                    ? palette.WindowText
                    : Color.FromArgb(0xFF, (byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

            case BwcThemeKind.Dark:
                return Color.FromArgb(0xFF, 0x6C, 0xB6, 0xFF);
            default:
                return Color.FromArgb(0xFF, 0x00, 0x00, 0xFF);
        }
    }

    private void OnScrollerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+C on the scroller (keyboard focus, no text selected by the pointer) copies the whole pane as text.
        if (e.Key != VirtualKey.C || e.Handled || !IsControlDown())
        {
            return;
        }

        e.Handled = true;
        try
        {
            var package = new DataPackage();
            package.SetText(AllText());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            UiDiagnostics.Warn?.Invoke($"The description pane could not copy its text: {ex.Message}");
        }
    }

    private static bool IsControlDown() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        BwcTheme.Changed += OnThemeChanged;
        DenseMetrics.Changed += OnMetricsChanged;
        ApplyBrushes();
        ApplyMetrics();
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

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleDescriptionPanePeer(this);

    private sealed class ConsoleDescriptionPanePeer : FrameworkElementAutomationPeer
    {
        public ConsoleDescriptionPanePeer(ConsoleDescriptionPane owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(ConsoleDescriptionPane);

        protected override bool IsControlElementCore() => true;
    }
}
