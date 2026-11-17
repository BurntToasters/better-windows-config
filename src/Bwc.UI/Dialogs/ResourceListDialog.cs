using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Resource Lists (RT_DIALOG 116) for a REG_RESOURCE_LIST value: one row per full descriptor.
/// Display... (and a double-click
/// on a row) opens the Resources viewer for the selected row. Display... is disabled until a row is selected.
/// </summary>
public sealed class ResourceListDialog : ClassicDialog
{
    private const int DisplayId = 1203;
    private readonly IReadOnlyList<ResourceFullView> _rows;
    private readonly ClassicColumnList _list;
    private readonly Button _display;

    private readonly Func<nint, byte[], Task> _showDeviceData;

    public ResourceListDialog(
        string caption,
        IReadOnlyList<ResourceFullView> rows,
        string okText,
        string displayText,
        Func<nint, byte[], Task> showDeviceData)
        : base(ClassicDialogTemplates.ResourceLists(RequireText(caption), RequireText(okText), RequireText(displayText)))
    {
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
        _showDeviceData = showDeviceData ?? throw new ArgumentNullException(nameof(showDeviceData));
        _list = RequireControl<ClassicColumnList>("ResourceListColumns");
        _display = RequireControl<Button>("ResourceDisplay");
        _display.IsEnabled = false;

        _list.SetRows(rows.Select(r => (IReadOnlyList<string>)new[]
        {
            ResourceText.Signed(r.BusNumber),
            ResourceText.InterfaceType(r.InterfaceType),
        }).ToArray());
        _list.SelectionChanged += (_, _) => _display.IsEnabled = _list.SelectedIndex >= 0;
        _list.RowActivated += (_, _) => _ = OpenSelectedAsync();
    }

    /// <summary>The selected full descriptor, or null when no row is selected.</summary>
    public ResourceFullView? SelectedRow => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null;

    /// <summary>Number of rows in the list.</summary>
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
        ResourceFullView? row = SelectedRow;
        if (row is null)
        {
            return;
        }

        var child = new ResourceDescriptorDialog(
            ResourceDescriptorDialog.CaptionText,
            row,
            ResourceDescriptorDialog.OkText,
            ResourceDescriptorDialog.DataText,
            _showDeviceData);
        _ = await child.ShowAsync(WindowNative.GetWindowHandle(this));
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value;
    }
}
