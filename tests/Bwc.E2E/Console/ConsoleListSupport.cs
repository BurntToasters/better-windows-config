using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using Bwc.E2E.Controls;
using Bwc.E2E.List;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Bwc.E2E.ConsoleFrame;

/// <summary>One realized list item as UI Automation shows it.</summary>
internal sealed record ListItemView(int Index, string Name, Rectangle Rect);

/// <summary>
/// One running Local Group Policy Editor whose result pane holds the console list, and the helpers the Console_List
/// tests share. The host fills the list from the E2E seam (BWC_E2E_CONSOLE_ROWS and friends, see GpeditRootView), so
/// the rows are known: row i has the name "Setting {(i * 37 + 11) % N:D5}", state i % 3 and comment i % 2.
/// The control reports its state in the UIA help text of the element ConsoleList (E2E flavor only).
/// </summary>
internal sealed class ListRun : IDisposable
{
    internal const string ListId = "ConsoleList";
    internal const string HeaderId = "ConsoleListHeader";
    internal const string NameSuffix = " setting";

    internal static readonly string[] States = ["Not configured", "Enabled", "Disabled"];

    private ListRun(ShellRun shell)
    {
        Shell = shell;
    }

    internal ShellRun Shell { get; }

    internal string Name => Shell.Name;

    internal double Scale => Shell.Scale;

    internal int RowCount { get; private set; }

