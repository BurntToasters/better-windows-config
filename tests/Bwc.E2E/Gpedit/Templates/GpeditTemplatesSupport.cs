using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Gpedit.Commands;
using Bwc.E2E.Gpedit.Filter;
using Bwc.E2E.Harness;
using Bwc.E2E.ImportExport;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.Gpedit.Templates;

/// <summary>
/// The ids of the Add/Remove Templates feature that the tests use. The contract is
/// docs/failure-modes/gpedit-templates.md: the
/// command id continues the numbers of the G5 commands (2004 to 2006), the dialog and the details box
/// are their own top level
/// windows of the editor process, and the parts are named after the dialog.
/// </summary>
internal static class TemplateIds
{
    internal const int AddRemove = 2007;
    internal const string Notice = "StatusBar.Pane2";

    internal const string Dialog = "GpeditTemplates";
    internal const string List = "GpeditTemplates.List";
    internal const string Add = "GpeditTemplates.Add";
    internal const string Remove = "GpeditTemplates.Remove";
    internal const string Close = "GpeditTemplates.Close";
    internal const string Problems = "GpeditTemplates.Problems";

    internal const string Details = "GpeditTemplateDetails";
    internal const string DetailsText = "GpeditTemplateDetails.Text";
    internal const string DetailsClose = "GpeditTemplateDetails.Close";

    /// <summary>The parts in reading order (DLG-115): the list, then Add, Remove and Close.</summary>
    internal static readonly string[] Parts = [List, Add, Remove, Close];

    internal const string StatusLoaded = "Loaded";
    internal const string StatusShadowed = "Shadowed";
    internal const string StatusNotLoaded = "NotLoaded";
}

/// <summary>One row of the imported templates list: the file name, the namespace and the status word.</summary>
internal sealed record TemplateRow(string Name, string Namespace, string Status);

/// <summary>One row of the load-error list: the file name, the engine code, the engine detail, the kind of
/// problem, the resource key of the sentence the row shows for the code and that sentence (the key itself when the
/// resource is missing).</summary>
internal sealed record ProblemRow(string Name, string Code, string Detail, string Kind, string SentenceKey, string Sentence)
{
    /// <summary>True when the row shows a real sentence for <paramref name="code"/>: the key is the one for that
    /// code, and the sentence is neither the key nor the bare code.</summary>
    internal bool ShowsSentenceFor(string code) =>
        SentenceKey == "TemplateProblem" + code &&
        Sentence.Length > 20 &&
        Sentence != SentenceKey &&
        Sentence != code &&
        Sentence.Contains(' ', StringComparison.Ordinal);
}

/// <summary>What Add did after the picker was accepted.</summary>
internal enum AddOutcome
{
    /// <summary>No file picker opened, or it could not be driven.</summary>
    NoPicker,

    /// <summary>The picker closed and neither a new row nor the details box showed in time.</summary>
    Nothing,

    /// <summary>A new row is in the list.</summary>
    Added,

    /// <summary>The details box shows.</summary>
    Refused,
}

/// <summary>
/// A small template that BWC authors for the import tests (no Microsoft text). One category and one
/// machine policy, optionally
/// with a using of another namespace and a parent category in it.
/// </summary>
internal sealed record TemplateSpec(string Id, string Namespace, string? UsesNamespace = null, string? UsesCategory = null)
{
    internal string File => "BwcImport" + Id;

    internal string AdmxName => File + ".admx";

    internal string CategoryName => "ImportRoot" + Id;

    internal string CategoryText => "BWC import category " + Id;

    internal string PolicyName => "ImportPolicy" + Id;

    internal string PolicyText => "BWC import policy " + Id;

    /// <summary>The name of the policy row in the list (the display name and the suffix every policy row
    /// has).</summary>
    internal string PolicyRow => FilterNames.Row(PolicyText);

