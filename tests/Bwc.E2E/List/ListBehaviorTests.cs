using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.List;

/// <summary>
/// Report view behavior of the details list in the shipped Registry Editor, driven with real
/// keyboard and mouse input against a seeded HKCU sandbox. The seeded names and the expected order
/// come from docs/parity/oracle/sort-order.json, which records classic regedit read-only. Each test
/// writes its observed values as JSON and PNG captures under artifacts/e2e/visual/list.
/// </summary>
[TestClass]
public sealed class ListBehaviorTests
{
    private const string OracleFile = @"docs\parity\oracle\sort-order.json";
    private const string LongName = "VeryLongValueNameForAutoSizeCheck";
    private const string ChildKey = "Few";
    private const int OracleRowCount = 27;
    private const string NameHeader = "HeaderName";
    private const string TypeHeader = "HeaderType";

    /// <summary>Classic names that start with "a", ignoring case, in classic order.</summary>
    private static readonly string[] AFamily =
    [
        "a b", "a01", "a1", "a10", "a1b", "a2", "A3", "ab", "a-b", "alpha",
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    public void List_OrderTypeAheadAndHeaderClick()
    {
        var artifact = new Artifact("list-order");
        int start = E2EResults.CheckCount;
        WithSession(null, null, (session, config) =>
        {
            IReadOnlyList<string> oracle = OracleArray(config, "classicOrder");
            ClickRow(session, 0);
            IReadOnlyList<string> initial = ReadOrderByKeyboard(session, oracle.Count);
            artifact.Set("oracleOrder", oracle);
            artifact.Set("observedOrder", initial);
            Record(
                "list.order.oracle",
                initial.SequenceEqual(oracle, StringComparer.Ordinal),
                $"observed {initial.Count} rows, expected {oracle.Count}; first mismatch: {FirstMismatch(initial, oracle)}.");

            ClickHeader(session, NameHeader);
            ClickHeader(session, TypeHeader);
            // Make the list the keyboard target again before the order is read: the read uses Home and Down.
            ClickRow(session, 0);
            IReadOnlyList<string> afterHeaders = ReadOrderByKeyboard(session, oracle.Count);
            artifact.Set("orderAfterHeaderClicks", afterHeaders);
            Record(
                "list.header.click-no-reorder",
                afterHeaders.SequenceEqual(oracle, StringComparer.Ordinal),
                $"Name then Type header clicks; first mismatch: {FirstMismatch(afterHeaders, oracle)}.");

            ClickRow(session, 0);
            var cycled = new List<string>();
            for (var press = 0; press < AFamily.Length + 1; press++)
            {
                ControlTestSupport.SendKeys(session, "a");
                cycled.Add(WaitSelectedName(session, previous: press == 0 ? "(Default)" : cycled[press - 1]));
            }

            string[] expectedCycle = AFamily.Concat(new[] { AFamily[0] }).ToArray();
            artifact.Set("typeAheadCycle", cycled);
            Record(
                "list.typeahead.cycle",
                cycled.SequenceEqual(expectedCycle, StringComparer.OrdinalIgnoreCase),
                $"repeated 'a' selected [{string.Join(", ", cycled)}], expected [{string.Join(", ", expectedCycle)}].");

            Thread.Sleep(1200);
            ControlTestSupport.SendKeys(session, "a", "l");
            string prefixed = WaitSelectedName(session, previous: cycled[^1], expected: "alpha");
            artifact.Set("typeAheadPrefix", prefixed);
            Record("list.typeahead.prefix", prefixed == "alpha", $"typed 'al' selected '{prefixed}', expected 'alpha'.");

            Thread.Sleep(1200);
            ControlTestSupport.SendKeys(session, "z");
            string afterPause = WaitSelectedName(session, previous: prefixed, expected: "z10");
            artifact.Set("typeAheadAfterPause", afterPause);
            Record(
                "list.typeahead.timeout",
                afterPause == "z10",
                $"'z' after a 1.2 s pause selected '{afterPause}', expected 'z10' (the buffer reset).");

            SaveCapture(session, artifact, "order-after-typeahead");
        });

        artifact.Write(GuiGate.Config);
        FailIfNeeded(start);
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    public void List_KeyboardSelectionAndFocus()
    {
        var artifact = new Artifact("list-keyboard");
        int start = E2EResults.CheckCount;
        WithSession(null, null, (session, config) =>
        {
            ClickRow(session, 0);
            int single = SelectedCount(session);
            Record(
                "list.select.click",
                single == 1 && IsRowSelected(session, 0),
                $"click row 0: selected count {single}.");

            CtrlKey(session, RawKeyboard.Down);
            CtrlKey(session, RawKeyboard.Down);
            int afterCtrlArrow = SelectedCount(session);
            bool focusOnly = afterCtrlArrow == 1 && IsRowSelected(session, 0) && !IsRowSelected(session, 2);
            artifact.Set("selectedAfterCtrlArrow", afterCtrlArrow);
            Record(
                "list.ctrl-arrow.focus-only",
                focusOnly,
                $"Control+Down twice: selected count {afterCtrlArrow}, row 0 selected={IsRowSelected(session, 0)}, row 2 selected={IsRowSelected(session, 2)}.");

            CtrlKey(session, RawKeyboard.Space);
            bool toggledOn = WaitUntilTrue(() => SelectedCount(session) == 2 && IsRowSelected(session, 2));
            Record(
                "list.ctrl-space.toggles-focused",
                toggledOn,
                $"Control+Space on the unselected focused row 2: selected count {SelectedCount(session)}, row 2 selected={IsRowSelected(session, 2)}.");
            SaveCapture(session, artifact, "ctrl-space-focus-on-unselected-row");

            CtrlKey(session, RawKeyboard.Space);
            bool toggledOff = WaitUntilTrue(() => SelectedCount(session) == 1 && !IsRowSelected(session, 2));
            Record(
                "list.ctrl-space.toggles-back",
                toggledOff,
                $"second Ctrl+Space: selected count {SelectedCount(session)}.");

            ClickRow(session, 0);
            // One Shift hold covers all three Down presses, so Shift is already down when each arrow is handled.
            _ = ControlTestSupport.EnsureForeground(session);
            RawKeyboard.WithHeld(RawKeyboard.Shift, () =>
            {
                for (var press = 0; press < 3; press++)
                {
                    RawKeyboard.Tap(RawKeyboard.Down);
                    Thread.Sleep(30);
                }
            });
            Thread.Sleep(150);

            bool range = WaitUntilTrue(() => SelectedCount(session) == 4 &&
                IsRowSelected(session, 0) && IsRowSelected(session, 3) && !IsRowSelected(session, 4));
            E2EResults.Note("list.shift.trace", ListHelpText(session));
            artifact.Set("selectedAfterShiftRange", SelectedCount(session));
            Record(
                "list.shift-arrow.range",
                range,
                $"Shift+Down x3 from row 0: selected count {SelectedCount(session)}, expected 4 (rows 0 to 3).");
            SaveCapture(session, artifact, "shift-range");

            CtrlKey(session, RawKeyboard.A);

            bool all = WaitUntilTrue(() => SelectedCount(session) == OracleRowCount);
            artifact.Set("selectedAfterCtrlA", SelectedCount(session));
            Record(
                "list.ctrl-a.selects-all",
                all,
                $"Control+A: selected count {SelectedCount(session)}, expected {OracleRowCount}.");
            SaveCapture(session, artifact, "ctrl-a-all-selected");
        });

        artifact.Write(GuiGate.Config);
        FailIfNeeded(start);
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    public void List_MouseSelectionAndEmptyArea()
    {
        var artifact = new Artifact("list-mouse");
        int start = E2EResults.CheckCount;
        WithSession(null, null, (session, config) =>
        {
            ClickRow(session, 1);
            CtrlClickRow(session, 2);
            CtrlClickRow(session, 3);
            int multi = SelectedCount(session);
            Record("list.multi.ctrl-click", multi == 3, $"row 1, then Ctrl+click rows 2 and 3: selected count {multi}.");

            RightClickRow(session, 2);
            bool kept = WaitUntilTrue(() => SelectedCount(session) == 3 &&
                IsRowSelected(session, 1) && IsRowSelected(session, 2) && IsRowSelected(session, 3));
            artifact.Set("selectedAfterRightClickSelected", SelectedCount(session));
            Record(
                "list.right-click.keeps-multi-selection",
                kept,
                $"right-click on selected row 2: selected count {SelectedCount(session)}, expected 3.");
            SaveCapture(session, artifact, "right-click-keeps-selection");
            _ = ControlTestSupport.CloseOpenMenus(session);

            RightClickRow(session, 5);
            bool replaced = WaitUntilTrue(() => SelectedCount(session) == 1 && IsRowSelected(session, 5));
            artifact.Set("selectedAfterRightClickUnselected", SelectedCount(session));
            Record(
                "list.right-click.unselected-selects-only",
                replaced,
                $"right-click on unselected row 5: selected count {SelectedCount(session)}, row 5 selected={IsRowSelected(session, 5)}.");
            _ = ControlTestSupport.CloseOpenMenus(session);
        });

        WithSession(ChildKey, null, (session, config) =>
        {
            ClickRow(session, 0);
            int before = SelectedCount(session);
            // The body grid has no UIA peer, so the point is taken from the list control, 30 px above
            // its bottom edge. Two rows fill only the top of the body, so that point is empty.
            Rectangle bounds = session.Find(ControlTestSupport.ListControl)?.BoundingRectangle
                ?? throw new InvalidOperationException("ListControl is missing.");
            var point = new Point(bounds.Left + 20, bounds.Bottom - 30);
            DescribeElementAt(point, "list.empty-area.hit");
            Mouse.Click(point);
            bool cleared = WaitUntilTrue(() => SelectedCount(session) == 0);
            artifact.Set("selectedBeforeEmptyClick", before);
            artifact.Set("selectedAfterEmptyClick", SelectedCount(session));
            Record(
                "list.empty-area.clears-selection",
                before == 1 && cleared,
                $"before the empty-area click: {before} selected, after: {SelectedCount(session)} (list body {bounds.Height} px tall).");
            SaveCapture(session, artifact, "empty-area-cleared");
        });

        artifact.Write(GuiGate.Config);
        FailIfNeeded(start);
    }

    [TestMethod]
    [TestCategory("Controls")]
    [TestCategory("Mutating")]
    public void List_DividersAndGridPattern()
    {
        var artifact = new Artifact("list-columns");
        int start = E2EResults.CheckCount;
        WithSession(null, LongName, (session, config) =>
        {
            double scale = ControlTestSupport.WindowDpi(session) / 96.0;
            double before = NameColumnWidth(session);

            DragDivider(session, 60 * scale);
            E2EResults.Note("list.divider.trace", ListHelpText(session));
            double widened = WaitForWidth(session, before + (60 * scale) - 2, before + (60 * scale) + 2);
            bool dragged = Math.Abs((widened - before) - (60 * scale)) <= 2;
            artifact.Set("nameWidthBeforeDrag", before);
            artifact.Set("nameWidthAfterDrag", widened);
            Record(
                "list.divider.drag-resizes",
                dragged,
                $"Name divider dragged +60 DIP: width {before:0.#} -> {widened:0.#} px (expected +{60 * scale:0.#}).");

            DragDivider(session, -400 * scale);
            double narrow = NameColumnWidth(session);
            artifact.Set("nameWidthNarrow", narrow);

            DoubleClickDivider(session);
            double autoSized = WaitForWidth(session, 140 * scale, 2000 * scale);
            bool fits = autoSized >= 140 * scale && autoSized > narrow;
            artifact.Set("nameWidthAutoSized", autoSized);
            Record(
                "list.divider.double-click-autosizes",
                fits,
                $"Name divider double-clicked at {narrow:0.#} px: width {autoSized:0.#} px, must hold '{LongName}' (at least 140 DIP).");
            SaveCapture(session, artifact, "divider-autosized");

            AutomationElement list = session.Find(ControlTestSupport.ListControl)
                ?? throw new InvalidOperationException("ListControl is missing.");
            int rows = list.Patterns.Grid.Pattern.RowCount.Value;
            int columns = list.Patterns.Grid.Pattern.ColumnCount.Value;
            artifact.Set("uiaGridRowCount", rows);
            artifact.Set("uiaGridColumnCount", columns);
            int expectedRows = OracleRowCount + 1;
            Record(
                "list.uia.grid.counts",
                rows == expectedRows && columns == 3,
                $"Grid pattern RowCount={rows} (expected {expectedRows}), ColumnCount={columns} (expected 3).");

            bool tablePresent = list.Patterns.Table.IsSupported;
            int headerItems = list.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem)).Length;
            bool nameIsHeaderItem = session.Find(NameHeader)?.ControlType == ControlType.HeaderItem;
            Record(
                "list.uia.table.header-items",
                tablePresent && headerItems == 3 && nameIsHeaderItem,
                $"Table pattern supported={tablePresent}, HeaderItem descendants={headerItems} (expected 3), {NameHeader} is HeaderItem={nameIsHeaderItem}.");

            AutomationElement? firstRow = session.Find(ControlTestSupport.ListRowPrefix + "0");
            bool rowIsSelectable = firstRow is not null &&
                firstRow.ControlType == ControlType.DataItem &&
                firstRow.Patterns.SelectionItem.IsSupported;
            Record(
                "list.uia.row.data-item-selection",
                rowIsSelectable,
                "ListRow_0 is a DataItem with the SelectionItem pattern.");
        });

        artifact.Write(GuiGate.Config);
        FailIfNeeded(start);
    }

    /// <summary>
    /// Seeds the oracle names (plus the long name when given) under the run key, and optionally a child
    /// key with two values. Launches the Registry Editor, navigates to the key, and runs the body.
    /// </summary>
    private static void WithSession(string? childKey, string? longName, Action<AppSession, E2EConfig> body)
    {
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();
        try
        {
            Seed(config, sandbox, childKey, longName);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("list-" + (childKey ?? "root").ToLowerInvariant()),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            // The window paints after launch, so the list is waited for rather than read once.
            bool listFound = ControlTestSupport.WaitUntil(
                () => session.Find(ControlTestSupport.ListControl) is not null,
                TimeSpan.FromSeconds(15));
            E2EResults.Record("list.found", listFound, listFound ? "ListControl is present." : "ListControl is missing.");
            if (!listFound)
            {
                Assert.Fail("ListControl is missing.");
            }

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "list.sandbox");

            bool rowsReady = ControlTestSupport.WaitUntil(
                () => session.Find(ControlTestSupport.ListRowPrefix + "0") is not null,
                TimeSpan.FromSeconds(15));
            E2EResults.Record("list.rows.ready", rowsReady, $"ListRow_0 present={rowsReady}.");
            _ = ControlTestSupport.EnsureForeground(session);
            body(session, config);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>Writes the seeded values through the sandbox helper, which only touches this run's key.</summary>
    private static void Seed(E2EConfig config, SandboxRegistry sandbox, string? childKey, string? longName)
    {
        sandbox.SetString(string.Empty, string.Empty, "Default value text");
        foreach (string name in OracleArray(config, "insertionOrder"))
        {
            sandbox.SetString(string.Empty, name, "v-" + name);
        }

        if (longName is not null)
        {
            sandbox.SetString(string.Empty, longName, "long");
        }

        if (childKey is not null)
        {
            sandbox.SetString(childKey, "One", "1");
            sandbox.SetString(childKey, "Two", "2");
        }
    }

    private static IReadOnlyList<string> OracleArray(E2EConfig config, string property)
    {
        string path = Path.Combine(config.RepoRoot, OracleFile);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(property).EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>Reads every row name in order by moving a single selection down from the first row.</summary>
    private static IReadOnlyList<string> ReadOrderByKeyboard(AppSession session, int count)
    {
        var names = new List<string>(count);
        ControlTestSupport.SendKeys(session, "Home");
        string previous = string.Empty;
        for (var index = 0; index < count; index++)
        {
            string name = WaitSelectedName(session, previous, expected: null);
            names.Add(name);
            previous = name;
            if (index < count - 1)
            {
                ControlTestSupport.SendKeys(session, "Down");
            }
        }

        return names;
    }

    /// <summary>
    /// Waits until the single selected row is a different row than <paramref name="previous"/> and,
    /// when given, has the expected name. Returns the last name read.
    /// </summary>
    private static string WaitSelectedName(AppSession session, string previous, string? expected = null)
    {
        string name = previous;
        ControlTestSupport.WaitUntil(
            () =>
            {
                name = SelectedRowName(session);
                if (expected is not null)
                {
                    return string.Equals(name, expected, StringComparison.Ordinal);
                }

                return name.Length > 0 && !string.Equals(name, previous, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(3));
        return name;
    }

    private static string SelectedRowName(AppSession session)
    {
        AutomationElement? selected = ControlTestSupport
            .Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(ControlTestSupport.IsSelected);
        return ControlTestSupport.SafeName(selected);
    }

    private static bool IsRowSelected(AppSession session, int index) =>
        ControlTestSupport.IsSelected(session.Find(ControlTestSupport.ListRowPrefix + index));

    /// <summary>Selected count from the E2E-only UIA help text of the list control. Negative when unreadable.</summary>
    private static int SelectedCount(AppSession session)
    {
        AutomationElement? list = session.Find(ControlTestSupport.ListControl);
        if (list is null)
        {
            return -1;
        }

        try
        {
            string help = list.Properties.HelpText.Value ?? string.Empty;
            const string marker = "Selected=";
            int at = help.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            string text = help[(at + marker.Length)..];
            int end = text.IndexOf(';', StringComparison.Ordinal);
            if (end >= 0)
            {
                text = text[..end];
            }

            return int.TryParse(text, out int count) ? count : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static AutomationElement RealizedRow(AppSession session, int index) =>
        session.Find(ControlTestSupport.ListRowPrefix + index)
        ?? throw new InvalidOperationException($"ListRow_{index} is not realized.");

    private static void ClickRow(AppSession session, int index)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        RealizedRow(session, index).Click();
        _ = ControlTestSupport.WaitUntil(() => IsRowSelected(session, index), TimeSpan.FromSeconds(3));
    }

    private static void CtrlClickRow(AppSession session, int index)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        RawKeyboard.WithHeld(RawKeyboard.Control, () => RealizedRow(session, index).Click());
        _ = ControlTestSupport.WaitUntil(() => IsRowSelected(session, index), TimeSpan.FromSeconds(3));
    }

    /// <summary>One key pressed with Control held, sent as raw input to the foreground window.</summary>
    private static void CtrlKey(AppSession session, ushort key)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        RawKeyboard.WithHeld(RawKeyboard.Control, () => RawKeyboard.Tap(key));
    }

    private static void RightClickRow(AppSession session, int index)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        RealizedRow(session, index).RightClick();
        Thread.Sleep(250);
    }

    private static void ClickHeader(AppSession session, string headerId)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        (session.Find(headerId) ?? throw new InvalidOperationException($"{headerId} is missing.")).Click();
        Thread.Sleep(300);
    }

    /// <summary>Drags the divider at the right edge of a header column by a physical pixel distance.</summary>
    private static void DragDivider(AppSession session, double deltaPixels)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        Point grip = DividerPoint(session);
        DescribeElementAt(grip, "list.divider.hit");
        int x = grip.X;
        int y = grip.Y;
        MoveAbsolute(x, y);
        Thread.Sleep(80);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        const int steps = 16;
        for (var step = 1; step <= steps; step++)
        {
            MoveAbsolute(x + (int)Math.Round(deltaPixels * step / steps), y);
            Thread.Sleep(20);
        }

        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(250);
    }

    private static void DoubleClickDivider(AppSession session)
    {
        _ = ControlTestSupport.EnsureForeground(session);
        Point point = DividerPoint(session);
        Mouse.DoubleClick(point);
        Thread.Sleep(400);
    }

    // Input events, not just cursor moves: SetCursorPos moves the cursor without a pointer message, so a
    // drag sent with it reaches the app as a single press. MOUSEEVENTF_ABSOLUTE takes a 0..65535 range.
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseMoveAbsolute = 0x8001;
    private const int SystemMetricsCxScreen = 0;
    private const int SystemMetricsCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Moves the pointer to a physical screen point with a real move event on the primary monitor.</summary>
    private static void MoveAbsolute(int x, int y)
    {
        int width = GetSystemMetrics(SystemMetricsCxScreen);
        int height = GetSystemMetrics(SystemMetricsCyScreen);
        uint nx = (uint)(x * 65535L / (width - 1));
        uint ny = (uint)(y * 65535L / (height - 1));
        mouse_event(MouseMoveAbsolute, nx, ny, 0, UIntPtr.Zero);
    }

    private static Rectangle HeaderBounds(AppSession session, string headerId) =>
        session.Find(headerId)?.BoundingRectangle
        ?? throw new InvalidOperationException($"{headerId} is missing.");

    /// <summary>
    /// The Name column divider. The Name and Type titles are inset inside their columns, so the
    /// boundary is the midpoint between the Name title's right edge and the Type title's left edge.
    /// </summary>
    private static Point DividerPoint(AppSession session)
    {
        Rectangle name = HeaderBounds(session, NameHeader);
        Rectangle type = HeaderBounds(session, TypeHeader);
        return new Point((name.Right + type.Left) / 2, name.Top + (name.Height / 2));
    }

    /// <summary>The E2E-only trace in the list control's UIA help text, or empty when unreadable.</summary>
    private static string ListHelpText(AppSession session)
    {
        try
        {
            return session.Find(ControlTestSupport.ListControl)?.Properties.HelpText.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Records the UIA element under a screen point, so a miss can be attributed to its receiver.</summary>
    private static void DescribeElementAt(Point point, string checkId)
    {
        try
        {
            using var automation = new FlaUI.UIA3.UIA3Automation();
            AutomationElement? element = automation.FromPoint(point);
            string described = element is null
                ? "none"
                : $"name='{ControlTestSupport.SafeName(element)}' id='{ControlTestSupport.SafeId(element)}' type={element.ControlType}";
            E2EResults.Note(checkId, $"element at ({point.X},{point.Y}): {described}.");
        }
        catch (Exception ex)
        {
            E2EResults.Note(checkId, $"element lookup at ({point.X},{point.Y}) failed: {ex.Message}");
        }
    }

    /// <summary>Name column width in physical pixels: the distance between the Name and Type column starts.</summary>
    private static double NameColumnWidth(AppSession session) =>
        HeaderBounds(session, TypeHeader).Left - HeaderBounds(session, NameHeader).Left;

    private static double WaitForWidth(AppSession session, double minimum, double maximum)
    {
        double width = 0;
        ControlTestSupport.WaitUntil(
            () =>
            {
                width = NameColumnWidth(session);
                return width >= minimum && width <= maximum;
            },
            TimeSpan.FromSeconds(3));
        return width;
    }

    private static bool WaitUntilTrue(Func<bool> condition) =>
        ControlTestSupport.WaitUntil(condition, TimeSpan.FromSeconds(3));

    private static string FirstMismatch(IReadOnlyList<string> observed, IReadOnlyList<string> expected)
    {
        int count = Math.Max(observed.Count, expected.Count);
        for (var index = 0; index < count; index++)
        {
            string left = index < observed.Count ? observed[index] : "<missing>";
            string right = index < expected.Count ? expected[index] : "<missing>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return $"index {index} observed '{left}' expected '{right}'";
            }
        }

        return "none";
    }

    private static void SaveCapture(AppSession session, Artifact artifact, string name)
    {
        string dir = Path.Combine(GuiGate.Config.ArtifactsDir, "visual", "list");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        bool written;
        try
        {
            _ = WindowCapture.Save(session.MainHwnd, path);
            written = File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex)
        {
            written = false;
            path += $" ({ex.GetType().Name}: {ex.Message})";
        }

        artifact.Set("capture-" + name, path);
        // The PNG is the artifact. Writing it is not a product claim, so it is a note.
        E2EResults.Note("list.capture." + name, $"saved '{path}', written={written}.");
    }

    private static void Record(string id, bool pass, string notes) => E2EResults.Record(id, pass, notes);

    private static void FailIfNeeded(int start)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            $"List checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>Observed values for one test, written as JSON beside the captures.</summary>
    private sealed class Artifact
    {
        private readonly string _name;
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public Artifact(string name)
        {
            _name = name;
        }

        public void Set(string key, object? value) => _values[key] = value;

        public void Write(E2EConfig config)
        {
            string dir = Path.Combine(config.ArtifactsDir, "visual", "list");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, _name + ".json"),
                JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
