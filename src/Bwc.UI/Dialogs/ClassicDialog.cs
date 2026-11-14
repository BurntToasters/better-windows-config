using System.Runtime.InteropServices;
using Bwc.UI;
using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// A compact, owned, modal dialog that follows a classic Win32 template. The client area is the
/// template size in DLU converted for the window DPI. Controls sit at their template rectangles
/// in template order, have Win32 mnemonics and tab order, and use the dense styles in
/// <c>ClassicDialogStyles.xaml</c>. There is no heading and no title-bar icon.
/// </summary>
/// <remarks>
/// Owner handling is the one in <see cref="ModalDialogWindow"/>: the owner is disabled while the
/// dialog is open and restored on close, including when the dialog fails before it is shown.
/// Enter invokes the default button (BS_DEFPUSHBUTTON), Escape invokes the button with id 2
/// (IDCANCEL), Tab and Shift+Tab follow the template order, and Alt+letter follows the mnemonics.
/// </remarks>
public class ClassicDialog : ModalDialogWindow
{
    private const string StylesSource = "ms-appx:///Bwc.UI/Dialogs/ClassicDialogStyles.xaml";

    /// <summary>The glyph (13 DIP) and its 4 DIP gap, the space a check or radio label does not get.</summary>
    private const double GlyphAdvanceDip = 17;

    private readonly List<ClassicHostedControl> _controls = [];
    private readonly Dictionary<string, ClassicHostedControl> _byAutomationId = new(StringComparer.Ordinal);
    private readonly Dictionary<char, string> _mnemonics = [];
    private readonly ResourceDictionary _styles;
    private readonly DialogRootControl _root;
    private readonly Canvas? _canvas;
    private readonly ClassicDialogDefinition? _template;
    private ClassicFontSpec _font;
    private ClassicDialogMetrics _metrics;
    private bool _metricsReady;
    private FrameworkElement? _initialFocus;
    private bool _windowActive;
    private bool _cueVisible;

    /// <summary>Template mode: the controls are placed at the template rectangles.</summary>
    protected ClassicDialog(ClassicDialogDefinition template)
        : this(template?.Caption ?? throw new ArgumentNullException(nameof(template)), new ClassicFontSpec(template.FontFace, template.FontPointSize), template)
    {
    }

    /// <summary>
    /// Flow mode: the caller lays out the content (used by message boxes, whose size follows the
    /// text). Controls are created with <see cref="CreateControl"/> and placed by the caller.
    /// </summary>
    protected ClassicDialog(string caption, ClassicFontSpec font)
        : this(caption, font, template: null)
    {
    }

