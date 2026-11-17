using System.Diagnostics;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Bwc.E2E.Icons;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.ValueDisplay;

/// <summary>One value row as the list shows it: the Name, Type, and Data cells.</summary>
internal sealed record ValueRow(string Name, string Type, string Data);

/// <summary>
/// Reads the value list of classic regedit (C:\Windows\regedit.exe) and of BWC through UI Automation, and writes
/// the compared rows to an artifact under the run's artifacts folder.
/// </summary>
internal static class ClassicValueCompare
{
    internal const string ArtifactFolder = "value-display";
    internal const string RunKeyDisplayPrefix = @"HKEY_CURRENT_USER\Software\BWC-E2E\";
    private static readonly TimeSpan RowsTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StablePoll = TimeSpan.FromMilliseconds(150);

    /// <summary>The display path of a key below the run's sandbox root, as classic writes it in LastKey.</summary>
    internal static string DisplayPathFor(E2EConfig config, string childKey) =>
        RunKeyDisplayPrefix + config.RunId + @"\" + childKey;

    /// <summary>
    /// Starts classic regedit on one key through its LastKey applet value, reads the rows once they are stable,
    /// closes classic regedit, and restores the applet state. The applet state is snapshotted before the write.
    /// </summary>
    internal static ValueRow[] ReadClassicRows(E2EConfig config, string displayPath)
    {
        AppletState before = AppletState.Capture();
        try
        {
            AppletState.WriteLastKey(displayPath);
            using var session = new AppSession(config, ClassicRegeditCapture.RegeditPath);
            ValueRow[] rows = WaitForStableRows(() => ReadClassicRowsOnce(session), RowsTimeout);
            if (rows.Length == 0)
            {
                // Recorded so a run that finds no classic rows shows what classic regedit exposed instead.
                string shot = E2EResults.CaptureScreenshot("valuedisplay.classic.empty", session);
                E2EResults.Note("valuedisplay.classic.empty", $"{ClassicWindowSummary(session)} Screenshot: {shot}.");
            }

            session.Close();
            return rows;
        }
        finally
        {
            RestoreApplet(before);
        }
    }

    /// <summary>Reads the BWC list from the realized rows: Name from the row, Type and Data from text cells.</summary>
    internal static ValueRow[] ReadBwcRows(AppSession session)
    {
        var rows = new List<ValueRow>();
        foreach (AutomationElement row in OrderedBwcRows(session))
        {
            string[] texts = row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Select(ControlTestSupport.SafeName)
                .ToArray();
            string type = texts.Length >= 2 ? texts[1] : string.Empty;
            string data = texts.Length >= 3 ? texts[2] : string.Empty;
            rows.Add(new ValueRow(ControlTestSupport.SafeName(row), type, data));
        }

        return rows.ToArray();
    }

    /// <summary>
    /// Compares the two lists row by row. Writes the artifact, then records one check with the counts. A list
    /// with no rows on either side fails, because an empty comparison proves nothing.
    /// </summary>
    internal static bool Compare(
        E2EConfig config,
        string caseId,
        string checkId,
        ValueRow[] bwc,
        ValueRow[] classic,
        bool noteWhenDifferent = false)
    {
        var differences = new List<object>();
        int count = Math.Max(bwc.Length, classic.Length);
        for (var index = 0; index < count; index += 1)
        {
            ValueRow? left = index < bwc.Length ? bwc[index] : null;
            ValueRow? right = index < classic.Length ? classic[index] : null;
            if (left != right)
            {
                differences.Add(new { index, bwc = left, classic = right });
            }
        }

        bool pass = bwc.Length > 0 && classic.Length > 0 && differences.Count == 0;
        string artifact = WriteArtifact(config, caseId, pass, bwc, classic, differences);
        string summary =
            $"{bwc.Length} BWC row(s) and {classic.Length} classic row(s), {differences.Count} difference(s). Artifact: {artifact}.";

        // A case classic cannot show is a note, not a pass or a failure: the artifact still holds both lists.
        if (!pass && noteWhenDifferent)
        {
            E2EResults.Note(checkId, $"Not measured against classic regedit: {summary}");
            return false;
        }

        E2EResults.Record(checkId, pass, summary);
        return pass;
    }

    /// <summary>Writes the compared rows as JSON under the run's artifacts folder, replaced on each run.</summary>
    internal static string WriteArtifact(
        E2EConfig config,
        string caseId,
        bool pass,
        ValueRow[] bwc,
        ValueRow[] classic,
        IReadOnlyList<object> differences)
    {
        string folder = Path.Combine(config.ArtifactsDir, ArtifactFolder);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"classic-compare-{caseId}-{config.Rid}.json");
        string classicVersion = File.Exists(ClassicRegeditCapture.RegeditPath)
            ? FileVersionInfo.GetVersionInfo(ClassicRegeditCapture.RegeditPath).FileVersion ?? string.Empty
            : string.Empty;

