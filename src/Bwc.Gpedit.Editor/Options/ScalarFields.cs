using System.Globalization;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>A static text of the presentation: a label with no value.</summary>
internal sealed class LabelField : OptionField
{
    private readonly TextBlock _label;

    internal LabelField(OptionsContext context, PresentationControl control)
        : base(context, control)
    {
        _label = (TextBlock)Create(ClassicControlKind.Label, "label" + Label, Label);
        _label.TextWrapping = TextWrapping.Wrap;
        Parts = [_label];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override void Load(ElementValue? stored)
    {
    }

    internal override ElementValue? Read() => null;

    internal override void SetEnabled(bool enabled)
    {
    }

    internal override double Layout(double top, double availableWidth)
    {
        double width = Math.Max(40, availableWidth - OptionsMetrics.Left);
        double height = Math.Max(15, Math.Ceiling(MeasureText(_label, width).Height));
        Place(_label, OptionsMetrics.Left, top + OptionsMetrics.ControlTop, width, height);
        return height + (2 * OptionsMetrics.ControlTop);
    }
}

/// <summary>A check box for a boolean element.</summary>
internal sealed class CheckField : OptionField
{
    private readonly CheckBox _box;
    private readonly TextBlock? _text;

    internal CheckField(OptionsContext context, PresentationControl control)
        : base(context, control)
    {
        _box = (CheckBox)Create(ClassicControlKind.CheckBox, IdFor("checkBox"), Label);
        _box.IsTabStop = true;
        _text = _box.Content as TextBlock;
        if (_text is not null)
        {
            _text.TextWrapping = TextWrapping.Wrap;
            _text.TextTrimming = TextTrimming.None;
        }

        _box.Checked += OnToggled;
        _box.Unchecked += OnToggled;
        Parts = [_box];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_box, _box);

    internal override void Load(ElementValue? stored) =>
        _box.IsChecked = stored is BooleanValue { Checked: true };

    internal override ElementValue? Read() => new BooleanValue(_box.IsChecked == true);

    internal override void SetEnabled(bool enabled) => _box.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth)
    {
        double room = Math.Max(40, availableWidth - OptionsMetrics.Left);
        double width = room;
        double height = OptionsMetrics.CheckHeight;
        if (_text is not null)
        {
            // The base has set the text to the width of the template at show time: the layout sets its own width.
            _text.Width = double.NaN;
            Windows.Foundation.Size natural = MeasureText(_text, double.PositiveInfinity);
            width = Math.Min(room, Math.Ceiling(natural.Width) + OptionsMetrics.GlyphAdvance + 4);
            Windows.Foundation.Size wrapped = MeasureText(_text, Math.Max(20, width - OptionsMetrics.GlyphAdvance));
            height = Math.Max(OptionsMetrics.CheckHeight, Math.Ceiling(wrapped.Height) + 4);
            _text.Width = double.NaN;
        }

        Place(_box, OptionsMetrics.Left, top + OptionsMetrics.ControlTop, width, height);
        return Math.Max(OptionsMetrics.CheckRowHeight, height + (2 * OptionsMetrics.ControlTop));
    }

    private void OnToggled(object sender, RoutedEventArgs e) => NotifyChanged();
}

/// <summary>A decimal or long decimal element: a number box with spin buttons, or a plain box with the same digit
/// filter.</summary>
internal sealed class NumberField : OptionField
{
    private readonly TextBlock? _label;
    private readonly Control _control;
    private readonly TextBox _box;
    private readonly ulong _min;
    private readonly ulong _max;
    private ulong _last;

