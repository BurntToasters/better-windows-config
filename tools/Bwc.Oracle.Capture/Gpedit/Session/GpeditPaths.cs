namespace Bwc.Oracle.Capture.Gpedit.Session;

// One registry key the session may delete. Nothing outside the allowed list is ever deleted (docs/failure-modes/oracle-gpedit-session.md, section 9).
internal sealed record RegistryTarget(bool LocalMachine, string SubKey)
{
    internal string Display => (LocalMachine ? "HKLM" : "HKCU") + "\\" + SubKey;
}

// Every folder, file and key the session touches. Real() is the machine; ForSandbox() is only used by `gpedit selftest`.
internal sealed class GpeditPaths
{
    internal const string PolicyKey = @"Software\Policies\BWC-Oracle";
    internal const string GpeditKey = @"Software\BWC-Oracle-Gpedit";
    internal const string SelftestKey = @"Software\BWC-Oracle-Gpedit-Selftest";

    private GpeditPaths()
    {
    }

    internal bool Sandbox { get; private init; }

    internal string OutputDir { get; private init; } = string.Empty;

    internal string BackupDir { get; private init; } = string.Empty;

    internal string ManifestPath { get; private init; } = string.Empty;

    internal string GroupPolicyDir { get; private init; } = string.Empty;

    internal string MmcStatePath { get; private init; } = string.Empty;

    internal string PolicyDefinitionsDir { get; private init; } = string.Empty;

    internal string PolicyDefinitionsEnUsDir => Path.Combine(PolicyDefinitionsDir, "en-US");

    internal IReadOnlyList<RegistryTarget> RegistryTargets { get; private init; } = Array.Empty<RegistryTarget>();

    internal string GpeditMsc => Path.Combine(Environment.SystemDirectory, "gpedit.msc");

    internal static GpeditPaths Real(RepoPaths repo)
    {
        string output = Path.Combine(repo.Root, "artifacts", "oracle", "gpedit");
        string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new GpeditPaths
        {
            Sandbox = false,
            OutputDir = output,
            BackupDir = Path.Combine(output, "backup"),
            ManifestPath = Path.Combine(output, "backup", "manifest.json"),
            GroupPolicyDir = Path.Combine(Environment.SystemDirectory, "GroupPolicy"),
            MmcStatePath = Path.Combine(appData, "Microsoft", "MMC", "gpedit"),
            PolicyDefinitionsDir = Path.Combine(systemRoot, "PolicyDefinitions"),
            RegistryTargets = new[]
            {
                new RegistryTarget(true, PolicyKey),
                new RegistryTarget(false, PolicyKey),
                new RegistryTarget(true, GpeditKey),
                new RegistryTarget(false, GpeditKey),
            },
        };
    }

    // The sandbox folders are created by the caller. The one registry key is under HKCU and is not one of the real four.
    internal static GpeditPaths ForSandbox(RepoPaths repo)
    {
        string output = Path.Combine(repo.Root, "artifacts", "oracle", "gpedit", "selftest");
        string box = Path.Combine(output, "sandbox");
        return new GpeditPaths
        {
            Sandbox = true,
            OutputDir = output,
            BackupDir = Path.Combine(output, "backup"),
            ManifestPath = Path.Combine(output, "backup", "manifest.json"),
            GroupPolicyDir = Path.Combine(box, "System32", "GroupPolicy"),
            MmcStatePath = Path.Combine(box, "AppData", "Microsoft", "MMC", "gpedit"),
            PolicyDefinitionsDir = Path.Combine(box, "PolicyDefinitions"),
            RegistryTargets = new[] { new RegistryTarget(false, SelftestKey) },
        };
    }

    internal string SandboxRoot => Path.Combine(OutputDir, "sandbox");
}
