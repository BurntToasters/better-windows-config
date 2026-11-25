using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>What a field needs from the dialog that hosts the pane, and the commands of its buttons.</summary>
internal sealed class OptionsContext
{
    private readonly Dictionary<int, Action> _commands = [];
    private int _nextCommand = FirstCommandId;

    /// <summary>The first control id of a button of the pane. The template of the dialog uses ids below it.</summary>
    internal const int FirstCommandId = 2000;

    /// <summary>Creates a control through the dialog base (classic style, font, automation id).</summary>
    internal required Func<ClassicControlDefinition, FrameworkElement> Create { get; init; }

    /// <summary>Raised when a control changed (also for a change the pane made itself; the dialog compares the
    /// values).</summary>
    internal required Action Changed { get; init; }

    /// <summary>The window handle of the dialog, the owner of Show Contents.</summary>
    internal required Func<nint> OwnerHandle { get; init; }

    /// <summary>Registers the action of a button and returns the control id to give it.</summary>
    internal int Register(Action action)
    {
        int id = _nextCommand++;
        _commands[id] = action;
        return id;
    }

    /// <summary>Runs the action of a button of the pane; false when the id is not one of the pane.</summary>
    internal bool TryRun(int id)
    {
        if (!_commands.TryGetValue(id, out Action? action))
        {
            return false;
        }

        action();
        return true;
    }
}

/// <summary>
/// The sizes of the Options pane from the probe of classic (100 percent, DIP). The first label is 4 DIP from the pane edge:
/// 3 are the margin used here and 1 is the border of the pane.
/// </summary>
internal static class OptionsMetrics
{
    internal const double Left = 3;
    internal const double InlineGap = 6;
    internal const double LabelPadding = 6;
    internal const double ControlTop = 3;
    internal const double SpinWidth = 120;
    internal const double EditWidth = 220;
    internal const double EditHeight = 23;
    internal const double DropWidth = 143;
    internal const double DropHeight = 24;
    internal const double ButtonWidth = 58;
    internal const double ButtonHeight = 28;
    internal const double CheckHeight = 22;
    internal const double LabelHeight = 26;
    internal const double ListLabelHeight = 28;
    internal const double ListRowHeight = 34;
    internal const double InlineRowHeight = 29;
    internal const double CheckRowHeight = 28;
    internal const double ScrollBarReserve = 17;
    internal const double GlyphAdvance = 20;

    /// <summary>The length limit of a text element that gives none (the engine uses the same number).</summary>
    internal const ulong DefaultTextMaxLength = 1023;

    /// <summary>The height of one line of a multi-line box, and what its frame adds.</summary>
    internal const double LineHeight = 15;
    internal const double MultiFrame = 8;

    /// <summary>The size in dialog units of a placeholder: the layout sets the exact size in DIP.</summary>
    internal static int Dlu(double dip) => Math.Max(1, (int)Math.Round(dip * 2.0 / 3.0));
}

/// <summary>
/// One control of the Options pane, made from a presentation control: its parts, the value it reads and shows, its layout.
/// The failure modes are in docs/failure-modes/gpedit-options.md.
/// </summary>
internal abstract class OptionField
{
    protected OptionField(OptionsContext context, PresentationControl control)
    {
        Context = context;
        Control = control;
        Label = (control.Label ?? string.Empty).Trim();
    }

    /// <summary>The presentation control.</summary>
    internal PresentationControl Control { get; }

    /// <summary>The element the control edits, or null for a static text.</summary>
    internal PolicyElement? Element => Control.Element;

    /// <summary>The element id, or an empty string.</summary>
    internal string ElementId => Element?.Id ?? string.Empty;

    /// <summary>The label text without surrounding white space.</summary>
    internal string Label { get; }

    /// <summary>The label without its closing colon: what a message names as the field. The element id when there is
    /// no label.</summary>
    internal string FieldName
    {
        get
        {
            string name = Label.TrimEnd(':').TrimEnd();
            return name.Length > 0 ? name : ElementId;
        }
    }

    /// <summary>The controls to place on the canvas of the pane.</summary>
    internal abstract IReadOnlyList<FrameworkElement> Parts { get; }

    /// <summary>The control that is a tab stop (and owns the focus when anything inside it has it) and the element
    /// that takes the focus.</summary>
    internal virtual (FrameworkElement Owner, FrameworkElement Target)? TabStop => null;

    /// <summary>The items of a drop-down or combo box in display order, or null.</summary>
    internal virtual IReadOnlyList<string>? Items => null;

    /// <summary>Shows a value: the stored one, or the default of <see cref="PolicyDefaults.For"/>. Null shows none.</summary>
    internal abstract void Load(ElementValue? stored);

    /// <summary>The value of the control, or null for a static text.</summary>
    internal abstract ElementValue? Read();

    /// <summary>Takes what is stored now as the base of the next read (the list fields use it for the removed
    /// entries).</summary>
    internal virtual void Rebase(ElementValue? stored)
    {
    }

