using System.Globalization;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The classic numeric up-down: a text box with a column of two spin buttons. Up and Down on the text box and the buttons
/// change the value by <see cref="Step"/>, clamped to <see cref="Minimum"/> and <see cref="Maximum"/> for signed callers.
/// The policy-number path keeps an unsigned range and step. Typed text is never changed: <see cref="Text"/> returns it as
/// typed and <see cref="TryGetValue"/> says whether it fits a signed <see cref="long"/>, so the caller decides what to do
/// with text that is not (docs/failure-modes/classic-dialog.md, section 14).
/// </summary>
public sealed class ClassicSpinBox : ContentControl, IClassicFocusHost
{
    /// <summary>Width of the button column inside the frame.</summary>
    internal const double ButtonColumnDip = 15;

    private readonly TextBox _text;
    private readonly RepeatButton _up;
    private readonly RepeatButton _down;
    private readonly (ulong Minimum, ulong Maximum, ulong Step)? _unsignedRange;
    private readonly long _minimum;
    private readonly long _maximum;
    private readonly long _step;
    private long _lastValid;
    private ulong _lastUnsignedValid;

    /// <param name="minimum">Smallest value.</param>
    /// <param name="maximum">Largest value, at least <paramref name="minimum"/>.</param>
    /// <param name="step">Change of one key press or button press. Positive.</param>
    /// <param name="font">The dialog font.</param>
    /// <param name="styles">The classic dialog styles.</param>
    public ClassicSpinBox(long minimum, long maximum, long step, ClassicFontSpec font, ResourceDictionary styles)
        : this(minimum, maximum, step, font, styles, null)
    {
    }

    /// <summary>Creates the policy-number version of the control, whose bounds and step use the full unsigned range.</summary>
    internal static ClassicSpinBox CreateUnsigned(
        ulong minimum,
        ulong maximum,
        ulong step,
        ClassicFontSpec font,
        ResourceDictionary styles) =>
        new(0, 0, 1, font, styles, (minimum, maximum, step));

