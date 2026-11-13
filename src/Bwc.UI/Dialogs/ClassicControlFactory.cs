using Bwc.UI.Hex;
using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Bwc.UI.Dialogs;

/// <summary>A control created for a classic dialog, with the data the dialog needs to route input to it.</summary>
internal sealed class ClassicHostedControl
{
    internal ClassicHostedControl(
        ClassicControlDefinition definition,
        FrameworkElement element,
        ClassicMnemonicText? mnemonic,
        TextBlock? textView)
    {
        Definition = definition;
        Element = element;
        Mnemonic = mnemonic;
        TextView = textView;
    }

    /// <summary>The template row that produced this control.</summary>
    internal ClassicControlDefinition Definition { get; }

    /// <summary>The WinUI control.</summary>
    internal FrameworkElement Element { get; }

    /// <summary>The parsed text for labels, buttons, check boxes, and radio buttons; null otherwise.</summary>
    internal ClassicMnemonicText? Mnemonic { get; }

    /// <summary>The text block that shows <see cref="Mnemonic"/>, or null for controls with no text.</summary>
    internal TextBlock? TextView { get; }

    /// <summary>Position in template order. Tab order and mnemonic fallbacks use it.</summary>
    internal int Index { get; set; }

    /// <summary>Shows or hides the access-key underline.</summary>
    internal void RenderMnemonic(bool cue)
    {
        if (Mnemonic is not null && TextView is not null)
        {
            Mnemonic.Render(TextView.Inlines, cue);
        }
    }
}

/// <summary>
/// Creates the WinUI control for one template row. Each control gets the classic style for its
/// kind, the template font, an automation id, and its name. Radio buttons in one dialog share one
/// group, and check and radio buttons carry their mnemonic text.
/// </summary>
internal static class ClassicControlFactory
{
    private const string RadioGroupName = "BwcClassicGroup";

    internal static ClassicHostedControl Create(
        ClassicControlDefinition definition,
        ClassicMnemonicText? mnemonic,
        ResourceDictionary styles,
        ClassicFontSpec font,
        Action<int, string> onCommand)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(onCommand);

        FrameworkElement element;
        TextBlock? textView = null;
        string name = mnemonic?.Plain ?? definition.Text;

        switch (definition.Kind)
        {
            case ClassicControlKind.Label:
                textView = TextFor(RequireText(mnemonic, definition), font, styles, "BwcClassicLabelStyle");
                element = textView;
                break;

            case ClassicControlKind.Button:
                textView = TextFor(RequireText(mnemonic, definition), font, styles, null);
                var button = new Button
                {
                    Content = textView,
                    Style = StyleOf(styles, definition.IsDefault ? "BwcClassicDefaultButtonStyle" : "BwcClassicButtonStyle"),
                    FontFamily = new FontFamily(font.Face),
                    FontSize = font.DipSize,
                    IsTabStop = definition.TabStop,
                };
                button.Click += (_, _) => onCommand(definition.Id, definition.AutomationId);
                element = button;
                break;

            case ClassicControlKind.Edit:
                element = CreateEdit(definition, font, styles);
                break;

            case ClassicControlKind.CheckBox:
                textView = TextFor(RequireText(mnemonic, definition), font, styles, null);
                var check = new ClassicCheckBox
                {
                    Content = textView,
                    Style = StyleOf(styles, "BwcClassicCheckBoxStyle"),
                    FontFamily = new FontFamily(font.Face),
                    FontSize = font.DipSize,
                    IsChecked = definition.Checked,
                    IsTabStop = definition.TabStop,
                };
                element = check;
                break;

            case ClassicControlKind.RadioButton:
                textView = TextFor(RequireText(mnemonic, definition), font, styles, null);
                var radio = new ClassicRadioButton
                {
                    Content = textView,
                    GroupName = RadioGroupName,
                    Style = StyleOf(styles, "BwcClassicRadioButtonStyle"),
                    FontFamily = new FontFamily(font.Face),
                    FontSize = font.DipSize,
                    IsChecked = definition.Checked,
                    IsTabStop = definition.TabStop,
                };
                element = radio;
                break;

            case ClassicControlKind.GroupBox:
                element = new ClassicFrame(definition.Text, font, styles);
                break;

            case ClassicControlKind.ListBox:
                // A classic list is painted in COLOR_WINDOW, and its rows are one text line high, not a Fluent item.
                element = new ListBox
                {
                    Style = StyleOf(styles, "BwcClassicListBoxStyle"),
                    Background = BwcBrushes.Window,
                    Foreground = BwcBrushes.WindowText,
                    ItemContainerStyle = StyleOf(styles, "BwcClassicListBoxItemStyle"),
                    FontFamily = new FontFamily(font.Face),
                    FontSize = font.DipSize,
                    IsTabStop = definition.TabStop,
                    SelectionMode = definition.MultiSelect ? SelectionMode.Multiple : SelectionMode.Single,
                };
                break;

            case ClassicControlKind.HexEditor:
                // The HEX control is read-only until the dialog that owns it enables editing.
                element = new HexEditor
                {
                    IsReadOnly = true,
                    IsTabStop = definition.TabStop,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                break;

            case ClassicControlKind.ComboBox:
                element = new ComboBox
                {
                    Style = StyleOf(styles, "BwcClassicComboBoxStyle"),
                    FontFamily = new FontFamily(font.Face),
                    FontSize = font.DipSize,
                    IsTabStop = definition.TabStop,
                };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(definition), definition.Kind, "Unknown control kind.");
        }

