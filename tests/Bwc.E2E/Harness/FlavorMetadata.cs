using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Bwc.E2E.Harness;

/// <summary>
/// Reads the BwcFlavor AssemblyMetadata attribute that Directory.Build.props stamps into each app assembly.
/// The file is parsed as metadata only. It is never loaded into the process or executed. BwcUpdate.exe is NativeAOT
/// and has no assembly: its flavor is the marker string BwcUpdateFlavor=ship or BwcUpdateFlavor=E2E that the exe uses
/// (src/Bwc.Update/Cli/BuildFlavor.cs, update-elevation.md HN14), read from the file bytes as UTF-16LE or UTF-8.
/// </summary>
internal static class FlavorMetadata
{
    public const string AttributeName = "BwcFlavor";
    public const string E2EValue = "E2E";

    private const string UpdateExeName = "BwcUpdate.exe";
    private const string UpdateMarkerPrefix = "BwcUpdateFlavor=";

    // The main assembly of each managed app the publish folder holds. A flavor mix in any one of them fails the guard.
    private static readonly string[] Assemblies = { "BetterWindowsConfig.dll", "BwcRegedit.dll", "BwcGpedit.dll" };

    /// <summary>
    /// Returns null when every app assembly in the publish folder says the e2e flavor. Otherwise returns the
    /// first problem found: a missing file, an unreadable file, a missing attribute, or another flavor value.
    /// </summary>
    public static string? E2EProblem(string publishDir)
    {
        foreach (string name in Assemblies)
        {
            string path = Path.Combine(publishDir, name);
            if (!File.Exists(path))
            {
                return $"'{path}' is missing";
            }

            IReadOnlyList<string> values;
            try
            {
                values = ReadValues(path);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return $"'{path}' could not be read as an assembly ({ex.GetType().Name})";
            }

            if (values.Count == 0)
            {
                return $"'{path}' has no {AttributeName} metadata";
            }

            if (values.Any(value => !string.Equals(value, E2EValue, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{path}' says {AttributeName}={string.Join(", ", values)}";
            }
        }

        return UpdateExeProblem(publishDir);
    }

    /// <summary>The flavor markers in BwcUpdate.exe: null when it names only the e2e flavor, else the first
    /// problem.</summary>
    public static string? UpdateExeProblem(string publishDir)
    {
        string path = Path.Combine(publishDir, UpdateExeName);
        if (!File.Exists(path))
        {
            return $"'{path}' is missing";
        }

        IReadOnlyList<string> values = ReadUpdateMarkers(File.ReadAllBytes(path));
        if (values.Count == 0)
        {
            return $"'{path}' has no {UpdateMarkerPrefix} marker";
        }

        return values.Any(value => !string.Equals(value, E2EValue, StringComparison.Ordinal))
            ? $"'{path}' says {UpdateMarkerPrefix}{string.Join(", ", values)}"
            : null;
    }

    /// <summary>The distinct values after the marker prefix in the bytes, as UTF-8 or UTF-16LE.</summary>
    public static IReadOnlyList<string> ReadUpdateMarkers(byte[] bytes)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        foreach (System.Text.Encoding encoding in new System.Text.Encoding[] { System.Text.Encoding.UTF8, System.Text.Encoding.Unicode })
        {
            int unit = encoding == System.Text.Encoding.UTF8 ? 1 : 2;
            byte[] needle = encoding.GetBytes(UpdateMarkerPrefix);
            for (int at = bytes.AsSpan().IndexOf(needle); at >= 0;)
            {
                int start = at + needle.Length;
                int end = start;
                while (end + unit <= bytes.Length)
                {
                    int code = unit == 1 ? bytes[end] : bytes[end] | (bytes[end + 1] << 8);
                    if (!(code is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                    {
                        break;
                    }

                    end += unit;
                }

                values.Add(encoding.GetString(bytes, start, end - start));
                int next = bytes.AsSpan(at + 1).IndexOf(needle);
                at = next < 0 ? -1 : at + 1 + next;
            }
        }

        return values.ToList();
    }

    /// <summary>The BwcFlavor values an assembly file declares. Empty when it declares none.</summary>
    public static IReadOnlyList<string> ReadValues(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new InvalidDataException($"'{path}' has no managed metadata.");
        }

        MetadataReader reader = pe.GetMetadataReader();
        var values = new List<string>();
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataConstructor(reader, attribute.Constructor))
            {
                continue;
            }

            // The blob is a 2 byte prolog (0x0001), then two serialized strings: the key and the value.
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 1)
            {
                continue;
            }

            string? key = blob.ReadSerializedString();
            string? value = blob.ReadSerializedString();
            if (string.Equals(key, AttributeName, StringComparison.Ordinal) && value is not null)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static bool IsAssemblyMetadataConstructor(MetadataReader reader, EntityHandle constructor)
    {
        if (constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        MemberReference member = reader.GetMemberReference((MemberReferenceHandle)constructor);
        if (member.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
        return reader.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute") &&
            reader.StringComparer.Equals(type.Namespace, "System.Reflection");
    }
}
