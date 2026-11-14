using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Bwc.E2E.Harness;

/// <summary>
/// Reads the BwcFlavor AssemblyMetadata attribute that Directory.Build.props stamps into each app assembly.
/// The file is parsed as metadata only. It is never loaded into the process or executed.
/// </summary>
internal static class FlavorMetadata
{
    public const string AttributeName = "BwcFlavor";
    public const string E2EValue = "E2E";

    // The main assembly of each app the publish folder holds. A flavor mix in any one of them fails the guard.
    private static readonly string[] Assemblies = { "BetterWindowsConfig.dll", "BwcRegedit.dll", "BwcUpdate.dll" };

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

        return null;
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
