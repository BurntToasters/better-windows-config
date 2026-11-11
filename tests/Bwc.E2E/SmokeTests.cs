using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E;

/// <summary>
/// Launches the Hub and the Registry Editor and checks their main windows. Every check is also
/// written to artifacts/e2e/result-&lt;rid&gt;-&lt;flavor&gt;.json by assembly cleanup.
/// </summary>
[TestClass]
public sealed class SmokeTests
{
    private const string HubTitle = "Better Windows Config";
    private const string RegeditTitle = "Registry Editor";
    private const string RegeditRootId = "RegeditRoot";

    [TestMethod]
    [TestCategory("Smoke")]
    public void Smoke_HubAndRegeditShowTheirMainWindows()
    {
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
            E2EResults.Note("smoke.publish.executables", config.PublishDir);
        }
        catch (FileNotFoundException ex)
        {
            E2EResults.Record("smoke.publish.executables", false, ex.Message);
            Assert.Fail(ex.Message);
        }

        VerifyHub(config);
        VerifyRegedit(config);

        var failures = E2EResults.FailedChecks();
        Assert.AreEqual(
            0,
            failures.Count,
            $"E2E checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void VerifyHub(E2EConfig config)
    {
        try
        {
            using var session = new AppSession(config, config.HubExePath);
            var title = session.Window.Title;
            E2EResults.Record(
                "smoke.hub.title",
                string.Equals(title, HubTitle, StringComparison.Ordinal),
                $"expected '{HubTitle}', got '{title}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("smoke.hub.error", false, ex.Message);
        }
    }

    private static void VerifyRegedit(E2EConfig config)
    {
        try
        {
            using var session = new AppSession(config, config.RegeditExePath);
            var title = session.Window.Title;
            E2EResults.Record(
                "smoke.regedit.title",
                string.Equals(title, RegeditTitle, StringComparison.Ordinal),
                $"expected '{RegeditTitle}', got '{title}'.");

            var rootText = session.WaitForText(RegeditRootId, RegeditTitle, TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "smoke.regedit.root",
                string.Equals(rootText, RegeditTitle, StringComparison.Ordinal),
                $"automation id '{RegeditRootId}' expected '{RegeditTitle}', got '{rootText ?? "(none)"}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("smoke.regedit.error", false, ex.Message);
        }
    }
}
