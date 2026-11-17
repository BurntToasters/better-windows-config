using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Bwc.E2E.AppServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Remote;

/// <summary>
/// The connect error text (RT_STRING 152 to 154) and Import into a connected computer (dialog 115, RT_STRING 128 and
/// 132). E2E builds read BWC_E2E_CONNECT_TARGET, so File > Connect runs its real command path without the picker.
/// Remote writes go to HKEY_USERS\SID\Software\BWC-E2E\run-id, the local sandbox key with the root swapped. The
/// sandbox helpers seed and clean, and Registry.Users only reads back. Section 17 of
/// docs/failure-modes/remote-registry.md is the spec. A connect that the remote registry refuses is a note
/// only while RemoteRegistry is not Running (see RemoteRegistryService); otherwise it fails.
/// </summary>
[TestClass]
public sealed class RemoteConnectImportTests
{
    private const string LoopbackComputer = "127.0.0.1";
    private const string LoopbackNode = @"\\127.0.0.1";
    private const string UnreachableComputer = "bwc-e2e-unreachable.invalid";
    private const string ConnectTargetVariable = "BWC_E2E_CONNECT_TARGET";
    private const int MenuFile = 512;
    private const int MenuFileConnect = 657;
    private const int MenuFileImport = 658;
    private const string DialogRootId = "DialogRoot";
    private const string DialogPrimaryId = "DialogPrimary";
    private const string DialogSecondaryId = "DialogSecondary";
    private const string DialogErrorTextId = "DialogErrorText";
    private const string ImportLabelId = "ImportComputerLabel";
    private const string ImportListId = "ImportComputerList";
    private const string ConnectErrorCaption = "Error Connecting Network Registry";
    private const string ImportCaption = "Import Registry File";
    private const string ImportLabel = "Select Computer:";
    private const string OwnComputerText = "You cannot connect to your own computer.";
    private const string UnreachableText =
        "Unable to connect to " + UnreachableComputer + ".  Make sure that this computer is on the network, " +
        "has remote administration enabled, and that both computers are running the remote registry service.";
    private const string SuccessOnLoopback = "successfully added to the registry on " + LoopbackComputer + ".";
    private const string PartialWriteText = "Not all data was successfully written to the registry";
    private const string ImportedKeyName = "Imported";
    private const string ImportedLocalKeyName = "ImportedLocal";
    private const string ImportedNameValue = "Name";
    private const string ImportedNameData = "remote-import";
    private const string ImportedCountValue = "Count";
    private const int ImportedCountData = 42;
    private const int ConnectBudgetSeconds = 60;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_ConnectOwnComputerShowsStringOneFiftyThree()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        string node = @"\\" + Environment.MachineName;
        try
        {
            sandbox.Cleanup();
            using AppSession session = StartApp(config, sandbox, "remote-own", Environment.MachineName);
            using var driver = new EditingDriver(session);

            ConnectOutcome outcome = ConnectUntilOutcome(session, driver, node, TimeSpan.FromSeconds(30));
            bool nodeAfter = FindTreeRow(session, node) is not null;
            E2EResults.Record(
                "remote.connect.own.message",
                string.Equals(outcome.Text, OwnComputerText, StringComparison.Ordinal) &&
                    string.Equals(outcome.Caption, ConnectErrorCaption, StringComparison.Ordinal) &&
                    outcome.Dismissed,
                $"caption='{outcome.Caption}', text='{outcome.Text}', dismissed={outcome.Dismissed}.");
            E2EResults.Record(
                "remote.connect.own.no-node",
                !outcome.NodeAppeared && !nodeAfter && !outcome.Hung,
                $"node '{node}' appeared={outcome.NodeAppeared}, present after={nodeAfter}, UI hung={outcome.Hung}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-own");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    public void Remote_ConnectUnreachableShowsStringOneFiftyFour()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        string node = @"\\" + UnreachableComputer;
        try
        {
            sandbox.Cleanup();
            using AppSession session = StartApp(config, sandbox, "remote-unreachable", UnreachableComputer);
            using var driver = new EditingDriver(session);

            ConnectOutcome outcome = ConnectUntilOutcome(session, driver, node, TimeSpan.FromSeconds(ConnectBudgetSeconds));
            E2EResults.Record(
                "remote.connect.unreachable.message",
                string.Equals(outcome.Text, UnreachableText, StringComparison.Ordinal) &&
                    string.Equals(outcome.Caption, ConnectErrorCaption, StringComparison.Ordinal),
                $"caption='{outcome.Caption}', text='{outcome.Text}'.");
            E2EResults.Record(
                "remote.connect.unreachable.bounded",
                outcome.Text.Length > 0 && outcome.Seconds < ConnectBudgetSeconds,
                $"error shown after {outcome.Seconds:F1} s (budget {ConnectBudgetSeconds} s).");
            E2EResults.Record(
                "remote.connect.unreachable.responsive",
                !outcome.Hung,
                $"main window reported hung during the connect={outcome.Hung}.");

            bool nodeAfter = FindTreeRow(session, node) is not null;
            E2EResults.Record(
                "remote.connect.unreachable.no-node",
                !outcome.NodeAppeared && !nodeAfter,
                $"node '{node}' appeared={outcome.NodeAppeared}, present after={nodeAfter}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-unreachable");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("ImportExport")]
    public void Remote_ImportWritesThroughTheConnection()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        string sid = CurrentSid();
        string file = ReservedFile(config, "remote-import", "remote-import.reg");
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(string.Empty);
            WriteRegFile(file, UsersSection(sid, config.RunId, ImportedKeyName));

            using AppSession session = StartApp(config, sandbox, "remote-import", LoopbackComputer);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            RequireLoopback(session, driver);

            ImportRun run = RunImport(session, driver, dialogs, file, LoopbackComputer, cancel: false);
            E2EResults.Record(
                "remote.import.computer-dialog",
                run.FileChosen &&
                    run.ComputerDialogShown &&
                    string.Equals(run.Caption, ImportCaption, StringComparison.Ordinal) &&
                    string.Equals(run.Label, ImportLabel, StringComparison.Ordinal) &&
                    run.LocalListed &&
                    run.RemoteListed &&
                    run.RemoteSelected &&
                    run.DialogClosed,
                $"file={run.FileChosen}, dialog={run.ComputerDialogShown}, caption='{run.Caption}', " +
                $"label='{run.Label}', local listed={run.LocalListed}, '{LoopbackComputer}' listed={run.RemoteListed}, " +
                $"selected={run.RemoteSelected}, closed={run.DialogClosed}.");

            E2EResults.Record(
                "remote.import.result",
                run.ResultDismissed && run.ResultText.Contains(SuccessOnLoopback, StringComparison.Ordinal),
                $"result='{run.ResultText}', dismissed={run.ResultDismissed}.");

            bool readBack = false;
            string described = "key missing";
            using (RegistryKey? imported = OpenRemoteKey(sid, config.RunId, ImportedKeyName))
            {
                if (imported is not null)
                {
                    readBack = imported.GetValue(ImportedNameValue) as string == ImportedNameData &&
                        imported.GetValue(ImportedCountValue) is int count && count == ImportedCountData;
                    described = $"{ImportedNameValue}='{imported.GetValue(ImportedNameValue)}', " +
                        $"{ImportedCountValue}={imported.GetValue(ImportedCountValue)}";
                }
            }

            E2EResults.Record(
                "remote.import.readback",
                readBack,
                $"HKU\\{sid}\\...\\{ImportedKeyName} read through Registry.Users: {described}.");
            // The run key is one key in both views (HKCU and HKEY_USERS\SID), so the import shows in the sandbox too.
            E2EResults.Record(
                "remote.import.same-key",
                sandbox.Exists(ImportedKeyName),
                $"sandbox view of the run key holds {ImportedKeyName}={sandbox.Exists(ImportedKeyName)}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-import");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("ImportExport")]
    public void Remote_ImportCancelImportsNothing()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        string sid = CurrentSid();
        string file = ReservedFile(config, "remote-import-cancel", "remote-import-cancel.reg");
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(string.Empty);
            WriteRegFile(file, UsersSection(sid, config.RunId, ImportedKeyName));