    internal string Admx()
    {
        string usings = UsesNamespace is null ? string.Empty : $"""<using prefix="dep" namespace="{UsesNamespace}" />""";
        string parent = UsesCategory is null ? string.Empty : $"""<parentCategory ref="dep:{UsesCategory}" />""";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- BWC import test template. Authored by Better Windows Config. Not a Microsoft file. -->
            <policyDefinitions revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
              <policyNamespaces>
                <target prefix="imp" namespace="{Namespace}" />
                {usings}
              </policyNamespaces>
              <resources minRequiredRevision="1.0" />
              <supportedOn>
                <definitions>
                  <definition name="SUPPORTED_Import" displayName="$(string.SUPPORTED_Import)" />
                </definitions>
              </supportedOn>
              <categories>
                <category name="{CategoryName}" displayName="$(string.{CategoryName})">{parent}</category>
              </categories>
              <policies>
                <policy name="{PolicyName}" class="Machine" displayName="$(string.{PolicyName})" explainText="$(string.{PolicyName}_Help)" key="Software\Policies\BWC-Oracle\Imported\{Id}" valueName="ImportFlag">
                  <parentCategory ref="{CategoryName}" />
                  <supportedOn ref="SUPPORTED_Import" />
                </policy>
              </policies>
            </policyDefinitions>
            """;
    }

    internal string Adml() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!-- BWC import test template. Authored by Better Windows Config. Not a Microsoft file. -->
        <policyDefinitionResources revision="1.0" schemaVersion="1.0" xmlns="http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions">
          <displayName>BWC import test {Id}</displayName>
          <description>A small template used to test the import.</description>
          <resources>
            <stringTable>
              <string id="{CategoryName}">{CategoryText}</string>
              <string id="SUPPORTED_Import">BWC import test platform</string>
              <string id="{PolicyName}">{PolicyText}</string>
              <string id="{PolicyName}_Help">A policy of a template that a test imported.</string>
            </stringTable>
          </resources>
        </policyDefinitionResources>
        """;
}

/// <summary>
/// The source folders of the imports, all inside the run data root of the sandbox. This class holds the
/// file writes of the
/// template tests: small templates that BWC authors, copies of fixture files, and a corrupt file. It
/// names no policy folder.
/// </summary>
internal sealed class ImportSources
{
    private readonly GpeditSandbox _sandbox;

    internal ImportSources(GpeditSandbox sandbox)
    {
        _sandbox = sandbox;
        Root = Path.Combine(sandbox.DataRoot, "import-src");
    }

    internal string Root { get; }

    /// <summary>The temp location of the folder of the template files that the picker will open.</summary>
    internal string Folder(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.Combine(path, "en-US"));
        return path;
    }

    /// <summary>Writes a spec as an ADMX and an en-US ADML in the folder; returns the ADMX path.</summary>
    internal string Write(string folderName, TemplateSpec spec)
    {
        string folder = Folder(folderName);
        string admx = Path.Combine(folder, spec.AdmxName);
        File.WriteAllText(admx, spec.Admx());
        File.WriteAllText(Path.Combine(folder, "en-US", spec.File + ".adml"), spec.Adml());
        return admx;
    }

    /// <summary>Copies the base template of the oracle fixture (a namespace the system folder defines) into
    /// a folder.</summary>
    internal string CopyFixtureBase(string folderName)
    {
        string folder = Folder(folderName);
        string admx = Path.Combine(folder, "BwcOracleBase.admx");
        File.Copy(Path.Combine(GpeditSandbox.FixtureRoot, "BwcOracleBase.admx"), admx, overwrite: true);
        File.Copy(Path.Combine(GpeditSandbox.FixtureRoot, "en-US", "BwcOracleBase.adml"), Path.Combine(folder, "en-US", "BwcOracleBase.adml"), overwrite: true);
        return admx;
    }

