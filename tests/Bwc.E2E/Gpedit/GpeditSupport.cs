using System.Drawing;
using System.Globalization;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.Gpo.Refresh;
using Bwc.Gpo.Store;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

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

    /// <summary>The sandbox for a test. <paramref name="name"/> is the name given to <see cref="GpeditRun.Open"/>.</summary>
    internal static GpeditSandbox Create(string name) => new("console-" + name);

    internal GpoStore Store => new(new SandboxGpoStorePolicy(GpoRoot, DataRoot, null, new RecordingRefresher()));

    /// <summary>The environment of the editor process for this sandbox.</summary>
    internal Dictionary<string, string> Environment(bool shape = false)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_GPEDIT_HOST"] = "editor",
            ["BWC_E2E_ADMX_ROOT"] = TemplateRoot,
            ["BWC_E2E_IMPORT_STORE"] = ImportStore,
            ["BWC_E2E_GPO_ROOT"] = GpoRoot,
        };
        if (shape)
        {
            environment["BWC_E2E_GPEDIT_SHAPE"] = "1";
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
    internal void WritePolicyFile(GpoSide side, IReadOnlyList<(string Name, uint Value)> values)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(new byte[] { 0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00 });
            foreach ((string name, uint value) in values)
            {
                writer.Write('[');
                writer.Write(System.Text.Encoding.Unicode.GetBytes(BasicsKey + '\0'));
                writer.Write(';');
                writer.Write(System.Text.Encoding.Unicode.GetBytes(name + '\0'));
                writer.Write(';');
                writer.Write(4u);
                writer.Write(';');
                writer.Write(4u);
                writer.Write(';');
                writer.Write(value);
                writer.Write(']');
            }
        }

        string path = GpoFileNames.PolPath(GpoRoot, side);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
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
    internal static GpeditRun Open(string name, GpeditSandbox sandbox, bool shape = false, bool placeWindow = true, TimeSpan? loadTimeout = null)
    {
        ShellRun shell = ShellRun.Open(name, sandbox.Environment(shape));
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
        _ = ShellRun.WaitUntil(() => Int("Rows") == Rows().Count, TimeSpan.FromSeconds(8));
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
