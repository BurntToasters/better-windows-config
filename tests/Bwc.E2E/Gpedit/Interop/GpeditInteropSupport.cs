using System.Diagnostics;
using System.Text.Json.Nodes;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.Policy.Tests.Golden;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Win32;

namespace Bwc.E2E.Gpedit.Interop;

/// <summary>
/// The real policy folder of the machine, backed up in memory before a test changes it and restored
/// byte for byte in finally. Only the five known files of a GPO folder are named; nothing else in the
/// folder is read for a write or removed.
/// </summary>
internal sealed class RealPolicyFolder
{
    /// <summary>The five files of a GPO folder, by the flat name a golden step folder uses.</summary>
    internal static readonly (string Flat, string Relative)[] Files =
    [
        ("gpt.ini", "gpt.ini"),
        ("Machine.Registry.pol", @"Machine\Registry.pol"),
        ("Machine.comment.cmtx", @"Machine\comment.cmtx"),
        ("User.Registry.pol", @"User\Registry.pol"),
        ("User.comment.cmtx", @"User\comment.cmtx"),
    ];

    private readonly Dictionary<string, byte[]?> _before = new(StringComparer.OrdinalIgnoreCase);

    private RealPolicyFolder(string root)
    {
        Root = root;
    }

    internal string Root { get; }

