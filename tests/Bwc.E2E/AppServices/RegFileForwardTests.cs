using System.Diagnostics;
using System.Text;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.AppServices;

/// <summary>
/// Second launches that name a .reg file, checked with real processes and real dialogs. A relative
/// path is resolved in the folder of the second launch. A file forwarded while a confirmation is open
/// is queued and asked about later, never dropped. A plain second launch brings the window forward.
/// Every import writes only under the run sandbox, and every confirmation is answered No.
/// </summary>
[TestClass]
public sealed class RegFileForwardTests
{
    private const string Caption = "Registry Editor";
    private const string Adding = "Adding information";
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("AppServices")]
    [TestCategory("Mutating")]
    public void AppServices_SecondLaunchResolvesRelativeRegFileInItsOwnFolder()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        RequirePublish(config);
        E2EGuards.RequireMutatingFlavor(config);

        var sandbox = new SandboxRegistry(config);
        string folder = Path.Combine(config.ArtifactsDir, "appservices", config.RunId, "relative");
        string file = Path.Combine(folder, "rel.reg");
        string dataRoot = AppServicesSupport.FreshDataRoot(config, "relreg");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(folder);
            WriteImportFile(file, sandbox, @"Import\Relative", "relative");
            E2EGuards.RequireImportFileInsideSandbox(file, sandbox, "appsvc.rel.file");
            sandbox.CreateKey("Import");
            IReadOnlyDictionary<string, string> environment = SandboxEnvironment(sandbox);

            // The first instance runs in the publish folder, where the relative name does not exist.
            using RegeditProcess first = RegeditProcess.Start(config, dataRoot, Array.Empty<string>(), environment: environment);
            using Process second = RegeditProcess.StartProcess(
                config,
                dataRoot,
                new[] { "rel.reg" },
                environment,
                workingDirectory: folder);

            IntPtr confirm = AppServicesSupport.WaitForOwnedWindow(first.MainHwnd, null, Caption, DialogTimeout);
            E2EResults.Record("appsvc.rel.confirm.shown", confirm != IntPtr.Zero, $"confirmation found={confirm != IntPtr.Zero}.");
            if (confirm == IntPtr.Zero)
            {
                Assert.Fail("The relative .reg path did not reach the running instance as a confirmation.");
            }

            string text = AppServicesSupport.WaitForDialogText(
                confirm,
                t => t.Contains(Adding, StringComparison.Ordinal) && t.Contains(file, StringComparison.Ordinal),
                DialogTimeout);
            E2EResults.Record(
                "appsvc.rel.confirm.fullPath",
                text.Contains(Adding, StringComparison.Ordinal) && text.Contains(file, StringComparison.Ordinal),
                $"The confirmation names the file in the second launch's folder: '{file}'.");

            AppServicesSupport.ClickButton(first, confirm, "No");
            bool closed = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirm), DialogTimeout);
            E2EResults.Record("appsvc.rel.confirm.closed", closed, "The confirmation closed after No.");

            bool exited = second.WaitForExit(15000);
            E2EResults.Record("appsvc.rel.second.exited", exited && second.ExitCode == 0, $"second launch exited={exited}, code={(exited ? second.ExitCode : -1)}.");

            bool absent = !sandbox.Exists(@"Import\Relative");
            E2EResults.Record("appsvc.rel.nothingWritten", absent, "No key was written after No.");

            first.Close();
            FailIfNeeded(checkStart);
        }
        finally
        {
            sandbox.Cleanup();
            DeleteFolder(folder);
        }
    }

    [TestMethod]
    [TestCategory("AppServices")]
    [TestCategory("Mutating")]
    public void AppServices_ForwardedRegFileWaitsForOpenDialogInsteadOfBeingDropped()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        RequirePublish(config);
        E2EGuards.RequireMutatingFlavor(config);

        var sandbox = new SandboxRegistry(config);
        string folder = Path.Combine(config.ArtifactsDir, "appservices", config.RunId, "queue");
        string fileA = Path.Combine(folder, "a.reg");
        string fileB = Path.Combine(folder, "b.reg");
        string dataRoot = AppServicesSupport.FreshDataRoot(config, "queuereg");

        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(folder);
            WriteImportFile(fileA, sandbox, @"Import\QueueA", "a");
            WriteImportFile(fileB, sandbox, @"Import\QueueB", "b");
            E2EGuards.RequireImportFileInsideSandbox(fileA, sandbox, "appsvc.queue.fileA");
            E2EGuards.RequireImportFileInsideSandbox(fileB, sandbox, "appsvc.queue.fileB");
            sandbox.CreateKey("Import");
            IReadOnlyDictionary<string, string> environment = SandboxEnvironment(sandbox);

            using RegeditProcess first = RegeditProcess.Start(config, dataRoot, new[] { fileA }, environment: environment);
            IntPtr confirmA = AppServicesSupport.WaitForOwnedWindow(first.MainHwnd, null, Caption, DialogTimeout);
            bool aShown = confirmA != IntPtr.Zero && AppServicesSupport.WaitForDialogText(
                confirmA,
                t => t.Contains(fileA, StringComparison.Ordinal),
                DialogTimeout).Contains(fileA, StringComparison.Ordinal);
            E2EResults.Record("appsvc.queue.a.shown", aShown, "The first file's confirmation is open.");
            if (!aShown)
            {
                Assert.Fail("The first .reg confirmation did not open.");
            }

            // The second file arrives while the first confirmation is open.
            using Process second = RegeditProcess.StartProcess(config, dataRoot, new[] { fileB }, environment);
            bool secondExited = second.WaitForExit(15000);
            E2EResults.Record(
                "appsvc.queue.second.exited",
                secondExited && second.ExitCode == 0,
                $"second launch exited={secondExited}, code={(secondExited ? second.ExitCode : -1)}.");

            AppServicesSupport.ClickButton(first, confirmA, "No");
            bool closedA = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirmA), DialogTimeout);
            E2EResults.Record("appsvc.queue.a.closed", closedA, "The first confirmation closed after No.");

            IntPtr confirmB = AppServicesSupport.WaitForOwnedWindow(first.MainHwnd, null, Caption, DialogTimeout, confirmA);
            string textB = confirmB == IntPtr.Zero
                ? string.Empty
                : AppServicesSupport.WaitForDialogText(confirmB, t => t.Contains(fileB, StringComparison.Ordinal), DialogTimeout);
            bool bShown = textB.Contains(Adding, StringComparison.Ordinal) && textB.Contains(fileB, StringComparison.Ordinal);
            E2EResults.Record(
                "appsvc.queue.b.shown",
                bShown,
                $"The forwarded file's confirmation appeared after the first answer, found={confirmB != IntPtr.Zero}.");

            if (confirmB != IntPtr.Zero)
            {
                AppServicesSupport.ClickButton(first, confirmB, "No");
                _ = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirmB), DialogTimeout);
            }

            bool absent = !sandbox.Exists(@"Import\QueueA") && !sandbox.Exists(@"Import\QueueB");
            E2EResults.Record("appsvc.queue.nothingWritten", absent, "Both files were answered No, so nothing was written.");

            first.Close();
            FailIfNeeded(checkStart);
        }
        finally
        {
            sandbox.Cleanup();
            DeleteFolder(folder);
        }
    }

    [TestMethod]
    [TestCategory("AppServices")]
    public void AppServices_SecondLaunchBringsFirstWindowToForeground()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        RequirePublish(config);

        string dataRoot = AppServicesSupport.FreshDataRoot(config, "fgreg");
        using RegeditProcess first = RegeditProcess.Start(config, dataRoot, Array.Empty<string>());
        using Process second = RegeditProcess.StartProcess(config, dataRoot, Array.Empty<string>());
        bool exited = second.WaitForExit(15000);
        E2EResults.Record("appsvc.fg.second.exited", exited && second.ExitCode == 0, $"second launch exited={exited}.");

        bool foreground = AppServicesSupport.WaitUntil(() => AppServicesSupport.IsForeground(first.MainHwnd), TimeSpan.FromSeconds(5));
        E2EResults.Record("appsvc.fg.first", foreground, "The first window is the foreground window after the second launch.");

        first.Close();
        FailIfNeeded(checkStart);
    }

    /// <summary>Writes a UTF-16 .reg file with a BOM that creates one key and one value under the sandbox.</summary>
    private static void WriteImportFile(string file, SandboxRegistry sandbox, string relativeKey, string valueData)
    {
        string text = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\" + relativeKey + "]\r\n"
            + "\"Value\"=\"" + valueData + "\"\r\n";
        byte[] body = Encoding.Unicode.GetBytes(text);
        File.WriteAllBytes(file, Encoding.Unicode.GetPreamble().Concat(body).ToArray());
    }

    private static IReadOnlyDictionary<string, string> SandboxEnvironment(SandboxRegistry sandbox) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit path may still hold a file for a moment; the artifact folder is per run.
        }
    }

    private static void RequirePublish(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("appsvc.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    private static void FailIfNeeded(int startIndex)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(startIndex);
        if (failures.Count > 0)
        {
            Assert.Fail($"Checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }
}
