using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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

    /// <summary>Deletes the five known GPO files so a case starts from an empty policy folder.</summary>
    internal void ClearForCase()
    {
        foreach ((string _, string relative) in Files)
        {
            string path = Path.Combine(Root, relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Compares the five GPO files against one golden step and returns any differences.</summary>
    internal List<string> CompareGoldenStep(GoldenStep step)
    {
        var problems = new List<string>();
        foreach ((string flat, string relative) in Files)
        {
            string path = Path.Combine(Root, relative);
            byte[]? actual = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (step.Files[flat] is null)
            {
                if (actual is not null)
                {
                    problems.Add($"{flat} must be absent but holds {actual.Length} bytes");
                }

                continue;
            }

            byte[] expected = File.ReadAllBytes(step.PathOf(flat));
            if (actual is null)
            {
                problems.Add($"{flat} is missing; expected {expected.Length} bytes");
            }
            else if (!expected.AsSpan().SequenceEqual(actual))
            {
                problems.Add($"{flat} differs: {DiffBytes(expected, actual)}");
            }
        }

        return problems;
    }

    /// <summary>Reads the size and SHA-256 of each of the five GPO files for the verification artifact.</summary>
    internal Dictionary<string, (long Size, string Sha256)?> SnapshotFileMetadata()
    {
        var map = new Dictionary<string, (long Size, string Sha256)?>(StringComparer.Ordinal);
        foreach ((string flat, string relative) in Files)
        {
            string path = Path.Combine(Root, relative);
            if (!File.Exists(path))
            {
                map[flat] = null;
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            map[flat] = (bytes.LongLength, sha);
        }

        return map;
    }

    private static string DiffBytes(byte[] expected, byte[] actual)
    {
        int at = 0;
        while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
        {
            at++;
        }

        return $"expected {expected.Length} bytes, got {actual.Length}, first difference at offset {at}";
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
    private static readonly TimeSpan TreeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private const string MainWindowClass = "MMCMainFrame";
    private const string TreeClass = "SysTreeView32";

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
            _ = Stop(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>Expands the path from Computer Configuration down to a category and selects the last node.</summary>
    internal void SelectCategory(params string[] path)
    {
        AutomationElement tree = WaitForTree();
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
        int pid = _process.Id;
        bool gone;
        try
        {
            gone = Stop(_process);
        }
        finally
        {
            _automation.Dispose();
            _process.Dispose();
        }

        if (!gone)
        {
            throw new InvalidOperationException($"mmc.exe (pid {pid}) was still running after it was closed and killed.");
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

            IntPtr hwnd = FindMainWindow(process.Id);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException(
            $"mmc.exe showed no {MainWindowClass} window within {timeout.TotalSeconds:0} s. Top-level classes seen: {TopLevelClasses(process.Id)}.");
    }

    // The visible MMCMainFrame top-level window with an area, of this process only. Process.MainWindowHandle is
    // not used: it can name a window that is not the console frame.
    private static IntPtr FindMainWindow(int processId)
    {
        foreach (IntPtr hwnd in TopLevelWindows(processId))
        {
            if (Win32.IsVisible(hwnd) && HasArea(hwnd) && string.Equals(Win32.ClassNameOf(hwnd), MainWindowClass, StringComparison.Ordinal))
            {
                return hwnd;
            }
        }

        return IntPtr.Zero;
    }

    private static bool Stop(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            _ = process.CloseMainWindow();
            if (process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited while it was being closed, or it could not be closed; HasExited tells which.
            return process.HasExited;
        }
    }

    // The tree is the one visible SysTreeView32 child window with an area, taken with automation FromHandle.
    // Only when that finds nothing, the UI Automation searches over the main window run: by control type Tree,
    // then by class SysTreeView32.
    private AutomationElement? FindTree(out string route)
    {
        AutomationElement? byWindow = TreeByWindow();
        if (byWindow is not null)
        {
            route = "hwnd";
            return byWindow;
        }

        AutomationElement window = MainWindow;
        AutomationElement? byType = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        if (byType is not null)
        {
            route = "uiaType";
            return byType;
        }

        AutomationElement? byClass = window.FindFirstDescendant(cf => cf.ByClassName(TreeClass));
        route = byClass is null ? "none" : "uiaClass";
        return byClass;
    }

    private AutomationElement? TreeByWindow()
    {
        List<IntPtr> trees = ChildWindows(Hwnd)
            .Where(child => string.Equals(Win32.ClassNameOf(child), TreeClass, StringComparison.Ordinal) && Win32.IsVisible(child) && HasArea(child))
            .ToList();
        if (trees.Count != 1)
        {
            return null;
        }

        IntPtr tree = trees[0];
        AutomationElement element = _automation.FromHandle(tree);
        bool same = new IntPtr(element.Properties.NativeWindowHandle.Value) == tree;
        return same && Win32.IsWindow(tree) && Win32.IsVisible(tree) ? element : null;
    }

    // Polls until a tree exists and has at least one TreeItem child, for up to 60 s.
    // A failed lookup inside the poll is a miss, not an error.
    private AutomationElement WaitForTree()
    {
        AutomationElement? tree = null;
        string route = "none";
        bool ready = ControlTestSupport.WaitUntil(
            () =>
            {
                try
                {
                    tree = FindTree(out route);
                    return tree is not null && tree.FindFirstChild(cf => cf.ByControlType(ControlType.TreeItem)) is not null;
                }
                catch (Exception)
                {
                    return false;
                }
            },
            TreeTimeout);
        if (ready && tree is not null)
        {
            return tree;
        }

        throw new InvalidOperationException(
            $"The classic console has no tree with an item within {TreeTimeout.TotalSeconds:0} s. Last route: {route}. {DescribeChildren()}");
    }

    private string DescribeChildren()
    {
        try
        {
            List<string> classes = ChildWindows(Hwnd).Select(Win32.ClassNameOf).ToList();
            int trees = classes.Count(name => string.Equals(name, TreeClass, StringComparison.Ordinal));
            return $"Child window classes of the main window ({classes.Count}): {string.Join("|", classes.Distinct(StringComparer.Ordinal))}. " +
                $"{TreeClass} windows: {trees}.";
        }
        catch (Exception ex)
        {
            return "The child windows could not be read: " + ex.Message;
        }
    }

    private static bool HasArea(IntPtr hwnd) =>
        Win32.GetWindowRect(hwnd, out Win32.NativeRect rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;

    private static string TopLevelClasses(int processId) =>
        string.Join("|", TopLevelWindows(processId).Select(hwnd => Win32.ClassNameOf(hwnd) + (Win32.IsVisible(hwnd) ? "" : " (hidden)")));

    private static List<IntPtr> TopLevelWindows(int processId)
    {
        var handles = new List<IntPtr>();
        _ = NativeEnum.EnumWindows(
            (hwnd, _) =>
            {
                if (Win32.ProcessIdOf(hwnd) == unchecked((uint)processId))
                {
                    handles.Add(hwnd);
                }

                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var handles = new List<IntPtr>();
        _ = NativeEnum.EnumChildWindows(
            parent,
            (hwnd, _) =>
            {
                handles.Add(hwnd);
                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private static class NativeEnum
    {
        internal delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // The two enumerations are small and need no LibraryImport plumbing.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
#pragma warning restore SYSLIB1054
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
                .FirstOrDefault(row => RowNameIs(ShellRun.SafeName(row), policyName));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a list row is the policy: classic can name the row with the bare display name or with the
    /// "<c>display name</c> setting" spelling BWC uses. The setting dialog title is always the bare name.
    /// </summary>
    private static bool RowNameIs(string rowName, string policyName) =>
        string.Equals(rowName, policyName, StringComparison.Ordinal)
        || string.Equals(rowName, policyName + " setting", StringComparison.Ordinal);

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

    /// <summary>
    /// The result-list row of the fixture policy. Classic names a policy row "<c>display name</c> setting", and BWC
    /// copies that, so the list lookups use this name. The dialog title stays the bare display name.
    /// </summary>
    internal const string FixturePolicyRow = FixturePolicy + " setting";

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

    /// <summary>
    /// Walks Local Computer Policy, Computer or User Configuration, and each path segment in tree order, searching
    /// strictly after the previous node so shared names under Computer and User never collide.
    /// </summary>
    internal static bool SelectPath(GpeditRun run, string root, IReadOnlyList<string> path, string expectedRow)
    {
        string rootNode = string.Equals(root, "Computer", StringComparison.Ordinal)
            ? "Computer Configuration"
            : "User Configuration";
        var segments = new List<string>(2 + path.Count) { GpeditRun.RootName, rootNode };
        segments.AddRange(path);

        int afterIndex = -1;
        for (int i = 0; i < segments.Count; i++)
        {
            string segment = segments[i];
            TimeSpan timeout = i == 0 ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(60);
            int foundIndex = -1;
            AutomationElement? node = null;
            bool ok = ShellRun.WaitUntil(
                () =>
                {
                    (node, foundIndex) = FindTreeNodeAfter(run, segment, afterIndex);
                    return node is not null;
                },
                timeout);
            if (!ok || node is null)
            {
                return false;
            }

            string state = ControlTestSupport.StateOf(node);
            if (state is "Collapsed" or "PartiallyExpanded")
            {
                _ = ControlTestSupport.Expand(node);
            }

            ControlTestSupport.Select(node);
            Thread.Sleep(250);
            afterIndex = foundIndex;
        }

        return ShellRun.WaitUntil(
            () => run.Rows().Any(r => string.Equals(r.Name, expectedRow, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15));
    }

    private static (AutomationElement? Element, int Index) FindTreeNodeAfter(GpeditRun run, string name, int afterIndex)
    {
        try
        {
            AutomationElement? tree = run.Shell.Find(ShellRun.TreeControlId);
            if (tree is null)
            {
                return (null, -1);
            }

            AutomationElement[] items = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
            for (int i = afterIndex + 1; i < items.Length; i++)
            {
                if (string.Equals(ShellRun.SafeName(items[i]), name, StringComparison.Ordinal))
                {
                    return (items[i], i);
                }
            }
        }
        catch (Exception)
        {
            // The tree may rebuild while expanding; the caller polls again.
        }

        return (null, -1);
    }
}

/// <summary>
/// Reads, backs up, clears, and restores the four fixed BWC-Oracle registry keys (HKLM and HKCU,
/// Software\Policies\BWC-Oracle and Software\BWC-Oracle-Gpedit) using the same JSON shape as the oracle driver.
/// </summary>
internal sealed class OracleLiveRegistry
{
    private sealed record RootSpec(RegistryHive Hive, string SubKey, string Display);

    private sealed record CapturedValue(string Name, RegistryValueKind Kind, object Data);

    private sealed class CapturedKey
    {
        internal List<CapturedValue> Values { get; } = [];
        internal Dictionary<string, CapturedKey> Children { get; } = new(StringComparer.Ordinal);
    }

    private static readonly RootSpec[] AllowedRoots =
    [
        new(RegistryHive.LocalMachine, @"Software\Policies\BWC-Oracle", @"HKLM\Software\Policies\BWC-Oracle"),
        new(RegistryHive.LocalMachine, @"Software\BWC-Oracle-Gpedit", @"HKLM\Software\BWC-Oracle-Gpedit"),
        new(RegistryHive.CurrentUser, @"Software\Policies\BWC-Oracle", @"HKCU\Software\Policies\BWC-Oracle"),
        new(RegistryHive.CurrentUser, @"Software\BWC-Oracle-Gpedit", @"HKCU\Software\BWC-Oracle-Gpedit"),
    ];

    private readonly Dictionary<string, CapturedKey?> _before;
    private readonly string _beforeJson;

    private OracleLiveRegistry(Dictionary<string, CapturedKey?> before, string beforeJson)
    {
        _before = before;
        _beforeJson = beforeJson;
    }

    /// <summary>Snapshots all four BWC-Oracle roots in memory without modifying the registry.</summary>
    internal static OracleLiveRegistry Capture()
    {
        var before = new Dictionary<string, CapturedKey?>(StringComparer.Ordinal);
        foreach (RootSpec spec in AllowedRoots)
        {
            AssertAllowed(spec.Hive, spec.SubKey);
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(spec.SubKey, writable: false);
            before[spec.Display] = key is null ? null : CaptureTree(key);
        }

        return new OracleLiveRegistry(before, Read().ToJsonString());
    }

    /// <summary>Reads the four BWC-Oracle roots in the exact JSON format recorded by the oracle capture.</summary>
    internal static JsonObject Read()
    {
        var result = new JsonObject();
        foreach (RootSpec spec in AllowedRoots)
        {
            AssertAllowed(spec.Hive, spec.SubKey);
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(spec.SubKey, writable: false);
            if (key is null)
            {
                result[spec.Display] = null;
                continue;
            }

            Walk(key, spec.Display, result);
        }

        return result;
    }

    /// <summary>Clears all four BWC-Oracle roots before a Group G case and verifies they stay absent.</summary>
    internal static void ClearAll()
    {
        Thread.Sleep(500);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            foreach (RootSpec spec in AllowedRoots)
            {
                AssertAllowed(spec.Hive, spec.SubKey);
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(spec.SubKey, throwOnMissingSubKey: false);
            }

            Thread.Sleep(200);
            if (AreAllAbsent())
            {
                return;
            }
        }

        throw new InvalidOperationException("One or more BWC-Oracle registry keys remained after ClearAll: " + Read().ToJsonString());
    }

    /// <summary>Restores all four BWC-Oracle roots to their captured baseline and reports any problems.</summary>
    internal IReadOnlyList<string> Restore()
    {
        var problems = new List<string>();
        try
        {
            foreach (RootSpec spec in AllowedRoots)
            {
                AssertAllowed(spec.Hive, spec.SubKey);
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
                baseKey.DeleteSubKeyTree(spec.SubKey, throwOnMissingSubKey: false);
                CapturedKey? captured = _before[spec.Display];
                if (captured is not null)
                {
                    using RegistryKey created = baseKey.CreateSubKey(spec.SubKey, writable: true);
                    RestoreTree(created, captured);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problems.Add("registry: " + ex.Message);
            return problems;
        }

        string actualJson = Read().ToJsonString();
        if (!string.Equals(_beforeJson, actualJson, StringComparison.Ordinal))
        {
            problems.Add($"BWC-Oracle registry differs after restore. Expected {_beforeJson}, got {actualJson}.");
        }

        return problems;
    }

    private static void AssertAllowed(RegistryHive hive, string subKey)
    {
        bool match = AllowedRoots.Any(r => r.Hive == hive && string.Equals(r.SubKey, subKey, StringComparison.OrdinalIgnoreCase));
        if (!match)
        {
            throw new InvalidOperationException($"Refused registry operation outside the four BWC-Oracle roots: {hive}\\{subKey}.");
        }
    }

    private static bool AreAllAbsent()
    {
        foreach (RootSpec spec in AllowedRoots)
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(spec.SubKey, writable: false);
            if (key is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static CapturedKey CaptureTree(RegistryKey key)
    {
        var node = new CapturedKey();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            RegistryValueKind kind = key.GetValueKind(name);
            object? data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is not null)
            {
                node.Values.Add(new CapturedValue(name, kind, data));
            }
        }

        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using RegistryKey? child = key.OpenSubKey(sub, writable: false);
            if (child is not null)
            {
                node.Children[sub] = CaptureTree(child);
            }
        }

        return node;
    }

    private static void RestoreTree(RegistryKey target, CapturedKey captured)
    {
        foreach (CapturedValue value in captured.Values)
        {
            target.SetValue(value.Name, value.Data, value.Kind);
        }

        foreach ((string subName, CapturedKey child) in captured.Children)
        {
            using RegistryKey subKey = target.CreateSubKey(subName, writable: true);
            RestoreTree(subKey, child);
        }
    }

    private static void Walk(RegistryKey key, string path, JsonObject into)
    {
        var values = new JsonObject();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            RegistryValueKind kind = key.GetValueKind(name);
            object? data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values[name.Length == 0 ? "(Default)" : name] = new JsonObject
            {
                ["type"] = kind.ToString(),
                ["data"] = Format(data),
            };
        }

        into[path] = values;
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using RegistryKey? child = key.OpenSubKey(sub, writable: false);
            if (child is not null)
            {
                Walk(child, path + "\\" + sub, into);
            }
        }
    }

    private static JsonNode? Format(object? data) => data switch
    {
        null => null,
        byte[] bytes => JsonValue.Create(Convert.ToHexString(bytes).ToLowerInvariant()),
        string[] lines => new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
        string text => JsonValue.Create(text),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        _ => JsonValue.Create(data.ToString()),
    };
}

/// <summary>
/// Replays one Group G capture case through the ship Local Group Policy Editor on the real policy folder and
/// verifies both the five GPO files and the live BWC-Oracle registry at 0, 4, and 8 s.
/// </summary>
internal sealed class ShipGReplayDriver
{
    private static readonly TimeSpan FileSettleTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RegistrySettleTimeout = TimeSpan.FromSeconds(6);

    // Classic omits RP_FORCE, so in the 2026-11-21 capture these 8 cases wrote gpt.ini v=1 right after *_Ok left
    // gpsvc cached at v=1 and skipped applying step 1. BWC intentionally passes RP_FORCE and applies Flag=1.
    private static readonly Dictionary<string, (string Hive, string Flag, bool HasDisabledStep)> VersionCollisionCases =
        new(StringComparer.Ordinal)
        {
            ["G/Machine_Apply"] = ("HKLM", "MachineFlag", false),
            ["G/User_Apply"] = ("HKCU", "UserFlag", false),
            ["G/Both_Computer_Apply"] = ("HKLM", "BothFlag", false),
            ["G/Both_User_Apply"] = ("HKCU", "BothFlag", false),
            ["G/Machine_Disabled"] = ("HKLM", "MachineFlag", true),
            ["G/User_Disabled"] = ("HKCU", "UserFlag", true),
            ["G/Both_Computer_Disabled"] = ("HKLM", "BothFlag", true),
            ["G/Both_User_Disabled"] = ("HKCU", "BothFlag", true),
        };

    private readonly RealPolicyFolder _policy;
    private readonly GpeditChecks _checks;
    private readonly GroupGReport _report;

    internal ShipGReplayDriver(RealPolicyFolder policy, GpeditChecks checks, GroupGReport report)
    {
        _policy = policy;
        _checks = checks;
        _report = report;
    }

    internal bool Run(GoldenCase golden)
    {
        _policy.ClearForCase();
        OracleLiveRegistry.ClearAll();

        string scope = "gpedit-interop-" + SanitizeScope(golden.Id);
        using GpeditRun run = GpeditRun.OpenShip(scope, InteropShip.EditorExe);
        bool allPassed = true;
        int stepIndex = 0;

        foreach (GoldenOp op in golden.Ops)
        {
            switch (op.Kind)
            {
                case "wait":
                    Thread.Sleep(op.Op["waitMs"]?.GetValue<int>() ?? 4000);
                    break;
                case "snapshot":
                    (JsonObject expectedReg, string regMode) = ExpectedLiveRegistry(golden, op);
                    if (op.IsStepSnapshot)
                    {
                        GoldenStep step = golden.Steps[stepIndex];
                        bool filesOk = VerifyStepFiles(golden, step, op.Index);
                        bool regOk = VerifyLiveRegistry(golden, op, expectedReg, regMode, settle: true);
                        allPassed &= filesOk && regOk;
                        stepIndex++;
                    }
                    else
                    {
                        bool regOk = VerifyLiveRegistry(golden, op, expectedReg, regMode, settle: false);
                        allPassed &= regOk;
                    }

                    break;
                case "editPolicy":
                    bool editOk = ExecuteEdit(run, golden, op);
                    allPassed &= editOk;
                    if (!editOk)
                    {
                        return false;
                    }

                    break;
                default:
                    _checks.Record($"{golden.Id}.op{op.Index}.kind", false, $"Unsupported Group G op kind '{op.Kind}'.");
                    return false;
            }
        }

        return allPassed;
    }

    private bool VerifyStepFiles(GoldenCase golden, GoldenStep step, int opIndex)
    {
        bool matched = ShellRun.WaitUntil(() => _policy.CompareGoldenStep(step).Count == 0, FileSettleTimeout);
        List<string> problems = _policy.CompareGoldenStep(step);
        Dictionary<string, (long Size, string Sha256)?> actualMeta = _policy.SnapshotFileMetadata();
        _report.AddFileStep(golden, step, actualMeta, problems);

        bool pass = matched || problems.Count == 0;
        _checks.Record(
            $"{golden.Id}.step{step.Number}.files",
            pass,
            pass
                ? $"after op {opIndex} the five GPO files match '{step.Label}' byte for byte."
                : $"after op {opIndex} ('{step.Label}'): {string.Join(" | ", problems)}");
        return pass;
    }

    private bool VerifyLiveRegistry(GoldenCase golden, GoldenOp op, JsonObject expected, string mode, bool settle)
    {
        if (settle)
        {
            _ = ShellRun.WaitUntil(() => CompareLiveRegistry(expected, OracleLiveRegistry.Read()).Count == 0, RegistrySettleTimeout);
            Thread.Sleep(500);
        }

        JsonObject actual = OracleLiveRegistry.Read();
        List<string> problems = CompareLiveRegistry(expected, actual);
        _report.AddRegistrySnapshot(golden.Id, op.Index, op.Text("label") ?? string.Empty, mode, expected, actual, problems);

        bool pass = problems.Count == 0;
        _checks.Record(
            $"{golden.Id}.op{op.Index}.registry",
            pass,
            pass
                ? $"liveRegistry ({op.Text("label")}, {mode}) matches expected."
                : $"liveRegistry ({op.Text("label")}, {mode}): {string.Join(" | ", problems)}");
        return pass;
    }

    private bool ExecuteEdit(GpeditRun run, GoldenCase golden, GoldenOp op)
    {
        string ctx = $"{golden.Id}.op{op.Index}";
        string root = op.Text("root") ?? throw new InvalidOperationException($"{ctx}: missing root.");
        string[] path = (op.Op["path"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToArray();
        string policy = op.Text("policy") ?? throw new InvalidOperationException($"{ctx}: missing policy.");
        string rowName = policy + " setting";

        bool selected = InteropShip.SelectPath(run, root, path, rowName);
        _checks.Record($"{ctx}.navigate", selected, $"Selected {root}/{string.Join("/", path)} with row '{rowName}'.");
        if (!selected)
        {
            return false;
        }

        using GpeditDialog? dialog = GpeditDialog.Open(run, rowName, waitReadout: false);
        _checks.Record($"{ctx}.opens", dialog is not null, $"Opened setting dialog for '{rowName}'.");
        if (dialog is null)
        {
            return false;
        }

        JsonArray? options = op.Op["options"] as JsonArray;
        string? state = op.Text("state") ?? (options is { Count: > 0 } ? "Enabled" : null);
        if (state is not null)
        {
            string radio = state switch
            {
                "Enabled" => GpeditDialog.Enabled,
                "Disabled" => GpeditDialog.Disabled,
                _ => GpeditDialog.NotConfigured,
            };
            if (!dialog.IsSelected(radio))
            {
                _ = dialog.Select(radio);
            }

            bool stateOk = string.Equals(InteropShip.DialogState(dialog), state, StringComparison.Ordinal);
            _checks.Record($"{ctx}.state", stateOk, $"State is '{InteropShip.DialogState(dialog)}', expected '{state}'.");
            if (!stateOk)
            {
                return false;
            }
        }

        if (options is { Count: > 0 })
        {
            if (!WaitFirstOptionEnabled(dialog, options))
            {
                _checks.Record($"{ctx}.optionsEnabled", false, "Option controls did not enable after selecting Enabled.");
                return false;
            }

            foreach (JsonObject option in options.OfType<JsonObject>())
            {
                if (!SetShipOption(dialog, option, ctx))
                {
                    return false;
                }
            }
        }

        if (op.Text("comment") is { } comment && !string.Equals(dialog.TextOf(GpeditDialog.Comment), comment, StringComparison.Ordinal))
        {
            bool typed = dialog.SetComment(comment);
            bool commentOk = typed && string.Equals(dialog.TextOf(GpeditDialog.Comment), comment, StringComparison.Ordinal);
            _checks.Record($"{ctx}.comment", commentOk, $"Comment is '{dialog.TextOf(GpeditDialog.Comment)}', expected '{comment}'.");
            if (!commentOk)
            {
                return false;
            }
        }

        return ExecutePresses(dialog, op, ctx);
    }

    private static bool WaitFirstOptionEnabled(GpeditDialog dialog, JsonArray options)
    {
        JsonObject? first = options.OfType<JsonObject>().FirstOrDefault();
        if (first is null)
        {
            return true;
        }

        string kind = first["expectKind"]?.GetValue<string>() ?? string.Empty;
        string label = first["label"]?.GetValue<string>() ?? string.Empty;
        string elementId = first["elementId"]?.GetValue<string>() ?? string.Empty;
        string suffix = label.Length > 0 ? label : elementId;
        string[] candidateIds = kind switch
        {
            "decimalTextBox" or "longDecimalTextBox" or "spinner" => ["textBox" + suffix, "numericUpDown" + suffix + ".Text"],
            "textBox" => ["textBox" + suffix],
            "comboBox" => ["comboBox" + suffix],
            "dropdownList" => ["dropDownList" + suffix],
            "multiText" => ["multiTextBox" + suffix],
            "list" => ["button" + suffix],
            _ => [],
        };
        if (candidateIds.Length == 0)
        {
            return true;
        }

        return ShellRun.WaitUntil(
            () => candidateIds.Any(id => dialog.Find(id, 0.2)?.IsEnabled == true),
            TimeSpan.FromSeconds(3));
    }

    private bool SetShipOption(GpeditDialog dialog, JsonObject option, string ctx)
    {
        string kind = option["expectKind"]?.GetValue<string>() ?? string.Empty;
        string elementId = option["elementId"]?.GetValue<string>() ?? string.Empty;
        string label = option["label"]?.GetValue<string>() ?? string.Empty;
        string suffix = label.Length > 0 ? label : elementId;
        string id = $"{ctx}.option.{elementId}";

        switch (kind)
        {
            case "decimalTextBox":
            case "longDecimalTextBox":
            case "spinner":
                string digits = option["text"]!.GetValue<string>();
                string? numberBox = new[] { "textBox" + suffix, "numericUpDown" + suffix + ".Text" }
                    .FirstOrDefault(c => dialog.Find(c, 0.5) is not null);
                bool numOk = numberBox is not null
                    && dialog.TypeInto(numberBox, digits)
                    && string.Equals(dialog.TextOf(numberBox), digits, StringComparison.Ordinal);
                _checks.Record(id, numOk, $"Number box '{suffix}' holds '{(numberBox is null ? "<missing>" : dialog.TextOf(numberBox))}', expected '{digits}'.");
                return numOk;

            case "textBox":
            case "comboBox":
                string text = option["text"]?.GetValue<string>() ?? option["item"]!.GetValue<string>();
                string box = (kind == "comboBox" ? "comboBox" : "textBox") + suffix;
                bool textOk = dialog.TypeInto(box, text) && string.Equals(dialog.TextOf(box), text, StringComparison.Ordinal);
                _checks.Record(id, textOk, $"Text box '{suffix}' holds '{dialog.TextOf(box)}', expected '{text}'.");
                return textOk;

            case "dropdownList":
                string item = option["item"]!.GetValue<string>();
                bool chosen = dialog.Choose("dropDownList" + suffix, item);
                _checks.Record(id, chosen, $"Selected '{item}' in drop-down '{suffix}'.");
                return chosen;

            case "multiText":
                string[] lines = (option["lines"] as JsonArray ?? []).Select(l => l!.GetValue<string>()).ToArray();
                bool multiOk = TypeMultiLines(dialog, "multiTextBox" + suffix, lines);
                _checks.Record(id, multiOk, $"Typed {lines.Length} line(s) into '{suffix}'.");
                return multiOk;

            case "list":
                return SetShipList(dialog, option, suffix, id);

            default:
                _checks.Record(id, false, $"Unsupported Group G option kind '{kind}'.");
                return false;
        }
    }

    private bool SetShipList(GpeditDialog dialog, JsonObject option, string suffix, string id)
    {
        List<string[]> rows = (option["rows"] as JsonArray ?? [])
            .Select(r => ((JsonArray)r!).Select(c => c!.GetValue<string>()).ToArray())
            .ToList();
        bool clear = option["clearRows"]?.GetValue<bool>() == true;
        _ = dialog.Invoke("button" + suffix);
        GpeditContents? contents = GpeditContents.Wait(dialog);
        _checks.Record($"{id}.opens", contents is not null, $"Show... of '{suffix}' opened Show Contents.");
        if (contents is null)
        {
            return false;
        }

        int columns = contents.Cell(0, 1) is null ? 1 : 2;
        int existing = contents.Rows(columns).Count;
        if (clear)
        {
            for (int i = 0; i < existing; i++)
            {
                contents.DeleteRow(0);
            }
        }

        int at = clear ? 0 : existing;
        foreach (string[] cells in rows)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                contents.Edit(at, c, cells[c]);
            }

            at++;
        }

        _ = contents.Ok();
        bool closed = contents.WaitClosed();
        _checks.Record($"{id}.closes", closed, "Show Contents closed after OK.");
        return closed;
    }

    private static bool TypeMultiLines(GpeditDialog dialog, string controlId, string[] lines)
    {
        if (!dialog.FocusControl(controlId))
        {
            return false;
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        Keyboard.Type(VirtualKeyShort.DELETE);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                Keyboard.Type(VirtualKeyShort.RETURN);
            }

            Keyboard.Type(lines[i]);
        }

        Thread.Sleep(300);
        return true;
    }

    private bool ExecutePresses(GpeditDialog dialog, GoldenOp op, string ctx)
    {
        string[] presses = (op.Op["press"] as JsonArray)?.Select(p => p!.GetValue<string>()).ToArray() ?? ["OK"];
        for (int k = 0; k < presses.Length; k++)
        {
            string buttonName = presses[k];
            string buttonId = buttonName switch
            {
                "OK" => GpeditDialog.Ok,
                "Apply" => GpeditDialog.Apply,
                "Cancel" => GpeditDialog.Cancel,
                _ => throw new InvalidOperationException($"Unsupported button '{buttonName}'."),
            };

            bool invoked = dialog.Invoke(buttonId);
            if (buttonName == "Apply")
            {
                bool applied = invoked && ShellRun.WaitUntil(() => !dialog.IsEnabled(GpeditDialog.Apply), TimeSpan.FromSeconds(30));
                _checks.Record($"{ctx}.press{k}.applied", applied, "Apply completed and disabled again.");
                if (!applied)
                {
                    return false;
                }
            }
            else
            {
                bool closed = invoked && InteropShip.WaitClosed(dialog, TimeSpan.FromSeconds(30));
                _checks.Record($"{ctx}.press{k}.closes", closed, $"{buttonName} closed the dialog.");
                if (!closed)
                {
                    return false;
                }
            }
        }

        if (dialog.Find(GpeditDialog.Cancel, 0.4) is not null)
        {
            _ = dialog.Invoke(GpeditDialog.Cancel);
            bool closedAtEnd = InteropShip.WaitClosed(dialog, TimeSpan.FromSeconds(15));
            _checks.Record($"{ctx}.cancelAtEnd", closedAtEnd, "Cancel closed the dialog after Apply.");
            return closedAtEnd;
        }

        return true;
    }

    private static (JsonObject Expected, string Mode) ExpectedLiveRegistry(GoldenCase golden, GoldenOp op)
    {
        if (VersionCollisionCases.TryGetValue(golden.Id, out (string Hive, string Flag, bool HasDisabledStep) info))
        {
            string label = op.Text("label") ?? string.Empty;
            bool isAppliedStep = label.StartsWith("step apply", StringComparison.Ordinal)
                || label.StartsWith("apply:", StringComparison.Ordinal)
                || label.StartsWith("step enabled", StringComparison.Ordinal)
                || label.StartsWith("enabled:", StringComparison.Ordinal);
            if (isAppliedStep)
            {
                return (BuildFlagLiveRegistry(info.Hive, info.Flag), "rp-force-override");
            }

            return (op.LiveRegistry!, info.HasDisabledStep && label.Contains("disabled", StringComparison.Ordinal)
                ? "classic-disabled"
                : "classic-start");
        }

        return (op.LiveRegistry!, "classic-verbatim");
    }

    private static JsonObject BuildFlagLiveRegistry(string hive, string flagName)
    {
        string policyRoot = hive + @"\Software\Policies\BWC-Oracle";
        string basicsKey = policyRoot + @"\Basics";
        string gpeditRoot = hive + @"\Software\BWC-Oracle-Gpedit";
        string otherHive = hive == "HKLM" ? "HKCU" : "HKLM";

        var root = new JsonObject();
        if (hive == "HKLM")
        {
            root[policyRoot] = new JsonObject();
            root[basicsKey] = new JsonObject
            {
                [flagName] = new JsonObject { ["type"] = "DWord", ["data"] = 1 },
            };
            root[gpeditRoot] = null;
            root[otherHive + @"\Software\Policies\BWC-Oracle"] = null;
            root[otherHive + @"\Software\BWC-Oracle-Gpedit"] = null;
        }
        else
        {
            root[otherHive + @"\Software\Policies\BWC-Oracle"] = null;
            root[otherHive + @"\Software\BWC-Oracle-Gpedit"] = null;
            root[policyRoot] = new JsonObject();
            root[basicsKey] = new JsonObject
            {
                [flagName] = new JsonObject { ["type"] = "DWord", ["data"] = 1 },
            };
            root[gpeditRoot] = null;
        }

        return root;
    }

    private static List<string> CompareLiveRegistry(JsonObject expected, JsonObject actual)
    {
        var problems = new List<string>();
        foreach (KeyValuePair<string, JsonNode?> pair in expected)
        {
            if (!actual.TryGetPropertyValue(pair.Key, out JsonNode? actualNode))
            {
                problems.Add($"missing key '{pair.Key}'");
                continue;
            }

            if (pair.Value is null && actualNode is null)
            {
                continue;
            }

            if (pair.Value is null || actualNode is null)
            {
                problems.Add($"key '{pair.Key}' expected {(pair.Value is null ? "null" : "present")}, got {(actualNode is null ? "null" : "present")}");
                continue;
            }

            if (pair.Value is JsonObject expectedValues && actualNode is JsonObject actualValues)
            {
                CompareValues(pair.Key, expectedValues, actualValues, problems);
            }
            else
            {
                problems.Add($"key '{pair.Key}' shape mismatch");
            }
        }

        foreach (KeyValuePair<string, JsonNode?> pair in actual)
        {
            if (!expected.ContainsKey(pair.Key))
            {
                problems.Add($"unexpected key '{pair.Key}'");
            }
        }

        return problems;
    }

    private static void CompareValues(string keyPath, JsonObject expected, JsonObject actual, List<string> problems)
    {
        foreach (KeyValuePair<string, JsonNode?> pair in expected)
        {
            if (!actual.TryGetPropertyValue(pair.Key, out JsonNode? actualEntry) || actualEntry is not JsonObject actualObj || pair.Value is not JsonObject expectedObj)
            {
                problems.Add($"{keyPath}\\{pair.Key} missing or invalid");
                continue;
            }

            string? expectedType = expectedObj["type"]?.GetValue<string>();
            string? actualType = actualObj["type"]?.GetValue<string>();
            if (!string.Equals(expectedType, actualType, StringComparison.Ordinal))
            {
                problems.Add($"{keyPath}\\{pair.Key} type expected '{expectedType}', got '{actualType}'");
            }

            if (!JsonDataEquals(expectedObj["data"], actualObj["data"]))
            {
                problems.Add($"{keyPath}\\{pair.Key} data expected {expectedObj["data"]?.ToJsonString()}, got {actualObj["data"]?.ToJsonString()}");
            }
        }

        foreach (KeyValuePair<string, JsonNode?> pair in actual)
        {
            if (!expected.ContainsKey(pair.Key))
            {
                problems.Add($"unexpected value '{keyPath}\\{pair.Key}'");
            }
        }
    }

    private static bool JsonDataEquals(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null && actual is null)
        {
            return true;
        }

        if (expected is null || actual is null)
        {
            return false;
        }

        if (expected is JsonValue ev && actual is JsonValue av)
        {
            if (ev.TryGetValue(out decimal ed) && av.TryGetValue(out decimal ad))
            {
                return ed == ad;
            }

            return string.Equals(ev.ToJsonString(), av.ToJsonString(), StringComparison.Ordinal);
        }

        if (expected is JsonArray ea && actual is JsonArray aa)
        {
            return ea.Count == aa.Count && Enumerable.Range(0, ea.Count).All(i => JsonDataEquals(ea[i], aa[i]));
        }

        return string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal);
    }

    private static string SanitizeScope(string caseId) =>
        caseId.Replace('/', '-').Replace('_', '-').ToLowerInvariant();
}

/// <summary>
/// Writes the verifiable Group G elevated replay artifact to
/// <c>artifacts/e2e/visual/gpedit/gpedit-interop-groupg.json</c>.
/// </summary>
internal sealed class GroupGReport
{
    private readonly List<FileStepRow> _fileRows = [];
    private readonly List<RegistrySnapshotRow> _registryRows = [];

    internal sealed record FileStepRow(
        string Case,
        int Step,
        string Label,
        string File,
        long? ExpectedSize,
        string? ExpectedSha256,
        long? ActualSize,
        string? ActualSha256,
        bool Equal);

    internal sealed record RegistrySnapshotRow(
        string Case,
        int OpIndex,
        string Label,
        string Mode,
        JsonObject Expected,
        JsonObject Actual,
        bool Equal,
        IReadOnlyList<string> Problems);

    internal void AddFileStep(
        GoldenCase golden,
        GoldenStep step,
        Dictionary<string, (long Size, string Sha256)?> actualMeta,
        List<string> problems)
    {
        _ = problems;
        foreach (string flat in GoldenFiles.Names)
        {
            GoldenFile? expected = step.Files[flat];
            actualMeta.TryGetValue(flat, out (long Size, string Sha256)? actual);
            bool equal = expected is null
                ? actual is null
                : actual is not null && expected.Size == actual.Value.Size && string.Equals(expected.Sha256, actual.Value.Sha256, StringComparison.OrdinalIgnoreCase);
            _fileRows.Add(new FileStepRow(
                golden.Id,
                step.Number,
                step.Label,
                flat,
                expected?.Size,
                expected?.Sha256,
                actual?.Size,
                actual?.Sha256,
                equal));
        }
    }

    internal void AddRegistrySnapshot(
        string caseId,
        int opIndex,
        string label,
        string mode,
        JsonObject expected,
        JsonObject actual,
        List<string> problems)
    {
        _registryRows.Add(new RegistrySnapshotRow(
            caseId,
            opIndex,
            label,
            mode,
            (JsonObject)expected.DeepClone(),
            (JsonObject)actual.DeepClone(),
            problems.Count == 0,
            problems));
    }

    internal string Write()
    {
        string folder = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", GpeditRun.Area);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "gpedit-interop-groupg.json");
        var document = new
        {
            report = "interop-groupg",
            fileChecks = _fileRows.Count,
            fileEqual = _fileRows.Count(r => r.Equal),
            fileDifferent = _fileRows.Count(r => !r.Equal),
            registrySnapshots = _registryRows.Count,
            registryEqual = _registryRows.Count(r => r.Equal),
            registryDifferent = _registryRows.Count(r => !r.Equal),
            fileRows = _fileRows,
            registryRows = _registryRows,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        E2EResults.Note("gpedit.interop.groupg.artifact", path);
        return path;
    }
}
