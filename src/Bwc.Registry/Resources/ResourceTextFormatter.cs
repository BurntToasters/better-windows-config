using System.Globalization;

namespace Bwc.Registry.Resources;

/// <summary>
/// The classic text layout of REG_RESOURCE_LIST (8), REG_FULL_RESOURCE_DESCRIPTOR (9), and
/// REG_RESOURCE_REQUIREMENTS_LIST (10). The lines are whole lines, indented as the golden shows them. Layouts that
/// the golden does not show are marked as open points in docs/failure-modes/export.md, section 2.
/// </summary>
internal static class ResourceTextFormatter
{
    private const int ListIndent = 19;
    private const int DescriptorIndent = 21;
    private const int FieldIndent = 23;
    private const int ListFieldLabelWidth = 21;
    private const int DescriptorLabelWidth = 19;
    private const int FieldLabelWidth = 17;
    private const byte TypePort = 1;
    private const byte TypeInterrupt = 2;
    private const byte TypeMemory = 3;
    private const byte TypeDma = 4;
    private const byte TypeDeviceSpecific = 5;
    private const byte TypeMemoryLarge = 7;

    /// <summary>
    /// Returns the lines after the Data label, or null when the bytes do not parse, in which case the caller writes
    /// a hex dump. An empty list is returned for a structure with no descriptors.
    /// </summary>
    public static List<string>? TryFormat(uint type, byte[] data)
    {
        var lines = new List<string>();

        switch (type)
        {
            case 8:
            {
                var parsed = CmResourceParser.ParseList(data);
                if (!parsed.Succeeded)
                {
                    return null;
                }

                int index = 0;
                foreach (CmFullResourceDescriptor full in parsed.Value!.Full)
                {
                    AddLine(lines, ListIndent, "Full Resource Descriptor " + Decimal((uint)index));
                    AddFull(lines, full);
                    index += 1;
                }

                return lines;
            }

            case 9:
            {
                var parsed = CmResourceParser.ParseFull(data);
                if (!parsed.Succeeded)
                {
                    return null;
                }

                AddFull(lines, parsed.Value!);
                return lines;
            }

            case 10:
            {
                var parsed = IoRequirementsParser.Parse(data);
                if (!parsed.Succeeded)
                {
                    return null;
                }

                AddRequirements(lines, parsed.Value!);
                return lines;
            }

            default:
                return null;
        }
    }

    private static void AddFull(List<string> lines, CmFullResourceDescriptor full)
    {
        AddField(lines, DescriptorIndent, DescriptorLabelWidth, "Interface Type:", ResourceNames.InterfaceType(full.InterfaceType));
        AddField(lines, DescriptorIndent, DescriptorLabelWidth, "Bus Number:", Signed(full.BusNumber));
        AddField(lines, DescriptorIndent, DescriptorLabelWidth, "Version:", Decimal(full.Version));
        AddField(lines, DescriptorIndent, DescriptorLabelWidth, "Revision:", Decimal(full.Revision));

        for (int index = 0; index < full.Partials.Count; index += 1)
        {
            AddLine(lines, DescriptorIndent, "Partial Descriptor " + Decimal((uint)index));
            AddPartial(lines, full.Partials[index]);
            lines.Add(string.Empty);
        }

        lines.Add(string.Empty);
    }

    private static void AddPartial(List<string> lines, CmPartialResourceDescriptor partial)
    {
        AddField(lines, FieldIndent, FieldLabelWidth, "Resource:", ResourceNames.PartialType(partial.Type));
        AddField(lines, FieldIndent, FieldLabelWidth, "Disposition:", ResourceNames.ShareDisposition(partial.ShareDisposition));

        switch (partial.Type)
        {
            case TypePort:
                AddField(lines, FieldIndent, FieldLabelWidth, "Start:", Hex(partial.Start));
                AddField(lines, FieldIndent, FieldLabelWidth, "Length:", BareHex(partial.Length));
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.PortType(partial.Flags));
                break;

            case TypeInterrupt:
                AddField(lines, FieldIndent, FieldLabelWidth, "Vector:", Decimal(partial.Vector));
                AddField(lines, FieldIndent, FieldLabelWidth, "Level:", Decimal(partial.Level));
                AddField(lines, FieldIndent, FieldLabelWidth, "Affinity:", Hex(partial.Affinity));
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.InterruptType(partial.Flags));
                break;

