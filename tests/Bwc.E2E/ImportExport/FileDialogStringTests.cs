using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ImportExport;

/// <summary>
/// The Import and Export common dialogs carry the classic titles and filter names (strings 300, 301, 302, 305;
/// docs/failure-modes/file-dialogs.md section 17). The titles and the file-type entries are read through UI
/// Automation and compared with the pieces of the oracle strings, split on '#'. The default extension is
/// checked through a name typed without an extension. The test is mutating and runs only on the e2e flavor.
/// </summary>
[TestClass]
public sealed class FileDialogStringTests
{
    private const int MenuFile = 512;
    private const int MenuFileImport = 658;
    private const int MenuFileExport = 659;
    private const int StringImportTitle = 300;
    private const int StringExportTitle = 301;
    private const int StringImportFilters = 302;
    private const int StringExportFilters = 305;
    private const string ClassicMui = "en-US/regedit.exe.mui";
    private const string ExpectedDefaultExtension = "REG";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("FileDialogs")]
    [TestCategory("Mutating")]
    public void FileDialogs_TitleAndFiltersMatchClassicStrings()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        string resourcesPath = Path.Combine(config.RepoRoot, "docs", "parity", "oracle", "regedit-resources.json");
        using JsonDocument resources = JsonDocument.Parse(File.ReadAllText(resourcesPath));
        string importTitle = ClassicString(resources, StringImportTitle);
        string exportTitle = ClassicString(resources, StringExportTitle);
        string[] importNames = ClassicFilterNames(ClassicString(resources, StringImportFilters));
        string[] exportNames = ClassicFilterNames(ClassicString(resources, StringExportFilters));

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            sandbox.SetString(string.Empty, "RootMarker", "root");

            using var session = StartApp(config, sandbox, "file-dialog-strings");
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record("filedialogs.navigate", navigated, $"navigated={navigated}.");
            E2EGuards.RequireSandboxSelection(session, sandbox, "filedialogs.sandbox.selection");

            CheckDialog(driver, dialogs, automation, MenuFileImport, "filedialogs.import", importTitle, importNames);
            CheckDialog(driver, dialogs, automation, MenuFileExport, "filedialogs.export", exportTitle, exportNames);

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    [TestMethod]
    [TestCategory("ImportExport")]
    [TestCategory("FileDialogs")]
    [TestCategory("Mutating")]
    public void FileDialogs_ExportDefaultExtensionIsREG()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        string tempDir = Path.Combine(config.ArtifactsDir, "import-export", config.RunId, "default-extension");
        try
        {
            sandbox.Cleanup();
            Directory.CreateDirectory(tempDir);
            sandbox.SetString(string.Empty, "RootMarker", "root");

            using var session = StartApp(config, sandbox, "file-dialog-extension");
            using var driver = new EditingDriver(session);
            using var dialogs = new SystemFileDialogDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EResults.Record("defext.navigate", navigated, $"navigated={navigated}.");
            E2EGuards.RequireSandboxSelection(session, sandbox, "defext.sandbox.selection");

            IntPtr dialog = OpenFileCommand(driver, dialogs, MenuFileExport, "defext");
            if (dialog == IntPtr.Zero)
            {
                FailIfNeeded(checkStart);
                return;
            }

            string typedName = Path.Combine(tempDir, "defext");
            string nameNotes = "not attempted";
            string acceptNotes = "not attempted";
            bool typed = dialogs.TrySetFileName(dialog, typedName, out nameNotes);
            bool accepted = typed && dialogs.TryAccept(dialog, "Save", out acceptNotes) && dialogs.WaitGone(dialog, TimeSpan.FromSeconds(15));

            // The export runs after the dialog closes and writes a temp file that it renames when done, so the folder
            // is polled until a file exists and no temp file is left.
            string[] written = Array.Empty<string>();
            _ = ControlTestSupport.WaitUntil(
                () => (written = DefextFiles(tempDir)).Length > 0 &&
                    !written.Any(name => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(30));
            bool named = written.Any(name => string.Equals(name, "defext." + ExpectedDefaultExtension, StringComparison.OrdinalIgnoreCase));
            bool exactCase = written.Contains("defext." + ExpectedDefaultExtension, StringComparer.Ordinal);
            E2EResults.Record(
                "defext.saved-name",
                typed && accepted && named,
                $"{nameNotes} saved={accepted}, files=[{string.Join(",", written)}], expected 'defext.{ExpectedDefaultExtension}' ignoring case.");
            E2EResults.Note(
                "defext.exact-case",
                exactCase
                    ? $"defext.{ExpectedDefaultExtension} as the classic string."
                    : $"known-gap: the shell stored [{string.Join(",", written)}]. The shell takes the case of the selected " +
                      "filter pattern (*.reg), and the classic stored case is not measured.");
            if (!accepted)
            {
                _ = dialogs.TryCancel(dialog);
            }

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckDialog(
        EditingDriver driver,
        SystemFileDialogDriver dialogs,
        UIA3Automation automation,
        int commandId,
        string checkId,
        string expectedTitle,
        string[] expectedNames)
    {
        IntPtr dialog = OpenFileCommand(driver, dialogs, commandId, checkId);
        if (dialog == IntPtr.Zero)
        {
            return;
        }

        string title = ClassicDialogTests.CaptionOf(automation, dialog);
        E2EResults.Record(
            checkId + ".title",
            string.Equals(title, expectedTitle, StringComparison.Ordinal),
            $"title='{title}', expected '{expectedTitle}'.");

        AutomationElement? combo = dialogs.WaitForFileTypeCombo(dialog, TimeSpan.FromSeconds(8));
        string[] entries = combo is null ? Array.Empty<string>() : ReadComboEntries(automation, dialog, combo);
        bool same = entries.SequenceEqual(expectedNames, StringComparer.Ordinal);
        E2EResults.Record(
            checkId + ".filters",
            same,
            $"file types=[{string.Join(" | ", entries)}], expected [{string.Join(" | ", expectedNames)}].");

        _ = dialogs.TryCancel(dialog);
    }

    /// <summary>Opens the file-type list and reads its entries, from the combo or the dialog, then closes it.</summary>
    private static string[] ReadComboEntries(UIA3Automation automation, IntPtr dialog, AutomationElement combo)
    {
        try
        {
            if (combo.Patterns.ExpandCollapse.IsSupported)
            {
                combo.Patterns.ExpandCollapse.Pattern.Expand();
            }
        }
        catch (Exception)
        {
            // A combo that cannot expand still exposes its items when it is focused.
        }

        AutomationElement? window = null;
        try
        {
            window = automation.FromHandle(dialog);
        }
        catch (Exception)
        {
            // The dialog window is gone. The combo's own items are read below.
        }

        string[] names = Array.Empty<string>();
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                names = ListItemNames(combo, window);
                return names.Length > 0;
            },
            TimeSpan.FromSeconds(3));

        try
        {
            if (combo.Patterns.ExpandCollapse.IsSupported)
            {
                combo.Patterns.ExpandCollapse.Pattern.Collapse();
            }
        }
        catch (Exception)
        {
            // Collapsing is best effort. The dialog is cancelled next.
        }

        return names;
    }

    private static string[] ListItemNames(AutomationElement combo, AutomationElement? window)
    {
        var found = new List<string>();
        try
        {
            found.AddRange(combo.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)).Select(ControlTestSupport.SafeName));
            if (found.Count == 0 && window is not null)
            {
                found.AddRange(window.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Select(ControlTestSupport.SafeName)
                    .Where(name => name.Length > 0));
            }
        }
        catch (Exception)
        {
            // UIA can throw while the popup list opens. The caller retries.
        }

        return found.Where(name => name.Length > 0).ToArray();
    }

    /// <summary>
    /// The file-type entries the shell shows for an oracle filter string (name, spec, name, spec, ... with a trailing
    /// '#'). The shell shows a name that has no parenthesized pattern with its pattern added, so "All Files" with
    /// "*.*" is listed as "All Files (*.*)". Classic goes through the same shell, so the same rule applies to it.
    /// </summary>
    private static string[] ClassicFilterNames(string filterString)
    {
        string[] pieces = filterString.Split('#');
        int count = pieces.Length;
        if (count > 0 && pieces[count - 1].Length == 0)
        {
            count -= 1;
        }

        if (count % 2 != 0)
        {
            throw new InvalidOperationException($"The classic filter string has an odd number of pieces: '{filterString}'.");
        }

        var names = new List<string>();
        for (int index = 0; index < count; index += 2)
        {
            string name = pieces[index];
            string spec = pieces[index + 1];
            names.Add(name.Contains('(') ? name : $"{name} ({spec})");
        }

        return names.ToArray();
    }

    /// <summary>The saved file names in the folder that start with "defext".</summary>
    private static string[] DefextFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(folder, "defext*")
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToArray();
    }

    private static string ClassicString(JsonDocument resources, int id)
    {
        foreach (JsonElement module in resources.RootElement.GetProperty("modules").EnumerateArray())
        {
            if (!string.Equals(module.GetProperty("module").GetString(), ClassicMui, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (JsonElement table in module.GetProperty("stringTables").EnumerateArray())
            {
                foreach (JsonElement entry in table.GetProperty("strings").EnumerateArray())
                {
                    if (entry.GetProperty("id").GetInt32() == id)
                    {
                        return entry.GetProperty("text").GetString() ?? string.Empty;
                    }
                }
            }
        }

        throw new InvalidOperationException($"The classic resources have no string {id}.");
    }

    private static AppSession StartApp(E2EConfig config, SandboxRegistry sandbox, string scope)
    {
        string dataRoot = config.DataRootFor(scope);
        if (Directory.Exists(dataRoot))
        {
            Directory.Delete(dataRoot, recursive: true);
        }

        Directory.CreateDirectory(dataRoot);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };
        return new AppSession(config, config.RegeditExePath, environment: environment);
    }

    private static IntPtr OpenFileCommand(EditingDriver driver, SystemFileDialogDriver dialogs, int commandId, string checkId)
    {
        try
        {
            driver.OpenMenu(MenuFile);
        }
        catch (Exception ex)
        {
            E2EResults.Record(checkId + ".menu", false, $"File menu did not open: {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }

        AutomationElement? command = null;
        _ = ControlTestSupport.WaitUntil(
            () => (command = dialogs.FindProcessElement("MenuCommand_" + commandId)) is not null,
            TimeSpan.FromSeconds(5));
        if (command is null)
        {
            E2EResults.Record(checkId + ".menu", false, $"command {commandId} not found.");
            return IntPtr.Zero;
        }

        command.Focus();
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.RETURN);
        IntPtr dialog = dialogs.WaitForDialog(UiTimeout);
        E2EResults.Record(checkId + ".dialog", dialog != IntPtr.Zero, $"command={commandId}, file dialog hwnd=0x{dialog.ToInt64():X}.");
        return dialog;
    }

    private static void FailIfNeeded(int startIndex)
    {
        var failures = E2EResults.FailedChecksSince(startIndex);
        Assert.AreEqual(
            0,
            failures.Count,
            $"File dialog string checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