    /// <summary>
    /// Copies a template (its ADMX and its en-US ADML) into the sandbox copy of the system folder that
    /// <see cref="GpeditSandbox.UseTemplatesWithBadCases"/> made. It refuses any other system folder, so
    /// the oracle fixture is never written.
    /// </summary>
    internal void AddToSystemFolder(string admxPath)
    {
        string target = Path.GetFullPath(_sandbox.TemplateRoot);
        if (!target.StartsWith(Path.GetFullPath(_sandbox.DataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The system folder of this run is not a copy under the run data root: " + target);
        }

        string name = Path.GetFileNameWithoutExtension(admxPath);
        File.Copy(admxPath, Path.Combine(target, name + ".admx"), overwrite: true);
        File.Copy(Path.Combine(Path.GetDirectoryName(admxPath)!, "en-US", name + ".adml"), Path.Combine(target, "en-US", name + ".adml"), overwrite: true);
    }

    /// <summary>Writes a file with the given text into a folder; returns its path.</summary>
    internal string WriteFile(string folderName, string fileName, string text)
    {
        string path = Path.Combine(Folder(folderName), fileName);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Overwrites a file with text that is not XML.</summary>
    internal static void Corrupt(string path) => File.WriteAllText(path, "<not xml");

    /// <summary>The path of a bad fixture template, read in place (an import never writes its source).</summary>
    internal static string BadFixture(string name) => Path.Combine(GpeditSandbox.FixtureRoot, "bad", name, "Bwc" + name + ".admx");

    /// <summary>The SHA-256 of every file below a folder, by relative path. Empty for a folder that does
    /// not exist.</summary>
    internal static SortedDictionary<string, string> Hashes(string folder)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
        {
            return map;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(folder, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return map;
    }

    internal static bool SameHashes(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out string? other) && other == p.Value);

    internal static string Describe(IReadOnlyDictionary<string, string> hashes) =>
        hashes.Count == 0 ? "(no file)" : string.Join(", ", hashes.Keys);

    /// <summary>The files of the import store, relative to it (empty when the folder does not exist).</summary>
    internal string[] StoreFiles() => Hashes(_sandbox.ImportStore).Keys.ToArray();

    /// <summary>Makes the import store a plain file, which is not a usable store (status NotADirectory).</summary>
    internal void MakeStoreAFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sandbox.ImportStore)!);
        File.WriteAllText(_sandbox.ImportStore, "a file where the store folder goes");
    }
}

/// <summary>Helpers over a running editor for the template tests.</summary>
internal static class GpeditTemplatesRun
{
    internal static readonly string[] AdminComputer = [GpeditNames.Root, GpeditNames.Computer, GpeditNames.Admin];
    internal static readonly string[] AdminUser = [GpeditNames.Root, GpeditNames.User, GpeditNames.Admin];

    /// <summary>The text of the template problem notice in the status bar, or empty while the pane is absent.</summary>
    internal static string Notice(this GpeditRun run)
    {
        AutomationElement? pane = run.Shell.Find(TemplateIds.Notice);
        return pane is not null && run.Shell.Rect(TemplateIds.Notice) is not null ? ShellRun.SafeName(pane) : string.Empty;
    }

    /// <summary>The numbers in the notice text, in order.</summary>
    internal static int[] NoticeNumbers(this GpeditRun run) =>
        Regex.Matches(run.Notice(), @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Waits until the notice is shown (or gone).</summary>
    internal static bool WaitNotice(this GpeditRun run, bool shown, double seconds = 15) =>
        ShellRun.WaitUntil(() => run.Notice().Length > 0 == shown, TimeSpan.FromSeconds(seconds));

    /// <summary>Waits until the notice shows exactly this number of problems.</summary>
    internal static bool WaitNoticeCount(this GpeditRun run, int count, double seconds = 15) =>
        ShellRun.WaitUntil(() => run.NoticeNumbers().SequenceEqual([count]), TimeSpan.FromSeconds(seconds));

    /// <summary>The names of the rows of the current list.</summary>
    internal static string[] RowNamesNow(this GpeditRun run) => run.Rows().Select(r => r.Name).ToArray();

    /// <summary>Waits until a row of the current list has this name.</summary>
    internal static bool WaitRow(this GpeditRun run, string name, bool present = true, double seconds = 15) =>
        ShellRun.WaitUntil(() => run.RowNamesNow().Contains(name, StringComparer.Ordinal) == present, TimeSpan.FromSeconds(seconds));

    /// <summary>Waits until a reload that the dialog started has finished: the counter is at the value and
    /// the model is loaded.</summary>
    internal static bool WaitDialogReload(this GpeditRun run, int expected, double seconds = 30) =>
        run.WaitCount("TemplateReloads", expected, seconds) && run.WaitReloadSettled(seconds);

    internal static string Describe(this IEnumerable<TemplateRow> rows) =>
        string.Join("|", rows.Select(r => $"{r.Name}:{r.Namespace}:{r.Status}"));

    internal static string Describe(this IEnumerable<ProblemRow> rows) =>
        string.Join("|", rows.Select(r => $"{r.Name}:{r.Code}:{r.Kind}:{r.SentenceKey}"));
}

/// <summary>
/// The Add/Remove Templates dialog of a running editor, seen through UI Automation. It is a separate
/// top level window of the
/// editor process, found by the automation ids of <see cref="TemplateIds"/>. The file picker of Add is
/// the real common item
/// dialog, driven with real input.
/// </summary>
internal sealed class GpeditTemplatesDialog : IDisposable
{
    private readonly GpeditRun _run;
    private readonly EditingDriver _driver;