    internal NumberField(OptionsContext context, PresentationControl control, DecimalElement element)
        : base(context, control)
    {
        ulong ceiling = element.Kind == PolicyElementKind.Decimal ? uint.MaxValue : ulong.MaxValue;
        _min = element.MinValue ?? 0;
        _max = Math.Max(_min, Math.Min(element.MaxValue ?? ceiling, ceiling));
        _last = _min;

        _label = MakeLabel();
        if (control.Spin != false)
        {
            ulong step = Math.Max(control.SpinStep ?? 1UL, 1UL);
            ClassicControlDefinition definition = new(
                ClassicControlKind.Spinner,
                0,
                IdFor("numericUpDown"),
                Label,
                0,
                0,
                OptionsMetrics.Dlu(OptionsMetrics.SpinWidth),
                OptionsMetrics.Dlu(OptionsMetrics.EditHeight))
            {
                UnsignedMinimum = _min,
                UnsignedMaximum = _max,
                UnsignedStep = step,
                LiteralText = true,
            };
            var spin = (ClassicSpinBox)Context.Create(definition);
            _control = spin;
            _box = spin.TextBox;
            spin.TextChanged += OnTextChanged;
        }
        else
        {
            _box = (TextBox)Create(ClassicControlKind.Edit, IdFor("textBox"), Label);
            _control = _box;
            _box.TextChanged += (_, _) => OnTextChanged(_box, EventArgs.Empty);
        }

        _box.IsTabStop = true;
        _box.BeforeTextChanging += OnBeforeTextChanging;
        Parts = _label is null ? [_control] : [_label, _control];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_control, _box);

    /// <summary>The number the control holds now (the last valid number while the text is empty).</summary>
    internal ulong Entered => ulong.TryParse(_box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ? value : _last;

    internal override void Load(ElementValue? stored)
    {
        ulong value = stored is NumberValue number ? number.Number : _min;
        _last = value;
        _box.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    internal override ElementValue? Read() => new NumberValue(Entered);

    /// <summary>Puts a number in the box (the reset to a limit after a validation message).</summary>
    internal void Reset(ulong value)
    {
        _last = value;
        _box.Text = value.ToString(CultureInfo.InvariantCulture);
        _box.SelectAll();
    }

    internal override void SetEnabled(bool enabled) => _control.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutInline(_label, _control, OptionsMetrics.SpinWidth, OptionsMetrics.EditHeight, OptionsMetrics.LabelHeight, OptionsMetrics.InlineRowHeight, top, availableWidth);

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (ulong.TryParse(_box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            _last = value;
        }

        NotifyChanged();
    }

    /// <summary>Digits only: a letter, a sign or a separator is refused as it is typed or pasted.</summary>
    private static void OnBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        string text = args.NewText;
        if (text.Length > 20 || !text.All(char.IsAsciiDigit) ||
            (text.Length > 0 && !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            args.Cancel = true;
        }
    }
}

/// <summary>A text element: a text box with the length limit of the element.</summary>
internal sealed class TextBoxField : OptionField
{
    private readonly TextBlock? _label;
    private readonly TextBox _box;

    internal TextBoxField(OptionsContext context, PresentationControl control, TextElement element)
        : base(context, control)
    {
        _label = MakeLabel();
        _box = (TextBox)Create(ClassicControlKind.Edit, IdFor("textBox"), Label);
        _box.IsTabStop = true;
        _box.MaxLength = (int)Math.Clamp(element.MaxLength ?? OptionsMetrics.DefaultTextMaxLength, 1UL, (ulong)int.MaxValue);
        _box.TextChanged += (_, _) => NotifyChanged();
        Parts = _label is null ? [_box] : [_label, _box];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_box, _box);

    internal override void Load(ElementValue? stored) => _box.Text = stored is TextValue text ? text.Text : string.Empty;

    internal override ElementValue? Read() => new TextValue(_box.Text ?? string.Empty);

    internal override void SetEnabled(bool enabled) => _box.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutStacked(_label, _box, OptionsMetrics.EditWidth, OptionsMetrics.EditHeight, top, availableWidth);
}

/// <summary>A text element shown as an editable combo box with suggestions.</summary>
internal sealed class ComboField : OptionField
{
    private readonly TextBlock? _label;
    private readonly ComboBox _combo;
    private readonly List<string> _items;
    private readonly int _maxLength;
    private TextBox? _inner;
    private string _text = string.Empty;

    internal ComboField(OptionsContext context, PresentationControl control, TextElement element)
        : base(context, control)
    {
        _label = MakeLabel();
        _combo = (ComboBox)Create(ClassicControlKind.ComboBox, IdFor("comboBox"), Label);
        _combo.IsTabStop = true;
        _combo.IsEditable = true;
        _items = control.NoSort == true
            ? [.. control.Suggestions]
            : [.. control.Suggestions.OrderBy(s => s, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))];
        _combo.ItemsSource = _items;
        _maxLength = (int)Math.Clamp(element.MaxLength ?? OptionsMetrics.DefaultTextMaxLength, 1UL, (ulong)int.MaxValue);
        _combo.Loaded += OnLoaded;
        _combo.TextSubmitted += (_, args) =>
        {
            _text = args.Text ?? string.Empty;
            NotifyChanged();
        };
        _combo.SelectionChanged += (_, _) => NotifyChanged();

        // Focus given to the combo box itself (by automation, or by a click on its frame) goes to its text box, where
        // the typing is.
        _combo.GotFocus += (_, e) =>
        {
            if (_inner is not null && ReferenceEquals(e.OriginalSource, _combo))
            {
                _ = _inner.Focus(FocusState.Programmatic);
            }
        };
        Parts = _label is null ? [_combo] : [_label, _combo];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_combo, _inner is null ? _combo : _inner);

    internal override IReadOnlyList<string>? Items => _items;

    internal override void Load(ElementValue? stored)
    {
        _text = stored is TextValue text ? text.Text : string.Empty;
        _combo.Text = _text;
        if (_inner is not null)
        {
            _inner.Text = _text;
        }
    }

    internal override ElementValue? Read() => new TextValue(_inner?.Text ?? _combo.Text ?? _text);

    internal override void SetEnabled(bool enabled) => _combo.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutStacked(_label, _combo, OptionsMetrics.EditWidth, OptionsMetrics.DropHeight, top, availableWidth);

    /// <summary>The text of an editable combo box is the text box inside it: its changes are the changes of the
    /// value.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_inner is not null)
        {
            return;
        }

        _inner = FindText(_combo);
        if (_inner is not null)
        {
            _inner.MaxLength = _maxLength;
            _inner.TextChanged += (_, _) => NotifyChanged();
            if (_inner.Text != _text && _text.Length > 0)
            {
                _inner.Text = _text;
            }
        }
    }

