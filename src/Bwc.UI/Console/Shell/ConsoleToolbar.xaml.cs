using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// The standard console toolbar: Back, Forward, Up One Level, Show/Hide Console Tree, Properties, Refresh, Export
/// List and Help, then the host's extension buttons. The toolbar only shows state and raises
/// <see cref="Invoked"/>; the host decides what a command does and when it is enabled. A standard button is disabled
/// until the host enables it, except Show/Hide Console Tree, which the shell runs itself, and Export List, which is
/// enabled in every state of the classic console (TOOL-011).
/// </summary>
public sealed partial class ConsoleToolbar : UserControl
{
    private const string GlyphFont = "Segoe MDL2 Assets";
    private const double GlyphSize = 14;

    private readonly Dictionary<int, Button> _buttons = [];
    private readonly Dictionary<int, string> _glyphs = [];
    private readonly List<FrameworkElement> _separators = [];
    private readonly List<int> _order = [];
    private readonly HashSet<int> _hidden = [];
    private int _imagePixels = 16;
    private bool _standardVisible = true;
    private bool _extensionVisible = true;

    public ConsoleToolbar()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, ConsoleStrings.Get("ConsoleToolbarName"));

        AddStandard(ConsoleToolbarIds.Back, "ConsoleToolbarBack", "Back", "", initiallyEnabled: false);
        AddStandard(ConsoleToolbarIds.Forward, "ConsoleToolbarForward", "Forward", "", initiallyEnabled: false);
        AddStandard(ConsoleToolbarIds.UpOneLevel, "ConsoleToolbarUp", "UpOneLevel", "", initiallyEnabled: false);
        AddSeparator();
        AddStandard(ConsoleToolbarIds.ShowHideConsoleTree, "ConsoleToolbarShowHideTree", "ShowHideConsoleTree", "", initiallyEnabled: true);
        AddSeparator();
        AddStandard(ConsoleToolbarIds.Properties, "ConsoleToolbarProperties", "Properties", "", initiallyEnabled: false);
        AddStandard(ConsoleToolbarIds.Refresh, "ConsoleToolbarRefresh", "Refresh", "", initiallyEnabled: false);
        AddStandard(ConsoleToolbarIds.ExportList, "ConsoleToolbarExportList", "ExportList", "", initiallyEnabled: true);
        AddSeparator();
        AddStandard(ConsoleToolbarIds.Help, "ConsoleToolbarHelp", "Help", "", initiallyEnabled: false);

        RefreshImages();
    }

    /// <summary>Raised for a click on an enabled button. The shell raises it again for the keys that stand for one.</summary>
    public event EventHandler<ConsoleToolbarEventArgs>? Invoked;

    /// <summary>Raised after the set of visible buttons changed (a button or a group was shown or hidden).</summary>
    public event EventHandler? VisibleButtonsChanged;

    /// <summary>
    /// Shows or hides the eight standard buttons as one group (the Standard toolbar switch of Customize View). A button
    /// that the host hid with <see cref="SetButtonVisible"/> stays hidden when the group shows.
    /// </summary>
    public bool StandardGroupVisible
    {
        get => _standardVisible;
        set
        {
            if (_standardVisible != value)
            {
                _standardVisible = value;
                ApplyVisibility();
            }
        }
    }

    /// <summary>
    /// Shows or hides the host's extension buttons as one group (the Snap-in toolbars switch of Customize View). A button
    /// that the host hid with <see cref="SetButtonVisible"/> stays hidden when the group shows.
    /// </summary>
    public bool ExtensionGroupVisible
    {
        get => _extensionVisible;
        set
        {
            if (_extensionVisible != value)
            {
                _extensionVisible = value;
                ApplyVisibility();
            }
        }
    }

    /// <summary>True while at least one button shows. The shell hides the band that holds the toolbar when none does.</summary>
    public bool HasVisibleButtons => _buttons.Values.Any(button => button.Visibility == Visibility.Visible);

    /// <summary>Where the images came from: the module and resource, or "fallback" for the font glyphs.</summary>
    public string IconSource => ConsoleToolbarIcons.Source;

    /// <summary>True while a button with this id exists and is enabled.</summary>
    public bool IsButtonEnabled(int id) => _buttons.TryGetValue(id, out Button? button) && button.IsEnabled;

    /// <summary>Enables or disables a button. An unknown id throws, so a typo does not go unnoticed.</summary>
    public void SetButtonEnabled(int id, bool enabled)
    {
        Button button = ButtonFor(id);
        if (button.IsEnabled == enabled)
        {
            return;
        }

        button.IsEnabled = enabled;
        ApplyImage(id, button);
    }

    /// <summary>
    /// Shows or hides a button for the host (a node that has no filter has no Filter button). A separator shows only between
    /// two visible buttons. A button whose group is hidden stays hidden whatever the host says.
    /// </summary>
    public void SetButtonVisible(int id, bool visible)
    {
        _ = ButtonFor(id);
        if (visible)
        {
            _ = _hidden.Remove(id);
        }
        else
        {
            _ = _hidden.Add(id);
        }

        ApplyVisibility();
    }

    /// <summary>
    /// Adds a button after the standard ones, after a separator. The id must be at least
    /// <see cref="ConsoleToolbarIds.FirstExtension"/> and not used yet. The glyph is a character of the Segoe MDL2
    /// Assets font. A new button starts enabled.
    /// </summary>
    public void AddExtensionButton(int id, string name, string glyph, string? toolTip = null)
    {
        AddExtensionButton(id, name, glyph, toolTip, idText: null, toggle: false);
    }

    /// <summary>
    /// As the overload without the extra parameters. <paramref name="idText"/> is the end of the automation id
    /// (<c>ConsoleToolbar_</c> plus it; the default is <c>Ext</c> plus the id). A <paramref name="toggle"/> button can show
    /// itself pressed (<see cref="SetButtonChecked"/>) and has the Toggle pattern as well as Invoke.
    /// </summary>
    public void AddExtensionButton(int id, string name, string glyph, string? toolTip, string? idText, bool toggle)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(glyph);
        if (id < ConsoleToolbarIds.FirstExtension)
        {
            throw new ArgumentException($"Extension button id {id} is below {ConsoleToolbarIds.FirstExtension}.", nameof(id));
        }

        if (_buttons.ContainsKey(id))
        {
            throw new ArgumentException($"Toolbar button id {id} is already registered.", nameof(id));
        }

        if (!_buttons.Keys.Any(existing => existing >= ConsoleToolbarIds.FirstExtension))
        {
            AddSeparator();
        }

        AddButton(id, name, string.IsNullOrEmpty(idText) ? "Ext" + id : idText, glyph, toolTip ?? name, initiallyEnabled: true, toggle);
        ApplyVisibility();
        RefreshImages();
    }

    /// <summary>Draws a toggle button pressed or released. A button that is not a toggle button ignores it.</summary>
    public void SetButtonChecked(int id, bool isChecked)
    {
        if (ButtonFor(id) is ConsoleToolbarToggleButton toggle)
        {
            toggle.IsToggled = isChecked;
        }
    }

    /// <summary>True while a toggle button is drawn pressed.</summary>
    public bool IsButtonChecked(int id) => _buttons.TryGetValue(id, out Button? button) && button is ConsoleToolbarToggleButton { IsToggled: true };

    /// <summary>Reads the images again at the pixel size of the current scale (a DPI change); reapplies brushes.</summary>
    public void RefreshImages()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        _imagePixels = Math.Max(1, (int)Math.Round(16 * (double.IsFinite(scale) && scale > 0 ? scale : 1.0), MidpointRounding.AwayFromZero));
        foreach ((int id, Button button) in _buttons)
        {
            ApplyImage(id, button);
        }

        AutomationProperties.SetHelpText(this, $"IconPx={_imagePixels};IconSource={IconSource}");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ConsoleToolbarPeer(this);

    private void AddStandard(int id, string nameKey, string idText, string glyph, bool initiallyEnabled)
    {
        string name = ConsoleStrings.Get(nameKey);
        AddButton(id, name, idText, glyph, name, initiallyEnabled);
    }

    private void AddButton(int id, string name, string idText, string glyph, string toolTip, bool initiallyEnabled, bool toggle = false)
    {
        Button button = toggle ? new ConsoleToolbarToggleButton() : new Button();
        button.Style = (Style)Resources["ConsoleToolbarButtonStyle"];
        button.IsEnabled = initiallyEnabled;
        button.Tag = id;
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, "ConsoleToolbar_" + idText);
        ToolTipService.SetToolTip(button, toolTip);
        button.Click += (_, _) =>
        {
            if (button.IsEnabled)
            {
                Invoked?.Invoke(this, new ConsoleToolbarEventArgs(id));
            }
        };

        _buttons[id] = button;
        _glyphs[id] = glyph;
        _order.Add(id);
        ButtonRow.Children.Add(button);
    }

    private void AddSeparator()
    {
        // An etched line: the shadow pixel and the highlight pixel side by side, in an 8 DIP slot.
        var line = new Grid { Width = 8, Height = 22 };
        line.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(3, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = BwcBrushes.Shadow,
        });
        line.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(4, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = BwcBrushes.Highlight3D,
        });
        _separators.Add(line);
        ButtonRow.Children.Add(line);
    }

    private Button ButtonFor(int id) =>
        _buttons.TryGetValue(id, out Button? button)
            ? button
            : throw new ArgumentException($"No toolbar button has id {id}.", nameof(id));

    /// <summary>Shows each button that its group and the host allow, then the separators between the visible ones.</summary>
    private void ApplyVisibility()
    {
        foreach ((int id, Button button) in _buttons)
        {
            bool group = id >= ConsoleToolbarIds.FirstExtension ? _extensionVisible : _standardVisible;
            button.Visibility = group && !_hidden.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateSeparators();
        VisibleButtonsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A separator is visible when a visible button comes before it and another after it.</summary>
    private void UpdateSeparators()
    {
        bool seenButton = false;
        FrameworkElement? pending = null;
        foreach (UIElement child in ButtonRow.Children)
        {
            if (child is Button button)
            {
                if (button.Visibility == Visibility.Visible)
                {
                    if (pending is not null && seenButton)
                    {
                        pending.Visibility = Visibility.Visible;
                    }

                    pending = null;
                    seenButton = true;
                }
            }
            else if (child is FrameworkElement separator)
            {
                separator.Visibility = Visibility.Collapsed;
                pending = separator;
            }
        }
    }

    private void ApplyImage(int id, Button button)
    {
        bool disabled = !button.IsEnabled;
        string source = "fallback";
        if (ConsoleToolbarIcons.TryGet(id, _imagePixels, disabled, out ImageSource? image) && image is not null)
        {
            double dip = _imagePixels / Math.Max(0.01, XamlRoot?.RasterizationScale ?? 1.0);
            button.Content = new Image
            {
                Source = image,
                Width = dip,
                Height = dip,
                Stretch = Stretch.Fill,
            };
            source = ConsoleToolbarIcons.Source;
        }
        else
        {
            button.Content = new FontIcon
            {
                Glyph = _glyphs[id],
                FontFamily = new FontFamily(GlyphFont),
                FontSize = GlyphSize,
                Foreground = disabled ? BwcBrushes.GrayText : BwcBrushes.WindowText,
            };
        }

        AutomationProperties.SetHelpText(button, "Icon=" + source);
    }

    private sealed class ConsoleToolbarPeer : FrameworkElementAutomationPeer
    {
        public ConsoleToolbarPeer(ConsoleToolbar owner)
            : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ToolBar;

        protected override string GetClassNameCore() => nameof(ConsoleToolbar);
    }
}
