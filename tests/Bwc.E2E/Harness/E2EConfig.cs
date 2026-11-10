using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Bwc.E2E.Harness;

/// <summary>
/// Resolves the publish folder, target rid, flavor, and run id used by one E2E test assembly.
/// Environment variables are read once at construction. Relative publish folders resolve against
/// the repository root.
/// </summary>
public sealed class E2EConfig
{
    public const string HubExeName = "BetterWindowsConfig.exe";
    public const string RegeditExeName = "BwcRegedit.exe";
    public const string DataRootEnvironmentVariable = "BWC_DATA_ROOT";

    private const string SolutionFileName = "BetterWindowsConfig.slnx";
    private const string SandboxParentPath = @"HKCU\Software\BWC-E2E";

    private string? _dataRoot;

    private E2EConfig()
    {
        RepoRoot = FindRepoRoot();
        Rid = ReadRid();
        Flavor = ReadFlavor();
        RunId = ReadRunId();
        PublishDir = ResolvePublishDir();
    }

    public string PublishDir { get; }

    public string Rid { get; }

    public string Flavor { get; }

    public string RunId { get; }

    public string RepoRoot { get; }

    public string ArtifactsDir => Path.Combine(RepoRoot, "artifacts", "e2e");

    /// <summary>
    /// Per-run data root for application settings. Created on first use so the applications can
    /// write their settings without touching the developer's per-user folder.
    /// </summary>
    public string DataRoot => _dataRoot ??= EnsureDataDirectory(Path.Combine(ArtifactsDir, "data", RunId));

    public string ScreenshotsDir => Path.Combine(ArtifactsDir, "screenshots");

    public string SandboxRootPath => $@"{SandboxParentPath}\{RunId}";

    public string HubExePath => Path.Combine(PublishDir, HubExeName);

    public string RegeditExePath => Path.Combine(PublishDir, RegeditExeName);

    public string PublishCommand => $"node scripts/dotnet-build.js --rid {Rid} --flavor {Flavor}";

    public static E2EConfig Create() => new();

    /// <summary>
    /// Creates a named directory below <see cref="DataRoot"/>. Tests that must not inherit another
    /// test's LastKey use a scope such as <c>controls-tree</c>.
    /// </summary>
    public string DataRootFor(string scope)
    {
        if (!IsValidRunId(scope))
        {
            throw new ArgumentException(
                $"Data root scope '{scope}' is invalid. Use only letters, digits, dots, and hyphens.",
                nameof(scope));
        }

        return EnsureDataDirectory(Path.Combine(DataRoot, scope));
    }

    /// <summary>Environment override that points a child process at a scoped data root.</summary>
    public IReadOnlyDictionary<string, string> DataRootEnvironment(string scope) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DataRootEnvironmentVariable] = DataRootFor(scope),
        };

    public static bool IsValidRunId(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies both application executables exist and throws an error that names the publish command.
    /// </summary>
    public void EnsureExecutablesExist()
    {
        var missing = new List<string>();
        if (!File.Exists(HubExePath))
        {
            missing.Add(HubExeName);
        }

        if (!File.Exists(RegeditExePath))
        {
            missing.Add(RegeditExeName);
        }

        if (missing.Count == 0)
        {
            return;
        }

        throw new FileNotFoundException(
            $"Publish folder '{PublishDir}' is missing {string.Join(", ", missing)}. " +
            $"Build it with: {PublishCommand}. Or set BWC_PUBLISH_DIR to an existing publish folder.",
            missing[0]);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"{SolutionFileName} was not found above {AppContext.BaseDirectory}. Run the E2E tests from the repository.");
    }

    private static string ReadRid()
    {
        var configured = Environment.GetEnvironmentVariable("BWC_RID");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var rid = configured.Trim().ToLowerInvariant();
            if (rid is not ("win-x64" or "win-arm64"))
            {
                throw new InvalidOperationException(
                    $"BWC_RID '{configured}' is invalid. Expected win-x64 or win-arm64.");
            }

            return rid;
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            var architecture => throw new InvalidOperationException(
                $"Cannot infer a Windows rid from process architecture '{architecture}'. Set BWC_RID to win-x64 or win-arm64."),
        };
    }

    private static string ReadFlavor()
    {
        var configured = Environment.GetEnvironmentVariable("BWC_FLAVOR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "e2e";
        }

        var flavor = configured.Trim().ToLowerInvariant();
        if (flavor is not ("ship" or "e2e"))
        {
            throw new InvalidOperationException(
                $"BWC_FLAVOR '{configured}' is invalid. Expected ship or e2e.");
        }

        return flavor;
    }

    private static string ReadRunId()
    {
        var configured = Environment.GetEnvironmentVariable("BWC_RUN_ID");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            return $"{stamp}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        }

        var runId = configured.Trim();
        if (!IsValidRunId(runId))
        {
            throw new InvalidOperationException(
                $"BWC_RUN_ID '{configured}' is invalid. Use only letters, digits, dots, and hyphens.");
        }

        return runId;
    }

    private string ResolvePublishDir()
    {
        var configured = Environment.GetEnvironmentVariable("BWC_PUBLISH_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim(), RepoRoot);
        }

        var folder = Flavor == "e2e" ? $"{Rid}-e2e" : Rid;
        return Path.Combine(RepoRoot, "publish", folder);
    }

    private static string EnsureDataDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
