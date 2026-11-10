using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Bwc.Spike.E2E;

/// <summary>
/// Verifies the published folders in spikes/S1-publish/out/&lt;rid&gt;/ (produced by publish.ps1).
/// Each RID runs the same checks; a RID whose folder is missing is Inconclusive and is recorded as notPublished.
/// GUI work runs under Local\BwcGuiTestLock. Every launch sets __COMPAT_LAYER=RunAsInvoker, so the highestAvailable
/// Tool never raises a UAC prompt. The result artifact is written to out/s1-result.json at class cleanup.
/// </summary>
[TestClass]
public sealed class S1PublishTests
{
    private const string GuiMutexName = @"Local\BwcGuiTestLock";
    private const string CompatLayer = "RunAsInvoker";
    private const string LibraryText = "Shared library string OK";
    private const string ProbeBadgeId = "ProbeBadge";
    private const string ElevationId = "Elevation";

    private static readonly string[] Rids = ["win-x64", "win-arm64"];
    private static readonly Dictionary<string, RidResult> Results = new();
    private static readonly object ResultsGate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HeadSpec Hub = new("BwcSpikeHub.exe", "BwcSpikeHub", "Hub", "asInvoker", "BWC Spike Hub");
    private static readonly HeadSpec Tool = new("BwcSpikeTool.exe", "BwcSpikeTool", "Tool", "highestAvailable", "BWC Spike Tool");

    [TestMethod]
    public void Win_x64_HeadsPassAllChecks() => RunRid("win-x64");

    [TestMethod]
    public void Win_arm64_HeadsPassAllChecks() => RunRid("win-arm64");

