using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Bwc.E2E.Gpedit;

/// <summary>One list row as UI Automation shows it: the index, the row name and the cell texts.</summary>
internal sealed record GpeditRow(int Index, string Name, string[] Cells);

/// <summary>
/// The files one Gpedit tree or list test runs on, all inside the scope's run data root: a sandbox policy object (the real
/// folder is never named), the template folder (the F1 fixture, or a copy with broken templates added) and the import
/// store. The class holds the only file writes of the Gpedit tree and list tests, and it names no policy file: the test
/// classes that name policy paths keep no write API (scripts/check-policy-writer.js).
/// </summary>
internal sealed class GpeditSandbox
{
    internal const string Namespace = "BWC.Policies.Oracle";
    internal const string BasicsKey = @"Software\Policies\BWC-Oracle\Basics";
    internal const string AdditiveListKey = @"Software\Policies\BWC-Oracle\Lists\Additive";
    internal const string ExplicitListKey = @"Software\Policies\BWC-Oracle\Lists\Explicit";

    private GpeditSandbox(string scope)
    {
        Scope = scope;
        DataRoot = GuiGate.Config.DataRootFor(scope);
        GpoRoot = Path.Combine(DataRoot, "gpo");
        TemplateRoot = FixtureRoot;
        ImportStore = Path.Combine(DataRoot, "template-store");
    }

    internal static string FixtureRoot => Path.Combine(GuiGate.Config.RepoRoot, "docs", "parity", "oracle", "gpedit", "fixture");

    internal string Scope { get; }

    /// <summary>The run data root of the editor process (BWC_DATA_ROOT).</summary>
    internal string DataRoot { get; }

    internal string GpoRoot { get; }

    internal string TemplateRoot { get; private set; }

    internal string ImportStore { get; }

    /// <summary>
    /// True when the editor opens the setting dialog for an edit request. The default is false: the list tests that only count
    /// the request (BWC_E2E_GPEDIT_NO_DIALOG=1) keep their modeless view, and a dialog test turns it on.
    /// </summary>
    internal bool DialogEnabled { get; set; }

    /// <summary>True makes the in-memory store of the editor report read-only (BWC_E2E_GPEDIT_READONLY=1).</summary>
    internal bool ReadOnly { get; set; }

    /// <summary>The sandbox for a test. <paramref name="name"/> is the name given to <see cref="GpeditRun.Open"/>.</summary>
    internal static GpeditSandbox Create(string name) => new("console-" + name);

    internal GpoStore Store => new(new SandboxGpoStorePolicy(GpoRoot, DataRoot, null, new RecordingRefresher()));

    /// <summary>The environment of the editor process for this sandbox.</summary>
    internal Dictionary<string, string> Environment(bool shape = false, bool icons = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_GPEDIT_HOST"] = "editor",
            ["BWC_E2E_ADMX_ROOT"] = TemplateRoot,
            ["BWC_E2E_IMPORT_STORE"] = ImportStore,
            ["BWC_E2E_GPO_ROOT"] = GpoRoot,
            ["BWC_E2E_GPEDIT_NO_DIALOG"] = DialogEnabled ? "0" : "1",
        };
        if (ReadOnly)
        {
            environment["BWC_E2E_GPEDIT_READONLY"] = "1";
        }

        if (shape)
        {
            environment["BWC_E2E_GPEDIT_SHAPE"] = "1";
        }

        if (icons)
        {
            environment["BWC_E2E_GPEDIT_ICONS"] = "1";
        }