    /// <summary>Starts the editor with the seam rows, waits for the list and puts the window at a known size.</summary>
    internal static ListRun Open(string name, int rows, IReadOnlyDictionary<string, string>? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BWC_E2E_CONSOLE_ROWS"] = rows.ToString(CultureInfo.InvariantCulture),
        };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> pair in extra)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        ShellRun shell = ShellRun.Open("list-" + name, environment);
        var run = new ListRun(shell) { RowCount = rows };
        try
        {
            shell.PlaceWindow(20, 20, Math.Min(1900, ShellRun.ScreenWidth - 60), 1000);
            bool ready = ShellRun.WaitUntil(() => run.Element is not null && run.Int("Rows") == rows, TimeSpan.FromSeconds(60));
            if (!ready)
            {
                throw new TimeoutException($"The console list did not show {rows} rows. Readout: '{run.RawReadout()}'.");
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

    internal AutomationElement? Element => Shell.Find(ListId);

    internal string RawReadout() => Shell.HelpTextOf(ListId);

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

    internal string Str(string key) => Readout().TryGetValue(key, out string? value) ? value : string.Empty;

    internal int Int(string key) =>
        int.TryParse(Str(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MinValue;

    internal int[] Ints(string key) => Str(key)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : int.MinValue)
        .ToArray();

    internal double[] Doubles(string key) => Str(key)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
        .ToArray();

    internal string[] Strings(string key) => Str(key).Split(',', StringSplitOptions.RemoveEmptyEntries);

    internal int Focus => Int("Focus");

    internal int SelectedCount => Int("Selected");

    internal string Mode => Str("Mode");

    // ---- expected data (mirrors the seam) ---------------------------------------------------------

    internal static string RowName(int index, int count, int generation = 0) =>
        (generation > 0 ? "Refreshed " : "Setting ") + (((index * 37L) + 11) % Math.Max(1, count)).ToString("D5", CultureInfo.InvariantCulture);

    internal string ExpectedName(int index) => RowName(index, RowCount);

    internal static string ExpectedState(int index) => States[index % 3];

    internal static string ExpectedComment(int index) => index % 2 == 0 ? "No" : "Yes";

    // ---- elements ---------------------------------------------------------------------------------

    internal Rectangle? ListRect() => Shell.Rect(ListId);

    internal Rectangle? Rect(string id) => Shell.Rect(id);

    /// <summary>The realized items as UI Automation shows them, in display order.</summary>
    internal List<ListItemView> Items()
    {
        var items = new List<ListItemView>();
        AutomationElement? list = Element;
        if (list is null)
        {
            return items;
        }

        foreach (AutomationElement element in list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
        {
            string id = ShellRun.SafeId(element);
            const string prefix = "ConsoleListRow_";
            if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(id[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            string name = ShellRun.SafeName(element);
            if (name.EndsWith(NameSuffix, StringComparison.Ordinal))
            {
                name = name[..^NameSuffix.Length];
            }

            items.Add(new ListItemView(index, name, element.BoundingRectangle));
        }

        items.Sort((a, b) => a.Index.CompareTo(b.Index));
        return items;
    }

    internal AutomationElement? ItemElement(int index) => Shell.Find("ConsoleListRow_" + index.ToString(CultureInfo.InvariantCulture));

    internal static string HeaderItemId(string column) => "ConsoleListHeader_" + column;

    internal static string DividerId(string column) => "ConsoleListDivider_" + column;

    internal string HeaderStatus(string column) =>
        Shell.Find(HeaderItemId(column))?.Properties.ItemStatus.ValueOrDefault ?? string.Empty;

    internal double Logical(double physical) => physical / Scale;

    // ---- input ------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the keyboard focus into the list: a click on a tree node gives the window the keyboard (nothing has the
    /// focus at start), then F6 goes to the result pane. The selection of the list is not changed.
    /// </summary>
    internal bool TryFocusList()
    {
        if (Shell.FocusedId() == ListId)
        {
            return true;
        }

        Shell.ClickTreeNode("Computer Configuration");
        for (int i = 0; i < 3; i++)
        {
            Shell.Tap(ShellRun.VkF6);
            if (ShellRun.WaitUntil(() => Shell.FocusedId() == ListId, TimeSpan.FromSeconds(2)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Like <see cref="TryFocusList"/>, but a list that cannot be focused stops the test with the focus
    /// chain.
    /// </summary>
    internal void FocusList()
    {
        if (!TryFocusList())
        {
            throw new InvalidOperationException($"The list did not take the keyboard focus (pane={Shell.FocusedPane()}, id='{Shell.FocusedId()}').");
        }
    }

    /// <summary>Opens the View menu with Alt+V and presses the access key of one view command.</summary>
    internal bool SwitchView(char accessKey, string expectedMode)
    {
        _ = ControlTestSupport.EnsureForeground(Shell.Session);
        Shell.Chord(RawKeyboard.Alt, ShellRun.VkV);
        _ = ShellRun.WaitUntil(() => Shell.Find("MenuCommand_1004") is not null, TimeSpan.FromSeconds(5));
        Shell.Tap((ushort)char.ToUpperInvariant(accessKey));
        bool changed = ShellRun.WaitUntil(() => Mode == expectedMode, TimeSpan.FromSeconds(8));
        Thread.Sleep(500);
        return changed;
    }

    /// <summary>Types a string with Unicode input (WM_CHAR), as the keyboard would deliver the characters.</summary>
    internal void Type(string text)
    {
        _ = ControlTestSupport.EnsureForeground(Shell.Session);
        FlaUI.Core.Input.Keyboard.Type(text);
        Thread.Sleep(ShellRun.KeySettle);
    }

    internal static void Pause(int milliseconds) => Thread.Sleep(milliseconds);

    // ---- pictures and pixels ----------------------------------------------------------------------

    /// <summary>
    /// The number of pixels inside a screen rectangle that differ from the top left pixel of the rectangle by more
    /// than a small distance. A sort arrow or an icon on a flat background gives a count above zero.
    /// </summary>
    internal int InkInScreenRect(Bitmap picture, Point origin, Rectangle screenRect)
    {
        int left = Math.Max(0, screenRect.Left - origin.X);
        int top = Math.Max(0, screenRect.Top - origin.Y);
        int right = Math.Min(picture.Width, screenRect.Right - origin.X);
        int bottom = Math.Min(picture.Height, screenRect.Bottom - origin.Y);
        if (right <= left || bottom <= top)
        {
            return -1;
        }

        Color reference = picture.GetPixel(left, top);
        int ink = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                Color c = picture.GetPixel(x, y);
                int distance = Math.Abs(c.R - reference.R) + Math.Abs(c.G - reference.G) + Math.Abs(c.B - reference.B);
                if (distance > 90)
                {
                    ink++;
                }
            }
        }

        return ink;
    }

    /// <summary>The region at the top center of a header cell where the sort arrow is drawn, in screen pixels.</summary>
    internal Rectangle ArrowRegion(Rectangle headerCell)
    {
        int cx = headerCell.Left + (headerCell.Width / 2);
        int half = (int)Math.Round(6 * Scale);
        int top = headerCell.Top + (int)Math.Round(2 * Scale);
        int bottom = headerCell.Top + (int)Math.Round(9 * Scale);
        return Rectangle.FromLTRB(cx - half, top, cx + half, bottom);
    }

    /// <summary>
    /// The arrow ink of a header cell, from a fresh picture of the window. -1 when the picture cannot be
    /// judged.
    /// </summary>
    internal int ArrowInk(string column)
    {
        Rectangle? cell = Rect(HeaderItemId(column));
        if (cell is null)
        {
            return -1;
        }

        using Bitmap? picture = Shell.CaptureClient(out Point origin);
        return picture is null ? -1 : InkInScreenRect(picture, origin, ArrowRegion(cell.Value));
    }

    internal void Measure(string key, object? value) => Shell.Measure(key, value);

    internal void WriteArtifacts(string picture) => Shell.WriteArtifacts(picture, "console-list");

    /// <summary>Times an action in milliseconds.</summary>
    internal static long TimeMs(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.ElapsedMilliseconds;
    }
}
