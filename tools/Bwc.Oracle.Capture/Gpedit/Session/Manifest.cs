using System.Text.Json;

namespace Bwc.Oracle.Capture.Gpedit.Session;

internal sealed class TemplateEntry
{
    // Relative to PolicyDefinitions: "Name.admx" or "en-US\Name.adml".
    public string RelativePath { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }
}

internal sealed class StartedProcess
{
    public int Pid { get; set; }

    public string Name { get; set; } = string.Empty;

    public long StartUtcTicks { get; set; }
}

// The record of everything a session changed or may change. It is written once the backup is verified and before any template is
// copied, and it is the only thing `gpedit restore` trusts. Deleted last, only after restore verified clean.
internal sealed class Manifest
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public int Version { get; set; } = 1;

    public string Command { get; set; } = string.Empty;

    public string CreatedUtc { get; set; } = string.Empty;

    public bool Sandbox { get; set; }

    public TreeSnapshot GroupPolicy { get; set; } = new();

    public TreeSnapshot MmcState { get; set; } = new();

    public List<TemplateEntry> Templates { get; set; } = new();

    public List<StartedProcess> Processes { get; set; } = new();

    public List<string> RegistryKeys { get; set; } = new();

    internal static string BackupLocation(GpeditPaths paths, string label) => Path.Combine(paths.BackupDir, label);

    // Temp file, flush to disk, then an atomic replace: a crash leaves no manifest or a whole one.
    internal void Write(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("manifest path has no folder");
        Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    internal static Manifest Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(bytes, Options) ?? throw new InvalidDataException("manifest is empty");
        if (manifest.Version != 1)
        {
            throw new InvalidDataException($"manifest version {manifest.Version} is not supported");
        }

        manifest.GroupPolicy.ValidatePaths();
        manifest.MmcState.ValidatePaths();
        foreach (TemplateEntry entry in manifest.Templates)
        {
            TemplateSet.ValidateRelative(entry.RelativePath);
        }

        return manifest;
    }
}
