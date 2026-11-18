using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Bwc.Oracle.Resources.Model;
using ClrResourceReader = System.Resources.ResourceReader;

namespace Bwc.Oracle.Resources.Managed;

/// Reads the embedded .resources streams of a managed assembly without loading the assembly for execution:
/// the PE file is opened with System.Reflection.Metadata, each manifest resource is cut out of the managed resources
/// directory, and System.Resources.ResourceReader hands back the raw data of each entry. Nothing is deserialized with
/// BinaryFormatter and no type is instantiated.
internal static class ManagedResources
{
    private const int MaxResourceBytes = 64 * 1024 * 1024;

    public static void Read(string assemblyName, string path, FormsFile into)
    {
        using FileStream stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new InvalidDataException($"{path} has no managed metadata");
        }

        MetadataReader metadata = pe.GetMetadataReader();
        DirectoryEntry directory = pe.PEHeaders.CorHeader?.ResourcesDirectory ?? default;
        if (directory.Size == 0)
        {
            into.Skipped.Add(new SkippedManagedResource { Assembly = assemblyName, Name = "(managed resources directory)", Reason = "the assembly has no managed resources directory" });
            return;
        }

        PEMemoryBlock block = pe.GetSectionData(directory.RelativeVirtualAddress);
        if (block.Length < directory.Size)
        {
            throw new InvalidDataException($"{path}: managed resources directory is {directory.Size} bytes but only {block.Length} are mapped");
        }

        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            string name = metadata.GetString(resource.Name);
            if (!resource.Implementation.IsNil)
            {
                into.Skipped.Add(new SkippedManagedResource { Assembly = assemblyName, Name = name, Reason = "not embedded (linked file or assembly reference)" });
                continue;
            }

            long offset = resource.Offset;
            if (offset < 0 || offset + 4 > directory.Size)
            {
                throw new InvalidDataException($"{path}: manifest resource '{name}' starts outside the resources directory");
            }

            BlobReader sizeReader = block.GetReader((int)offset, 4);
            int length = sizeReader.ReadInt32();
            if (length < 0 || length > MaxResourceBytes || offset + 4 + length > directory.Size)
            {
                throw new InvalidDataException($"{path}: manifest resource '{name}' claims {length} bytes, which leave the resources directory");
            }

            byte[] bytes = block.GetReader((int)offset + 4, length).ReadBytes(length);
            if (!name.EndsWith(".resources", StringComparison.Ordinal))
            {
                into.Skipped.Add(new SkippedManagedResource { Assembly = assemblyName, Name = name, ByteLength = length, Reason = "not a .resources stream" });
                continue;
            }

            into.ResourceFiles.Add(ReadResourceFile(assemblyName, name, bytes));
        }
    }

    private static ManagedResourceFile ReadResourceFile(string assemblyName, string name, byte[] bytes)
    {
        var file = new ManagedResourceFile { Assembly = assemblyName, Name = name };
        try
        {
            using var reader = new ClrResourceReader(new MemoryStream(bytes, writable: false));
            var names = new List<string>();
            // Only the key is read. Entry and Value would deserialize the value, which is not supported and not wanted.
            System.Collections.IDictionaryEnumerator keys = reader.GetEnumerator();
            while (keys.MoveNext())
            {
                names.Add((string)keys.Key);
            }
            names.Sort(StringComparer.Ordinal);

            foreach (string entryName in names)
            {
                file.Entries.Add(ReadEntry(reader, entryName));
            }
            file.EntryCount = file.Entries.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            file.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        return file;
    }

    private static ManagedResourceEntry ReadEntry(ClrResourceReader reader, string name)
    {
        var entry = new ManagedResourceEntry { Name = name };
        try
        {
            reader.GetResourceData(name, out string type, out byte[] data);
            entry.Type = type;
            entry.ByteLength = data.Length;
            WinFormsValueDecoder.Decode(entry, data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.Kind = "error";
            entry.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        return entry;
    }
}
