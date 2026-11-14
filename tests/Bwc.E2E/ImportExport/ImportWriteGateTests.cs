using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// An import runs under the write gate, so a close request waits for the last key. The test answers Yes
/// on a 2000-key file and closes the window as soon as the import starts. The writes stay under the run
/// sandbox, and the artifact folder is removed in finally.
/// </summary>
[TestClass]
public sealed class ImportWriteGateTests
{
    private const string Caption = "Registry Editor";
    private const int KeyCount = 2000;
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void Import_CloseDuringImportWaitsForEveryKey()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("gate.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        string folder = Path.Combine(config.ArtifactsDir, "importgate", config.RunId);
        string file = Path.Combine(folder, "gate.reg");
        string dataRoot = AppServicesSupport.FreshDataRoot(config, "importgate");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(folder);
            WriteFile(file, sandbox);
            E2EGuards.RequireImportFileInsideSandbox(file, sandbox, "gate.file");
            sandbox.CreateKey("Gate");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using RegeditProcess app = RegeditProcess.Start(config, dataRoot, new[] { file }, environment: environment);
            IntPtr confirm = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout);
            E2EResults.Record("gate.confirm.shown", confirm != IntPtr.Zero, $"confirmation found={confirm != IntPtr.Zero}.");
            if (confirm == IntPtr.Zero)
            {
                Assert.Fail("The import confirmation did not open.");
            }

            AppServicesSupport.ClickButton(app, confirm, "Yes");
            _ = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirm), DialogTimeout);

            // The close request arrives while the import is running. A held close lets the write finish.
            app.Close();

            int written = 0;
            for (int i = 0; i < KeyCount; i++)
            {
                if (sandbox.Exists(KeyRelativePath(i)))
                {
                    written++;
                }
            }

            E2EResults.Record(
                "gate.allKeys",
                written == KeyCount,
                $"keys written={written} of {KeyCount} after the process exited.");
        }
        finally
        {
            sandbox.Cleanup();
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The artifact folder is per run; a locked file is left for the next sweep.
            }
        }

        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(checkStart);
        if (failures.Count > 0)
        {
            Assert.Fail($"Checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }

    private static string KeyRelativePath(int index) => @"Gate\k" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Writes a UTF-16 .reg file with a BOM: one key and one value per index, all under the sandbox.</summary>
    private static void WriteFile(string file, SandboxRegistry sandbox)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        for (int i = 0; i < KeyCount; i++)
        {
            text.Append("\r\n[HKEY_CURRENT_USER\\").Append(sandbox.RunRootPath).Append('\\').Append(KeyRelativePath(i)).Append("]\r\n");
            text.Append("\"v\"=\"").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\"\r\n");
        }

        byte[] body = Encoding.Unicode.GetBytes(text.ToString());
        File.WriteAllBytes(file, Encoding.Unicode.GetPreamble().Concat(body).ToArray());
    }
}
