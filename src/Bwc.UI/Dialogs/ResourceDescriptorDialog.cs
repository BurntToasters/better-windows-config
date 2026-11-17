using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Resources (RT_DIALOG 1305) for one full resource descriptor. The partial descriptors are grouped
/// into five column lists by
/// type (DMA, Interrupt, Memory, Port, Device specific). The Data... button is enabled only while a
/// Device Specific row with
/// data is selected, and opens the bytes of that row in the binary viewer.
/// </summary>
public sealed class ResourceDescriptorDialog : ClassicDialog
{
    public const string CaptionText = "Resources";
    public const string OkText = "OK";
    public const string DataText = "&Data...";

    private const int DataId = 1204;
    private readonly ClassicColumnList _deviceList;
    private readonly ResourcePartialView[] _deviceRows;
    private readonly Button _data;

    private readonly Func<nint, byte[], Task> _showDeviceData;

    /// <param name="showDeviceData">Opens the device data bytes in the binary viewer, owned by the given
    /// window handle.</param>
    public ResourceDescriptorDialog(
        string caption,
        ResourceFullView? full,
        string okText,
        string dataText,
        Func<nint, byte[], Task> showDeviceData)
        : base(ClassicDialogTemplates.ResourceDescriptor(RequireText(caption), RequireText(okText), RequireText(dataText)))
    {
        _showDeviceData = showDeviceData ?? throw new ArgumentNullException(nameof(showDeviceData));
        _data = RequireControl<Button>("ResourceData");
        _data.IsEnabled = false;

        // A malformed value with no readable header leaves the header fields empty.
        if (full is not null)
        {
            ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("BusNumberValue"), ResourceText.Signed(full.BusNumber));
            ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("VersionValue"), ResourceText.Decimal(full.Version));
            ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("RevisionValue"), ResourceText.Decimal(full.Revision));
            ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("InterfaceTypeValue"), ResourceText.InterfaceType(full.InterfaceType));
        }

        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("ShareUndetermined"), ResourceText.ShareDisposition(0));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("ShareShared"), ResourceText.ShareDisposition(3));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("ShareDeviceExclusive"), ResourceText.ShareDisposition(1));
        ClassicControlFactory.SetStaticText(RequireControl<FrameworkElement>("ShareDriverExclusive"), ResourceText.ShareDisposition(2));

        IReadOnlyList<ResourcePartialView> partials = full?.Partials ?? [];
        FillList("DmaColumns", partials.Where(p => p.Type == CmTypes.Dma), p => new[]
        {
            ResourceText.Decimal(p.Channel),
            ResourceText.Decimal(p.Port),
        });
        FillList("InterruptColumns", partials.Where(p => p.Type == CmTypes.Interrupt), p => new[]
        {
            ResourceText.Decimal(p.Vector),
            ResourceText.Decimal(p.Level),
            ResourceText.Hex(p.Affinity),
            ResourceText.InterruptType(p.Flags),
        });
        FillList("MemoryColumns", partials.Where(p => p.Type is CmTypes.Memory or CmTypes.MemoryLarge), p => new[]
        {
            ResourceText.Hex(p.Start),
            ResourceText.Hex(p.Length),
            ResourceText.MemoryAccess(p.Flags),
        });
        FillList("PortColumns", partials.Where(p => p.Type == CmTypes.Port), p => new[]
        {
            ResourceText.Hex(p.Start),
            ResourceText.Hex(p.Length),
            ResourceText.PortType(p.Flags),
        });

        _deviceRows = partials.Where(p => p.Type == CmTypes.DeviceSpecific).ToArray();
        _deviceList = RequireControl<ClassicColumnList>("DeviceSpecificColumns");
        _deviceList.SetRows(_deviceRows.Select(p => (IReadOnlyList<string>)new[]
        {
            ResourceText.Decimal(p.Reserved1),
            ResourceText.Decimal(p.Reserved2),
            ResourceText.Decimal(p.DataSize),
        }).ToArray());
        _deviceList.SelectionChanged += (_, _) => _data.IsEnabled = DeviceRowHasData();
        _deviceList.RowActivated += (_, _) => OpenData();
    }

    private static class CmTypes
    {
        public const byte Port = 1;
        public const byte Interrupt = 2;
        public const byte Memory = 3;
        public const byte Dma = 4;
        public const byte DeviceSpecific = 5;
        public const byte MemoryLarge = 7;
    }

    protected override void OnCommand(int id, string automationId)
    {
        if (id == DataId)
        {
            OpenData();
            return;
        }

        base.OnCommand(id, automationId);
    }

    private void FillList(string automationId, IEnumerable<ResourcePartialView> rows, Func<ResourcePartialView, string[]> cells)
    {
        ClassicColumnList list = RequireControl<ClassicColumnList>(automationId);
        list.SetRows(rows.Select(p => (IReadOnlyList<string>)cells(p)).ToArray());
    }

    private bool DeviceRowHasData()
    {
        int index = _deviceList.SelectedIndex;
        return index >= 0 && index < _deviceRows.Length && _deviceRows[index] is { DeviceData.Length: > 0 };
    }

    private void OpenData()
    {
        int index = _deviceList.SelectedIndex;
        if (!DeviceRowHasData() || index < 0)
        {
            return;
        }

        _ = _showDeviceData(WindowNative.GetWindowHandle(this), _deviceRows[index].DeviceData!);
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value;
    }
}
