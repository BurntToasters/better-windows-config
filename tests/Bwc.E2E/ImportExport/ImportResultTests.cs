using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// Checks what the window says after a .reg file is imported. The file is opened the startup way
/// (regedit file.reg), Yes is answered on the confirmation, and the result dialog is read and dismissed.
/// Three files are used. A file with one line the write policy refuses imports the rest and shows
/// string 132. A file with one malformed line imports the rest and shows string 228, because classic
/// regedit skips that line without a message. A file that is not a registry file is refused with
/// string 133 and writes nothing. Only the first file names a key outside the run sandbox, and the
/// write policy must refuse it.
/// </summary>
[TestClass]
public sealed class ImportResultTests
{
    private const string Caption = "Registry Editor";
    private const string OutsideSubKey = @"Software\BWC-Import-Outside";
    private const string PartialText = "Not all data was successfully written to the registry.";
    private const string SuccessText = "successfully added to the registry";
    private const string NotRegistryText = "is not a registry file";
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
    public void ImportResult_DeniedLineImportsTheRestAndShowsStringThirtyTwo()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        bool outsidePreexisted = OutsideKeyExists();
        E2EResults.Record(
            "importresult.denied.outsideAbsent",
            !outsidePreexisted,
            $"HKCU\\{OutsideSubKey} existed before the test={outsidePreexisted}.");
        if (outsidePreexisted)
        {
            Assert.Fail($"HKCU\\{OutsideSubKey} already exists. The test will not modify or delete it.");
        }

        string folder = FileFolder(config, "denied");
        try
        {
            sandbox.Cleanup();
            SandboxRoot(sandbox);
            string file = WriteFile(folder, "denied.reg", Utf16Bom(
                "Windows Registry Editor Version 5.00",
                "",
                "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\Before]",
                "\"Value\"=\"before\"",
                "",
                "[HKEY_CURRENT_USER\\" + OutsideSubKey + "]",
                "\"Value\"=\"outside\"",
                "",
                "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\After]",
                "\"Value\"=\"after\""));

            Outcome outcome = ImportThroughWindow(config, sandbox, "denied", file);
            E2EResults.Record(
                "importresult.denied.confirm",
                outcome.ConfirmShown,
                $"confirmation shown={outcome.ConfirmShown}.");
            E2EResults.Record(
                "importresult.denied.partialText",
                outcome.ResultShown && outcome.ResultText.Contains(PartialText, StringComparison.Ordinal),
                $"result dialog shown={outcome.ResultShown}, text={Shorten(outcome.ResultText)}.");

            bool before = sandbox.ReadString("Import\\Before", "Value") == "before";
            bool after = sandbox.ReadString("Import\\After", "Value") == "after";
            E2EResults.Record(
                "importresult.denied.rest",
                before && after,
                $"lines before and after the denied section imported: before={before}, after={after}.");

            bool outsideCreated = OutsideKeyExists();
            E2EResults.Record(
                "importresult.denied.outsideNotCreated",
                !outsideCreated,
                $"HKCU\\{OutsideSubKey} exists after the import={outsideCreated}.");
        }
        finally
        {
            sandbox.Cleanup();
            DeleteFolder(folder);
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ImportResult_MalformedLineIsSkippedWithoutStringThirtyTwo()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        string folder = FileFolder(config, "malformed");
        try
        {
            sandbox.Cleanup();
            SandboxRoot(sandbox);
            string file = WriteFile(folder, "malformed.reg", Utf16Bom(
                "Windows Registry Editor Version 5.00",
                "",
                "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\Before]",
                "\"Value\"=\"before\"",
                "",
                "this line is not a registry entry",
                "",
                "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\After]",
                "\"Value\"=\"after\""));

            Outcome outcome = ImportThroughWindow(config, sandbox, "malformed", file);
            E2EResults.Record(
                "importresult.malformed.successText",
                outcome.ResultShown && outcome.ResultText.Contains(SuccessText, StringComparison.Ordinal),
                $"result dialog shown={outcome.ResultShown}, text={Shorten(outcome.ResultText)}.");
            E2EResults.Record(
                "importresult.malformed.noPartialText",
                outcome.ResultShown && !outcome.ResultText.Contains(PartialText, StringComparison.Ordinal),
                "a skipped line does not show string 132.");

            bool before = sandbox.ReadString("Import\\Before", "Value") == "before";
            bool after = sandbox.ReadString("Import\\After", "Value") == "after";
            E2EResults.Record(
                "importresult.malformed.rest",
                before && after,
                $"sections around the skipped line imported: before={before}, after={after}.");
        }
        finally
        {
            sandbox.Cleanup();
            DeleteFolder(folder);
        }

        FailIfNeeded(checkStart);
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("Mutating")]
    public void ImportResult_RefusedFileShowsStringOneThirtyThreeAndWritesNothing()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        string folder = FileFolder(config, "refused");
        try
        {
            sandbox.Cleanup();

            // No header: the first line is a key section, so the whole file is refused.
            string file = WriteFile(folder, "refused.reg", Utf16Bom(
                "[HKEY_CURRENT_USER\\" + sandbox.RunRootPath + "\\Import\\Refused]",
                "\"Value\"=\"refused\""));

            Outcome outcome = ImportThroughWindow(config, sandbox, "refused", file);
            E2EResults.Record(
                "importresult.refused.text",
                outcome.ResultShown && outcome.ResultText.Contains(NotRegistryText, StringComparison.Ordinal),
                $"result dialog shown={outcome.ResultShown}, text={Shorten(outcome.ResultText)}.");

            bool written = sandbox.Exists("Import\\Refused");
            E2EResults.Record(
                "importresult.refused.nothingWritten",
                !written,
                $"the refused section was not created: exists={written}.");
        }
        finally
        {
            sandbox.Cleanup();
            DeleteFolder(folder);
        }