        AutomationProperties.SetAutomationId(element, definition.AutomationId);
        AutomationProperties.SetName(element, name);
        if (element is Control control)
        {
            control.IsEnabled = !definition.Disabled;
        }

        return new ClassicHostedControl(definition, element, mnemonic, textView);
    }

    private static ClassicMnemonicText RequireText(ClassicMnemonicText? mnemonic, ClassicControlDefinition definition) =>
        mnemonic ?? throw new InvalidOperationException($"Control '{definition.AutomationId}' needs parsed text.");

    private static TextBlock TextFor(ClassicMnemonicText text, ClassicFontSpec font, ResourceDictionary styles, string? styleKey)
    {
        var view = new TextBlock
        {
            FontFamily = new FontFamily(font.Face),
            FontSize = font.DipSize,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = BwcBrushes.WindowText,
        };
        if (styleKey is not null)
        {
            view.Style = StyleOf(styles, styleKey);
        }

        text.Render(view.Inlines, cue: false);
        return view;
    }

    private static TextBox CreateEdit(ClassicControlDefinition definition, ClassicFontSpec font, ResourceDictionary styles)
    {
        var box = new TextBox
        {
            Style = StyleOf(styles, "BwcClassicTextBoxStyle"),
            // A classic edit is painted in COLOR_WINDOW (the window color), not in the dialog face.
            Background = BwcBrushes.Window,
            FontFamily = new FontFamily(font.Face),
            FontSize = font.DipSize,
            AcceptsReturn = definition.Multiline,
            TextWrapping = definition.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsReadOnly = definition.ReadOnly,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            IsTabStop = definition.TabStop,
        };

        if (definition.Multiline)
        {
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }

        return box;
    }

    internal static Style? StyleOf(ResourceDictionary styles, string key) =>
        styles.TryGetValue(key, out object? value) ? value as Style : null;
}

/// <summary>A check box. In the E2E flavor it publishes the position of its glyph for the geometry checks.</summary>
internal sealed class ClassicCheckBox : CheckBox
{
#if BWC_E2E
    /// <summary>The glyph rectangle relative to this control in DIP, or null before the template is applied.</summary>
    internal Windows.Foundation.Rect? GlyphBoundsInControl() => ClassicGlyph.BoundsIn(this, GetTemplateChild("Glyph"));
#endif
}