        return environment;
    }

    /// <summary>
    /// The standard seed. Computer side: Class machine flag enabled (with a comment), Class both flag enabled (with a
    /// comment), Decimal enabled and disabled values disabled. User side: Class user flag enabled, Class both flag disabled.
    /// </summary>
    internal void SeedStandard()
    {
        // The policy files are written as bytes by this class, not by the product's writer, so the test and the reader do not
        // share the code that makes the format.
        WritePolicyFile(GpoSide.Machine, [("MachineFlag", 1u), ("BothFlag", 1u), ("ModeNumber", 9u)]);
        WritePolicyFile(GpoSide.User, [("UserFlag", 1u), ("BothFlag", 0u)]);

        // The comments go through the store, which knows the shape of a comment file.
        _ = Store.Save(GpoSide.Machine, session =>
        {
            session.Comments.SetComment(Namespace, "Pol_Class_Machine", "machine note");
            session.Comments.SetComment(Namespace, "Pol_Class_Both", "both note");
        });
    }

    /// <summary>
    /// Writes a policy file of a side with one DWORD value per entry under the Basics key: the signature, the version, then
    /// the records <c>[key;name;type;size;data]</c> (strings are UTF-16LE with a terminator, the numbers are little endian).
    /// </summary>
    internal void WritePolicyFile(GpoSide side, IReadOnlyList<(string Name, uint Value)> values) =>
        WriteRecords(side, values.Select(v => (BasicsKey, v.Name, 4u, BitConverter.GetBytes(v.Value))).ToList());

    /// <summary>
    /// The standard machine seed plus string entries in the keys of the additive list (three entries) and the explicit list
    /// (two entries) of the fixture, so those two policies read as Enabled with a list.
    /// </summary>
    internal void SeedLists()
    {
        var records = new List<(string Key, string Name, uint Type, byte[] Data)>
        {
            (BasicsKey, "MachineFlag", 4u, BitConverter.GetBytes(1u)),
            (BasicsKey, "BothFlag", 4u, BitConverter.GetBytes(1u)),
            (BasicsKey, "ModeNumber", 4u, BitConverter.GetBytes(9u)),
        };
        // A list with no valuePrefix stores each entry under its own text as the value name (the engine's rule).
        foreach ((string name, string data) in new[] { ("alpha", "alpha"), ("bravo", "bravo"), ("charlie", "charlie") })
        {
            records.Add((AdditiveListKey, name, 1u, System.Text.Encoding.Unicode.GetBytes(data + '\0')));
        }

        foreach ((string name, string data) in new[] { ("first", "one"), ("second", "two") })
        {
            records.Add((ExplicitListKey, name, 1u, System.Text.Encoding.Unicode.GetBytes(data + '\0')));
        }

        WriteRecords(GpoSide.Machine, records);
    }

    private void WriteRecords(GpoSide side, IReadOnlyList<(string Key, string Name, uint Type, byte[] Data)> records)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(new byte[] { 0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00 });
            foreach ((string key, string name, uint type, byte[] data) in records)
            {
                writer.Write('[');
                writer.Write(System.Text.Encoding.Unicode.GetBytes(key + '\0'));
                writer.Write(';');
                writer.Write(System.Text.Encoding.Unicode.GetBytes(name + '\0'));
                writer.Write(';');
                writer.Write(type);
                writer.Write(';');
                writer.Write((uint)data.Length);
                writer.Write(';');
                writer.Write(data);
                writer.Write(']');
            }
        }

        string path = GpoFileNames.PolPath(GpoRoot, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    /// <summary>
    /// The SHA-256 of each policy file of the sandbox (the policy file and the comment file of both sides, and the version
    /// file), or "absent". Read only: a test compares two readings to prove that nothing was written.
    /// </summary>
    internal SortedDictionary<string, string> PolicyFileHashes()
    {
        var paths = new List<string>
        {
            GpoFileNames.GptIniPath(GpoRoot),
            GpoFileNames.PolPath(GpoRoot, GpoSide.Machine),
            GpoFileNames.PolPath(GpoRoot, GpoSide.User),
            GpoFileNames.CmtxPath(GpoRoot, GpoSide.Machine),
            GpoFileNames.CmtxPath(GpoRoot, GpoSide.User),
        };
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string key = Path.GetRelativePath(GpoRoot, path);
            map[key] = File.Exists(path)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                : "absent";
        }

        return map;
    }

    /// <summary>Writes bytes that are not a valid policy file as the policy file of a side, after the seed.</summary>
    internal void WriteUnreadablePolicyFile(GpoSide side)
    {
        string path = GpoFileNames.PolPath(GpoRoot, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);
    }

    /// <summary>
    /// Replaces the template folder by a copy of the fixture with the named broken templates added. A case is a folder
    /// below fixture/bad.
    /// </summary>
    internal void UseTemplatesWithBadCases(params string[] cases)
    {
        string target = Path.Combine(DataRoot, "templates");
        Directory.CreateDirectory(Path.Combine(target, "en-US"));
        foreach (string file in Directory.GetFiles(FixtureRoot, "*.admx"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string file in Directory.GetFiles(Path.Combine(FixtureRoot, "en-US"), "*.adml"))
        {
            File.Copy(file, Path.Combine(target, "en-US", Path.GetFileName(file)), overwrite: true);
        }

        foreach (string name in cases)
        {
            string bad = Path.Combine(FixtureRoot, "bad", name);
            foreach (string file in Directory.GetFiles(bad, "*.admx"))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            string language = Path.Combine(bad, "en-US");
            if (Directory.Exists(language))
            {
                foreach (string file in Directory.GetFiles(language, "*.adml"))
                {
                    File.Copy(file, Path.Combine(target, "en-US", Path.GetFileName(file)), overwrite: true);
                }
            }
        }

        TemplateRoot = target;
    }

    /// <summary>
    /// Adds the files of a broken case to the template copy that <see cref="UseTemplatesWithBadCases"/> made, while an editor
    /// may be running on it. The copy must exist.
    /// </summary>
    internal void AddBadTemplateCase(string name)
    {
        string target = Path.Combine(DataRoot, "templates");
        string bad = Path.Combine(FixtureRoot, "bad", name);
        foreach (string file in Directory.GetFiles(bad, "*.admx"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        string language = Path.Combine(bad, "en-US");
        if (Directory.Exists(language))
        {
            foreach (string file in Directory.GetFiles(language, "*.adml"))
            {
                File.Copy(file, Path.Combine(target, "en-US", Path.GetFileName(file)), overwrite: true);
            }
        }
    }

    /// <summary>
    /// Removes one template (the template file and its language file, by the shared base name) from the copy that
    /// <see cref="UseTemplatesWithBadCases"/> made, while an editor may be running on it.
    /// </summary>
    internal void RemoveTemplate(string baseName)
    {
        string target = Path.Combine(DataRoot, "templates");
        File.Delete(Path.Combine(target, baseName + ".admx"));
        File.Delete(Path.Combine(target, "en-US", baseName + ".adml"));
    }

    /// <summary>The settings file of the editor in the data root, or null when none was written.</summary>
    internal string? SettingsFilePath() =>
        Directory.Exists(DataRoot) ? Directory.GetFiles(DataRoot, "settings.json", SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Writes a settings file of the editor into the data root before the first start.</summary>
    internal void WriteSettingsFile(string json)
    {
        string directory = Path.Combine(DataRoot, "gpedit");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), json);
    }

    private sealed class RecordingRefresher : IPolicyRefresher
    {
        public void Refresh(GpoSide side)
        {
            // The seed never needs a policy refresh.
        }
    }
}

/// <summary>
/// One running Local Group Policy Editor with the real tree and list (BWC_E2E_GPEDIT_HOST=editor), and the helpers the
/// Gpedit tree and list tests share: the E2E readout, navigation by the tree and folder rows, rows with their cells, the
/// description pane and the artifacts under artifacts/e2e/visual/gpedit.
/// </summary>
internal sealed class GpeditRun : IDisposable
{
    internal const string RootId = "GpeditRoot";
    internal const string ListId = "ConsoleList";
    internal const string Area = "gpedit";
    internal const string RootName = "Local Computer Policy";

    private GpeditRun(ShellRun shell, GpeditSandbox sandbox)
    {
        Shell = shell;
        Sandbox = sandbox;
    }

    internal ShellRun Shell { get; }

    internal GpeditSandbox Sandbox { get; }

    internal string Name => Shell.Name;

    internal double Scale => Shell.Scale;

    /// <summary>
    /// Starts the editor on a sandbox and waits until the templates are loaded. <paramref name="name"/> becomes the data
    /// root scope (prefixed with console-); a second Open with the same name sees the same settings file.
    /// </summary>
    internal static GpeditRun Open(string name, GpeditSandbox sandbox, bool shape = false, bool placeWindow = true, TimeSpan? loadTimeout = null, bool icons = false)
    {
        ShellRun shell = ShellRun.Open(name, sandbox.Environment(shape, icons));
        var run = new GpeditRun(shell, sandbox);
        try
        {
            if (placeWindow)
            {
                shell.PlaceWindow(20, 20, Math.Min(1900, ShellRun.ScreenWidth - 60), 1000);
            }

            bool ready = ShellRun.WaitUntil(() => run.Readout().GetValueOrDefault("Loaded") == "1", loadTimeout ?? TimeSpan.FromSeconds(60));
            if (!ready)
            {
                throw new TimeoutException($"The templates did not load. Readout: '{run.RawReadout()}'.");
            }

            return run;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts another publish folder of the editor, normally the ship editor that the elevated runner
    /// verifies, on the real policy object. It waits for the real tree, not for the E2E readout.
    /// </summary>
    internal static GpeditRun OpenShip(string name, string exePath, TimeSpan? loadTimeout = null)
    {
        ShellRun shell = ShellRun.Open(name, exePath: exePath);
        var run = new GpeditRun(shell, GpeditSandbox.Create(name));
        try
        {
            shell.PlaceWindow(20, 20, Math.Min(1900, ShellRun.ScreenWidth - 60), 1000);
            bool ready = ShellRun.WaitUntil(
                () => TreeNames(shell).Contains(RootName, StringComparer.Ordinal),
                loadTimeout ?? TimeSpan.FromSeconds(120));
            if (!ready)
            {
                throw new TimeoutException($"The real tree did not show '{RootName}'. Tree rows: '{string.Join("|", TreeNames(shell))}'.");
            }

            return run;
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    /// <summary>The names of the realized rows of the real tree, in display order.</summary>
    internal static List<string> TreeNames(ShellRun shell)
    {
        var names = new List<string>();
        try
        {
            AutomationElement? tree = shell.Find(ShellRun.TreeControlId);
            if (tree is null)
            {
                return names;
            }

            foreach (AutomationElement row in tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)))
            {
                names.Add(ShellRun.SafeName(row));
            }
        }
        catch (Exception)
        {
            // The tree is being rebuilt; the caller polls again.
        }

        return names;
    }

    public void Dispose() => Shell.Dispose();

    // ---- readout ----------------------------------------------------------------------------------

    internal string RawReadout() => Shell.HelpTextOf(RootId);

    internal Dictionary<string, string> Readout()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in RawReadout().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                map[part[..at]] = part[(at + 1)..];
            }
        }

        return map;
    }

    internal string Str(string key) => Readout().GetValueOrDefault(key, string.Empty);

    internal int Int(string key) =>
        int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MinValue;

    /// <summary>The names from the root to the selected node.</summary>
    internal string[] NodePath() => Str("Node").Split(" > ", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The whole tree as indented text (the run must have been opened with <c>shape: true</c>).</summary>
    internal string Shape()
    {
        try
        {
            return Shell.Find(RootId)?.Properties.FullDescription.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The tree with the icon of each node (opened with <c>icons: true</c>): name, tab, icon kind.</summary>
    internal string Icons()
    {
        try
        {
            return Shell.Find(RootId)?.Properties.FullDescription.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ---- navigation -------------------------------------------------------------------------------

    /// <summary>
    /// Selects a node by the toolbar (Up One Level) and the folder rows (a double click), the way a user drills down. The
    /// path starts with the root, "Local Computer Policy".
    /// </summary>
    internal void GoTo(params string[] path)
    {
        string[] current = NodePath();
        int common = 0;
        while (common < current.Length && common < path.Length && current[common] == path[common])
        {
            common++;
        }

        for (int level = current.Length; level > Math.Max(common, 1); level--)
        {
            int before = level;
            AutomationElement? up = Shell.Find(ShellRun.ButtonId("UpOneLevel"));
            up?.Patterns.Invoke.PatternOrDefault?.Invoke();
            if (!ShellRun.WaitUntil(() => NodePath().Length < before, TimeSpan.FromSeconds(8)))
            {
                throw new InvalidOperationException($"Up One Level did not leave '{string.Join(" > ", NodePath())}'.");
            }
        }

        for (int index = Math.Max(common, 1); index < path.Length; index++)
        {
            string name = path[index];
            GpeditRow? row = null;
            bool found = ShellRun.WaitUntil(
                () =>
                {
                    row = Rows().FirstOrDefault(r => r.Name == name);
                    return row is not null;
                },
                TimeSpan.FromSeconds(15));
            if (!found || row is null)
            {
                throw new InvalidOperationException($"No row '{name}' under '{string.Join(" > ", NodePath())}'. Rows: {string.Join("|", Rows().Select(r => r.Name))}.");
            }

            int depth = index + 1;
            string[] expected = path[..depth];
            bool opened = false;
            for (int attempt = 0; attempt < 3 && !opened; attempt++)
            {
                // A double click that finds no clickable point (the window is still being placed or covered) is tried again.
                _ = ControlTestSupport.EnsureForeground(Shell.Session);
                try
                {
                    Shell.Find("ConsoleListRow_" + row.Index.ToString(CultureInfo.InvariantCulture))?.DoubleClick();
                }
                catch (FlaUI.Core.Exceptions.NoClickablePointException)
                {
                    Thread.Sleep(500);
                    continue;
                }

                opened = ShellRun.WaitUntil(() => NodePath().SequenceEqual(expected), TimeSpan.FromSeconds(attempt == 2 ? 15 : 6));
            }

            if (!opened)
            {
                throw new InvalidOperationException($"Opening '{name}' did not select '{string.Join(" > ", expected)}'. Node='{string.Join(" > ", NodePath())}'.");
            }
        }

        // The list is rebuilt for the node; its row count is reported with the readout.
        // A long list realizes only the rows in view, so the realized count stays below the readout. The list reports
        // its whole row count through the grid pattern, and that count must match the readout.
        _ = ShellRun.WaitUntil(
            () =>
            {
                int total = Int("Rows");
                int realized = Rows().Count;
                return realized == total ||
                    (realized > 0 && realized < total && ListElement?.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault == total);
            },
            TimeSpan.FromSeconds(8));
    }

    // ---- the list ---------------------------------------------------------------------------------

    internal AutomationElement? ListElement => Shell.Find(ListId);

    /// <summary>The realized rows in display order, with their cell texts.</summary>
    internal List<GpeditRow> Rows()
    {
        var rows = new List<GpeditRow>();
        AutomationElement? list = ListElement;
        if (list is null)
        {
            return rows;
        }

        var grid = list.Patterns.Grid.PatternOrDefault;
        int columns = grid?.ColumnCount.ValueOrDefault ?? 0;
        foreach (AutomationElement element in list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
        {
            string id = ShellRun.SafeId(element);
            const string prefix = "ConsoleListRow_";
            if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(id[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            var cells = new string[columns];
            for (int column = 0; column < columns; column++)
            {
                cells[column] = ShellRun.SafeName(grid?.GetItem(index, column));
            }

            rows.Add(new GpeditRow(index, ShellRun.SafeName(element), cells));
        }

        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows;
    }

    /// <summary>The names of the column headers, left to right.</summary>
    internal string[] HeaderNames(params string[] columnIds) =>
        columnIds.Select(id => ShellRun.SafeName(Shell.Find(ListRun_HeaderItemId(id)))).ToArray();

    internal static string ListRun_HeaderItemId(string column) => "ConsoleListHeader_" + column;

    internal static string DividerId(string column) => "ConsoleListDivider_" + column;

    /// <summary>Selects a policy row by a click.</summary>
    internal void SelectRow(string rowName)
    {
        GpeditRow? row = Rows().FirstOrDefault(r => r.Name == rowName)
            ?? throw new InvalidOperationException($"No row '{rowName}'. Rows: {string.Join("|", Rows().Select(r => r.Name))}.");
        Shell.Find("ConsoleListRow_" + row.Index.ToString(CultureInfo.InvariantCulture))?.Click();
        Thread.Sleep(ShellRun.KeySettle);
    }

    // ---- the description pane ---------------------------------------------------------------------

    internal string DescriptionTitle => ShellRun.SafeName(Shell.Find("ConsoleDescriptionTitle"));

    internal string DescriptionText(string section) => ShellRun.SafeName(Shell.Find("ConsoleDescriptionText_" + section));

    internal AutomationElement? EditLink => Shell.Find("ConsoleDescriptionLink_edit");

    // ---- artifacts --------------------------------------------------------------------------------

    internal void Measure(string key, object? value) => Shell.Measure(key, value);

    internal void WriteArtifacts(string picture) => Shell.WriteArtifacts(picture, Area);

    internal Rectangle? Rect(string id) => Shell.Rect(id);
}

/// <summary>Records the checks of one Gpedit test under a common id prefix and asserts them at the end.</summary>
internal sealed class GpeditChecks
{
    private readonly string _prefix;
    private readonly List<string> _failures = [];

    internal GpeditChecks(string group, string name)
    {
        _prefix = $"gpedit.{group}.{name}";
    }

    internal void Record(string id, bool pass, string notes)
    {
        string fullId = $"{_prefix}.{id}";
        _ = E2EResults.Record(fullId, pass, notes);
        if (!pass)
        {
            _failures.Add($"{fullId}: {notes}");
        }
    }

    internal void Note(string id, string text) => E2EResults.Note($"{_prefix}.{id}", text);

    internal void AssertAllPassed() =>
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
            0,
            _failures.Count,
            $"Gpedit checks failed:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, _failures)}");
}

/// <summary>
/// The policy-setting dialog of a running editor, seen through UI Automation. The dialog is a separate
/// top level window of the
/// editor process, so its controls are found through <see cref="EditingDriver"/>. The automation ids
/// are the control names of the
/// classic form (buttonOK, radioButtonEnabled, textBoxComment and so on).
/// </summary>
internal sealed class GpeditDialog : IDisposable
{
    internal const string RootId = "DialogRoot";
    internal const string Previous = "buttonPrevious";
    internal const string Next = "buttonNext";
    internal const string NotConfigured = "radioButtonNotConfigured";
    internal const string Enabled = "radioButtonEnabled";
    internal const string Disabled = "radioButtonDisabled";
    internal const string Comment = "textBoxComment";
    internal const string SupportedOn = "labelSupportedOnText";
    internal const string Help = "textBoxHelp";
    internal const string Ok = "buttonOK";
    internal const string Cancel = "buttonCancel";
    internal const string Apply = "buttonApply";
    internal const string Title = "labelTitle";
    internal const string OptionsPane = "flowLayoutPanelDynamic";

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly GpeditRun _run;
    private readonly EditingDriver _driver;

    private GpeditDialog(GpeditRun run, EditingDriver driver)
    {
        _run = run;
        _driver = driver;
    }

    internal EditingDriver Driver => _driver;

    internal GpeditRun Run => _run;

    /// <summary>
    /// Selects a policy row and opens its dialog with Enter (<paramref name="byKey"/>) or a double click. Returns null when no
    /// dialog appeared.
    /// </summary>
    internal static GpeditDialog? Open(GpeditRun run, string rowName, bool byKey = true)
    {
        var driver = new EditingDriver(run.Shell.Session);
        var dialog = new GpeditDialog(run, driver);
        run.SelectRow(rowName);
        _ = ControlTestSupport.EnsureForeground(run.Shell.Session);
        if (byKey)
        {
            Keyboard.Type(VirtualKeyShort.RETURN);
        }
        else
        {
            GpeditRow? row = run.Rows().FirstOrDefault(r => r.Name == rowName);
            run.Shell.Find("ConsoleListRow_" + (row?.Index ?? 0).ToString(CultureInfo.InvariantCulture))?.DoubleClick();
        }

        if (driver.WaitForElement(RootId, TimeSpan.FromSeconds(10)) is null)
        {
            dialog.Dispose();
            return null;
        }

        _ = dialog.WaitForReadout();
        _ = ClassicDialogTestsBridge.EnsureForeground(dialog.Hwnd);
        return dialog;
    }

    public void Dispose() => _driver.Dispose();

    internal AutomationElement? Root => _driver.WaitForElement(RootId, TimeSpan.FromSeconds(3));

    internal IntPtr Hwnd => Root is { } root ? ClassicDialogTestsBridge.HandleOf(root) : IntPtr.Zero;

    internal AutomationElement? Find(string id, double seconds = 3) => _driver.WaitForElement(id, TimeSpan.FromSeconds(seconds));

    /// <summary>The window caption.</summary>
    internal string Caption() => Root is { } root ? ShellRun.SafeName(ClassicDialogTestsBridge.WindowOf(root)) : string.Empty;

    /// <summary>The text of a text box (its value) or a label (its name).</summary>
    internal string TextOf(string id) => _driver.TextOf(id);

    internal bool IsSelected(string radioId) => ControlTestSupport.IsSelected(Find(radioId));

    internal bool IsEnabled(string id) => Find(id)?.IsEnabled ?? false;

    internal bool IsReadOnly(string id)
    {
        try
        {
            return Find(id)?.Patterns.Value.PatternOrDefault?.IsReadOnly.ValueOrDefault ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool Select(string radioId)
    {
        try
        {
            Find(radioId)?.Patterns.SelectionItem.Pattern.Select();
            Thread.Sleep(150);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool SetComment(string text)
    {
        bool done = _driver.SetText(Comment, text);
        Thread.Sleep(200);
        return done;
    }

    internal bool Invoke(string id)
    {
        bool done = _driver.Invoke(id);
        Thread.Sleep(250);
        return done;
    }

    internal void Press(VirtualKeyShort key)
    {
        _ = ClassicDialogTestsBridge.EnsureForeground(Hwnd);
        Keyboard.Type(key);
        Thread.Sleep(300);
    }

    internal bool WaitClosed() => _driver.WaitForNoDialog(TimeSpan.FromSeconds(10));

    internal string FocusedId() => _run.Shell.FocusedId();

    /// <summary>The dialog state published as the help text of DialogRoot (E2E flavor).</summary>
    internal Dictionary<string, string> Readout()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = string.Empty;
        try
        {
            text = Root?.Properties.HelpText.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            // The dialog is closing; an empty readout is returned.
        }

        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                map[part[..at]] = part[(at + 1)..];
            }
        }

        return map;
    }

    internal string Read(string key) => Readout().GetValueOrDefault(key, string.Empty);

    internal bool WaitForReadout(
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        ShellRun.WaitUntil(() => Readout().ContainsKey("Policy"), TimeSpan.FromSeconds(5), callerFile: callerFile, callerLine: callerLine);

    /// <summary>
    /// The rectangle of a control in DIP relative to the client
    /// area of the dialog, or null when it has none.
    /// </summary>
    internal RectangleF? RectDip(string id)
    {
        AutomationElement? element = Find(id, 1);
        IntPtr hwnd = Hwnd;
        if (element is null || hwnd == IntPtr.Zero)
        {
            return null;
        }

        var origin = new NativePoint();
        _ = ClientToScreen(hwnd, ref origin);
        Rectangle box = element.BoundingRectangle;
        double scale = _run.Scale;
        return new RectangleF(
            (float)((box.Left - origin.X) / scale),
            (float)((box.Top - origin.Y) / scale),
            (float)(box.Width / scale),
            (float)(box.Height / scale));
    }

    /// <summary>The client size in DIP.</summary>
    internal SizeF ClientDip()
    {
        IntPtr hwnd = Hwnd;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out NativeRect rect))
        {
            return SizeF.Empty;
        }

        return new SizeF((float)((rect.Right - rect.Left) / _run.Scale), (float)((rect.Bottom - rect.Top) / _run.Scale));
    }

    /// <summary>Resizes the window so that the client area is the given size in DIP, keeping the position.</summary>
    internal void ResizeClientDip(double width, double height)
    {
        IntPtr hwnd = Hwnd;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out NativeRect client) || !GetWindowRect(hwnd, out NativeRect window))
        {
            return;
        }

        int frameWidth = (window.Right - window.Left) - (client.Right - client.Left);
        int frameHeight = (window.Bottom - window.Top) - (client.Bottom - client.Top);
        int outerWidth = (int)Math.Round(width * _run.Scale) + frameWidth;
        int outerHeight = (int)Math.Round(height * _run.Scale) + frameHeight;
        _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, outerWidth, outerHeight, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        Thread.Sleep(600);
    }

    // ---- the Options pane ------------------------------------------------------------------------

    /// <summary>
    /// The E2E readout of the Options pane (the help text of <see cref="OptionsPane"/>): <c>Enabled</c>,
    /// <c>V.&lt;element id&gt;</c>
    /// (the value Apply would write, as JSON) and <c>I.&lt;element id&gt;</c> (the items of a drop-down in display order, as a
    /// JSON array). Values are percent-escaped by the product and returned unescaped here.
    /// </summary>
    internal Dictionary<string, string> OptionsReadout()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string text = string.Empty;
        try
        {
            text = Find(OptionsPane, 1)?.Properties.HelpText.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            // The dialog is closing; an empty readout is returned.
        }

        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                map[part[..at]] = Uri.UnescapeDataString(part[(at + 1)..]);
            }
        }

        return map;
    }

    /// <summary>True when the pane says its controls are enabled.</summary>
    internal bool OptionsEnabled() => OptionsReadout().GetValueOrDefault("Enabled") == "1";

    /// <summary>The value of an element as the pane would give it to Apply, or null when the pane has no such
    /// element.</summary>
    internal JsonElement? ValueOf(string elementId) =>
        OptionsReadout().TryGetValue("V." + elementId, out string? json) ? JsonDocument.Parse(json).RootElement.Clone() : null;

    /// <summary>The items of a drop-down in display order (empty when the element has none).</summary>
    internal string[] ItemsOf(string elementId) =>
        OptionsReadout().TryGetValue("I." + elementId, out string? json)
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : [];

    /// <summary>The ids of the elements that have a value in the pane readout.</summary>
    internal string[] ElementIds() =>
        OptionsReadout().Keys.Where(k => k.StartsWith("V.", StringComparison.Ordinal)).Select(k => k[2..]).ToArray();

    /// <summary>Gives a control the keyboard focus by UI Automation.</summary>
    internal bool FocusControl(string id)
    {
        try
        {
            _ = ClassicDialogTestsBridge.EnsureForeground(Hwnd);
            Find(id, 2)?.Focus();
            Thread.Sleep(150);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Replaces the text of a text box or an editable combo box by real keys: focus, select all, type.</summary>
    internal bool TypeInto(string id, string text)
    {
        if (!FocusControl(id))
        {
            return false;
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Thread.Sleep(100);
        if (text.Length == 0)
        {
            Keyboard.Type(VirtualKeyShort.DELETE);
        }
        else
        {
            Keyboard.Type(text);
        }

        Thread.Sleep(300);
        return true;
    }

    /// <summary>Presses Shift+Tab.</summary>
    internal void PressShiftTab()
    {
        _ = ClassicDialogTestsBridge.EnsureForeground(Hwnd);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Thread.Sleep(300);
    }

    /// <summary>Toggles a check box by UI Automation.</summary>
    internal bool Toggle(string id)
    {
        try
        {
            Find(id, 2)?.Patterns.Toggle.Pattern.Toggle();
            Thread.Sleep(200);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Chooses an item of a drop-down list by its text (expands the list and selects the item).</summary>
    internal bool Choose(string id, string item)
    {
        try
        {
            Find(id, 2)?.AsComboBox().Select(item);
            Thread.Sleep(300);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The control types and automation ids of everything inside the Options pane (diagnostic).</summary>
    internal string[] PaneControlIds()
    {
        try
        {
            AutomationElement? pane = Find(OptionsPane, 1);
            return pane is null
                ? []
                : pane.FindAllDescendants().Select(e => ShellRun.SafeId(e)).Where(id => id.Length > 0).ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Waits for a message box of the dialog and returns its caption and text, or null. The message is closed with
    /// <see cref="DismissMessage"/>.
    /// </summary>
    internal (string Caption, string Text)? WaitMessage(double seconds = 8)
    {
        AutomationElement? message = Find("DialogMessage", seconds);
        if (message is null)
        {
            return null;
        }

        return (ShellRun.SafeName(ClassicDialogTestsBridge.WindowOf(message)), ShellRun.SafeName(message));
    }

    /// <summary>Closes the message box with Enter and waits until it is gone.</summary>
    internal bool DismissMessage(
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0)
    {
        Press(VirtualKeyShort.RETURN);
        return ShellRun.WaitUntil(
            () => Find("DialogMessage", 0.3) is null,
            TimeSpan.FromSeconds(8),
            callerFile: callerFile,
            callerLine: callerLine);
    }

    internal string Screenshot(string name)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", GpeditRun.Area);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        try
        {
            return Root is { } root ? ClassicDialogTestsBridge.SaveScreenshot(root, path) : string.Empty;
        }
        catch (Exception ex)
        {
            E2EResults.Note("gpedit.dialog.capture." + name, $"picture not written: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Presses Tab <paramref name="count"/> times and returns the
    /// automation id that holds the focus after each press.
    /// </summary>
    internal string[] TabSequence(int count)
    {
        var ids = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string before = FocusedId();
            Press(VirtualKeyShort.TAB);
            _ = ShellRun.WaitUntil(() => FocusedId() != before, TimeSpan.FromSeconds(1));
            ids.Add(FocusedId());
        }

        return ids.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// The "Show Contents" dialog of a list element (the grid dialog that a list's "Show..." button opens), seen through UI
/// Automation. Its grid is <c>dataGrid</c> and its cells are <c>dataGrid.R&lt;row&gt;C&lt;column&gt;</c>; the buttons
/// carry the ids
/// of the classic form (<c>buttonOK</c>, <c>buttonCancel</c>) that the policy dialog also has, so they are found
/// inside this
/// window only.
/// </summary>
internal sealed class GpeditContents
{
    internal const string GridId = "dataGrid";

    private readonly GpeditDialog _owner;
    private readonly AutomationElement _window;

    private GpeditContents(GpeditDialog owner, AutomationElement window)
    {
        _owner = owner;
        _window = window;
    }

    /// <summary>Waits for the dialog to appear; null when it did not.</summary>
    internal static GpeditContents? Wait(GpeditDialog owner, double seconds = 10)
    {
        AutomationElement? grid = owner.Find(GridId, seconds);
        return grid is null ? null : new GpeditContents(owner, ClassicDialogTestsBridge.WindowOf(grid));
    }

    internal string Caption => ShellRun.SafeName(_window);

    internal bool IsOpen => _owner.Find(GridId, 0.5) is not null;

    internal AutomationElement? Cell(int row, int column) => _owner.Find($"{GridId}.R{row}C{column}", 2);

    internal string CellText(int row, int column)
    {
        try
        {
            AutomationElement? cell = Cell(row, column);
            return cell?.Patterns.Value.IsSupported == true ? cell.Patterns.Value.Pattern.Value.Value ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal string CellName(int row, int column) => ShellRun.SafeName(Cell(row, column));

    /// <summary>The texts of the rows that hold something (the empty last row is left out).</summary>
    internal List<string[]> Rows(int columns)
    {
        var rows = new List<string[]>();
        for (int row = 0; row < 64; row++)
        {
            string name = CellName(row, 0);
            if (name.Length == 0 || name.EndsWith("new row", StringComparison.Ordinal))
            {
                break;
            }

            rows.Add(Enumerable.Range(0, columns).Select(column => CellText(row, column)).ToArray());
        }

        return rows;
    }

    /// <summary>Types text into a cell: focus it, type, Enter commits.</summary>
    internal void Edit(int row, int column, string text)
    {
        _ = ClassicDialogTestsBridge.EnsureForeground(ClassicDialogTestsBridge.HandleOf(_window));
        Cell(row, column)?.Focus();
        Thread.Sleep(150);
        Keyboard.Type(text);
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.RETURN);
        Thread.Sleep(250);
    }

    /// <summary>Puts the focus on a cell and presses Delete (removes the row).</summary>
    internal void DeleteRow(int row)
    {
        _ = ClassicDialogTestsBridge.EnsureForeground(ClassicDialogTestsBridge.HandleOf(_window));
        Cell(row, 0)?.Focus();
        Thread.Sleep(150);
        Keyboard.Type(VirtualKeyShort.DELETE);
        Thread.Sleep(250);
    }

    internal bool Ok() => Press("buttonOK");

    internal bool Cancel() => Press("buttonCancel");

    internal bool WaitClosed(
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) =>
        ShellRun.WaitUntil(() => !IsOpen, TimeSpan.FromSeconds(8), callerFile: callerFile, callerLine: callerLine);

    private bool Press(string id)
    {
        try
        {
            AutomationElement? button = _window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            if (button is null)
            {
                return false;
            }

            button.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(300);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>The few helpers of the classic dialog tests that the policy dialog tests reuse.</summary>
internal static class ClassicDialogTestsBridge
{
    internal static bool EnsureForeground(IntPtr hwnd) => Bwc.E2E.Dialogs.ClassicDialogTests.EnsureForeground(hwnd);

    internal static IntPtr HandleOf(AutomationElement root) => Bwc.E2E.Dialogs.ClassicDialogTests.DialogHandle(root);

    internal static AutomationElement WindowOf(AutomationElement root) => Bwc.E2E.Dialogs.ClassicDialogTests.WindowOf(root);

    internal static string SaveScreenshot(AutomationElement root, string path) => Bwc.E2E.Dialogs.ClassicDialogTests.SaveScreenshot(root, path);
}
