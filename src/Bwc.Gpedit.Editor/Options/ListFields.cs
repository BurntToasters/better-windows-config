using System.Globalization;
using Bwc.Core;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Editor.Options;

/// <summary>An enum element: a drop-down list. The value is the template index of the chosen item, not its place in
/// the list.</summary>
internal sealed class DropField : OptionField
{
    private readonly TextBlock? _label;
    private readonly ComboBox _combo;
    private readonly List<string> _items = [];
    private readonly List<int> _order = [];

    internal DropField(OptionsContext context, PresentationControl control, EnumElement element)
        : base(context, control)
    {
        _label = MakeLabel();
        _combo = (ComboBox)Create(ClassicControlKind.ComboBox, IdFor("dropDownList"), Label);
        _combo.IsTabStop = true;

        IEnumerable<int> order = Enumerable.Range(0, element.Items.Count);
        if (control.NoSort != true)
        {
            // OrderBy is stable, so equal names keep the template order.
            order = order.OrderBy(i => element.Items[i].DisplayName, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true));
        }

        foreach (int index in order)
        {
            _order.Add(index);
            _items.Add(element.Items[index].DisplayName);
        }

        _combo.ItemsSource = _items;
        _combo.SelectionChanged += (_, _) => NotifyChanged();
        Parts = _label is null ? [_combo] : [_label, _combo];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_combo, _combo);

    internal override IReadOnlyList<string>? Items => _items;

    internal override void Load(ElementValue? stored)
    {
        // No stored choice and no default item: nothing is selected, as in classic, and OK is refused until one is chosen.
        _combo.SelectedIndex = stored is ChoiceValue choice ? _order.IndexOf(choice.ItemIndex) : -1;
    }

    internal override ElementValue? Read()
    {
        int selected = _combo.SelectedIndex;
        return selected >= 0 && selected < _order.Count ? new ChoiceValue(_order[selected]) : null;
    }

    internal override void SetEnabled(bool enabled) => _combo.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutInline(_label, _combo, OptionsMetrics.DropWidth, OptionsMetrics.DropHeight, OptionsMetrics.LabelHeight, OptionsMetrics.InlineRowHeight, top, availableWidth);
}

/// <summary>
/// A list element: a "Show..." button that opens the Show Contents dialog over the rows of the list. The base of the
/// list (what
/// was stored when the pane was loaded or last applied) gives <see cref="ListValue.Removed"/>.
/// </summary>
internal sealed class ListField : OptionField
{
    private readonly ListElement _element;
    private readonly TextBlock? _label;
    private readonly Button _button;
    private List<ListEntry> _rows = [];
    private List<ListEntry> _base = [];
    private bool _opening;

    internal ListField(OptionsContext context, PresentationControl control, ListElement element)
        : base(context, control)
    {
        _element = element;
        _label = MakeLabel();
        _button = (Button)CreateButton(IdFor("button"), OptionsStrings.Get("OptionsShowButton"), () => OpenAsync().Forget("list field show contents"));
        _button.IsTabStop = true;
        Parts = _label is null ? [_button] : [_label, _button];
    }

    internal override IReadOnlyList<FrameworkElement> Parts { get; }

    internal override (FrameworkElement Owner, FrameworkElement Target)? TabStop => (_button, _button);

    /// <summary>The button of the list (the offending control of a list problem).</summary>
    internal Button Button => _button;

    /// <summary>True when the rows have a name column.</summary>
    internal bool HasNames => _element.ExplicitValue;

    internal override void Load(ElementValue? stored)
    {
        _base = stored is ListValue list ? [.. list.Entries] : [];
        _rows = [.. _base.Select(e => _element.ExplicitValue ? e : ListEntry.Of(e.Data))];
    }

    internal override void Rebase(ElementValue? stored) => _base = stored is ListValue list ? [.. list.Entries] : [];

    internal override ElementValue? Read()
    {
        List<ListEntry> removed = [];
        foreach (ListEntry old in _base)
        {
            bool kept = _element.ExplicitValue
                ? _rows.Any(r => string.Equals(r.Name, old.Name, StringComparison.OrdinalIgnoreCase))
                : _rows.Any(r => string.Equals(r.Data, old.Data, StringComparison.Ordinal));
            if (!kept)
            {
                removed.Add(old);
            }
        }

        return new ListValue([.. _rows]) { Removed = removed };
    }

    internal override void SetEnabled(bool enabled) => _button.IsEnabled = enabled;

    internal override double Layout(double top, double availableWidth) =>
        LayoutInline(_label, _button, OptionsMetrics.ButtonWidth, OptionsMetrics.ButtonHeight, OptionsMetrics.ListLabelHeight, OptionsMetrics.ListRowHeight, top, availableWidth);

    private async Task OpenAsync()
    {
        if (_opening)
        {
            return;
        }

        _opening = true;
        try
        {
            IReadOnlyList<IReadOnlyList<string>> rows = _rows
                .Select(r => (IReadOnlyList<string>)(_element.ExplicitValue ? new[] { r.Name ?? string.Empty, r.Data } : new[] { r.Data }))
                .ToArray();
            StringComparer comparer = string.IsNullOrEmpty(_element.ValuePrefix) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var dialog = new ShowContentsDialog(FieldName, _element.ExplicitValue, rows, comparer);
            bool accepted = await dialog.ShowAsync(Context.OwnerHandle());
            if (accepted && dialog.Result is { } result)
            {
                _rows = [.. result.Select(r => _element.ExplicitValue ? new ListEntry(r[0], r[1]) : ListEntry.Of(r[0]))];
                NotifyChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Bwc.UI.UiDiagnostics.Warn?.Invoke("The contents of a list could not be shown: " + ex.Message);
        }
        finally
        {
            _opening = false;
            _ = _button.Focus(FocusState.Programmatic);
        }
    }
}