    [ClassCleanup]
    public static void WriteResultArtifact()
    {
        var outDir = Path.Combine(FindSpikeRoot(), "out");
        Directory.CreateDirectory(outDir);

        var rids = new Dictionary<string, RidResult>();
        lock (ResultsGate)
        {
            foreach (var rid in Rids)
            {
                rids[rid] = Results.TryGetValue(rid, out var r) ? r : new RidResult(rid) { Status = "notRun" };
            }
        }

        var artifact = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            overall = rids.Values.All(r => r.Status == "passed") ? "passed" : "failed",
            machine = new
            {
                osDescription = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
            },
            rids,
        };
        File.WriteAllText(Path.Combine(outDir, "s1-result.json"), JsonSerializer.Serialize(artifact, JsonOptions), Encoding.UTF8);
    }

    private static void RunRid(string rid)
    {
        var root = FindSpikeRoot();
        var folder = Path.Combine(root, "out", rid);
        var result = new RidResult(rid);
        lock (ResultsGate) Results[rid] = result;

        if (!Directory.Exists(folder))
        {
            result.Status = "notPublished";
            Assert.Inconclusive($"{folder} does not exist. Run: powershell -File spikes\\S1-publish\\publish.ps1 -Rid {rid}");
        }

        var (files, bytes) = FolderSize(folder);
        result.FolderFiles = files;
        result.FolderBytes = bytes;

        ReadPublishReport(root, rid, result);

        foreach (var head in new[] { Hub, Tool })
        {
            var exe = Path.Combine(folder, head.Exe);
            Record(result, $"{head.Label}: {head.Exe} present", File.Exists(exe), exe);
            if (!File.Exists(exe)) continue;

            var level = ManifestLevel(exe);
            Record(result, $"{head.Label}: embedded manifest requestedExecutionLevel is '{head.Level}'", level == head.Level, $"level='{level}'");

            var pri = Path.Combine(folder, head.Assembly + ".pri");
            var priText = File.Exists(pri) ? Encoding.Latin1.GetString(File.ReadAllBytes(pri)) : string.Empty;
            Record(result, $"{head.Label}: {head.Assembly}.pri present and holds the library string", priText.Contains("ProbeText") && priText.Contains(LibraryText), File.Exists(pri) ? pri : "missing");
        }

        foreach (var head in new[] { Hub, Tool })
        {
            var exe = Path.Combine(folder, head.Exe);
            if (!File.Exists(exe)) continue;
            RunWindowChecks(result, folder, head);
            RunExitCodeCheck(result, folder, head);
        }

        result.Status = result.Checks.All(c => c.Pass) ? "passed" : "failed";
        if (result.Status != "passed")
        {
            var failed = result.Checks.Where(c => !c.Pass).Select(c => $"{c.Name} [{c.Detail}]");
            Assert.Fail($"{rid}: {string.Join("; ", failed)}");
        }
    }

    private static void RunWindowChecks(RidResult result, string folder, HeadSpec head)
    {
        var mutex = AcquireGuiLock();
        try
        {
            using var automation = new UIA3Automation();
            var app = FlaUI.Core.Application.Launch(StartInfo(folder, head.Exe, string.Empty));
            try
            {
                var window = WaitFor(() => app.GetAllTopLevelWindows(automation).FirstOrDefault(w => w.Title == head.Title), TimeSpan.FromSeconds(30));
                Record(result, $"{head.Label}: window '{head.Title}' shown", window != null, window != null ? "found" : "not found within 30s");

                // The AutomationId is on the TextBlock inside ProbeBadge; its UIA Name is the displayed text.
                var badgeText = window == null ? null : WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId(ProbeBadgeId)), TimeSpan.FromSeconds(15));
                Record(result, $"{head.Label}: ProbeBadge (AutomationId) present", badgeText != null, badgeText != null ? "found" : "not found");
                Record(result, $"{head.Label}: ProbeBadge shows '{LibraryText}'", badgeText?.Name == LibraryText, $"text='{badgeText?.Name}'");

                var elevation = window == null ? null : WaitFor(() => window.FindFirstDescendant(cf => cf.ByAutomationId(ElevationId)), TimeSpan.FromSeconds(15));
                Record(result, $"{head.Label}: Elevation text is 'not elevated'", elevation?.Name == "not elevated", $"text='{elevation?.Name}'");
            }
            finally
            {
                CloseApp(app);
            }
        }
        finally
        {
            ReleaseGuiLock(mutex);
        }
    }

    private static void RunExitCodeCheck(RidResult result, string folder, HeadSpec head)
    {
        var mutex = AcquireGuiLock();
        try
        {
            using var proc = Process.Start(StartInfo(folder, head.Exe, "--exit-code 7 --auto-close-ms 1500"))
                ?? throw new InvalidOperationException("Process.Start returned null for " + head.Exe);
            var exited = proc.WaitForExit(30_000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            var code = exited ? proc.ExitCode : -1;
            Record(result, $"{head.Label}: --exit-code 7 --auto-close-ms 1500 closes itself and returns 7", exited && code == 7, $"exited={exited} exitCode={code}");
        }
        finally
        {
            ReleaseGuiLock(mutex);
        }
    }

    private static void ReadPublishReport(string root, string rid, RidResult result)
    {
        var path = Path.Combine(root, "out", $"publish-{rid}.json");
        if (!File.Exists(path))
        {
            Record(result, "publish report present (run publish.ps1)", false, path);
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;

        var shared = r.GetProperty("sharedFolder");
        Record(result, "publish report matches the folder (file count and bytes)",
            shared.GetProperty("files").GetInt32() == result.FolderFiles && shared.GetProperty("bytes").GetInt64() == result.FolderBytes,
            $"report files={shared.GetProperty("files").GetInt32()} bytes={shared.GetProperty("bytes").GetInt64()}; folder files={result.FolderFiles} bytes={result.FolderBytes}");

        var sep = r.GetProperty("separateFolders");
        result.SeparateHubBytes = sep.GetProperty("hubBytes").GetInt64();
        result.SeparateToolBytes = sep.GetProperty("toolBytes").GetInt64();
        result.SeparateTotalBytes = sep.GetProperty("totalBytes").GetInt64();
        result.SavedBytes = r.GetProperty("savedBytes").GetInt64();
        result.SavedPercent = r.GetProperty("savedPercent").GetDouble();

        var warnings = r.GetProperty("buildWarnings").EnumerateObject().Sum(p => p.Value.GetInt32());
        Record(result, "publish: 0 build warnings in spike projects", warnings == 0, $"warning lines={warnings}");

        var collisions = r.GetProperty("collisionsDifferent").GetArrayLength();
        Record(result, "publish: no file with different content written by the second publish", collisions == 0, $"collisions={collisions}");

        var removed = r.GetProperty("hubFilesRemoved").GetArrayLength();
        Record(result, "publish: second publish removed no Hub file", removed == 0, $"removed={removed}");
    }

    private static string ManifestLevel(string exe)
    {
        var xml = PeManifest.ReadXml(exe);
        return XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName == "requestedExecutionLevel")
            .Select(e => (string?)e.Attribute("level") ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;
    }

    private static ProcessStartInfo StartInfo(string folder, string exe, string arguments)
    {
        var psi = new ProcessStartInfo(Path.Combine(folder, exe), arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = folder,
        };
        psi.Environment["__COMPAT_LAYER"] = CompatLayer;
        return psi;
    }

    private static void CloseApp(FlaUI.Core.Application app)
    {
        try
        {
            if (!app.HasExited)
            {
                app.Close();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!app.HasExited && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
                if (!app.HasExited)
                {
                    app.Kill();
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static Mutex AcquireGuiLock()
    {
        var mutex = new Mutex(false, GuiMutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromMinutes(15)))
            {
                mutex.Dispose();
                throw new TimeoutException($"Timed out waiting for {GuiMutexName}");
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing it; this call now owns the mutex.
        }
        return mutex;
    }

    private static void ReleaseGuiLock(Mutex mutex)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    private static T? WaitFor<T>(Func<T?> find, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var value = find();
            if (value != null) return value;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(250);
        }
    }

    private static (int Files, long Bytes) FolderSize(string folder)
    {
        var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        return (files.Length, files.Sum(f => f.Length));
    }

    private static void Record(RidResult result, string name, bool pass, string detail)
    {
        result.Checks.Add(new CheckRecord(name, pass, detail));
    }

    private static string FindSpikeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "S1.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("S1.slnx not found above " + AppContext.BaseDirectory);
    }

    private sealed record HeadSpec(string Exe, string Assembly, string Label, string Level, string Title);

    private sealed record CheckRecord(string Name, bool Pass, string Detail);

    private sealed class RidResult
    {
        public RidResult(string rid)
        {
            Rid = rid;
            Folder = "out/" + rid;
        }

        public string Rid { get; }
        public string Folder { get; }
        public string Status { get; set; } = "notRun";
        public int FolderFiles { get; set; }
        public long FolderBytes { get; set; }
        public long? SeparateHubBytes { get; set; }
        public long? SeparateToolBytes { get; set; }
        public long? SeparateTotalBytes { get; set; }
        public long? SavedBytes { get; set; }
        public double? SavedPercent { get; set; }
        public List<CheckRecord> Checks { get; } = new();
    }
}