    private ClassicDialog(string caption, ClassicFontSpec font, ClassicDialogDefinition? template)
        : base(fluentLayout: false)
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);

        Title = caption;
        _font = font;
        _template = template;
        _styles = new ResourceDictionary { Source = new Uri(StylesSource) };

        _root = new DialogRootControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            IsTabStop = false,
            FontFamily = new FontFamily(font.Face),
            FontSize = font.DipSize,
        };
        AutomationProperties.SetAutomationId(_root, "DialogRoot");
        _root.Style = StyleFor("BwcClassicRootStyle") ?? _root.Style;
        ApplyPalette();
        BwcTheme.Changed += OnPaletteChanged;
        Closed += OnDialogClosed;
        // Handled events too: a focused Button marks Enter handled, and the dialog still needs Enter and Escape.
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        _root.KeyUp += OnRootKeyUp;
        _root.Loaded += OnRootLoaded;
        Activated += OnActivated;

        if (template is not null)
        {
            ValidateTemplate(template);
            // The surface paints the face itself. A ContentControl background is not painted on this root,
            // so the window backing (black) showed through and the palette text was drawn on it.
            _canvas = new Canvas { Background = BwcBrushes.Face };
            _root.Content = _canvas;
            foreach (ClassicControlDefinition definition in template.Controls)
            {
                ClassicHostedControl hosted = AddHosted(definition);
                _canvas.Children.Add(hosted.Element);
            }
        }

        Content = _root;
    }

    /// <summary>The metrics for the window DPI. Valid after the dialog has been sized, before it is shown.</summary>
    protected ClassicDialogMetrics Metrics => _metricsReady
        ? _metrics
        : throw new InvalidOperationException("The dialog metrics are not known until the dialog is sized.");

    /// <summary>The DIP size of one DLU horizontally at the window DPI.</summary>
    protected double DluX(int dlu) => Metrics.ToDipX(dlu);

    /// <summary>The DIP size of one DLU vertically at the window DPI.</summary>
    protected double DluY(int dlu) => Metrics.ToDipY(dlu);

    /// <summary>The template this dialog was built from, or null in flow mode.</summary>
    protected ClassicDialogDefinition? Template => _template;

    /// <summary>
    /// Creates a control from a definition and registers it for tab order, mnemonics, and automation.
    /// In template mode the control is placed by the constructor. In flow mode the caller places it.
    /// </summary>
    protected FrameworkElement CreateControl(ClassicControlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return AddHosted(definition).Element;
    }

    /// <summary>Sets the content of the dialog in flow mode. Template mode sets it in the constructor.</summary>
    protected void SetFlowContent(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_template is not null)
        {
            throw new InvalidOperationException("A template dialog places its own controls.");
        }

        var surface = new Grid { Background = BwcBrushes.Face };
        surface.Children.Add(content);
        _root.Content = surface;
    }

    /// <summary>
    /// Paints the dialog from the BWC palette rather than from the app's Fluent theme keys. The face and text are
    /// the palette brushes, which are live, so a palette change repaints them. The element theme follows the palette
    /// so the Fluent buttons, links, and text boxes match it, whatever the Windows app mode is.
    /// </summary>
    private void ApplyPalette()
    {
        _root.RequestedTheme = BwcTheme.ElementThemeFor(BwcTheme.Current);
        _root.Background = BwcBrushes.Face;
        _root.Foreground = BwcBrushes.WindowText;
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => ApplyPalette();

    /// <summary>A dialog that could not be shown never raises Closed, so its palette subscription is dropped here.</summary>
    protected override void OnShowAbandoned()
    {
        BwcTheme.Changed -= OnPaletteChanged;
        base.OnShowAbandoned();
    }

    /// <summary>
    /// Windows resets keyboard focus when the window is activated. When focus is not inside the dialog, the
    /// control that had it after load (or the first tab stop) takes it back, so Enter and Escape reach the root.
    /// </summary>
    protected override void OnDialogActivated()
    {
        // Activation can come before the content is loaded. The XAML root does not exist yet, so the
        // Loaded handler restores focus once it does.
        if (_root.XamlRoot is null)
        {
            return;
        }

        RestoreFocusInsideDialog();
    }

    private void RestoreFocusInsideDialog()
    {
        if (FocusIsInsideRoot())
        {
            return;
        }

        if (_initialFocus is { } initial && initial.IsTabStop)
        {
            _ = initial.Focus(FocusState.Programmatic);
            return;
        }

        FocusFirstTabStop();
    }

    private bool FocusIsInsideRoot()
    {
        if (_root.XamlRoot is null)
        {
            return false;
        }

        DependencyObject? node = FocusManager.GetFocusedElement(_root.XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, _root))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnDialogClosed(object? sender, WindowEventArgs args)
    {
        Closed -= OnDialogClosed;
        BwcTheme.Changed -= OnPaletteChanged;
    }

    /// <summary>Returns the control with this automation id, or null when the dialog has none.</summary>
    protected T? FindControl<T>(string automationId)
        where T : class =>
        _byAutomationId.TryGetValue(automationId, out ClassicHostedControl? hosted) ? hosted.Element as T : null;

    /// <summary>Returns the control with this automation id, or throws when the dialog has none.</summary>
    protected T RequireControl<T>(string automationId)
        where T : class =>
        FindControl<T>(automationId) ?? throw new InvalidOperationException($"The dialog has no '{automationId}' control.");

    /// <summary>
    /// Runs a button command. Ids follow Win32: 1 is IDOK and 2 is IDCANCEL. Subclasses map their
    /// other ids here and set their result before calling Accept or Cancel.
    /// </summary>
    protected virtual void OnCommand(int id, string automationId)
    {
        if (id == 1)
        {
            Accept();
        }
        else if (id == 2)
        {
            Cancel();
        }
    }

    /// <summary>Moves focus to the first tab stop. Called when the dialog has loaded.</summary>
    protected override void OnDialogLoaded()
    {
        FocusFirstTabStop();
    }

    /// <summary>
    /// Hook for flow mode. Runs after the dialog knows its DPI and work area and before the content
    /// is measured. Message boxes use it to bound the text width.
    /// </summary>
    protected virtual void PrepareFlowLayout(ClassicFlowContext context)
    {
    }

    /// <summary>
    /// Sizes the client area from the template, or from the measured content in flow mode, and
    /// centers the window on the owner.
    /// </summary>
    protected override void ApplySizeAndPosition(nint ownerHwnd)
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        uint dpi = Win32Dialog.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = Win32Dialog.GetDpiForWindow(ownerHwnd);
        }

        ApplyModalFrameStyle(hwnd);

        _metrics = ClassicDialogMetrics.Create(_font.Face, _font.PointSize, dpi);
        _metricsReady = true;
        double scale = _metrics.Scale;
        RectInt32 workArea = WorkAreaFor(ownerHwnd);

        int clientWidth;
        int clientHeight;
        if (_template is not null && _canvas is not null)
        {
            clientWidth = _metrics.ToPixelsX(_template.WidthDlu);
            clientHeight = _metrics.ToPixelsY(_template.HeightDlu);
            LayoutTemplate(_metrics);
            _canvas.Width = clientWidth / scale;
            _canvas.Height = clientHeight / scale;
        }
        else
        {
            PrepareFlowLayout(new ClassicFlowContext(_metrics, scale, workArea));
            _root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Windows.Foundation.Size desired = _root.DesiredSize;
            clientWidth = (int)Math.Ceiling(desired.Width * scale);
            clientHeight = (int)Math.Ceiling(desired.Height * scale);
            clientWidth = Math.Clamp(clientWidth, 1, Math.Max(1, workArea.Width));
            clientHeight = Math.Clamp(clientHeight, 1, Math.Max(1, workArea.Height));
        }

        AppWindow.ResizeClient(new SizeInt32(Math.Max(1, clientWidth), Math.Max(1, clientHeight)));
        PlaceOnOwner(ownerHwnd, workArea);
    }

    /// <summary>Enter, Escape, Tab, and Alt+letter. Other keys reach the focused control.</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // A control that handled the key owns it, except Enter on a focused button: a button marks Enter handled
        // without raising Click, so the dialog still answers it (the case below).
        if (e.Handled && e.Key != VirtualKey.Enter)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Menu:
                SetCueVisible(true);
                return;

            case VirtualKey.Tab:
                e.Handled = true;
                MoveFocus(IsShiftDown());
                return;

            case VirtualKey.Enter:
                if (FocusedIsMultilineEdit())
                {
                    return;
                }

                if (e.Handled && FocusedHosted()?.Definition.Kind != ClassicControlKind.Button)
                {
                    return;
                }

                e.Handled = true;
                InvokeEnterButton();
                return;

            case VirtualKey.Escape:
                e.Handled = true;
                InvokeEscapeButton();
                return;
        }

        if (IsAltDown() && TryMnemonic(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnRootKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Menu)
        {
            SetCueVisible(false);
        }
    }

    private void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_windowActive)
        {
            SetCueVisible(false);
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        _root.Loaded -= OnRootLoaded;
        try
        {
            OnDialogLoaded();
            _initialFocus = FocusManager.GetFocusedElement(_root.XamlRoot) as FrameworkElement;
            if (_windowActive)
            {
                RestoreFocusInsideDialog();
            }
        }
        catch (Exception ex)
        {
            // A dialog that cannot finish loading closes and gives the owner back. It never
            // stays open with the owner disabled.
            UiDiagnostics.Warn?.Invoke("A classic dialog could not finish loading: " + ex.Message);
            Cancel();
        }

#if BWC_E2E
        PublishGlyphGeometry();
#endif
    }