    private ClassicSpinBox(
        long minimum,
        long maximum,
        long step,
        ClassicFontSpec font,
        ResourceDictionary styles,
        (ulong Minimum, ulong Maximum, ulong Step)? unsignedRange)
    {
        ArgumentNullException.ThrowIfNull(styles);
        _unsignedRange = unsignedRange;
        if (unsignedRange is { } range)
        {
            if (range.Minimum > range.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(minimum), range.Minimum, "The minimum is above the maximum.");
            }

            if (range.Step == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step), range.Step, "The step must be positive.");
            }

            _lastValid = 0;
            _lastUnsignedValid = Math.Clamp(0UL, range.Minimum, range.Maximum);
        }
        else
        {
            if (minimum > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "The minimum is above the maximum.");
            }

            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, 0);
            _lastValid = Math.Clamp(0L, minimum, maximum);
        }

        _minimum = minimum;
        _maximum = maximum;
        _step = step;

        _text = new TextBox
        {
            Style = ClassicControlFactory.StyleOf(styles, "BwcClassicBareTextBoxStyle"),
            Background = BwcBrushes.Window,
            Foreground = BwcBrushes.WindowText,
            FontFamily = new FontFamily(font.Face),
            FontSize = font.DipSize,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            Text = _unsignedRange is not null
                ? _lastUnsignedValid.ToString(CultureInfo.InvariantCulture)
                : _lastValid.ToString(CultureInfo.InvariantCulture),
        };
        Bwc.UI.Menu.TextEditMenu.Attach(_text);
        _text.PreviewKeyDown += OnPreviewKeyDown;
        _text.TextChanged += OnTextChanged;

        _up = CreateButton(styles, up: true);
        _down = CreateButton(styles, up: false);

        var buttons = new Grid { Width = ButtonColumnDip };
        buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        buttons.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_up, 0);
        Grid.SetRow(_down, 1);
        buttons.Children.Add(_up);
        buttons.Children.Add(_down);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_text, 0);
        Grid.SetColumn(buttons, 1);
        layout.Children.Add(_text);
        layout.Children.Add(buttons);

        // The frame is the 1 DIP edit border, drawn inside the template rectangle like every classic edit.
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
    }

    /// <summary>Smallest signed value. This property is unavailable for an unsigned policy-number spinner.</summary>
    public long Minimum => _unsignedRange is null
        ? _minimum
        : throw new InvalidOperationException("This spinner uses an unsigned range.");

    /// <summary>Largest signed value. This property is unavailable for an unsigned policy-number spinner.</summary>
    public long Maximum => _unsignedRange is null
        ? _maximum
        : throw new InvalidOperationException("This spinner uses an unsigned range.");

    /// <summary>
    /// Signed change of one key or button press. This property is unavailable for an unsigned policy-number spinner.
    /// </summary>
    public long Step => _unsignedRange is null
        ? _step
        : throw new InvalidOperationException("This spinner uses an unsigned step.");

    /// <summary>The text box. Tests and callers that attach their own handlers use it.</summary>
    public TextBox TextBox => _text;

    /// <summary>The text exactly as typed or set. It is never clamped or reformatted by the control.</summary>
    public string Text
    {
        get => _text.Text ?? string.Empty;
        set => _text.Text = value ?? string.Empty;
    }

    /// <summary>
    /// The signed value when the text is valid and fits in a <see cref="long"/>, otherwise the last valid signed value. In
    /// unsigned mode, reading it throws when the value does not fit in a signed long; use <see cref="Text"/> for that path.
    /// Use <see cref="TryGetValue"/> to distinguish invalid text from a valid signed value.
    /// </summary>
    public long Value
    {
        get
        {
            if (_unsignedRange is not null)
            {
                ulong value = TryGetUnsignedValue(out ulong parsed) ? parsed : _lastUnsignedValid;
                return value <= long.MaxValue
                    ? (long)value
                    : throw new InvalidOperationException("The unsigned spinner value does not fit in a signed long.");
            }

            return TryGetValue(out long signedValue) ? signedValue : _lastValid;
        }
        set
        {
            if (_unsignedRange is { } range)
            {
                ulong unsignedValue = value <= 0 ? 0UL : (ulong)value;
                ShowUnsignedValue(Math.Clamp(unsignedValue, range.Minimum, range.Maximum));
                return;
            }

            ShowValue(Math.Clamp(value, Minimum, Maximum));
        }
    }

    /// <summary>Raised after the text changes, by typing, keys, buttons or <see cref="Text"/>.</summary>
    public event EventHandler? TextChanged;

    /// <summary>The text box is the element that takes focus.</summary>
    FrameworkElement IClassicFocusHost.FocusTarget => _text;

    /// <summary>
    /// True and the value when the text is a whole number (optional leading sign, digits) in the signed range. In unsigned
    /// mode it is false when the value does not fit in a <see cref="long"/>. False for empty, non-numeric or out-of-range
    /// text. Never throws.
    /// </summary>
    public bool TryGetValue(out long value)
    {
        if (_unsignedRange is { } unsignedRange)
        {
            if (TryParseUnsigned(Text, out ulong unsignedValue) && unsignedValue <= long.MaxValue &&
                unsignedValue >= unsignedRange.Minimum && unsignedValue <= unsignedRange.Maximum)
            {
                value = (long)unsignedValue;
                return true;
            }

            value = 0;
            return false;
        }

        if (TryParse(Text, out value) && value >= Minimum && value <= Maximum)
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// One step up (<paramref name="direction"/> 1) or down (-1). Text that is not a number starts from the last valid value.
    /// </summary>
    public void StepBy(int direction)
    {
        if (_unsignedRange is { } unsignedRange)
        {
            ulong currentUnsigned = TryParseUnsigned(Text, out ulong parsedUnsigned) ? parsedUnsigned : _lastUnsignedValid;
            currentUnsigned = Math.Clamp(currentUnsigned, unsignedRange.Minimum, unsignedRange.Maximum);
            Int128 unsignedNext = (Int128)currentUnsigned + (direction >= 0 ? (Int128)unsignedRange.Step : -(Int128)unsignedRange.Step);
            ShowUnsignedValue((ulong)Int128.Clamp(unsignedNext, (Int128)unsignedRange.Minimum, (Int128)unsignedRange.Maximum));
            return;
        }

        long current = TryParse(Text, out long parsed) ? parsed : _lastValid;
        current = Math.Clamp(current, Minimum, Maximum);
        Int128 next = (Int128)current + (direction >= 0 ? (Int128)Step : -(Int128)Step);
        ShowValue((long)Int128.Clamp(next, Minimum, Maximum));
    }

    internal bool TryGetUnsignedValue(out ulong value)
    {
        if (_unsignedRange is { } range && TryParseUnsigned(Text, out ulong parsed) &&
            parsed >= range.Minimum && parsed <= range.Maximum)
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private double AutomationMinimum => _unsignedRange is { } range ? range.Minimum : Minimum;

    private double AutomationMaximum => _unsignedRange is { } range ? range.Maximum : Maximum;

    private double AutomationStep => _unsignedRange is { } range ? range.Step : Step;

    private double AutomationValue => _unsignedRange is not null
        ? (double)(TryGetUnsignedValue(out ulong value) ? value : _lastUnsignedValid)
        : Value;

    private void SetAutomationValue(double value)
    {
        if (_unsignedRange is not { } range)
        {
            Value = (long)Math.Round(value);
            return;
        }

        if (!double.IsFinite(value))
        {
            throw new OverflowException("The automation value is not finite.");
        }

        double rounded = Math.Round(value);
        if (rounded >= (double)range.Maximum)
        {
            ShowUnsignedValue(range.Maximum);
        }
        else if (rounded <= (double)range.Minimum)
        {
            ShowUnsignedValue(range.Minimum);
        }
        else
        {
            decimal exact = (decimal)rounded;
            ulong unsignedValue = (ulong)exact;
            ShowUnsignedValue(Math.Clamp(unsignedValue, range.Minimum, range.Maximum));
        }
    }

    /// <summary>Sets the automation id and name of the control and its parts.</summary>
    internal void SetIdentity(string automationId, string name)
    {
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(_text, automationId + ".Text");
        AutomationProperties.SetName(_text, name);
        AutomationProperties.SetAutomationId(_up, automationId + ".Up");
        AutomationProperties.SetName(_up, "Increase");
        AutomationProperties.SetAutomationId(_down, automationId + ".Down");
        AutomationProperties.SetName(_down, "Decrease");
    }

    /// <summary>Makes the text box a tab stop, or not, as the template says. The buttons never are.</summary>
    internal void SetTabStop(bool tabStop) => _text.IsTabStop = tabStop;

    protected override AutomationPeer OnCreateAutomationPeer() => new ClassicSpinBoxPeer(this);

    private RepeatButton CreateButton(ResourceDictionary styles, bool up)
    {
        var arrow = new Polygon
        {
            Fill = BwcBrushes.WindowText,
            Width = 8,
            Height = 4,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var points = new PointCollection();
        foreach ((double x, double y) in up ? new[] { (0.0, 4.0), (4.0, 0.0), (8.0, 4.0) } : new[] { (0.0, 0.0), (8.0, 0.0), (4.0, 4.0) })
        {
            points.Add(new Windows.Foundation.Point(x, y));
        }

        arrow.Points = points;
        var button = new RepeatButton
        {
            Style = ClassicControlFactory.StyleOf(styles, "BwcClassicSpinButtonStyle"),
            Content = arrow,
            IsTabStop = false,
            Delay = 400,
            Interval = 60,
        };
        button.Click += (_, _) =>
        {
            StepBy(up ? 1 : -1);
            _ = _text.Focus(FocusState.Programmatic);
        };
        return button;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }

        e.Handled = true;
        StepBy(e.Key == VirtualKey.Up ? 1 : -1);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_unsignedRange is not null)
        {
            if (TryGetUnsignedValue(out ulong unsignedValue))
            {
                _lastUnsignedValid = unsignedValue;
            }
        }
        else if (TryGetValue(out long value))
        {
            _lastValid = value;
        }

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowValue(long value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(_text.Text, text, StringComparison.Ordinal))
        {
            _text.Text = text;
        }

        _text.SelectAll();
    }

    private void ShowUnsignedValue(ulong value)
    {
        _lastUnsignedValid = value;
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(_text.Text, text, StringComparison.Ordinal))
        {
            _text.Text = text;
        }

        _text.SelectAll();
    }

    private static bool TryParse(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool TryParseUnsigned(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>The spin box as UI Automation sees it: a Spinner with a range and a text value.</summary>
    private sealed class ClassicSpinBoxPeer : FrameworkElementAutomationPeer, IRangeValueProvider, IValueProvider
    {
        private readonly ClassicSpinBox _owner;

        internal ClassicSpinBoxPeer(ClassicSpinBox owner)
            : base(owner)
        {
            _owner = owner;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => nameof(ClassicSpinBox);

        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.RangeValue or PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

        bool IRangeValueProvider.IsReadOnly => false;

        double IRangeValueProvider.LargeChange => _owner.AutomationStep;

        double IRangeValueProvider.SmallChange => _owner.AutomationStep;

        double IRangeValueProvider.Maximum => _owner.AutomationMaximum;

        double IRangeValueProvider.Minimum => _owner.AutomationMinimum;

        double IRangeValueProvider.Value => _owner.AutomationValue;

        // Both providers name SetValue; the overloads differ by parameter type, so one public method serves each.
        public void SetValue(double value) => _owner.SetAutomationValue(value);

        bool IValueProvider.IsReadOnly => false;

        string IValueProvider.Value => _owner.Text;

        public void SetValue(string value) => _owner.Text = value;
    }
}