    private static TextBox? FindText(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox box)
            {
                return box;
            }

            if (FindText(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>A multiText element: a multi-line box. Its value is its lines.</summary>
internal sealed class MultiField : OptionField
{
    private readonly TextBlock? _label;
    private readonly TextBox _box;
    private readonly double _height;

    internal MultiField(OptionsContext context, PresentationControl control)
        : base(context, control)
    {
        _label = MakeLabel();
        ClassicControlDefinition definition = new(
            ClassicControlKind.Edit,
            0,
            IdFor("multiTextBox"),
            Label,
            0,
            0,
            OptionsMetrics.Dlu(OptionsMetrics.EditWidth),
            OptionsMetrics.Dlu(OptionsMetrics.EditHeight))
        {
            Multiline = true,
            LiteralText = true,
        };
        _box = (TextBox)Context.Create(definition);
        _box.IsTabStop = true;
        _box.TextChanged += (_, _) => NotifyChanged();
        double lines = Math.Clamp(control.DefaultHeight ?? 3UL, 1UL, 20UL);
        _height = (lines * OptionsMetrics.LineHeight) + OptionsMetrics.MultiFrame;
        Parts = _label is null ? [_box] : [_label, _box];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_box, _box);

    internal override void Load(ElementValue? stored) =>
        _box.Text = stored is MultiTextValue multi ? string.Join('\r', multi.Lines) : string.Empty;

    internal override ElementValue? Read()
    {
        string text = _box.Text ?? string.Empty;
        string[] lines = text.Length == 0 ? [] : text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r').Split('\r');
        return new MultiTextValue(lines);
    }

    internal override void SetEnabled(bool enabled) => _box.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutStacked(_label, _box, OptionsMetrics.EditWidth, _height, top, availableWidth);
}