            using AppSession session = StartApp(config, sandbox, "remote-import-cancel", LoopbackComputer);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            RequireLoopback(session, driver);

            ImportRun run = RunImport(session, driver, dialogs, file, LoopbackComputer, cancel: true);
            bool enabledAgain = ControlTestSupport.WaitUntil(
                () => Win32.IsWindowEnabled(session.MainHwnd),
                TimeSpan.FromSeconds(8));
            IntPtr result = ImportExportWindows.WaitForResult(
                session.MainHwnd,
                text => text.Contains("Cannot import", StringComparison.Ordinal) ||
                    text.Contains("successfully added to the registry", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            E2EResults.Record(
                "remote.import.cancel.dialog",
                run.FileChosen && run.ComputerDialogShown && run.DialogClosed && !run.Accepted && enabledAgain,
                $"file={run.FileChosen}, dialog={run.ComputerDialogShown}, closed={run.DialogClosed}, " +
                $"accepted={run.Accepted}, main enabled={enabledAgain}.");

            bool written;
            using (RegistryKey? imported = OpenRemoteKey(sid, config.RunId, ImportedKeyName))
            {
                written = imported is not null;
            }

            E2EResults.Record(
                "remote.import.cancel.nothing-written",
                !written && result == IntPtr.Zero,
                $"remote {ImportedKeyName} written={written}, result message shown={result != IntPtr.Zero}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-import-cancel");
        }
    }

