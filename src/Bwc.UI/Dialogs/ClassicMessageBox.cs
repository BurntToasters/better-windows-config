using Bwc.UI.Theme;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Streams;

namespace Bwc.UI.Dialogs;

/// <summary>The four stock icons a message box can show.</summary>
public enum ClassicMessageIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
}

/// <summary>The Win32 button sets. Each has a fixed order and fixed ids (IDOK 1, IDCANCEL 2, IDYES 6, IDNO 7).</summary>
public enum ClassicMessageButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

/// <summary>The button the user chose.</summary>
public enum ClassicMessageResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
}

/// <summary>
/// A classic message box. The icon sits at the left, the text wraps at three quarters of the work
/// area width, and the button row sits at the bottom right. The window is sized from its content,
/// centered on the owner, and clamped to the work area. The primary button is the default and
/// Escape presses the cancel button, or OK when the set has none.
/// </summary>
public class ClassicMessageBox : ClassicDialog
{
    private const int OkId = 1;
    private const int CancelId = 2;
    private const int YesId = 6;
    private const int NoId = 7;
    private const double MarginDip = 12;
    private const double IconDip = 32;
    private const double IconGapDip = 12;
    private const double ButtonGapDip = 6;
    private const int ButtonWidthDlu = 50;
    private const int ButtonHeightDlu = 14;
    private const int MinimumWidthDlu = 170;

    private readonly TextBlock _text;
    private readonly Grid _layout;
    private readonly List<Button> _buttons = [];
    private readonly Image? _stockIcon;
    private readonly ClassicStockIcon.Kind? _stockKind;