        FailIfNeeded(checkStart);
    }

    private sealed record Outcome(bool ConfirmShown, bool ResultShown, string ResultText);

    /// <summary>
    /// Opens the window on the file, answers Yes to the confirmation, reads the result dialog, and dismisses it
    /// with Enter, the default button of the message. The sandbox root is passed to the app so writes stay inside it.
    /// </summary>
    private static Outcome ImportThroughWindow(E2EConfig config, SandboxRegistry sandbox, string scope, string file)
    {
        string dataRoot = AppServicesSupport.FreshDataRoot(config, "importresult-" + scope);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        using RegeditProcess app = RegeditProcess.Start(config, dataRoot, new[] { file }, environment: environment);
        IntPtr confirm = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout);
        if (confirm == IntPtr.Zero)
        {
            app.Close();
            return new Outcome(false, false, string.Empty);
        }

        AppServicesSupport.ClickButton(app, confirm, "Yes");
        _ = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(confirm), DialogTimeout);

        IntPtr result = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout);
        if (result == IntPtr.Zero)
        {
            app.Close();
            return new Outcome(true, false, string.Empty);
        }

        string text = AppServicesSupport.DialogText(result);
        _ = AppServicesSupport.DismissMessage(result, DialogTimeout);
        app.Close();
        return new Outcome(true, true, text);
    }

    /// <summary>
    /// Creates the sandbox root. Import creates missing ancestors top down, and the write policy refuses
    /// anything above the sandbox, so the root must exist before the file is imported.
    /// </summary>
    private static void SandboxRoot(SandboxRegistry sandbox) => sandbox.CreateKey("Import");

    private static string FileFolder(E2EConfig config, string scope)
    {
        string folder = Path.Combine(config.ArtifactsDir, "import-result", config.RunId, scope);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string WriteFile(string folder, string name, byte[] bytes)
    {
        string file = Path.Combine(folder, name);
        File.WriteAllBytes(file, bytes);
        return file;
    }

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
            E2EResults.Record("importresult.cleanup", false, ex.Message);
        }
    }

    /// <summary>UTF-16LE with a byte-order mark, CRLF between lines, as regedit writes version 5 files.</summary>
    private static byte[] Utf16Bom(params string[] lines)
    {
        byte[] body = Encoding.Unicode.GetBytes(string.Join("\r\n", lines) + "\r\n");
        return Encoding.Unicode.GetPreamble().Concat(body).ToArray();
    }

    private static bool OutsideKeyExists()
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = hkcu.OpenSubKey(OutsideSubKey);
        return key is not null;
    }

    private static string Shorten(string text) =>
        text.Length <= 240 ? text : text[..240] + "...";

    private static void FailIfNeeded(int startIndex)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(startIndex);
        if (failures.Count > 0)
        {
            Assert.Fail($"Import result checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }
}