    [TestMethod]
    [TestCategory("Remote")]
    [TestCategory("Mutating")]
    [TestCategory("ImportExport")]
    public void Remote_ImportMixedRootsReportsStringOneThirtyTwo()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        EnsurePublished(config);

        var sandbox = new SandboxRegistry(config);
        string sid = CurrentSid();
        string file = ReservedFile(config, "remote-import-mixed", "remote-import-mixed.reg");
        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey(string.Empty);
            string localSection = $"[HKEY_CURRENT_USER\\{sandbox.RunRootPath}\\{ImportedLocalKeyName}]\r\n" +
                $"\"{ImportedNameValue}\"=\"local-only\"";
            WriteRegFile(file, UsersSection(sid, config.RunId, ImportedKeyName), localSection);

            using AppSession session = StartApp(config, sandbox, "remote-import-mixed", LoopbackComputer);
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            RequireLoopback(session, driver);

            ImportRun run = RunImport(session, driver, dialogs, file, LoopbackComputer, cancel: false);
            E2EResults.Record(
                "remote.import.mixed.result",
                run.ResultDismissed && run.ResultText.Contains(PartialWriteText, StringComparison.Ordinal),
                $"computer dialog={run.ComputerDialogShown}, result='{run.ResultText}', dismissed={run.ResultDismissed}.");

            bool usersWritten;
            using (RegistryKey? imported = OpenRemoteKey(sid, config.RunId, ImportedKeyName))
            {
                usersWritten = imported?.GetValue(ImportedNameValue) as string == ImportedNameData;
            }