        var document = new
        {
            caseId,
            rid = config.Rid,
            flavor = config.Flavor,
            runId = config.RunId,
            capturedUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            classicRegedit = new { path = ClassicRegeditCapture.RegeditPath, fileVersion = classicVersion },
            match = pass,
            differenceCount = differences.Count,
            differences,
            bwc,
            classic,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static IEnumerable<AutomationElement> OrderedBwcRows(AppSession session)
    {
        return ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .OrderBy(row => RowIndex(row));
    }

    private static int RowIndex(AutomationElement row)
    {
        string id = ControlTestSupport.SafeId(row);
        return id.StartsWith(ControlTestSupport.ListRowPrefix, StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan(ControlTestSupport.ListRowPrefix.Length), out int index)
                ? index
                : int.MaxValue;
    }

    private const string ClassicListClassName = "SysListView32";

    private static string ClassicWindowSummary(AppSession session)
    {
        try
        {
            AutomationElement window = session.Window;
            AutomationElement[] all = window.FindAllDescendants();
            int dataItems = all.Count(element => element.ControlType == ControlType.DataItem);
            AutomationElement? byGrid = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.DataGrid));
            AutomationElement? byClass = window.FindFirstDescendant(cf => cf.ByClassName("SysListView32"));
            AutomationElement? byRaw = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
            string classes = string.Join(", ", all.Take(12).Select(element => $"{element.ControlType}/{SafeClassName(element)}"));
            return $"Classic window: {all.Length} descendant(s), {dataItems} DataItem(s); DataGrid found={byGrid is not null}, " +
                $"SysListView32 found={byClass is not null}, List found={byRaw is not null}. First: {classes}.";
        }
        catch (Exception ex)
        {
            return $"Classic window could not be read: {ex.Message}.";
        }
    }

    private static string SafeClassName(AutomationElement element)
    {
        try
        {
            return element.ClassName ?? string.Empty;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static ValueRow[] ReadClassicRowsOnce(AppSession session)
    {
        // The classic list is a DataGrid (SysListView32) with the Grid pattern. Row r holds the Name, Type, and Data
        // cells in columns 0, 1, and 2. Descendant searches from the window do not reach the items.
        AutomationElement? list = session.Window.FindFirstDescendant(cf => cf.ByClassName(ClassicListClassName));
        if (list is null)
        {
            return Array.Empty<ValueRow>();
        }

        try
        {
            if (list.Patterns.Grid.IsSupported)
            {
                int count = list.Patterns.Grid.Pattern.RowCount.Value;
                var rows = new List<ValueRow>(count);
                for (var row = 0; row < count; row += 1)
                {
                    rows.Add(new ValueRow(CellText(list, row, 0), CellText(list, row, 1), CellText(list, row, 2)));
                }

                return rows.ToArray();
            }

            return ChildRows(list);
        }
        catch (Exception)
        {
            // A list that stops answering mid-read is retried by the caller until the timeout.
            return Array.Empty<ValueRow>();
        }
    }

    // Fallback when the list has no Grid pattern: each DataItem child carries its cells as Edit descendants.
    private static ValueRow[] ChildRows(AutomationElement list)
    {
        var rows = new List<ValueRow>();
        foreach (AutomationElement item in list.FindAllChildren().Where(element => element.ControlType == ControlType.DataItem))
        {
            string[] cells = item.FindAllDescendants()
                .Where(element => element.ControlType == ControlType.Edit)
                .Select(ControlTestSupport.SafeName)
                .ToArray();
            rows.Add(new ValueRow(
                cells.ElementAtOrDefault(0) ?? string.Empty,
                cells.ElementAtOrDefault(1) ?? string.Empty,
                cells.ElementAtOrDefault(2) ?? string.Empty));
        }

        return rows.ToArray();
    }

    private static string CellText(AutomationElement grid, int row, int column)
    {
        AutomationElement? cell = grid.Patterns.Grid.Pattern.GetItem(row, column);
        return cell is null ? string.Empty : ControlTestSupport.SafeName(cell);
    }

    // Returns the rows once unchanged for StableFor, with at least one row. Otherwise the last read.
    // Classic fills a long list over more than one dispatcher turn, so two equal reads are not enough.
    private static ValueRow[] WaitForStableRows(Func<ValueRow[]> read, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        ValueRow[] current = Array.Empty<ValueRow>();
        DateTime unchangedSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ValueRow[] next = read();
            if (!next.SequenceEqual(current))
            {
                current = next;
                unchangedSince = DateTime.UtcNow;
            }
            else if (current.Length > 0 && DateTime.UtcNow - unchangedSince >= StableFor)
            {
                return current;
            }

            Thread.Sleep(StablePoll);
        }

        return current;
    }

    private static void RestoreApplet(AppletState before)
    {
        try
        {
            before.Restore();
            E2EResults.Record(
                "valuedisplay.applet.restore",
                true,
                $"Applet values match the snapshot taken before classic regedit ran ({before.Summary()}).");
        }
        catch (Exception ex)
        {
            E2EResults.Record("valuedisplay.applet.restore", false, $"Applet restore failed: {ex.Message}");
        }
    }
}