    /// <summary>
    /// Creates the message box. <paramref name="primaryText"/>, <paramref name="secondaryText"/>, and
    /// <paramref name="tertiaryText"/> replace the default labels. A label without an ampersand gets
    /// the mnemonic on its first letter, so every button can be reached with Alt.
    /// </summary>
    public ClassicMessageBox(
        string caption,
        string text,
        ClassicMessageIcon icon,
        ClassicMessageButtons buttons,
        string? primaryText = null,
        string? secondaryText = null,
        string? tertiaryText = null,
        string textAutomationId = "DialogMessage",
        bool selectableText = false)
        : base(caption, ClassicFontSpec.MessageFont)
    {
        ArgumentException.ThrowIfNullOrEmpty(caption);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(textAutomationId);

        _text = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            IsTextSelectionEnabled = selectableText,
            FontFamily = new FontFamily(ClassicFontSpec.MessageFont.Face),
            FontSize = ClassicFontSpec.MessageFont.DipSize,
            Foreground = BwcBrushes.WindowText,
        };
        AutomationProperties.SetAutomationId(_text, textAutomationId);
        AutomationProperties.SetName(_text, text);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = ButtonGapDip,
            Margin = new Thickness(0, MarginDip, 0, 0),
        };
        foreach (ButtonSpec spec in SpecsFor(buttons, primaryText, secondaryText, tertiaryText))
        {
            var definition = new ClassicControlDefinition(
                ClassicControlKind.Button,
                spec.Id,
                spec.AutomationId,
                WithMnemonic(spec.Label),
                0,
                0,
                0,
                0)
            {
                TabStop = true,
                IsDefault = spec.IsDefault,
            };
            var element = (Button)CreateControl(definition);
            _buttons.Add(element);
            buttonRow.Children.Add(element);
        }

        var contentRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        FrameworkElement iconElement = CreateIcon(icon);
        _stockKind = StockKindFor(icon);
        if (_stockKind is not null && iconElement is Grid iconGrid)
        {
            // The system stock icon replaces the vector fallback once it has been drawn at the window DPI.
            _stockIcon = new Image
            {
                Width = IconDip,
                Height = IconDip,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed,
            };
            iconGrid.Children.Add(_stockIcon);
        }

        Grid.SetColumn(iconElement, 0);
        Grid.SetColumn(_text, 1);
        _text.Margin = new Thickness(icon == ClassicMessageIcon.None ? 0 : IconGapDip, 0, 0, 0);
        contentRow.Children.Add(iconElement);
        contentRow.Children.Add(_text);

        _layout = new Grid
        {
            Padding = new Thickness(MarginDip),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
            },
        };
        Grid.SetRow(contentRow, 0);
        Grid.SetRow(buttonRow, 1);
        _layout.Children.Add(contentRow);
        _layout.Children.Add(buttonRow);
        SetFlowContent(_layout);
    }

    /// <summary>The button the user chose. None until the dialog closes.</summary>
    public ClassicMessageResult Result { get; private set; }

    /// <summary>Runs the button command and records the result before the dialog accepts or closes.</summary>
    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case OkId:
                Result = ClassicMessageResult.Ok;
                Accept();
                return;

            case YesId:
                Result = ClassicMessageResult.Yes;
                Accept();
                return;

            case NoId:
                Result = ClassicMessageResult.No;
                Cancel();
                return;

            case CancelId:
                Result = ClassicMessageResult.Cancel;
                Cancel();
                return;
        }
    }

    /// <summary>Focuses the default button, as the Win32 message box does.</summary>
    protected override void OnDialogLoaded()
    {
        Button? primary = _buttons.FirstOrDefault(button => button.IsTabStop);
        _ = (primary ?? _buttons.FirstOrDefault())?.Focus(FocusState.Programmatic);
    }

    /// <summary>Bounds the text width and sets the button sizes from the message font metrics.</summary>
    protected override void PrepareFlowLayout(ClassicFlowContext context)
    {
        if (_stockIcon is not null && _stockKind is { } kind)
        {
            _ = LoadStockIconAsync(kind, Math.Max(1, (int)Math.Round(IconDip * context.Scale)));
        }

        double workWidthDip = context.WorkArea.Width / context.Scale;
        double textMax = workWidthDip * 0.75 - 2 * MarginDip - IconDip - IconGapDip;
        _text.MaxWidth = Math.Max(120, textMax);

        double buttonHeight = context.Metrics.ToDipY(ButtonHeightDlu);
        double buttonMinWidth = context.Metrics.ToDipX(ButtonWidthDlu);
        foreach (Button button in _buttons)
        {
            button.Height = buttonHeight;
            button.MinWidth = buttonMinWidth;
        }

        _layout.MinWidth = context.Metrics.ToDipX(MinimumWidthDlu);
    }

    private static IEnumerable<ButtonSpec> SpecsFor(
        ClassicMessageButtons buttons,
        string? primaryText,
        string? secondaryText,
        string? tertiaryText) =>
        buttons switch
        {
            ClassicMessageButtons.Ok =>
            [
                new ButtonSpec(OkId, "DialogPrimary", primaryText ?? "OK", IsDefault: true),
            ],
            ClassicMessageButtons.OkCancel =>
            [
                new ButtonSpec(OkId, "DialogPrimary", primaryText ?? "OK", IsDefault: true),
                new ButtonSpec(CancelId, "DialogSecondary", secondaryText ?? "Cancel", IsDefault: false),
            ],
            ClassicMessageButtons.YesNo =>
            [
                new ButtonSpec(YesId, "DialogPrimary", primaryText ?? "Yes", IsDefault: true),
                new ButtonSpec(NoId, "DialogSecondary", secondaryText ?? "No", IsDefault: false),
            ],
            ClassicMessageButtons.YesNoCancel =>
            [
                new ButtonSpec(YesId, "DialogPrimary", primaryText ?? "Yes", IsDefault: true),
                new ButtonSpec(NoId, "DialogSecondary", secondaryText ?? "No", IsDefault: false),
                new ButtonSpec(CancelId, "DialogTertiary", tertiaryText ?? "Cancel", IsDefault: false),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Unknown button set."),
        };

    /// <summary>Adds an ampersand before the first letter or digit when the label has no mnemonic.</summary>
    internal static string WithMnemonic(string label)
    {
        if (label.Contains('&', StringComparison.Ordinal))
        {
            return label;
        }

        for (int i = 0; i < label.Length; i++)
        {
            if (char.IsLetterOrDigit(label[i]))
            {
                return label.Insert(i, "&");
            }
        }

        return label;
    }

    private static ClassicStockIcon.Kind? StockKindFor(ClassicMessageIcon icon) => icon switch
    {
        ClassicMessageIcon.Information => ClassicStockIcon.Kind.Information,
        ClassicMessageIcon.Warning => ClassicStockIcon.Kind.Warning,
        ClassicMessageIcon.Error => ClassicStockIcon.Kind.Error,
        ClassicMessageIcon.Question => ClassicStockIcon.Kind.Question,
        _ => null,
    };

    /// <summary>
    /// Draws the system stock icon at the window pixel size and shows it in place of the vector fallback.
    /// Any failure leaves the vector fallback visible.
    /// </summary>
    private async Task LoadStockIconAsync(ClassicStockIcon.Kind kind, int pixels)
    {
        byte[]? bytes = ClassicStockIcon.RenderBmp(kind, pixels);
        if (bytes is null || _stockIcon is null)
        {
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            try
            {
                writer.WriteBytes(bytes);
                _ = await writer.StoreAsync();
                _ = await writer.FlushAsync();
                writer.DetachStream();
            }
            finally
            {
                writer.Dispose();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            _stockIcon.Source = bitmap;
        }
        catch (Exception)
        {
            return;
        }

        if (_stockIcon.Parent is Grid holder)
        {
            foreach (UIElement child in holder.Children)
            {
                if (!ReferenceEquals(child, _stockIcon))
                {
                    child.Visibility = Visibility.Collapsed;
                }
            }
        }

        _stockIcon.Visibility = Visibility.Visible;
    }

    private static FrameworkElement CreateIcon(ClassicMessageIcon icon)
    {
        var holder = new Grid
        {
            Width = IconDip,
            Height = IconDip,
            VerticalAlignment = VerticalAlignment.Top,
        };

        switch (icon)
        {
            case ClassicMessageIcon.None:
                holder.Width = 0;
                holder.Height = 0;
                return holder;

            case ClassicMessageIcon.Warning:
                holder.Children.Add(new Polygon
                {
                    Points = new PointCollection
                    {
                        new(16, 2),
                        new(30, 30),
                        new(2, 30),
                    },
                    Fill = BrushFor("SystemFillColorCautionBrush", new SolidColorBrush(Colors.Gold)),
                    Stroke = BwcBrushes.WindowText,
                    StrokeThickness = 1,
                });
                holder.Children.Add(GlyphText("!", Colors.Black, 18));
                return holder;

            case ClassicMessageIcon.Error:
                holder.Children.Add(new Ellipse
                {
                    Fill = BrushFor("SystemFillColorCriticalBrush", new SolidColorBrush(Colors.Red)),
                });
                holder.Children.Add(GlyphText("X", Colors.White, 16));
                return holder;

            case ClassicMessageIcon.Question:
                holder.Children.Add(new Ellipse
                {
                    Fill = BrushFor("AccentFillColorDefaultBrush", new SolidColorBrush(Colors.RoyalBlue)),
                });
                holder.Children.Add(GlyphText("?", Colors.White, 20));
                return holder;

            case ClassicMessageIcon.Information:
                holder.Children.Add(new Ellipse
                {
                    Fill = BrushFor("AccentFillColorDefaultBrush", new SolidColorBrush(Colors.RoyalBlue)),
                });
                holder.Children.Add(GlyphText("i", Colors.White, 20));
                return holder;

            default:
                throw new ArgumentOutOfRangeException(nameof(icon), icon, "Unknown icon.");
        }
    }

    private static TextBlock GlyphText(string glyph, Windows.UI.Color color, double size) =>
        new()
        {
            Text = glyph,
            Foreground = new SolidColorBrush(color),
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>Looks up a theme brush by key and falls back to a brush that is always available.</summary>
    internal static Brush BrushFor(string key, Brush fallback)
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(key, out object? value) &&
            value is Brush brush)
        {
            return brush;
        }

        return fallback;
    }

    private readonly record struct ButtonSpec(int Id, string AutomationId, string Label, bool IsDefault);
}