            E2EResults.Record(
                "remote.import.mixed.users-written",
                usersWritten,
                $"HKU section written through the connection={usersWritten}.");
            E2EResults.Record(
                "remote.import.mixed.hkcu-refused",
                !sandbox.Exists(ImportedLocalKeyName),
                $"local {ImportedLocalKeyName} exists={sandbox.Exists(ImportedLocalKeyName)} (the HKCU section wrote nothing).");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(config, "remote-import-mixed");
        }
    }

    /// <summary>
    /// Connects through File > Connect with the target from the environment. A refused connect is "not run" (a note
    /// and the test is Inconclusive) only while RemoteRegistry is not Running, the same as RequireConnected.
    /// Otherwise it is a failed check. A UI failure is a failed check.
    /// </summary>
    private static void RequireLoopback(AppSession session, EditingDriver driver)
    {
        ConnectOutcome outcome = ConnectUntilOutcome(session, driver, LoopbackNode, TimeSpan.FromSeconds(30));
        if (outcome.NodeAppeared)
        {
            E2EResults.Record("remote.connect", true, $"connected to '{LoopbackComputer}', node '{LoopbackNode}' present.");
            return;
        }

        if (outcome.Text.Length > 0)
        {
            RemoteRegistryService.RefusedConnect("remote.connect", "remote.connect.not-run", outcome.Text);
        }

        E2EResults.Record("remote.connect", false, $"Neither '{LoopbackNode}' nor an error appeared within 30 s.");
        Assert.Fail($"Neither '{LoopbackNode}' nor an error appeared within 30 s.");
    }

    /// <summary>
    /// Runs File > Connect and waits for the node or the error box. The UI is probed for a hang while waiting. The
    /// error box is read and dismissed here, so the caller sees the caption and the text.
    /// </summary>
    private static ConnectOutcome ConnectUntilOutcome(AppSession session, EditingDriver driver, string nodeName, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        if (!OpenFileCommand(driver, MenuFileConnect))
        {
            return new ConnectOutcome(string.Empty, string.Empty, false, false, 0, false);
        }

        bool hung = false;
        bool node = false;
        string text = string.Empty;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            hung |= RemoteWindows.IsHung(session.MainHwnd);
            if (FindTreeRow(session, nodeName) is not null)
            {
                node = true;
                break;
            }

            text = driver.TextOf(DialogErrorTextId);
            if (text.Length > 0)
            {
                break;
            }

            Thread.Sleep(200);
        }

        string caption = string.Empty;
        bool dismissed = false;
        if (text.Length > 0)
        {
            nint box = RemoteWindows.WaitForOwnedWindow(session, ConnectErrorCaption, TimeSpan.FromSeconds(3));
            caption = box != 0 ? RemoteWindows.TitleOf(box) : string.Empty;
            dismissed = driver.DismissErrorBox();
        }

        return new ConnectOutcome(caption, text, node, hung, clock.Elapsed.TotalSeconds, dismissed);
    }

    /// <summary>
    /// File > Import on a .reg file, then the computer dialog (dialog 115) answered with Select and OK, or with Cancel.
    /// Returns what each step showed. The result message is read and dismissed when there is one.
    /// </summary>
    private static ImportRun RunImport(
        AppSession session,
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        string file,
        string computerName,
        bool cancel)
    {
        bool invoked = ImportExportTests.InvokeFileCommand(driver, dialogs, MenuFileImport, "remote.import");
        IntPtr fileDialog = invoked ? dialogs.WaitForDialog(TimeSpan.FromSeconds(8)) : IntPtr.Zero;
        bool named = fileDialog != IntPtr.Zero && dialogs.TrySetFileName(fileDialog, file, out _);
        bool accepted = named && dialogs.TryAccept(fileDialog, "Open", out _);
        bool fileChosen = accepted && dialogs.WaitGone(fileDialog, TimeSpan.FromSeconds(10));
        if (fileDialog != IntPtr.Zero && !fileChosen)
        {
            _ = dialogs.TryCancel(fileDialog);
        }

        if (!fileChosen)
        {
            return ImportRun.NotChosen;
        }

        bool shown = driver.WaitForDialog(TimeSpan.FromSeconds(10));
        AutomationElement? root = shown ? driver.WaitForElement(DialogRootId) : null;
        string caption = root is null ? string.Empty : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(root));
        string label = shown ? driver.TextOf(ImportLabelId) : string.Empty;
        AutomationElement? list = shown ? driver.WaitForElement(ImportListId, TimeSpan.FromSeconds(5)) : null;
        bool localListed = list?.FindFirstDescendant(cf => cf.ByName(Environment.MachineName)) is not null;
        AutomationElement? remoteItem = list?.FindFirstDescendant(cf => cf.ByName(computerName));
        bool selected = remoteItem is not null && ControlTestSupport.WaitUntil(
            () =>
            {
                ControlTestSupport.Select(remoteItem);
                return ControlTestSupport.IsSelected(remoteItem);
            },
            TimeSpan.FromSeconds(5));

        bool closed = false;
        bool confirmed = false;
        if (shown)
        {
            if (cancel)
            {
                closed = driver.Invoke(DialogSecondaryId) && driver.WaitForNoDialog();
            }
            else
            {
                confirmed = selected && driver.Invoke(DialogPrimaryId) && driver.WaitForNoDialog();
                closed = confirmed;
            }
        }

        IntPtr result = confirmed
            ? ImportExportWindows.WaitForResult(
                session.MainHwnd,
                text => text.Contains("Cannot import", StringComparison.Ordinal) ||
                    text.Contains("successfully added to the registry", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30))
            : IntPtr.Zero;
        string resultText = result != IntPtr.Zero
            ? AppServicesSupport.WaitForDialogText(result, value => value.Any(char.IsLetterOrDigit), TimeSpan.FromSeconds(10))
            : string.Empty;
        bool dismissed = result != IntPtr.Zero && AppServicesSupport.DismissMessage(result, TimeSpan.FromSeconds(10));

        return new ImportRun(
            FileChosen: true,
            ComputerDialogShown: shown,
            Caption: caption,
            Label: label,
            LocalListed: localListed,
            RemoteListed: remoteItem is not null,
            RemoteSelected: selected,
            DialogClosed: closed,
            Accepted: confirmed,
            ResultText: resultText,
            ResultDismissed: dismissed);
    }

    private static bool OpenFileCommand(EditingDriver driver, int commandId)
    {
        try
        {
            driver.OpenMenu(MenuFile, commandId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope, string connectTarget)
    {
        string dataRoot = CreateFreshDataRoot(config, scope);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            [ConnectTargetVariable] = connectTarget,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static AutomationElement? FindTreeRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, name);

    private static string CurrentSid() =>
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current user SID is not available.");

    private static RegistryKey? OpenRemoteKey(string sid, string runId, string keyName) =>
        Registry.Users.OpenSubKey($@"{sid}\Software\BWC-E2E\{runId}\{keyName}", writable: false);

    private static string UsersSection(string sid, string runId, string keyName) =>
        $"[HKEY_USERS\\{sid}\\Software\\BWC-E2E\\{runId}\\{keyName}]\r\n" +
        $"\"{ImportedNameValue}\"=\"{ImportedNameData}\"\r\n" +
        $"\"{ImportedCountValue}\"=dword:{ImportedCountData:x8}";

    /// <summary>Writes a UTF-16 .reg file with a byte order mark. Each section is one string.</summary>
    private static void WriteRegFile(string path, params string[] sections)
    {
        string text = "Windows Registry Editor Version 5.00\r\n\r\n" + string.Join("\r\n\r\n", sections) + "\r\n";
        byte[] body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        body.CopyTo(bytes, 2);
        File.WriteAllBytes(path, bytes);
    }

    private static string ReservedFile(E2EConfig config, string scope, string fileName)
    {
        string dir = Path.Combine(config.ArtifactsDir, scope, config.RunId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    private static void EnsurePublished(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("remote.publish", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        // BWC_E2E_KEEP_DATA=1 keeps the data folder, as the harness does for a failed run, so the app log can be read.
        if (string.Equals(Environment.GetEnvironmentVariable("BWC_E2E_KEEP_DATA"), "1", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit path may still hold a settings file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Remote connect and import checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private sealed record ConnectOutcome(
        string Caption,
        string Text,
        bool NodeAppeared,
        bool Hung,
        double Seconds,
        bool Dismissed);

    private sealed record ImportRun(
        bool FileChosen,
        bool ComputerDialogShown,
        string Caption,
        string Label,
        bool LocalListed,
        bool RemoteListed,
        bool RemoteSelected,
        bool DialogClosed,
        bool Accepted,
        string ResultText,
        bool ResultDismissed)
    {
        public static ImportRun NotChosen { get; } = new(
            FileChosen: false,
            ComputerDialogShown: false,
            Caption: string.Empty,
            Label: string.Empty,
            LocalListed: false,
            RemoteListed: false,
            RemoteSelected: false,
            DialogClosed: false,
            Accepted: false,
            ResultText: string.Empty,
            ResultDismissed: false);
    }
}
