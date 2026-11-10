using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Bwc.E2E;

/// <summary>
/// Launches the Hub and the Registry Editor from a publish folder and checks their main windows.
/// BWC_PUBLISH_DIR selects the folder; a relative value is relative to the repo root. The default is publish/win-arm64.
/// Writes TestResults/e2e-smoke-&lt;folder&gt;.json.
/// </summary>
[TestClass]
public sealed class SmokeTests
{
    private const string GateName = @"Local\BwcGuiTestLock";
    private const string HubExe = "BetterWindowsConfig.exe";
    private const string RegeditExe = "BwcRegedit.exe";
    private const string HubTitle = "Better Windows Config";
    private const string RegeditTitle = "Registry Editor";
    private const string RegeditRootId = "RegeditRoot";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public void HubAndRegeditShowTheirMainWindows()
    {
        var repoRoot = FindRepoRoot();
        var publishDir = ResolvePublishDir(repoRoot);
        var hubPath = Path.Combine(publishDir, HubExe);
        var regeditPath = Path.Combine(publishDir, RegeditExe);

        var missing = new[] { hubPath, regeditPath }.Where(p => !File.Exists(p)).Select(p => Path.GetFileName(p)).ToArray();
        if (missing.Length > 0)
        {
            Assert.Fail($"BWC_PUBLISH_DIR '{publishDir}' does not contain {string.Join(", ", missing)}. Publish first (docs/build-setup.md) or set BWC_PUBLISH_DIR.");
        }

        var checks = new List<SmokeCheck>();
        try
        {
            using var gate = new Mutex(false, GateName);
            AcquireGate(gate);
            try
            {
                RunSmoke(hubPath, regeditPath, checks);
            }
            finally
            {
                gate.ReleaseMutex();
            }
        }
        finally
        {
            WriteArtifact(repoRoot, publishDir, checks);
        }

        var failed = checks.Where(c => !c.Pass).Select(c => $"{c.Name}: {c.Detail}").ToArray();
        Assert.AreEqual(0, failed.Length, string.Join(Environment.NewLine, failed));
    }

    private static void RunSmoke(string hubPath, string regeditPath, List<SmokeCheck> checks)
    {
        using var automation = new UIA3Automation();

        using (var hub = StartTool(hubPath))
        {
            try
            {
                var window = WaitForMainWindow(automation, hub, HubExe);
                Expect(checks, "Hub main window title", HubTitle, window.Title);
            }
            finally
            {
                Stop(hub);
            }
        }

        using (var regedit = StartTool(regeditPath))
        {
            try
            {
                var window = WaitForMainWindow(automation, regedit, RegeditExe);
                Expect(checks, "Registry Editor main window title", RegeditTitle, window.Title);
                var root = Retry.WhileNull(() => window.FindFirstDescendant(cf => cf.ByAutomationId(RegeditRootId)), TimeSpan.FromSeconds(15)).Result;
                Expect(checks, "Registry Editor RegeditRoot text", RegeditTitle, root?.Name);
            }
            finally
            {
                Stop(regedit);
            }
        }
    }

    private static void Expect(List<SmokeCheck> checks, string name, string expected, string? actual)
    {
        checks.Add(new SmokeCheck(name, actual == expected, $"expected '{expected}', got '{actual ?? "(none)"}'"));
    }

    private static Process StartTool(string exePath)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        // Keeps the highestAvailable manifest of BwcRegedit from raising a UAC prompt. The Hub passes it on to its children.
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Process.Start returned no process for {exePath}.");
    }

    // Polls the process directly. FlaUI's Application wrapper disposes the Process it is given, which breaks later calls.
    private static Window WaitForMainWindow(UIA3Automation automation, Process process, string exeName)
    {
        var deadline = DateTime.UtcNow + WindowTimeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{exeName} exited with code 0x{process.ExitCode:X8} before showing a main window.");
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return automation.FromHandle(process.MainWindowHandle).AsWindow();
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"{exeName} showed no main window within {WindowTimeout.TotalSeconds:0} s.");
    }

    private static void Stop(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.CloseMainWindow();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static void AcquireGate(Mutex gate)
    {
        try
        {
            if (!gate.WaitOne(TimeSpan.FromMinutes(15)))
            {
                throw new TimeoutException($"Timed out waiting for {GateName}.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder exited without releasing the mutex; ownership has transferred to this thread.
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BetterWindowsConfig.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"BetterWindowsConfig.slnx was not found above {AppContext.BaseDirectory}.");
    }

    private static string ResolvePublishDir(string repoRoot)
    {
        var configured = Environment.GetEnvironmentVariable("BWC_PUBLISH_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(repoRoot, "publish", "win-arm64")
            : Path.GetFullPath(configured, repoRoot);
    }

    private static void WriteArtifact(string repoRoot, string publishDir, List<SmokeCheck> checks)
    {
        var dir = Path.Combine(repoRoot, "TestResults");
        Directory.CreateDirectory(dir);
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(publishDir));
        var artifact = new
        {
            generatedUtc = DateTime.UtcNow,
            publishDir,
            pass = checks.Count > 0 && checks.All(c => c.Pass),
            checks,
        };
        File.WriteAllText(
            Path.Combine(dir, $"e2e-smoke-{folder}.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal sealed record SmokeCheck(string Name, bool Pass, string Detail);