            case TypeMemory:
            case TypeMemoryLarge:
                AddField(lines, FieldIndent, FieldLabelWidth, "Start:", Hex(partial.Start));
                AddField(lines, FieldIndent, FieldLabelWidth, "Length:", BareHex(partial.Length));
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.MemoryAccess(partial.Flags));
                break;

            case TypeDma:
                AddField(lines, FieldIndent, FieldLabelWidth, "Channel:", Decimal(partial.Channel));
                AddField(lines, FieldIndent, FieldLabelWidth, "Port:", Decimal(partial.Port));
                break;

            case TypeDeviceSpecific:
                AddField(lines, FieldIndent, FieldLabelWidth, "Reserved1:", Decimal(partial.Reserved1));
                AddField(lines, FieldIndent, FieldLabelWidth, "Reserved2:", Decimal(partial.Reserved2));
                AddField(lines, FieldIndent, FieldLabelWidth, "Data Size:", Decimal(partial.DataSize));
                break;
        }
    }

    private static void AddRequirements(List<string> lines, IoResourceRequirementsList list)
    {
        AddField(lines, ListIndent, ListFieldLabelWidth, "Interface Type:", ResourceNames.InterfaceType(list.InterfaceType));
        AddField(lines, ListIndent, ListFieldLabelWidth, "Bus Number:", Signed(list.BusNumber));
        AddField(lines, ListIndent, ListFieldLabelWidth, "Slot Number:", Signed(list.SlotNumber));

        for (int alternative = 0; alternative < list.Lists.Count; alternative += 1)
        {
            IoResourceList resourceList = list.Lists[alternative];
            AddLine(lines, ListIndent, "List " + Decimal((uint)alternative));

            for (int index = 0; index < resourceList.Descriptors.Count; index += 1)
            {
                AddLine(lines, DescriptorIndent, "Descriptor " + Decimal((uint)index));
                AddRequirement(lines, resourceList.Descriptors[index]);
                lines.Add(string.Empty);
            }

            lines.Add(string.Empty);
        }
    }

    private static void AddRequirement(List<string> lines, IoResourceDescriptor descriptor)
    {
        AddField(lines, FieldIndent, FieldLabelWidth, "Resource:", ResourceNames.PartialType(descriptor.Type));
        AddField(lines, FieldIndent, FieldLabelWidth, "Option:", "0x" + descriptor.Option.ToString("x8", CultureInfo.InvariantCulture));
        AddField(lines, FieldIndent, FieldLabelWidth, "Disposition:", ResourceNames.ShareDisposition(descriptor.ShareDisposition));

        switch (descriptor.Type)
        {
            case TypePort:
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.PortType(descriptor.Flags));
                AddField(lines, FieldIndent, FieldLabelWidth, "Length:", NonZeroHexPrefixed(descriptor.Length));
                AddField(lines, FieldIndent, FieldLabelWidth, "Alignment:", NonZeroHexPrefixed(descriptor.Alignment));
                AddField(lines, FieldIndent, FieldLabelWidth, "Minimum Address:", Hex(descriptor.Minimum));
                AddField(lines, FieldIndent, FieldLabelWidth, "Maximum Address:", Hex(descriptor.Maximum));
                break;

            case TypeMemory:
                // Assumed, not measured: the golden cannot tell Length from Alignment, so Minimum shows Alignment,
                // and Maximum shows the low DWORD at offset 8, as the golden's 0xfed00000 does.
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.MemoryAccess(descriptor.Flags));
                AddField(lines, FieldIndent, FieldLabelWidth, "Length:", BareHex(descriptor.Length));
                AddField(lines, FieldIndent, FieldLabelWidth, "Alignment:", BareHex(descriptor.Alignment));
                AddField(lines, FieldIndent, FieldLabelWidth, "Minimum Address:", Hex(descriptor.Alignment));
                AddField(lines, FieldIndent, FieldLabelWidth, "Maximum Address:", Hex((uint)descriptor.Minimum));
                break;

            case TypeInterrupt:
                AddField(lines, FieldIndent, FieldLabelWidth, "Type:", ResourceNames.InterruptType(descriptor.Flags));
                AddField(lines, FieldIndent, FieldLabelWidth, "Minimum Vector:", NonZeroHexPrefixed(descriptor.MinimumVector));
                AddField(lines, FieldIndent, FieldLabelWidth, "Maximum Vector:", NonZeroHexPrefixed(descriptor.MaximumVector));
                break;
        }
    }

    private static void AddLine(List<string> lines, int indent, string text) =>
        lines.Add(new string(' ', indent) + text);

    private static void AddField(List<string> lines, int indent, int labelWidth, string label, string value) =>
        lines.Add(new string(' ', indent) + label.PadRight(labelWidth) + value);

    private static string Hex(ulong value) => "0x" + value.ToString("x8", CultureInfo.InvariantCulture);

    private static string BareHex(ulong value) => value.ToString("x", CultureInfo.InvariantCulture);

    // C's %#x: zero prints as 0, anything else as 0x and lowercase hex.
    private static string NonZeroHexPrefixed(ulong value) =>
        value == 0 ? "0" : "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    private static string Decimal(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Signed(uint value) => ((int)value).ToString(CultureInfo.InvariantCulture);
}
