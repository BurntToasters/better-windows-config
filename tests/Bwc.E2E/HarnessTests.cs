using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E;

[TestClass]
public sealed class HarnessTests
{
    [TestMethod]
    [TestCategory("Harness")]
    public void SandboxRoundTripAndCleanup()
    {
        Run("harness.sandbox", () =>
        {
            var sandbox = new SandboxRegistry(GuiGate.Config);
            try
            {
                using (sandbox.CreateSubKey("roundtrip"))
                {
                    // The key must exist before values are written.
                }

                sandbox.SetString("roundtrip", "text", "hello");
                sandbox.SetDword("roundtrip", "number", 42);
                sandbox.SetBinary("roundtrip", "bytes", [1, 2, 3]);

                var bytes = sandbox.ReadBinary("roundtrip", "bytes");
                Assert.AreEqual("hello", sandbox.ReadString("roundtrip", "text"));
                Assert.AreEqual(42, sandbox.ReadDword("roundtrip", "number"));
                Assert.IsNotNull(bytes);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);

                sandbox.Cleanup();
                Assert.IsFalse(sandbox.Exists(string.Empty), "The run subtree still exists after Cleanup.");
            }
            finally
            {
                sandbox.Cleanup();
            }
        });
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void SweepStaleLeavesNonMatchingKeysAlone()
    {
        Run("harness.sweep.nonmatching", () =>
        {
            var sandbox = new SandboxRegistry(GuiGate.Config);
            try
            {
                using (sandbox.CreateSubKey("not-a-run-key"))
                {
                }

                sandbox.SetString("not-a-run-key", "value", "keep");
                sandbox.SweepStale(TimeSpan.FromHours(24));

                Assert.IsTrue(sandbox.Exists("not-a-run-key"), "SweepStale removed a non matching key.");
            }
            finally
            {
                sandbox.DeleteSubKeyTree("not-a-run-key");
            }
        });
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void ConfigDefaultsAndValidation()
    {
        Run("harness.config", () =>
        {
            var names = new[] { "BWC_PUBLISH_DIR", "BWC_RID", "BWC_FLAVOR", "BWC_RUN_ID" };
            var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

            try
            {
                foreach (var name in names)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }

                var config = E2EConfig.Create();
                Assert.IsTrue(E2EConfig.IsValidRunId(config.RunId), "Generated run id is not safe.");
                Assert.AreEqual("e2e", config.Flavor);
                Assert.AreEqual(Path.Combine(config.RepoRoot, "artifacts", "e2e"), config.ArtifactsDir);
                Assert.AreEqual($@"HKCU\Software\BWC-E2E\{config.RunId}", config.SandboxRootPath);
                Assert.IsTrue(config.PublishDir.EndsWith("-e2e", StringComparison.Ordinal));
                Assert.IsTrue(File.Exists(Path.Combine(config.RepoRoot, "BetterWindowsConfig.slnx")));

                Environment.SetEnvironmentVariable("BWC_RUN_ID", "bad/run");
                Assert.Throws<InvalidOperationException>(() => E2EConfig.Create());

                Environment.SetEnvironmentVariable("BWC_RUN_ID", null);
                Environment.SetEnvironmentVariable("BWC_RID", "linux-x64");
                Assert.Throws<InvalidOperationException>(() => E2EConfig.Create());

                Environment.SetEnvironmentVariable("BWC_RID", null);
                Environment.SetEnvironmentVariable("BWC_FLAVOR", "debug");
                Assert.Throws<InvalidOperationException>(() => E2EConfig.Create());
            }
            finally
            {
                foreach (var pair in saved)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }
            }
        });
    }

    [TestMethod]
    [TestCategory("Harness")]
    public void ScreenshotHelperWritesAPng()
    {
        Run("harness.screenshot", () =>
        {
            ControlTestSupport.PrepareDpi();
            var config = GuiGate.Config;
            using var session = new AppSession(config, config.HubExePath);
            bool toolRowsReady = ControlTestSupport.WaitUntil(
                () => IsVisibleAndUsableToolRow(session, "ToolRow_regedit") &&
                    IsVisibleAndUsableToolRow(session, "ToolRow_gpedit"),
                TimeSpan.FromSeconds(15));
            Assert.IsTrue(
                toolRowsReady,
                "ToolRow_regedit and ToolRow_gpedit did not both become visible with usable UIA bounds before capture.");

            var path = E2EResults.CaptureScreenshot("harness.screenshot", session);
            try
            {
                Assert.IsTrue(File.Exists(path), $"Screenshot '{path}' was not written.");
                Assert.IsTrue(new FileInfo(path).Length > 0, $"Screenshot '{path}' is empty.");
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });
    }

    private static bool IsVisibleAndUsableToolRow(AppSession session, string automationId)
    {
        var row = session.Find(automationId);
        if (row is null || row.Properties.IsOffscreen.ValueOrDefault)
        {
            return false;
        }

        var bounds = row.BoundingRectangle;
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static void Run(string id, Action action)
    {
        try
        {
            action();
            E2EResults.Note(id, "ok");
        }
        catch (WindowCapture.NotJudgedException ex)
        {
            // Covered by another process: nothing was judged, so this is a note and an inconclusive test, not a failure.
            E2EResults.Note(id, $"not judged: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            E2EResults.Record(id, false, ex.Message);
            throw;
        }
    }
}