    private GpeditTemplatesDialog(GpeditRun run, EditingDriver driver)
    {
        _run = run;
        _driver = driver;
    }

    internal GpeditRun Run => _run;

    /// <summary>Opens the dialog by the Action menu entry Add/Remove Templates. Null when no dialog appeared.</summary>
    internal static GpeditTemplatesDialog? Open(GpeditRun run, double seconds = 10)
    {
        var driver = new EditingDriver(run.Shell.Session);
        var dialog = new GpeditTemplatesDialog(run, driver);
        if (!run.InvokeMenuEntry(GpeditCommandIds.ActionMenu, TemplateIds.AddRemove)
            || driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromSeconds(seconds)) is null)
        {
            dialog.Dispose();
            return null;
        }

        return dialog;
    }

    public void Dispose() => _driver.Dispose();

    internal bool IsOpen => _driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromMilliseconds(300)) is not null;

    internal AutomationElement? Root => _driver.WaitForElement(TemplateIds.Dialog, TimeSpan.FromSeconds(3));

    internal AutomationElement? Find(string id, double seconds = 3) => _driver.WaitForElement(id, TimeSpan.FromSeconds(seconds));

    internal bool Has(string id) => Find(id, 1) is not null;

    internal bool IsEnabled(string id) => Find(id, 1)?.IsEnabled ?? false;

    internal bool WaitClosed(double seconds = 10) => ShellRun.WaitUntil(() => !IsOpen, TimeSpan.FromSeconds(seconds));

    internal Rectangle? RectOf(string id)
    {
        try
        {
            Rectangle box = Find(id, 1)?.BoundingRectangle ?? Rectangle.Empty;
            return box.Width > 0 && box.Height > 0 ? box : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal bool Press(string id)
    {
        bool done = _driver.Invoke(id);
        Thread.Sleep(300);
        return done;
    }

    internal IntPtr Handle() => Root is { } root ? ClassicDialogTestsBridge.HandleOf(root) : IntPtr.Zero;

    // ---- the lists --------------------------------------------------------------------------------

    private List<(string Name, Dictionary<string, string> Help)> Items(string listId)
    {
        var items = new List<(string, Dictionary<string, string>)>();
        try
        {
            foreach (AutomationElement item in Find(listId, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)) ?? [])
            {
                var help = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string part in (item.Properties.HelpText.ValueOrDefault ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int at = part.IndexOf('=', StringComparison.Ordinal);
                    if (at > 0)
                    {
                        help[part[..at]] = part[(at + 1)..];
                    }
                }

                items.Add((ShellRun.SafeName(item), help));
            }
        }
        catch (Exception)
        {
            // A list that is rebuilding returns what was read; the caller polls.
        }

        return items;
    }

    /// <summary>The rows of the imported templates list, in screen order.</summary>
    internal List<TemplateRow> Rows() =>
        Items(TemplateIds.List)
            .Select(i => new TemplateRow(i.Name, i.Help.GetValueOrDefault("Namespace", string.Empty), i.Help.GetValueOrDefault("Status", string.Empty)))
            .ToList();

    /// <summary>The rows of the load-error list, in screen order.</summary>
    internal List<ProblemRow> Problems() =>
        Items(TemplateIds.Problems)
            .Select(i => new ProblemRow(i.Name, i.Help.GetValueOrDefault("Code", string.Empty), i.Help.GetValueOrDefault("Detail", string.Empty), i.Help.GetValueOrDefault("Kind", string.Empty), i.Help.GetValueOrDefault("Key", string.Empty), i.Help.GetValueOrDefault("Sentence", string.Empty)))
            .ToList();

    internal bool WaitRows(Func<List<TemplateRow>, bool> condition, double seconds = 15) =>
        ShellRun.WaitUntil(() => condition(Rows()), TimeSpan.FromSeconds(seconds));

    /// <summary>Selects a row of the imported templates list by its file name.</summary>
    internal bool Select(string fileName)
    {
        try
        {
            AutomationElement? item = Find(TemplateIds.List, 2)?.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(i => ShellRun.SafeName(i) == fileName);
            if (item is null)
            {
                return false;
            }

            if (item.Patterns.SelectionItem.IsSupported)
            {
                item.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                item.Click();
            }

            Thread.Sleep(250);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- Add --------------------------------------------------------------------------------------

    /// <summary>
    /// Presses Add, types the path into the picker and presses Open, then waits until a row is added or the
    /// details box shows.
    /// </summary>
    internal AddOutcome AddFile(string path, out string notes)
    {
        int before = Rows().Count;
        if (!Press(TemplateIds.Add))
        {
            notes = "Add could not be pressed.";
            return AddOutcome.NoPicker;
        }

        using var pickers = new SystemFileDialogDriver(_run.Shell.Session);
        IntPtr picker = pickers.WaitForDialog(TimeSpan.FromSeconds(15));
        if (picker == IntPtr.Zero)
        {
            notes = "no file picker opened.";
            return AddOutcome.NoPicker;
        }

        bool typed = pickers.TrySetFileName(picker, path, out string typedNotes);
        string acceptNotes = "not attempted";
        bool accepted = typed && pickers.TryAccept(picker, "Open", out acceptNotes);
        bool gone = accepted && pickers.WaitGone(picker, TimeSpan.FromSeconds(10));
        notes = $"{typedNotes} {acceptNotes} closed={gone}.";
        if (!gone)
        {
            _ = pickers.TryCancel(picker);
            return AddOutcome.NoPicker;
        }

        AddOutcome outcome = AddOutcome.Nothing;
        _ = ShellRun.WaitUntil(
            () =>
            {
                if (HasDetails)
                {
                    outcome = AddOutcome.Refused;
                    return true;
                }

                if (Rows().Count > before)
                {
                    outcome = AddOutcome.Added;
                    return true;
                }

                return false;
            },
            TimeSpan.FromSeconds(20));
        return outcome;
    }

    /// <summary>Presses Add and cancels the picker with Escape. True when a picker opened and went away.</summary>
    internal bool CancelPicker(out string notes)
    {
        if (!Press(TemplateIds.Add))
        {
            notes = "Add could not be pressed.";
            return false;
        }

        using var pickers = new SystemFileDialogDriver(_run.Shell.Session);
        IntPtr picker = pickers.WaitForDialog(TimeSpan.FromSeconds(15));
        if (picker == IntPtr.Zero)
        {
            notes = "no file picker opened.";
            return false;
        }

        bool cancelled = pickers.TryCancel(picker);
        notes = $"picker cancelled={cancelled}.";
        return cancelled;
    }

    // ---- Remove and Close -------------------------------------------------------------------------

    /// <summary>Selects a row and presses Remove. False when the row or the button is missing.</summary>
    internal bool RemoveRow(string fileName) => Select(fileName) && Press(TemplateIds.Remove);

    internal bool Close() => Press(TemplateIds.Close) && WaitClosed();

    // ---- the details box --------------------------------------------------------------------------

    internal bool HasDetails => _driver.WaitForElement(TemplateIds.Details, TimeSpan.FromMilliseconds(200)) is not null;

    internal bool WaitDetails(double seconds = 10) => _driver.WaitForElement(TemplateIds.Details, TimeSpan.FromSeconds(seconds)) is not null;

    /// <summary>The text of the details box, or empty when none shows.</summary>
    internal string DetailsText() => _driver.TextOf(TemplateIds.DetailsText);

    internal bool CloseDetails()
    {
        if (!_driver.Invoke(TemplateIds.DetailsClose))
        {
            return false;
        }

        return ShellRun.WaitUntil(() => !HasDetails, TimeSpan.FromSeconds(8));
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
            E2EResults.Note("gpedit.templates.capture." + name, $"picture not written: {ex.Message}");
            return string.Empty;
        }
    }
}
