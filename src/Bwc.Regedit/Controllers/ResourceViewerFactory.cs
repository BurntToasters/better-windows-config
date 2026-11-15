using Bwc.Registry;
using Bwc.Registry.Resources;
using Bwc.UI.Dialogs;

namespace Bwc.Regedit.Controllers;

/// <summary>
/// Builds the classic viewer for a hardware resource value: Resource Lists for REG_RESOURCE_LIST, Resources for
/// REG_FULL_RESOURCE_DESCRIPTOR, and Requirements List for REG_RESOURCE_REQUIREMENTS_LIST. Malformed data still gets a
/// viewer with the rows that parsed; <paramref name="malformed"/> tells the caller to show the value error first.
/// </summary>
internal static class ResourceViewerFactory
{
    internal const string ListCaption = "Resource Lists";
    internal const string DescriptorCaption = "Resources";
    internal const string RequirementsCaption = "Requirements List";
    internal const string OkText = "OK";
    internal const string DisplayText = "Display...";
    internal const string DataText = "&Data...";

    internal static ModalDialogWindow Create(uint type, byte[] data, Func<nint, byte[], Task> showDeviceData, out bool malformed)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (type)
        {
            case (uint)RegistryValueType.ResourceList:
            {
                ResourceParseResult<CmResourceList> parsed = CmResourceParser.ParseList(data);
                malformed = !parsed.Succeeded;
                CmResourceList? value = parsed.Value ?? parsed.PartialValue;
                IReadOnlyList<ResourceFullView> rows = value is null ? [] : value.Full.Select(ToView).ToArray();
                return new ResourceListDialog(ListCaption, rows, OkText, DisplayText, showDeviceData);
            }

            case (uint)RegistryValueType.FullResourceDescriptor:
            {
                ResourceParseResult<CmFullResourceDescriptor> parsed = CmResourceParser.ParseFull(data);
                malformed = !parsed.Succeeded;
                ResourceFullView? view = parsed.Value is null ? null : ToView(parsed.Value);
                return new ResourceDescriptorDialog(DescriptorCaption, view, OkText, DataText, showDeviceData);
            }

            case (uint)RegistryValueType.ResourceRequirementsList:
            {
                ResourceParseResult<IoResourceRequirementsList> parsed = IoRequirementsParser.Parse(data);
                malformed = !parsed.Succeeded;
                IoResourceRequirementsList? value = parsed.Value ?? parsed.PartialValue;
                ResourceRequirementsView view = value is null
                    ? new ResourceRequirementsView(0, 0, 0, [])
                    : ToView(value);
                return new ResourceRequirementsDialog(RequirementsCaption, view, OkText, DisplayText);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "not a hardware resource type");
        }
    }

    private static ResourceFullView ToView(CmFullResourceDescriptor full) => new(
        full.InterfaceType,
        full.BusNumber,
        full.Version,
        full.Revision,
        full.Partials.Select(ToView).ToArray());

    private static ResourcePartialView ToView(CmPartialResourceDescriptor p) => new(
        p.Type,
        p.ShareDisposition,
        p.Flags,
        p.Start,
        p.Length,
        p.Level,
        p.Vector,
        p.Affinity,
        p.Channel,
        p.Port,
        p.Reserved1,
        p.Reserved2,
        p.DataSize,
        p.DeviceData);

    // The alternative list index is the IO_RESOURCE_LIST position. The resource list column is 0, since a
    // value holds one list.
    private static ResourceRequirementsView ToView(IoResourceRequirementsList list)
    {
        var rows = new List<ResourceRequirementView>();
        for (int i = 0; i < list.Lists.Count; i++)
        {
            IReadOnlyList<IoResourceDescriptor> descriptors = list.Lists[i].Descriptors;
            for (int j = 0; j < descriptors.Count; j++)
            {
                IoResourceDescriptor d = descriptors[j];
                rows.Add(new ResourceRequirementView(
                    i,
                    0,
                    j,
                    d.Option,
                    d.Type,
                    d.ShareDisposition,
                    d.Flags,
                    d.Length,
                    d.Alignment,
                    d.Minimum,
                    d.Maximum,
                    d.MinimumVector,
                    d.MaximumVector,
                    d.MinimumVector,
                    d.MaximumVector));
            }
        }

        return new ResourceRequirementsView(list.InterfaceType, list.BusNumber, list.SlotNumber, rows);
    }
}
