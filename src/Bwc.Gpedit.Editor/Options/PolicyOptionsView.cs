using System.Globalization;
using System.Text;
using Bwc.Gpedit.Editor.Dialog;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>A message for the first problem of an edit, and what to do with the controls once the user has closed
/// it.</summary>
/// <param name="Caption">The caption of the message box.</param>
/// <param name="Text">The text of the message box.</param>
/// <param name="AfterClose">Puts the focus on the offending control (and resets a number to its limit).</param>
internal sealed record OptionMessage(string Caption, string Text, Action AfterClose);

/// <summary>
/// The Options pane of one policy: one field for each control of its presentation, in presentation order, on the
/// canvas of the
/// pane. It loads the stored values or the defaults, gives the values that Apply writes, lays the fields out, and
/// describes the
/// problems of an edit with the messages of classic. The failure modes are in docs/failure-modes/gpedit-options.md.
/// </summary>
internal sealed class PolicyOptionsView
{
    private readonly PolicyDefinition _policy;
    private readonly Canvas _canvas;
    private readonly OptionsContext _context;
    private readonly List<OptionField> _fields = [];
    private readonly IReadOnlyDictionary<string, ElementValue> _defaults;
    private PolicyStateSnapshot? _snapshot;

    internal PolicyOptionsView(PolicyDefinition policy, Canvas canvas, OptionsContext context)
    {
        _policy = policy;
        _canvas = canvas;
        _context = context;
        _defaults = PolicyDefaults.For(policy);
        canvas.Children.Clear();
        foreach (PresentationControl control in policy.Presentation?.Controls ?? [])
        {
            OptionField? field = Build(control);
            if (field is null)
            {
                continue;
            }

            _fields.Add(field);
            foreach (FrameworkElement part in field.Parts)
            {
                canvas.Children.Add(part);
            }
        }
    }

    /// <summary>The fields in presentation order.</summary>
    internal IReadOnlyList<OptionField> Fields => _fields;

    private OptionField? Build(PresentationControl control)
    {
        PolicyElement? element = control.Element;
        switch (control.Kind)
        {
            case PresentationControlKind.Text:
                return new LabelField(_context, control);
            case PresentationControlKind.CheckBox when element is BooleanElement:
                return new CheckField(_context, control);
            case PresentationControlKind.DecimalTextBox or PresentationControlKind.LongDecimalTextBox when element is DecimalElement number:
                return new NumberField(_context, control, number);
            case PresentationControlKind.TextBox when element is TextElement text:
                return new TextBoxField(_context, control, text);
            case PresentationControlKind.ComboBox when element is TextElement text:
                return new ComboField(_context, control, text);
            case PresentationControlKind.MultiTextBox when element is MultiTextElement:
                return new MultiField(_context, control);
            case PresentationControlKind.DropdownList when element is EnumElement choice:
                return new DropField(_context, control, choice);
            case PresentationControlKind.ListBox when element is ListElement list:
                return new ListField(_context, control, list);
            default:
                UiLog($"The presentation control '{control.Kind}' of '{control.RefId}' in '{_policy.FullName}' does not fit its element and is not shown.");
                return null;
        }
    }

    /// <summary>
    /// Shows the stored values of the snapshot (only when its state is Enabled), or the defaults of
    /// <see cref="PolicyDefaults.For"/>, which hold what classic shows before anything is typed.
    /// </summary>
    internal void Load(PolicyStateSnapshot? snapshot)
    {
        _snapshot = snapshot;
        foreach (OptionField field in _fields)
        {
            field.Load(StoredFor(field));
        }
    }

    /// <summary>Takes the snapshot read after an Apply as the base of the list entries.</summary>
    internal void Rebase(PolicyStateSnapshot? snapshot)
    {
        _snapshot = snapshot;
        foreach (OptionField field in _fields)
        {
            field.Rebase(StoredFor(field));
        }
    }

    private ElementValue? StoredFor(OptionField field)
    {
        if (field.Element is not { } element)
        {
            return null;
        }

        if (_snapshot is { State: PolicyState.Enabled } snapshot && snapshot.Elements.TryGetValue(element.Id, out ElementValue? stored))
        {
            return stored;
        }

        return _defaults.TryGetValue(element.Id, out ElementValue? fallback) ? fallback : null;
    }