    /// <summary>Enables or disables the controls.</summary>
    internal abstract void SetEnabled(bool enabled);

    /// <summary>Places the parts from <paramref name="top"/> and returns the height of the row.</summary>
    internal abstract double Layout(double top, double availableWidth);

    /// <summary>Puts the focus on the control.</summary>
    internal virtual void Focus() => _ = TabStop?.Target.Focus(FocusState.Programmatic);

    protected OptionsContext Context { get; }

    /// <summary>Tells the pane that a control changed.</summary>
    protected void NotifyChanged() => Context.Changed();

    /// <summary>Creates a control of the dialog base with literal text. The control is not a tab stop of the base.</summary>
    protected FrameworkElement Create(ClassicControlKind kind, string automationId, string text) =>
        Context.Create(
            new ClassicControlDefinition(
                kind,
                0,
                automationId,
                text,
                0,
                0,
                OptionsMetrics.Dlu(OptionsMetrics.EditWidth),
                OptionsMetrics.Dlu(OptionsMetrics.EditHeight))
            {
                LiteralText = true,
            });

    /// <summary>Like <see cref="Create(ClassicControlKind, string, string)"/> for a button, which carries a command
    /// id.</summary>
    protected FrameworkElement CreateButton(string automationId, string text, Action action) =>
        Context.Create(
            new ClassicControlDefinition(
                ClassicControlKind.Button,
                Context.Register(action),
                automationId,
                text,
                0,
                0,
                OptionsMetrics.Dlu(OptionsMetrics.ButtonWidth),
                OptionsMetrics.Dlu(OptionsMetrics.ButtonHeight))
            {
                LiteralText = true,
            });

    /// <summary>The label of the field as a wrapping text block, or null when the presentation gives none.</summary>
    protected TextBlock? MakeLabel()
    {
        if (Label.Length == 0)
        {
            return null;
        }

        var block = (TextBlock)Create(ClassicControlKind.Label, "label" + Label, Label);
        block.TextWrapping = TextWrapping.Wrap;
        block.VerticalAlignment = VerticalAlignment.Center;
        return block;
    }

    /// <summary>The automation id of a control: the kind prefix of the classic form and the label (the element id
    /// without one).</summary>
    protected string IdFor(string prefix) => prefix + (Label.Length > 0 ? Label : ElementId);

    /// <summary>The size a text block needs at a width (infinity for its natural size).</summary>
    protected static Windows.Foundation.Size MeasureText(TextBlock block, double width)
    {
        block.Width = double.NaN;
        block.Height = double.NaN;
        block.Measure(new Windows.Foundation.Size(double.IsInfinity(width) ? double.PositiveInfinity : Math.Max(1, width), double.PositiveInfinity));
        return block.DesiredSize;
    }

    protected static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(1, width);
        element.Height = Math.Max(1, height);
    }

    /// <summary>
    /// A row with the label on the left and the control after it (number box, drop-down, list). A label that does not fit
    /// wraps in the room that is left.
    /// </summary>
    protected static double LayoutInline(
        TextBlock? label,
        FrameworkElement control,
        double controlWidth,
        double controlHeight,
        double labelHeight,
        double rowHeight,
        double top,
        double availableWidth)
    {
        double x = OptionsMetrics.Left;
        double labelBottom = 0;
        if (label is not null)
        {
            double room = Math.Max(40, availableWidth - x - OptionsMetrics.InlineGap - controlWidth);
            Windows.Foundation.Size natural = MeasureText(label, double.PositiveInfinity);
            double labelWidth = Math.Min(Math.Ceiling(natural.Width) + OptionsMetrics.LabelPadding, room);
            Windows.Foundation.Size wrapped = MeasureText(label, labelWidth);
            double height = Math.Max(labelHeight, Math.Ceiling(wrapped.Height));
            Place(label, x, top, labelWidth, height);
            labelBottom = height + OptionsMetrics.ControlTop;
            x += labelWidth + OptionsMetrics.InlineGap;
        }

        Place(control, x, top + OptionsMetrics.ControlTop, controlWidth, controlHeight);
        return Math.Max(rowHeight, labelBottom);
    }

    /// <summary>A row with the label above the control (text box, combo box, multi-line box).</summary>
    protected static double LayoutStacked(
        TextBlock? label,
        FrameworkElement control,
        double controlWidth,
        double controlHeight,
        double top,
        double availableWidth)
    {
        double y = top;
        double width = Math.Max(40, availableWidth - OptionsMetrics.Left);
        if (label is not null)
        {
            Windows.Foundation.Size wrapped = MeasureText(label, width);
            double height = Math.Max(OptionsMetrics.LabelHeight, Math.Ceiling(wrapped.Height));
            Place(label, OptionsMetrics.Left, y, width, height);
            y += height;
        }

        y += OptionsMetrics.ControlTop;
        Place(control, OptionsMetrics.Left, y, Math.Min(controlWidth, width), controlHeight);
        return y + controlHeight + OptionsMetrics.ControlTop - top;
    }
}
