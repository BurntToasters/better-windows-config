using System.Diagnostics;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit;

/// <summary>
/// Launches the published Local Group Policy Editor and checks its empty main window: the title, the root element, the
/// AppUserModelID, and that it closes. The window touches no policy: the scaffold has no store. Evidence is written to
/// artifacts/e2e/result-&lt;rid&gt;-&lt;flavor&gt;.json by assembly cleanup (docs/failure-modes/gpedit-scaffold.md).
/// </summary>
[TestClass]
public sealed class GpeditSmokeTests
{
    private const string GpeditTitle = "Local Group Policy Editor";
    private const string GpeditRootId = "GpeditRoot";
    private const string GpeditAppUserModelId = "BetterWindowsConfig.Gpedit";
    private const string SmokeScope = "gpedit-smoke";
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_Smoke_WindowOpens()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
            E2EResults.Note("gpedit.publish.executables", config.PublishDir);
        }
        catch (FileNotFoundException ex)
        {
            E2EResults.Record("gpedit.publish.executables", false, ex.Message);
            Assert.Fail(ex.Message);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(SmokeScope))
        {
            environment[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(SmokeScope))
        {
            environment[pair.Key] = pair.Value;
        }

        using var session = new AppSession(config, config.GpeditExePath, environment: environment);
        E2EResults.CurrentSession = session;

        string title = session.Window.Title;
        E2EResults.Record(
            "gpedit.title",
            string.Equals(title, GpeditTitle, StringComparison.Ordinal),
            $"expected '{GpeditTitle}', got '{title}'.");

        string? rootText = session.WaitForText(GpeditRootId, GpeditTitle, TimeSpan.FromSeconds(15));
        E2EResults.Record(
            "gpedit.root",
            string.Equals(rootText, GpeditTitle, StringComparison.Ordinal),
            $"automation id '{GpeditRootId}' expected '{GpeditTitle}', got '{rootText ?? "(none)"}'.");

        // The process-level id is not visible to another process through the window property store (the note below
        // keeps the reason). The app reads it back from its own process after setting it and logs it, so the check reads
        // the log under the run's data root. The window store is still read: an id found there must match.
        string expectedLine = "AppUserModelID: " + GpeditAppUserModelId;
        bool logged = WaitForLogLine(config.DataRootFor(SmokeScope), expectedLine, TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "gpedit.aumid",
            logged,
            logged
                ? $"The log of the running process holds '{expectedLine}'."
                : $"The log under the data root has no line '{expectedLine}' within 10 s.");

        WindowAppUserModelId.Result windowId = WindowAppUserModelId.Read(session.MainHwnd);
        if (windowId.Id is null)
        {
            E2EResults.Note("gpedit.aumid.window", $"The AppUserModelID is not in the window property store: {windowId.Detail}");
        }
        else
        {
            E2EResults.Record(
                "gpedit.aumid.window",
                string.Equals(windowId.Id, GpeditAppUserModelId, StringComparison.Ordinal),
                $"expected '{GpeditAppUserModelId}', got '{windowId.Id}'.");
        }

        int processId = session.ProcessId;
        session.Close();
        E2EResults.Record("gpedit.closed", !IsRunning(processId), $"Gpedit pid {processId} was closed by the test.");

        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Gpedit smoke checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>Polls the tool's daily log under the data root for a line that holds the text.</summary>
    private static bool WaitForLogLine(string dataRoot, string text, TimeSpan timeout)
    {
        string folder = Path.Combine(dataRoot, "gpedit", "logs");
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "log-*.txt"))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (line.Contains(text, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.WaitForExit(CloseTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