    /// <summary>
    /// The values that Apply writes: one for each field with an element. An element that has no field keeps the value that was
    /// read for it, so Apply never deletes a value the user cannot see.
    /// </summary>
    internal IReadOnlyDictionary<string, ElementValue> ReadValues()
    {
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        if (_snapshot is { State: PolicyState.Enabled } snapshot)
        {
            foreach ((string id, ElementValue value) in snapshot.Elements)
            {
                values[id] = value;
            }
        }

        foreach (OptionField field in _fields)
        {
            if (field.Read() is { } value && field.Element is { } element)
            {
                values[element.Id] = value;
            }
        }

        return values;
    }

    /// <summary>The values as one string that two calls can compare.</summary>
    internal string Fingerprint() => OptionValueJson.Serialize(ReadValues().OrderBy(p => p.Key, StringComparer.Ordinal));

    /// <summary>The value fingerprint of the current disk snapshot and defaults, without reading the live controls.</summary>
    internal string StoredFingerprint()
    {
        var values = new Dictionary<string, ElementValue>(StringComparer.OrdinalIgnoreCase);
        if (_snapshot is { State: PolicyState.Enabled } snapshot)
        {
            foreach ((string id, ElementValue value) in snapshot.Elements)
            {
                values[id] = value;
            }
        }

        foreach (OptionField field in _fields)
        {
            if (field.Element is { } element && !values.ContainsKey(element.Id) && _defaults.TryGetValue(element.Id, out ElementValue? fallback))
            {
                values[element.Id] = fallback;
            }
        }

        return OptionValueJson.Serialize(values.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>Enables or disables every control. The values stay.</summary>
    internal void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        foreach (OptionField field in _fields)
        {
            field.SetEnabled(enabled);
        }
    }

    /// <summary>True when the controls are enabled.</summary>
    internal bool Enabled { get; private set; }

    /// <summary>Runs the action of a button of the pane; false when the id is not one of the pane.</summary>
    internal bool TryRun(int id) => _context.TryRun(id);

    /// <summary>The tab stops of the pane in presentation order.</summary>
    internal IEnumerable<(FrameworkElement Owner, FrameworkElement Target)> TabStops() =>
        _fields.Select(f => f.TabStop).Where(s => s is not null).Select(s => s!.Value);

    /// <summary>
    /// Lays the fields out from the top of the canvas. When they are taller than the viewport the layout is done again without
    /// the room of the scroll bar.
    /// </summary>
    internal void Layout(double width, double height)
    {
        double total = LayoutAt(width);
        if (total > height)
        {
            width = Math.Max(40, width - OptionsMetrics.ScrollBarReserve);
            total = LayoutAt(width);
        }

        _canvas.Width = width;
        _canvas.Height = Math.Max(total, 1);
    }

    private double LayoutAt(double width)
    {
        double top = 0;
        foreach (OptionField field in _fields)
        {
            top += field.Layout(top, width);
        }

        return top;
    }

    /// <summary>The message for a problem of an edit, or null when the element has no field.</summary>
    internal OptionMessage? Describe(PolicyEditProblem problem)
    {
        OptionField? field = _fields.FirstOrDefault(f => string.Equals(f.ElementId, problem.ElementId, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return null;
        }

        string name = field.FieldName;
        Action focus = field.Focus;
        string neutralCaption = DialogStrings.Get("DialogMessageCaption");
        string neutralText = DialogStrings.Get("DialogProblemText");
        switch (problem.Kind, field)
        {
            case (PolicyProblemKind.Required, MultiField):
                return new OptionMessage(OptionsStrings.Format("OptionsEmptyLinesCaption", name), OptionsStrings.Get("OptionsEmptyLinesText"), focus);
            case (PolicyProblemKind.Required, ListField):
                return new OptionMessage(OptionsStrings.Format("OptionsListRequiredCaption", name), OptionsStrings.Get("OptionsListRequiredText"), focus);
            case (PolicyProblemKind.Required, _):
                return new OptionMessage(string.Format(CultureInfo.CurrentCulture, DialogStrings.Get("DialogRequiredCaption"), name), DialogStrings.Get("DialogRequiredText"), focus);
            case (PolicyProblemKind.BelowMinimum or PolicyProblemKind.AboveMaximum, NumberField number) when problem.Limit is { } limit:
                {
                    string key = problem.Kind == PolicyProblemKind.BelowMinimum ? "OptionsMinText" : "OptionsMaxText";
                    ulong entered = number.Entered;
                    return new OptionMessage(
                        OptionsStrings.Format("OptionsNumericCaption", name),
                        OptionsStrings.Format(key, limit, entered),
                        () =>
                        {
                            number.Reset(limit);
                            number.Focus();
                        });
                }

            case (PolicyProblemKind.TooManyLines, MultiField) when problem.Limit is { } limit:
                return new OptionMessage(OptionsStrings.Format("OptionsCountCaption", name), OptionsStrings.Format("OptionsCountText", limit, limit), focus);
            case (PolicyProblemKind.TooLong, MultiField) when problem.Limit is { } limit:
                return new OptionMessage(OptionsStrings.Format("OptionsLengthCaption", name), OptionsStrings.Format("OptionsLengthText", limit, limit), focus);
            case (PolicyProblemKind.EntryNameMissing, ListField list):
                return new OptionMessage(OptionsStrings.Get("ContentsErrorCaption"), OptionsStrings.Get("ContentsEmptyName"), list.Focus);
            case (PolicyProblemKind.EntryEmpty, ListField list):
                return new OptionMessage(OptionsStrings.Get("ContentsErrorCaption"), OptionsStrings.Get("ContentsEmptyValue"), list.Focus);
            case (PolicyProblemKind.DuplicateEntry, ListField list) when problem.EntryIndex is { } index && ReadValues().TryGetValue(problem.ElementId, out ElementValue? stored) && stored is ListValue entries && index < entries.Entries.Count:
                {
                    ListEntry entry = entries.Entries[index];
                    return list.HasNames
                        ? new OptionMessage(OptionsStrings.Get("ContentsErrorCaption"), OptionsStrings.Format("ContentsDuplicateName", entry.Name ?? string.Empty), list.Focus)
                        : new OptionMessage(OptionsStrings.Get("ContentsErrorCaption"), OptionsStrings.Format("ContentsDuplicateValue", entry.Data), list.Focus);
                }

            default:
                return new OptionMessage(neutralCaption, neutralText, focus);
        }
    }

    /// <summary>
    /// E2E flavor only: the readout of the pane. <c>Enabled</c>, one <c>V.&lt;element id&gt;</c> for each value that
    /// Apply would
    /// write (compact JSON, percent-escaped) and one <c>I.&lt;element id&gt;</c> for each drop-down or combo box (the items in
    /// display order, as a JSON array). Parts are separated by a semicolon.
    /// </summary>
    internal string Readout()
    {
        var text = new StringBuilder();
        text.Append("Enabled=").Append(Enabled ? '1' : '0').Append(';');
        IReadOnlyDictionary<string, ElementValue> values = ReadValues();
        foreach ((string id, ElementValue value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append("V.").Append(id).Append('=').Append(Uri.EscapeDataString(OptionValueJson.Serialize(value))).Append(';');
        }

        // A drop-down with nothing selected has no value to apply; the readout shows it as item -1 so the control is listed.
        foreach (OptionField field in _fields)
        {
            if (field.Element is { Kind: PolicyElementKind.Enum } element && !values.ContainsKey(element.Id))
            {
                text.Append("V.").Append(element.Id).Append('=').Append(Uri.EscapeDataString(OptionValueJson.Serialize(ElementValue.FromChoice(-1)))).Append(';');
            }
        }

        foreach (OptionField field in _fields)
        {
            if (field.Items is { } items && field.Element is { } element)
            {
                text.Append("I.").Append(element.Id).Append('=').Append(Uri.EscapeDataString(OptionValueJson.SerializeItems(items))).Append(';');
            }
        }

        return text.ToString();
    }

    private static void UiLog(string message) => Bwc.UI.UiDiagnostics.Warn?.Invoke(message);
}