    /// <summary>Takes the bytes of each of the five files, or records that it is absent.</summary>
    internal static RealPolicyFolder Capture()
    {
        var folder = new RealPolicyFolder(GpoGuard.RealRoot);
        foreach ((string _, string relative) in Files)
        {
            string path = Path.Combine(folder.Root, relative);
            folder._before[relative] = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        return folder;
    }

    /// <summary>True when the five files hold exactly the bytes of the golden step.</summary>
    internal bool MatchesGoldenStep(GoldenStep step)
    {
        foreach ((string flat, string relative) in Files)
        {
            string path = Path.Combine(Root, relative);
            if (step.Files[flat] is null)
            {
                if (File.Exists(path))
                {
                    return false;
                }
            }
            else if (!File.Exists(path) ||
                !File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(step.PathOf(flat))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes one golden step: a file the step records is copied over the real one, and a file the step
    /// says is absent is removed. The capture in <see cref="_before"/> is the only restore.
    /// </summary>
    internal void WriteGoldenStep(GoldenStep step)
    {
        foreach ((string flat, string relative) in Files)
        {
            string target = Path.Combine(Root, relative);
            if (step.Files[flat] is null)
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                continue;
            }

            string parent = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(step.PathOf(flat), target, overwrite: true);
        }
    }

    /// <summary>Puts every one of the five files back, including the absent ones, and returns the differences.</summary>
    internal IReadOnlyList<string> Restore()
    {
        var problems = new List<string>();
        foreach ((string _, string relative) in Files)
        {
            string path = Path.Combine(Root, relative);
            byte[]? expected = _before[relative];
            try
            {
                if (expected is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                else
                {
                    File.WriteAllBytes(path, expected);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(relative + ": " + ex.Message);
                continue;
            }

            byte[]? actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
            bool same = expected is null ? actual is null : actual is not null && expected.AsSpan().SequenceEqual(actual);
            if (!same)
            {
                problems.Add(relative + ": the bytes differ after the restore.");
            }
        }

        return problems;
    }
}

/// <summary>
/// The fixture templates in the system PolicyDefinitions folder. They are the same files the oracle
/// capture installs. Every target is backed up in memory first; the restore puts replaced files back
/// and removes the files the test added.
/// </summary>
internal sealed class RealTemplates
{
    private static readonly (string Source, string Relative)[] Items =
    [
        ("BwcOracle.admx", "BwcOracle.admx"),
        ("BwcOracleBase.admx", "BwcOracleBase.admx"),
        (@"en-US\BwcOracle.adml", @"en-US\BwcOracle.adml"),
        (@"en-US\BwcOracleBase.adml", @"en-US\BwcOracleBase.adml"),
    ];

    private readonly Dictionary<string, byte[]?> _before = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _createdLanguageFolder;

    private RealTemplates(string root, bool createdLanguageFolder)
    {
        Root = root;
        _createdLanguageFolder = createdLanguageFolder;
    }

    internal string Root { get; }

    /// <summary>The fixture files under docs/parity/oracle/gpedit/fixture.</summary>
    internal static string FixtureRoot => GpeditSandbox.FixtureRoot;

    /// <summary>Backs up the four targets and copies the fixture files over them.</summary>
    internal static RealTemplates Install()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.Combine(windows, "PolicyDefinitions");
        string language = Path.Combine(root, "en-US");
        bool createdLanguage = !Directory.Exists(language);
        if (createdLanguage)
        {
            Directory.CreateDirectory(language);
        }

        var templates = new RealTemplates(root, createdLanguage);
        foreach ((string _, string relative) in Items)
        {
            string target = Path.Combine(root, relative);
            templates._before[relative] = File.Exists(target) ? File.ReadAllBytes(target) : null;
        }

        try
        {
            foreach ((string source, string relative) in Items)
            {
                File.Copy(Path.Combine(FixtureRoot, source), Path.Combine(root, relative), overwrite: true);
            }
        }
        catch
        {
            // A copy that fails half way must not leave the files it already replaced.
            _ = templates.Restore();
            throw;
        }

        return templates;
    }

    /// <summary>Puts every target back, or removes it when the file did not exist before, and returns the problems.</summary>
    internal IReadOnlyList<string> Restore()
    {
        var problems = new List<string>();
        foreach ((string _, string relative) in Items)
        {
            string path = Path.Combine(Root, relative);
            byte[]? expected = _before[relative];
            try
            {
                if (expected is null)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                else
                {
                    File.WriteAllBytes(path, expected);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(relative + ": " + ex.Message);
                continue;
            }

            byte[]? actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
            bool same = expected is null ? actual is null : actual is not null && expected.AsSpan().SequenceEqual(actual);
            if (!same)
            {
                problems.Add(relative + ": the bytes differ after the restore.");
            }
        }

        string language = Path.Combine(Root, "en-US");
        try
        {
            if (_createdLanguageFolder && Directory.Exists(language) && Directory.GetFileSystemEntries(language).Length == 0)
            {
                Directory.Delete(language);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add("en-US: the folder this test created was not removed: " + ex.Message);
        }

        return problems;
    }
}

/// <summary>
/// The live registry value of the fixture policy, HKLM Software\Policies\BWC-Oracle\Basics ModeNumber.
/// The test backs up that one value and the two fixture keys, and puts them back in finally. No other
/// key or value is named.
/// </summary>
internal sealed class FixtureRegistry
{
    private const string ParentKey = @"Software\Policies\BWC-Oracle";
    private const string BasicsKey = @"Software\Policies\BWC-Oracle\Basics";
    private const string ValueName = "ModeNumber";

    private readonly object? _value;
    private readonly RegistryValueKind _kind;
    private readonly bool _parentExisted;
    private readonly bool _basicsExisted;

    private FixtureRegistry(object? value, RegistryValueKind kind, bool parentExisted, bool basicsExisted)
    {
        _value = value;
        _kind = kind;
        _parentExisted = parentExisted;
        _basicsExisted = basicsExisted;
    }

    /// <summary>Takes the value and the existence of its two keys, read only.</summary>
    internal static FixtureRegistry Capture()
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? parent = baseKey.OpenSubKey(ParentKey);
        using RegistryKey? basics = baseKey.OpenSubKey(BasicsKey);
        object? value = basics?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        RegistryValueKind kind = RegistryValueKind.Unknown;
        if (basics is not null && value is not null)
        {
            kind = basics.GetValueKind(ValueName);
        }

        return new FixtureRegistry(value, kind, parent is not null, basics is not null);
    }

    /// <summary>The applied value, or null when it is absent.</summary>
    internal static int? Read(out RegistryValueKind kind)
    {
        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey? key = baseKey.OpenSubKey(BasicsKey))
        {
            if (key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int value)
            {
                kind = key.GetValueKind(ValueName);
                return value;
            }
        }

        kind = RegistryValueKind.Unknown;
        return null;
    }

    /// <summary>Puts the one value and the two keys back, and returns the problems.</summary>
    internal IReadOnlyList<string> Restore()
    {
        var problems = new List<string>();
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (RegistryKey? basics = baseKey.OpenSubKey(BasicsKey, writable: true))
            {
                if (_value is null)
                {
                    basics?.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                else
                {
                    using RegistryKey created = baseKey.CreateSubKey(BasicsKey, writable: true);
                    created.SetValue(ValueName, _value, _kind);
                }
            }

            if (!_basicsExisted && IsEmpty(baseKey, BasicsKey))
            {
                baseKey.DeleteSubKeyTree(BasicsKey, throwOnMissingSubKey: false);
            }

            if (!_parentExisted && IsEmpty(baseKey, ParentKey))
            {
                baseKey.DeleteSubKeyTree(ParentKey, throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add("registry: " + ex.Message);
            return problems;
        }

        int? actual = Read(out _);
        int? expected = _value is int number ? number : null;
        if (actual != expected)
        {
            problems.Add($"ModeNumber is {(actual is null ? "absent" : actual.ToString())}, expected {(expected is null ? "absent" : expected.ToString())}.");
        }

        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (RegistryKey? parent = baseKey.OpenSubKey(ParentKey))
        {
            if (!_parentExisted && parent is not null)
            {
                problems.Add(ParentKey + " still exists after the restore.");
            }
        }

        return problems;
    }

    private static bool IsEmpty(RegistryKey baseKey, string path)
    {
        try
        {
            using RegistryKey? key = baseKey.OpenSubKey(path);
            return key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>
/// The classic Local Group Policy Editor (mmc.exe gpedit.msc) started for one interop test. It is
/// started by the test, elevated like the test, and only that process is closed. The tree is walked
/// by expanding each node, the policy row is clicked and Enter opens the setting dialog.
/// </summary>
internal sealed class ClassicGpedit : IDisposable
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly UIA3Automation _automation;

    private ClassicGpedit(Process process, UIA3Automation automation, IntPtr hwnd)
    {
        _process = process;
        _automation = automation;
        Hwnd = hwnd;
    }

    internal IntPtr Hwnd { get; }

    internal int ProcessId => _process.Id;

    internal Window MainWindow => _automation.FromHandle(Hwnd).AsWindow();

    /// <summary>Starts mmc.exe with gpedit.msc and waits for its main window.</summary>
    internal static ClassicGpedit Start()
    {
        string mmc = Path.Combine(Environment.SystemDirectory, "mmc.exe");
        string console = Path.Combine(Environment.SystemDirectory, "gpedit.msc");
        var start = new ProcessStartInfo(mmc)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        };
        start.ArgumentList.Add(console);
        Process process = Process.Start(start) ?? throw new InvalidOperationException("mmc.exe did not start.");
        var automation = new UIA3Automation();
        try
        {
            IntPtr hwnd = WaitForMainWindow(process, WindowTimeout);
            return new ClassicGpedit(process, automation, hwnd);
        }
        catch
        {
            automation.Dispose();
            Stop(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>Expands the path from Computer Configuration down to a category and selects the last node.</summary>
    internal void SelectCategory(params string[] path)
    {
        AutomationElement tree = WaitFor(
            () => MainWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree)),
            NavigationTimeout) ?? throw new InvalidOperationException("The classic console has no tree.");
        AutomationElement current = WaitFor(
            () => tree.FindFirstChild(cf => cf.ByControlType(ControlType.TreeItem)),
            NavigationTimeout) ?? throw new InvalidOperationException("The classic tree has no root item.");
        _ = ControlTestSupport.Expand(current);
        foreach (string name in path)
        {
            AutomationElement? child = WaitFor(() => ChildByName(current, name), NavigationTimeout);
            if (child is null)
            {
                throw new InvalidOperationException($"The classic tree has no node '{name}'. Children: {string.Join("|", ChildNames(current))}.");
            }

            _ = ControlTestSupport.Expand(child);
            ControlTestSupport.Select(child);
            current = child;
            Thread.Sleep(400);
        }
    }

    /// <summary>Opens the setting dialog of a policy row and reads its state, then presses Cancel.</summary>
    internal string ReadPolicyState(string policyName, params string[] path)
    {
        SelectCategory(path);
        using ClassicPolicyDialog dialog = OpenPolicy(policyName);
        string state = dialog.State();
        _ = dialog.Cancel();
        return state;
    }

    /// <summary>Waits for the row in the visible list, selects and clicks it, and waits for the setting dialog.</summary>
    internal ClassicPolicyDialog OpenPolicy(string policyName)
    {
        AutomationElement row = WaitFor(() => FindVisibleListRow(policyName), NavigationTimeout)
            ?? throw new InvalidOperationException($"The classic list has no row '{policyName}'. Rows: {string.Join("|", VisibleListRows())}.");
        ControlTestSupport.Select(row);
        _ = ForegroundGuard.Ensure(Hwnd, "classic.interop.foreground", TimeSpan.FromSeconds(3));
        try
        {
            row.Click();
        }
        catch (FlaUI.Core.Exceptions.NoClickablePointException)
        {
            // The row is selected; Enter below still opens it.
        }

        Thread.Sleep(300);
        _ = ForegroundGuard.Ensure(Hwnd, "classic.interop.foreground", TimeSpan.FromSeconds(3));
        Keyboard.Type(VirtualKeyShort.RETURN);
        ClassicPolicyDialog? dialog = WaitFor(
            () => FindSettingDialog(policyName),
            TimeSpan.FromSeconds(15));
        if (dialog is not null)
        {
            return dialog;
        }

        try
        {
            row.DoubleClick();
        }
        catch (FlaUI.Core.Exceptions.NoClickablePointException)
        {
            // The Enter route above has already failed; the dialog wait below reports it.
        }

        return WaitFor(() => FindSettingDialog(policyName), TimeSpan.FromSeconds(15))
            ?? throw new InvalidOperationException($"No setting dialog opened for '{policyName}'.");
    }

    public void Dispose()
    {
        try
        {
            Stop(_process);
        }
        finally
        {
            _automation.Dispose();
            _process.Dispose();
        }
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"mmc.exe exited with code 0x{process.ExitCode:X8} before showing a window.");
            }

            IntPtr hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero && Win32.IsWindow(hwnd) && Win32.IsVisible(hwnd))
            {
                return hwnd;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"mmc.exe showed no main window within {timeout.TotalSeconds:0} s.");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            _ = process.CloseMainWindow();
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited while it was being closed.
        }
    }

    private ClassicPolicyDialog? FindSettingDialog(string policyName)
    {
        try
        {
            foreach (AutomationElement window in ProcessWindows.Of(_automation, _process.Id))
            {
                AutomationElement? title = window.FindFirstDescendant(cf => cf.ByAutomationId("labelTitle"));
                if (title is not null && string.Equals(ShellRun.SafeName(title), policyName, StringComparison.Ordinal))
                {
                    return new ClassicPolicyDialog(window);
                }
            }
        }
        catch (Exception)
        {
            // A window closed during the walk; the caller polls again.
        }

        return null;
    }

    private AutomationElement? FindVisibleListRow(string policyName)
    {
        try
        {
            return MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Where(row => !row.IsOffscreen)
                .FirstOrDefault(row => string.Equals(ShellRun.SafeName(row), policyName, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private List<string> VisibleListRows()
    {
        var names = new List<string>();
        try
        {
            foreach (AutomationElement row in MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
            {
                if (!row.IsOffscreen)
                {
                    names.Add(ShellRun.SafeName(row));
                }
            }
        }
        catch (Exception)
        {
            // The list is being rebuilt; the caller reports the names it has.
        }

        return names;
    }

    private static AutomationElement? ChildByName(AutomationElement parent, string name) =>
        parent.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(child => string.Equals(ShellRun.SafeName(child), name, StringComparison.Ordinal));

    private static List<string> ChildNames(AutomationElement parent)
    {
        try
        {
            return parent.FindAllChildren(cf => cf.ByControlType(ControlType.TreeItem))
                .Select(ShellRun.SafeName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static T? WaitFor<T>(Func<T?> read, TimeSpan timeout)
        where T : class
    {
        T? result = null;
        _ = ControlTestSupport.WaitUntil(() => (result = read()) is not null, timeout);
        return result;
    }
}

/// <summary>The setting dialog of one policy in the classic editor: the state radios and the Cancel button.</summary>
internal sealed class ClassicPolicyDialog : IDisposable
{
    private readonly AutomationElement _window;

    internal ClassicPolicyDialog(AutomationElement window)
    {
        _window = window;
        Hwnd = new IntPtr(window.Properties.NativeWindowHandle.Value);
    }

    internal IntPtr Hwnd { get; }

    /// <summary>Enabled, Disabled or Not Configured from the selected radio button.</summary>
    internal string State()
    {
        if (ControlTestSupport.IsSelected(Find("radioButtonEnabled")))
        {
            return "Enabled";
        }

        if (ControlTestSupport.IsSelected(Find("radioButtonDisabled")))
        {
            return "Disabled";
        }

        if (ControlTestSupport.IsSelected(Find("radioButtonNotConfigured")))
        {
            return "Not Configured";
        }

        return "Unknown";
    }

    /// <summary>Presses Cancel and waits until the dialog window is gone.</summary>
    internal bool Cancel()
    {
        AutomationElement? button = Find("buttonCancel");
        if (button is null)
        {
            return false;
        }

        try
        {
            if (button.Patterns.Invoke.IsSupported)
            {
                button.Patterns.Invoke.Pattern.Invoke();
            }
            else
            {
                button.Click();
            }
        }
        catch (Exception)
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(() => !Win32.IsWindow(Hwnd), TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        if (Win32.IsWindow(Hwnd))
        {
            _ = Cancel();
        }
    }

    private AutomationElement? Find(string id) =>
        _window.FindFirstDescendant(cf => cf.ByAutomationId(id));
}

/// <summary>Reads the capture case of one golden and its next setting dialog state, for the file-copy test.</summary>
internal static class GoldenInterop
{
    /// <summary>The golden case of the file-copy test: B/Pol_Value_Decimal, the enabled step.</summary>
    internal const string CaseId = "B/Pol_Value_Decimal";
    internal const string StepLabel = "enabled-defaults";

    /// <summary>
    /// The step to copy, its expected state, and the options the next opening of the dialog shows. The
    /// next editPolicy operation's before read is the readout of the files this step wrote.
    /// </summary>
    internal static (GoldenCase Case, GoldenStep Step, string State, JsonArray Options) LoadStep()
    {
        GoldenCase capture = GoldenCase.Load(CaseId);
        GoldenStep step = capture.Steps.FirstOrDefault(candidate => candidate.Label == StepLabel)
            ?? throw new InvalidOperationException($"Golden {CaseId} has no step '{StepLabel}'.");
        string snapshotLabel = "step " + StepLabel;
        GoldenOp snapshot = capture.Ops.FirstOrDefault(op => op.Kind == "snapshot" && op.Text("label") == snapshotLabel)
            ?? throw new InvalidOperationException($"Golden {CaseId} has no snapshot for step '{StepLabel}'.");
        GoldenOp next = capture.Ops
            .Where(op => op.Index > snapshot.Index && op.Kind == "editPolicy")
            .OrderBy(op => op.Index)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Golden {CaseId} has no edit after step '{StepLabel}'.");
        JsonObject before = next.Data?["before"] as JsonObject
            ?? throw new InvalidOperationException($"Golden {CaseId} step '{StepLabel}' has no before read.");
        string state = before["state"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Golden {CaseId} step '{StepLabel}' has no before state.");
        JsonArray options = before["options"] as JsonArray ?? new JsonArray();
        return (capture, step, state, options);
    }
}

/// <summary>The ship editor, the fixture policy, and the classification of its setting dialog state.</summary>
internal static class InteropShip
{
    internal const string FixturePolicy = "Decimal enabled and disabled values";
    internal static readonly string[] CategoryPath = ["Computer Configuration", "Administrative Templates", "BWC Oracle", "Basics"];

    /// <summary>publish/&lt;rid&gt;, the ship folder the elevated runner verifies before the run.</summary>
    internal static string PublishDir()
    {
        E2EConfig config = GuiGate.Config;
        return Path.Combine(config.RepoRoot, "publish", config.Rid);
    }

    internal static string EditorExe => Path.Combine(PublishDir(), E2EConfig.GpeditExeName);

    internal static string UpdaterExe => Path.Combine(PublishDir(), "BwcUpdate.exe");

    /// <summary>Expands the tree from Local Computer Policy to Basics and selects Basics.</summary>
    internal static bool SelectCategory(GpeditRun run)
    {
        if (!SelectNode(run, GpeditRun.RootName, TimeSpan.FromSeconds(120)))
        {
            return false;
        }

        foreach (string name in CategoryPath)
        {
            if (!SelectNode(run, name, TimeSpan.FromSeconds(60)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True once the dialog root is gone; the save may take longer than the dialog helper's own wait.</summary>
    internal static bool WaitClosed(GpeditDialog dialog, TimeSpan timeout) =>
        ShellRun.WaitUntil(() => dialog.Find(GpeditDialog.RootId, 0.2) is null, timeout);

    /// <summary>Enabled, Disabled or Not Configured from the dialog radio buttons.</summary>
    internal static string DialogState(GpeditDialog dialog)
    {
        if (dialog.IsSelected(GpeditDialog.Enabled))
        {
            return "Enabled";
        }

        if (dialog.IsSelected(GpeditDialog.Disabled))
        {
            return "Disabled";
        }

        if (dialog.IsSelected(GpeditDialog.NotConfigured))
        {
            return "Not Configured";
        }

        return "Unknown";
    }

    /// <summary>The automation ids of the controls inside the options pane; empty when the policy has no elements.</summary>
    internal static string[] OptionControlIds(GpeditDialog dialog)
    {
        try
        {
            AutomationElement? pane = dialog.Find(GpeditDialog.OptionsPane);
            return pane is null
                ? []
                : pane.FindAllDescendants()
                    .Select(ShellRun.SafeId)
                    .Where(id => id.Length > 0)
                    .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// True when the dialog shows the option values the golden recorded: no control for an empty list,
    /// and every recorded text as the name or value of a control in the pane.
    /// </summary>
    internal static bool OptionsMatch(GpeditDialog dialog, JsonArray options)
    {
        string[] ids = OptionControlIds(dialog);
        if (options.Count == 0)
        {
            return ids.Length == 0;
        }

        try
        {
            AutomationElement? pane = dialog.Find(GpeditDialog.OptionsPane);
            if (pane is null)
            {
                return false;
            }

            var shown = new List<string>();
            foreach (AutomationElement element in pane.FindAllDescendants())
            {
                shown.Add(ShellRun.SafeName(element));
                if (element.Patterns.Value.IsSupported)
                {
                    shown.Add(element.Patterns.Value.Pattern.Value.Value ?? string.Empty);
                }
            }

            foreach (JsonNode? node in options)
            {
                string? text = (node as JsonObject)?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text) && !shown.Contains(text, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SelectNode(GpeditRun run, string name, TimeSpan timeout)
    {
        AutomationElement? row = null;
        if (!ShellRun.WaitUntil(() => (row = FindTreeNode(run, name)) is not null, timeout) || row is null)
        {
            return false;
        }

        string state = ControlTestSupport.StateOf(row);
        if (state is "Collapsed" or "PartiallyExpanded")
        {
            _ = ControlTestSupport.Expand(row);
        }

        ControlTestSupport.Select(row);
        Thread.Sleep(300);
        return true;
    }

    private static AutomationElement? FindTreeNode(GpeditRun run, string name)
    {
        AutomationElement? tree = run.Shell.Find(ShellRun.TreeControlId);
        if (tree is null)
        {
            return null;
        }

        return tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
            .FirstOrDefault(row => string.Equals(ShellRun.SafeName(row), name, StringComparison.Ordinal));
    }
}