/// <summary>A radio button. In the E2E flavor it publishes the position of its glyph for the geometry checks.</summary>
internal sealed class ClassicRadioButton : RadioButton
{
#if BWC_E2E
    /// <summary>The glyph rectangle relative to this control in DIP, or null before the template is applied.</summary>
    internal Windows.Foundation.Rect? GlyphBoundsInControl() => ClassicGlyph.BoundsIn(this, GetTemplateChild("Glyph"));
#endif
}

#if BWC_E2E
/// <summary>Measures a template glyph relative to its control. Compiled only in the E2E flavor.</summary>
internal static class ClassicGlyph
{
    internal static Windows.Foundation.Rect? BoundsIn(UIElement control, object? glyph)
    {
        if (glyph is not FrameworkElement element || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        GeneralTransform transform = element.TransformToVisual(control);
        return transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }
}
#endif

/// <summary>
/// The etched frame of a classic group box. The frame is five line segments and the caption. The top
/// line stops 2 DIP before and after the caption, so the caption sits over the etched line with no
/// background box. The control is named by its automation id, so the frame has a UIA rectangle that
/// the E2E checks compare with the template.
/// </summary>
internal sealed class ClassicFrame : ContentControl
{
    private const double CaptionGapDip = 2;
    private readonly Canvas _layer = new();
    private readonly Rectangle _topLeft = new();
    private readonly Rectangle _topRight = new();
    private readonly Rectangle _left = new();
    private readonly Rectangle _right = new();
    private readonly Rectangle _bottom = new();
    private readonly TextBlock _caption;

    internal ClassicFrame(string caption, ClassicFontSpec font, ResourceDictionary styles)
    {
        ArgumentNullException.ThrowIfNull(caption);

        // The classic group box is drawn in COLOR_3DSHADOW, and its caption in the window text color.
        foreach (Rectangle line in new[] { _topLeft, _topRight, _left, _right, _bottom })
        {
            line.Fill = BwcBrushes.Shadow;
            _layer.Children.Add(line);
        }

        _caption = new TextBlock
        {
            Text = caption,
            Style = ClassicControlFactory.StyleOf(styles, "BwcClassicCaptionTextStyle"),
            FontFamily = new FontFamily(font.Face),
            FontSize = font.DipSize,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = BwcBrushes.WindowText,
        };
        _layer.Children.Add(_caption);
        Content = _layer;
        IsTabStop = false;
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>Sizes the frame and places the caption and the line segments. All values are in DIP.</summary>
    internal void Arrange(double width, double height, double captionHeight, double captionLeft, double borderThickness)
    {
        Width = width;
        Height = height;
        _layer.Width = width;
        _layer.Height = height;

        double lineTop = captionHeight / 2;
        double thickness = Math.Max(borderThickness, 0.5);
        double captionWidth = MeasuredCaptionWidth();
        double rightStart = captionLeft + captionWidth + CaptionGapDip;
        double sideHeight = Math.Max(0, height - lineTop);

        Place(_topLeft, 0, lineTop, Math.Max(0, captionLeft - CaptionGapDip), thickness);
        Place(_topRight, rightStart, lineTop, Math.Max(0, width - rightStart), thickness);
        Place(_left, 0, lineTop, thickness, sideHeight);
        Place(_right, Math.Max(0, width - thickness), lineTop, thickness, sideHeight);
        Place(_bottom, 0, Math.Max(0, height - thickness), width, thickness);

        _caption.Height = captionHeight;
        Canvas.SetLeft(_caption, captionLeft);
        Canvas.SetTop(_caption, 0);
    }

    private double MeasuredCaptionWidth()
    {
        _caption.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _caption.DesiredSize.Width;
    }

    private static void Place(Rectangle line, double left, double top, double width, double height)
    {
        line.Width = width;
        line.Height = height;
        Canvas.SetLeft(line, left);
        Canvas.SetTop(line, top);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
