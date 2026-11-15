using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Requirements List (RT_DIALOG 1501) for a REG_RESOURCE_REQUIREMENTS_LIST value. Display... is disabled until a row is
/// selected. It (and a double-click on a row) opens the detail viewer for the row's type: Memory, Port, Interrupt, or DMA.
/// A row of another type opens nothing.
/// </summary>
public sealed class ResourceRequirementsDialog : ClassicDialog
{
    private const int DisplayId = 1506;
    private readonly ResourceRequirementsView _view;
    private readonly ClassicColumnList _list;
    private readonly Button _display;

    public ResourceRequirementsDialog(string caption, ResourceRequirementsView view, string okText, string displayText)
        : base(ClassicDialogTemplates.ResourceRequirements(RequireText(caption), RequireText(okText), RequireText(displayText)))
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("InterfaceTypeValue"), ResourceText.InterfaceType(view.InterfaceType));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("BusNumberValue"), ResourceText.Signed(view.BusNumber));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("SlotNumberValue"), ResourceText.Signed(view.SlotNumber));

        _list = RequireControl<ClassicColumnList>("RequirementColumns");
        _display = RequireControl<Button>("RequirementDisplay");
        _display.IsEnabled = false;
        _list.SetRows(view.Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            ResourceText.Decimal((ulong)r.AlternativeList),
            ResourceText.Decimal((ulong)r.ResourceList),
            ResourceText.Decimal((ulong)r.Descriptor),
            ResourceText.PartialType(r.Type),
        }).ToArray());
        _list.SelectionChanged += (_, _) => _display.IsEnabled = _list.SelectedIndex >= 0;
        _list.RowActivated += (_, _) => _ = OpenSelectedAsync();
    }

    /// <summary>Number of requirement rows.</summary>
    public int RowCount => _list.RowCount;

    /// <summary>Selects one row by index, as a click would.</summary>
    public void SelectRow(int index) => _list.SelectRow(index);

    protected override void OnCommand(int id, string automationId)
    {
        if (id == DisplayId)
        {
            _ = OpenSelectedAsync();
            return;
        }

        base.OnCommand(id, automationId);
    }

    private async Task OpenSelectedAsync()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _view.Rows.Count)
        {
            return;
        }

        ResourceRequirementView row = _view.Rows[index];
        ResourceDetailKind? kind = row.Type switch
        {
            1 => ResourceDetailKind.Port,
            2 => ResourceDetailKind.Interrupt,
            3 => ResourceDetailKind.Memory,
            4 => ResourceDetailKind.Dma,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        var detail = new ResourceDetailDialog(ResourceDetailDialog.CaptionFor(kind.Value), kind.Value, row, ResourceDetailDialog.OkText);
        _ = await detail.ShowAsync(WindowNative.GetWindowHandle(this));
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value;
    }
}