#if BWC_E2E
    /// <summary>
    /// Publishes each check and radio glyph as the control's help text, in pixels relative to the control:
    /// "glyph-px=L,T,R,B;control-px=W,H". The E2E checks read it to test that the glyph lies inside the box.
    /// A failure here only leaves the help text unset. It never closes the dialog. Compiled only in the E2E
    /// flavor, so screen readers never announce this debug text in a ship build.
    /// </summary>
    private void PublishGlyphGeometry()
    {
        double scale = _metricsReady ? _metrics.Scale : 1.0;
        foreach (ClassicHostedControl hosted in _controls)
        {
            try
            {
                Windows.Foundation.Rect? glyph = hosted.Element switch
                {
                    ClassicCheckBox check => check.GlyphBoundsInControl(),
                    ClassicRadioButton radio => radio.GlyphBoundsInControl(),
                    _ => null,
                };
                if (glyph is not { } bounds || hosted.Element is not Control control)
                {
                    continue;
                }

                AutomationProperties.SetHelpText(
                    control,
                    $"glyph-px={Px(bounds.Left * scale)},{Px(bounds.Top * scale)},{Px(bounds.Right * scale)},{Px(bounds.Bottom * scale)};" +
                    $"control-px={Px(control.ActualWidth * scale)},{Px(control.ActualHeight * scale)}");
            }
            catch (Exception)
            {
                // The glyph is not in the template yet. The control keeps no help text.
            }
        }
    }

    private static int Px(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
#endif

    private ClassicHostedControl AddHosted(ClassicControlDefinition definition)
    {
        ValidateDefinition(definition);

        ClassicMnemonicText? mnemonic = IsMnemonicKind(definition.Kind) ? new ClassicMnemonicText(definition.Text) : null;
        if (mnemonic?.Mnemonic is char letter)
        {
            if (_mnemonics.TryGetValue(letter, out string? existing))
            {
                throw new InvalidOperationException(
                    $"The mnemonic '{letter}' is used by both '{existing}' and '{definition.AutomationId}'.");
            }

            _mnemonics[letter] = definition.AutomationId;
        }

        ClassicHostedControl hosted = ClassicControlFactory.Create(definition, mnemonic, _styles, _font, OnCommand);
        hosted.Index = _controls.Count;
        _controls.Add(hosted);
        _byAutomationId[definition.AutomationId] = hosted;
        return hosted;
    }

    private void ValidateTemplate(ClassicDialogDefinition template)
    {
        if (template.WidthDlu <= 0 || template.HeightDlu <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(template), "A dialog template needs a positive size.");
        }

        int defaults = template.Controls.Count(control => control.IsDefault);
        if (defaults > 1)
        {
            throw new InvalidOperationException($"'{template.SourceTemplate}' declares {defaults} default buttons.");
        }
    }

    private static void ValidateDefinition(ClassicControlDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(definition.AutomationId);
        ArgumentNullException.ThrowIfNull(definition.Text);

        if (definition.Cx < 0 || definition.Cy < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition), "A control needs a non-negative size.");
        }
    }

    private static bool IsMnemonicKind(ClassicControlKind kind) =>
        kind is ClassicControlKind.Label or ClassicControlKind.Button or ClassicControlKind.CheckBox or
            ClassicControlKind.RadioButton;

    private void LayoutTemplate(ClassicDialogMetrics metrics)
    {
        double scale = metrics.Scale;
        foreach (ClassicHostedControl hosted in _controls)
        {
            ClassicControlDefinition definition = hosted.Definition;
            int left = metrics.ToPixelsX(definition.X);
            int right = metrics.ToPixelsX(definition.X + definition.Cx);
            int top = metrics.ToPixelsY(definition.Y);
            int bottom = metrics.ToPixelsY(definition.Y + definition.Cy);

            Canvas.SetLeft(hosted.Element, left / scale);
            Canvas.SetTop(hosted.Element, top / scale);
            double width = (right - left) / scale;
            double height = (bottom - top) / scale;
            if (hosted.Element is ClassicFrame frame)
            {
                frame.Arrange(width, height, metrics.BaseY / scale, metrics.ToDipX(6), 1.0 / scale);
            }
            else if (hosted.Element is FrameworkElement element)
            {
                element.Width = width;
                element.Height = height;
            }

            // A check box or radio label stays inside the control: the glyph and its 4 DIP gap take the left part.
            if (hosted.TextView is { } view &&
                (hosted.Element is ClassicCheckBox || hosted.Element is ClassicRadioButton))
            {
                view.Width = Math.Max(0, width - GlyphAdvanceDip);
                view.TextTrimming = TextTrimming.CharacterEllipsis;
            }
        }
    }

    private void ApplyModalFrameStyle(nint hwnd)
    {
        // WS_EX_DLGMODALFRAME removes the title-bar icon and keeps the classic caption and system menu.
        nint extended = Win32Dialog.GetWindowLongPtr(hwnd, Win32Dialog.GWL_EXSTYLE);
        _ = Win32Dialog.SetWindowLongPtr(hwnd, Win32Dialog.GWL_EXSTYLE, extended | Win32Dialog.WS_EX_DLGMODALFRAME);
        _ = Win32Dialog.SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            Win32Dialog.SWP_NOMOVE | Win32Dialog.SWP_NOSIZE | Win32Dialog.SWP_NOZORDER |
                Win32Dialog.SWP_NOACTIVATE | Win32Dialog.SWP_FRAMECHANGED);
    }

    private static RectInt32 WorkAreaFor(nint ownerHwnd)
    {
        if (ownerHwnd != 0)
        {
            DisplayArea? owner = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(ownerHwnd), DisplayAreaFallback.Nearest);
            if (owner is not null)
            {
                return owner.WorkArea;
            }
        }

        DisplayArea? primary = DisplayArea.Primary;
        return primary?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);
    }

    /// <summary>
    /// Centers the window on the owner and clamps it to the work area. The owner is the center
    /// reference, so a dialog that is larger than the work area is pinned to its top-left corner.
    /// </summary>
    private void PlaceOnOwner(nint ownerHwnd, RectInt32 workArea)
    {
        SizeInt32 outer = AppWindow.Size;
        int left;
        int top;
        if (Win32Dialog.TryGetWindowRect(ownerHwnd, out Win32Dialog.WindowRect owner) && owner.Width > 0 && owner.Height > 0)
        {
            left = owner.Left + (owner.Width - outer.Width) / 2;
            top = owner.Top + (owner.Height - outer.Height) / 2;
        }
        else
        {
            left = workArea.X + (workArea.Width - outer.Width) / 2;
            top = workArea.Y + (workArea.Height - outer.Height) / 2;
        }

        left = Math.Clamp(left, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - outer.Width));
        top = Math.Clamp(top, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - outer.Height));
        AppWindow.Move(new PointInt32(left, top));
    }

    private bool TryMnemonic(VirtualKey key)
    {
        if (key < VirtualKey.A || key > VirtualKey.Z)
        {
            return false;
        }

        char letter = (char)('A' + ((int)key - (int)VirtualKey.A));
        if (!_mnemonics.TryGetValue(letter, out string? automationId) ||
            !_byAutomationId.TryGetValue(automationId, out ClassicHostedControl? hosted))
        {
            return false;
        }

        ActivateMnemonic(hosted);
        return true;
    }

    private void ActivateMnemonic(ClassicHostedControl hosted)
    {
        switch (hosted.Definition.Kind)
        {
            case ClassicControlKind.Label:
                // Win32 gives a static label's mnemonic to the next tab stop after the label.
                FocusNextTabStopAfter(hosted.Index);
                return;

            case ClassicControlKind.Button:
                _ = hosted.Element.Focus(FocusState.Keyboard);
                OnCommand(hosted.Definition.Id, hosted.Definition.AutomationId);
                return;

            case ClassicControlKind.CheckBox when hosted.Element is CheckBox check:
                _ = check.Focus(FocusState.Keyboard);
                check.IsChecked = check.IsChecked != true;
                return;

            case ClassicControlKind.RadioButton when hosted.Element is RadioButton radio:
                _ = radio.Focus(FocusState.Keyboard);
                radio.IsChecked = true;
                return;
        }
    }

    private void InvokeEnterButton()
    {
        // Enter on a focused push button presses that button. Anywhere else it presses the default button.
        ClassicHostedControl? focused = FocusedHosted();
        if (focused?.Definition.Kind == ClassicControlKind.Button)
        {
            OnCommand(focused.Definition.Id, focused.Definition.AutomationId);
            return;
        }

        ClassicHostedControl? fallback = _controls.FirstOrDefault(hosted => hosted.Definition.IsDefault);
        if (fallback is not null)
        {
            OnCommand(fallback.Definition.Id, fallback.Definition.AutomationId);
        }
    }

    private void InvokeEscapeButton()
    {
        ClassicHostedControl? cancel = _controls.FirstOrDefault(hosted =>
            hosted.Definition.Kind == ClassicControlKind.Button && hosted.Definition.Id == 2);
        if (cancel is not null)
        {
            OnCommand(cancel.Definition.Id, cancel.Definition.AutomationId);
            return;
        }

        // A dialog without a cancel button, such as an OK-only message, treats Escape as OK.
        ClassicHostedControl? fallback = _controls.FirstOrDefault(hosted => hosted.Definition.IsDefault);
        if (fallback is not null)
        {
            OnCommand(fallback.Definition.Id, fallback.Definition.AutomationId);
        }
    }

    private bool FocusedIsMultilineEdit() =>
        FocusManager.GetFocusedElement(_root.XamlRoot) is TextBox { AcceptsReturn: true };

    private ClassicHostedControl? FocusedHosted()
    {
        object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        return _controls.FirstOrDefault(hosted => ReferenceEquals(hosted.Element, focused));
    }

    private void MoveFocus(bool backward)
    {
        List<ClassicHostedControl> stops = TabStops();
        if (stops.Count == 0)
        {
            return;
        }

        object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        int current = stops.FindIndex(hosted => ReferenceEquals(hosted.Element, focused));
        int next = current < 0
            ? (backward ? stops.Count - 1 : 0)
            : (current + (backward ? -1 : 1) + stops.Count) % stops.Count;
        _ = stops[next].Element.Focus(FocusState.Keyboard);
    }

    private void FocusNextTabStopAfter(int index)
    {
        List<ClassicHostedControl> stops = TabStops();
        if (stops.Count == 0)
        {
            return;
        }

        ClassicHostedControl next = stops.FirstOrDefault(hosted => hosted.Index > index) ?? stops[0];
        _ = next.Element.Focus(FocusState.Keyboard);
    }

    private void FocusFirstTabStop()
    {
        List<ClassicHostedControl> stops = TabStops();
        if (stops.Count > 0)
        {
            _ = stops[0].Element.Focus(FocusState.Programmatic);
        }
    }

    private List<ClassicHostedControl> TabStops() =>
        _controls.Where(hosted => hosted.Definition.TabStop && hosted.Element is not Control { IsEnabled: false }).ToList();

    private void SetCueVisible(bool visible)
    {
        if (_cueVisible == visible)
        {
            return;
        }

        _cueVisible = visible;
        foreach (ClassicHostedControl hosted in _controls)
        {
            hosted.RenderMnemonic(visible);
        }
    }

    private static bool IsShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsAltDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private Style? StyleFor(string key) =>
        _styles.TryGetValue(key, out object? value) ? value as Style : null;
}

/// <summary>The information a flow layout needs before it measures: metrics, scale, and the owner's work area.</summary>
/// <param name="Metrics">Base units at the window DPI.</param>
/// <param name="Scale">Window DPI divided by 96.</param>
/// <param name="WorkArea">The work area of the owner's display, in pixels.</param>
public readonly record struct ClassicFlowContext(ClassicDialogMetrics Metrics, double Scale, RectInt32 WorkArea);

/// <summary>A font for a classic dialog: a face name and a size in points.</summary>
/// <param name="Face">The face name. "MS Shell Dlg" is resolved through FontSubstitutes.</param>
/// <param name="PointSize">The size in points.</param>
public readonly record struct ClassicFontSpec(string Face, int PointSize)
{
    /// <summary>The message box font: Segoe UI 9 pt, as the Win32 MessageBox uses.</summary>
    public static ClassicFontSpec MessageFont { get; } = new("Segoe UI", 9);

    /// <summary>The size in device independent pixels (96 DPI points).</summary>
    public double DipSize => PointSize * 96.0 / 72.0;
}

