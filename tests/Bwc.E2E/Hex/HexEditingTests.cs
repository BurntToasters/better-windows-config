using System.Globalization;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hex;

/// <summary>
/// Seeds a 48 byte binary value and drives the double-click hex editor: edit and accept, insert and
/// delete then cancel, select all and copy, and Escape returning focus to the details list.
/// </summary>
[TestClass]
public sealed class HexEditingTests
{
    private const string BinaryValueName = "EditableBinary";
    private const string StringValueName = "EditableString";
    private const string StringText = "HexEdit";
    private const int PatternLength = 0x30;
    private const string CopyBehavior =
        "Ctrl+C after Ctrl+A copies all 48 selected bytes as space separated hex text, in the same format as the hex column.";
    private static readonly string OriginalHex = HexText(CreatePattern());
    private static readonly string EditedHex = HexText(EditedPattern());

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("HexEdit")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void HexEditing_EditsCancelsAndCopies()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("hexedit.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = CreateFreshDataRoot(config, "hexedit");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => ContainsAll(
                    ControlTestSupport.ListRowNames(session),
                    BinaryValueName,
                    StringValueName),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "hexedit.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "hexedit.sandbox.navigate");

            CheckOpenAndEdit(session, driver, sandbox);
            CheckInsertDeleteCancel(session, driver, sandbox);
            CheckCopyAndEscape(session, driver, sandbox);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckOpenAndEdit(
        AppSession session,
        EditingDriver driver,
        SandboxRegistry sandbox)
    {
        bool selected = SelectRow(session, BinaryValueName);
        E2EResults.Record(
            "hexedit.binary.select",
            selected,
            $"binary row selected={selected}.");

        E2EGuards.RequireSandboxSelection(session, sandbox, "hexedit.sandbox.open");
        bool opened = OpenEditor(session, driver, BinaryValueName);
        E2EResults.Record(
            "hexedit.dialog",
            opened,
            $"doubleClick opened editor={opened}. {_openDetail}");

        string status = StatusOf(driver);
        AutomationElement? firstRow = driver.WaitForElement("HexRow_0");
        string rowText = ControlTestSupport.SafeName(firstRow);
        bool rowOk = rowText.StartsWith("0000  00 01 02", StringComparison.Ordinal);
        bool statusOk = status.Contains("48 bytes", StringComparison.Ordinal) &&
            status.Contains("Overwrite", StringComparison.Ordinal);
        E2EResults.Record(
            "hexedit.dialog.content",
            driver.TextOf("ValueNameInput") == BinaryValueName && rowOk && statusOk,
            $"caption-name='{driver.TextOf("ValueNameInput")}', status='{status}', row0='{rowText}'.");

        AutomationElement? editor = driver.WaitForElement("HexEditor");
        bool focused = FocusEditor(session, firstRow, editor);
        bool selectAll = false;
        if (focused)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            selectAll = ControlTestSupport.WaitUntil(
                () => HelpTextOf(editor).Contains("SelectedLength=48", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            // The clipboard is cleared first, so only this copy can put text on it.
            _ = ClipboardProbe.Clear();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            ClipboardProbe.Reading original = ClipboardProbe.WaitForText(OriginalHex, TimeSpan.FromSeconds(3));
            if (selectAll)
            {
                ClipboardProbe.RecordCopy("hexedit.clipboard.original", original, OriginalHex, CopyBehavior);
            }
            else
            {
                E2EResults.Note("hexedit.clipboard.original", "not compared: select all did not reach 48 bytes (see hexedit.selectall).");
            }
        }
        else
        {
            E2EResults.Note("hexedit.clipboard.original", "not attempted: the hex editor did not take UIA keyboard focus (see hexedit.selectall).");
        }

        E2EResults.Record(
            "hexedit.selectall",
            selectAll,
            $"editorFocused={focused}, help='{HelpTextOf(editor)}'.");

        // Ctrl+Home moves from the select all range to byte 0, then AABB overwrites the first
        // two bytes. The dialog keeps its own buffer until OK. Keyboard input goes directly to
        // the modal dialog; AppSession.SendKeys would re-focus the disabled main window.
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        bool byteZero = ControlTestSupport.WaitUntil(
            () => HelpTextOf(editor).Contains("SelectedOffset=0;SelectedLength=1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        Keyboard.Type("AABB");
        bool typed = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.SafeName(driver.WaitForElement("HexRow_0"))
                .Contains("AA BB", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "hexedit.typed",
            byteZero && typed,
            $"byteZero={byteZero}, typed={typed}, row0='{ControlTestSupport.SafeName(driver.WaitForElement("HexRow_0"))}', help='{HelpTextOf(editor)}'.");

        bool invoked = driver.Invoke("DialogPrimary");
        bool closed = driver.WaitForNoDialog();

        byte[] expected = CreatePattern();
        expected[0] = 0xAA;
        expected[1] = 0xBB;
        bool saved = ControlTestSupport.WaitUntil(
            () => sandbox.ReadBinary(string.Empty, BinaryValueName) is { } bytes &&
                bytes.SequenceEqual(expected),
            TimeSpan.FromSeconds(10));
        bool typePreserved = ValueKindOf(sandbox, BinaryValueName) == RegistryValueKind.Binary;
        bool rowSelected = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindRow(session, BinaryValueName)),
            TimeSpan.FromSeconds(10));
        E2EResults.Record(
            "hexedit.saved",
            invoked && closed && saved && typePreserved && rowSelected,
            $"invoked={invoked}, closed={closed}, saved={saved}, kind={ValueKindOf(sandbox, BinaryValueName)}, rowSelected={rowSelected}.");
    }

    private static void CheckInsertDeleteCancel(
        AppSession session,
        EditingDriver driver,
        SandboxRegistry sandbox)
    {
        AutomationElement? row = FindRow(session, BinaryValueName);
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindRow(session, BinaryValueName)),
            TimeSpan.FromSeconds(5));

        E2EGuards.RequireSandboxSelection(session, sandbox, "hexedit.sandbox.reopen");
        bool opened = OpenEditor(session, driver, BinaryValueName);
        string status = StatusOf(driver);
        AutomationElement? editor = driver.WaitForElement("HexEditor");
        AutomationElement? firstRow = driver.WaitForElement("HexRow_0");
        bool focused = FocusEditor(session, firstRow, editor);
        E2EResults.Record(
            "hexedit.reopen",
            opened && focused && status.Contains("48 bytes", StringComparison.Ordinal),
            $"opened={opened}, focused={focused}, status='{status}'.");

        bool insert = focused;
        if (insert)
        {
            Keyboard.Press(VirtualKeyShort.INSERT);
        }

        bool insertStatus = insert && ControlTestSupport.WaitUntil(
            () => StatusOf(driver).Contains("Insert", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "hexedit.insertmode",
            insertStatus,
            $"insert={insert}, status='{StatusOf(driver)}', help='{HelpTextOf(editor)}'.");

        // Ctrl+Home puts the caret on byte 0, and one Right moves it to byte 1: Delete removes that byte and
        // Backspace removes the byte before the caret.
        bool moved = insertStatus;
        if (moved)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
            Keyboard.Press(VirtualKeyShort.RIGHT);
        }

        bool deleted = moved;
        if (deleted)
        {
            Keyboard.Press(VirtualKeyShort.DELETE);
        }

        bool backspaced = deleted;
        if (backspaced)
        {
            Keyboard.Press(VirtualKeyShort.BACK);
        }

        bool lengthChanged = backspaced && ControlTestSupport.WaitUntil(
            () => StatusOf(driver).Contains("46 bytes", StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "hexedit.insert.delete",
            moved && deleted && backspaced && lengthChanged,
            $"moved={moved}, deleted={deleted}, backspaced={backspaced}, status='{StatusOf(driver)}', help='{HelpTextOf(editor)}'.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();

        byte[] expected = CreatePattern();
        expected[0] = 0xAA;
        expected[1] = 0xBB;
        bool unchanged = sandbox.ReadBinary(string.Empty, BinaryValueName) is { } bytes &&
            bytes.SequenceEqual(expected);
        bool focusReturned = ControlTestSupport.WaitUntil(
            () => HasKeyboardFocus(session.Find(ControlTestSupport.ListControl)),
            TimeSpan.FromSeconds(5));
        E2EResults.Record(
            "hexedit.cancel",
            closed && unchanged && focusReturned,
            $"closed={closed}, unchanged={unchanged}, focusReturnedToList={focusReturned}.");
    }

    private static void CheckCopyAndEscape(
        AppSession session,
        EditingDriver driver,
        SandboxRegistry sandbox)
    {
        AutomationElement? row = FindRow(session, BinaryValueName);
        ControlTestSupport.Select(row);
        _ = ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindRow(session, BinaryValueName)),
            TimeSpan.FromSeconds(5));

        E2EGuards.RequireSandboxSelection(session, sandbox, "hexedit.sandbox.copy");
        bool opened = OpenEditor(session, driver, BinaryValueName);
        AutomationElement? editor = driver.WaitForElement("HexEditor");
        AutomationElement? firstRow = driver.WaitForElement("HexRow_0");
        bool focused = opened && FocusEditor(session, firstRow, editor);

        bool selectAll = false;
        if (focused)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            selectAll = ControlTestSupport.WaitUntil(
                () => HelpTextOf(editor).Contains("SelectedLength=48", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            // The copy must carry the edited bytes from CheckOpenAndEdit, not the original pattern.
            _ = ClipboardProbe.Clear();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            ClipboardProbe.Reading edited = ClipboardProbe.WaitForText(EditedHex, TimeSpan.FromSeconds(3));
            if (selectAll)
            {
                ClipboardProbe.RecordCopy("hexedit.clipboard.afteredit", edited, EditedHex, CopyBehavior);
            }
            else
            {
                E2EResults.Note("hexedit.clipboard.afteredit", "not compared: select all did not reach 48 bytes (see hexedit.copy.afteredit).");
            }
        }
        else
        {
            E2EResults.Note("hexedit.clipboard.afteredit", "not attempted: the hex editor did not take focus (see hexedit.copy.afteredit).");
        }

        E2EResults.Record(
            "hexedit.copy.afteredit",
            selectAll,
            $"focused={focused}, selectAll={selectAll}, help='{HelpTextOf(editor)}'.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool focusReturned = ControlTestSupport.WaitUntil(
            () => HasKeyboardFocus(session.Find(ControlTestSupport.ListControl)),
            TimeSpan.FromSeconds(5));
        bool unchanged = sandbox.ReadBinary(string.Empty, BinaryValueName) is { Length: PatternLength };
        E2EResults.Record(
            "hexedit.escape.focus",
            closed && focusReturned && unchanged,
            $"closed={closed}, focusReturnedToList={focusReturned}, unchangedLength={unchanged}.");
    }

    /// <summary>Why the last <see cref="OpenEditor"/> call failed, or what it saw when it worked.</summary>
    internal static string _openDetail = string.Empty;

    internal static bool OpenEditor(AppSession session, EditingDriver driver, string rowName)
    {
        _openDetail = string.Empty;
        if (!driver.WaitForNoDialog(TimeSpan.FromSeconds(1)))
        {
            _openDetail = "a dialog was already open before the double-click";
            return false;
        }

        _ = ControlTestSupport.EnsureForeground(session);
        AutomationElement? row = FindRow(session, rowName);
        if (row is null)
        {
            _openDetail = "the row was not found in the list";
            return false;
        }

        // The input is valid only when the app is foreground and the click point belongs to this row. The
        // wait covers that precondition; the editor must still open after the double-click.
        string target = string.Empty;
        bool onRow = ControlTestSupport.WaitUntil(
            () => IsForeground(session) && ClickTargetIsRow(session, row, out target),
            TimeSpan.FromSeconds(5));
        if (!onRow)
        {
            _openDetail = $"the click point never reached the row. {target}. {ForegroundOf(session)}.";
            return false;
        }

        try
        {
            row.DoubleClick();
        }
        catch (Exception ex)
        {
            _openDetail = $"DoubleClick threw {ex.GetType().Name}: {ex.Message}. {target}.";
            return false;
        }

        bool dialog = driver.WaitForDialog();
        bool root = driver.WaitForElement("DialogRoot") is not null;
        bool editor = driver.WaitForElement("HexEditor") is not null;
        bool opened = dialog && root && editor;
        _openDetail = opened
            ? $"{target}. {ListTrace(session)}."
            : $"no editor after the double-click. {target}. {ListTrace(session)}. {ForegroundOf(session)}.";
        return opened;
    }

    private static bool IsForeground(AppSession session) =>
        ForegroundGuard.Observe(session.MainHwnd, out _) == ForegroundState.Target;

    /// <summary>
    /// True when the row's click point is hit by the row itself or by one of its children. The description
    /// names what UI Automation found at the point, so a covered or empty target is visible in the result.
    /// </summary>
    private static bool ClickTargetIsRow(AppSession session, AutomationElement row, out string description)
    {
        description = string.Empty;
        try
        {
            if (!row.TryGetClickablePoint(out System.Drawing.Point point))
            {
                description = "no clickable point";
                return false;
            }

            using var automation = new UIA3Automation();
            AutomationElement? hit = automation.FromPoint(point);
            string hitText = hit is null
                ? "nothing"
                : $"{hit.ControlType} '{ControlTestSupport.SafeName(hit)}' in pid {SafeProcessId(hit)}";
            description = $"click point ({point.X},{point.Y}) hits {hitText}; app pid {session.ProcessId}";
            return hit is not null && IsRowOrDescendant(hit, row);
        }
        catch (Exception ex)
        {
            description = $"target lookup threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool IsRowOrDescendant(AutomationElement element, AutomationElement row)
    {
        int[] rowId = RuntimeIdOf(row);
        for (AutomationElement? current = element; current is not null; current = SafeParent(current))
        {
            if (rowId.Length > 0 && RuntimeIdOf(current).SequenceEqual(rowId))
            {
                return true;
            }
        }

        return false;
    }

    private static int[] RuntimeIdOf(AutomationElement element)
    {
        try
        {
            return element.FrameworkAutomationElement.RuntimeId.ValueOrDefault ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static AutomationElement? SafeParent(AutomationElement element)
    {
        try
        {
            return element.Parent;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int SafeProcessId(AutomationElement element)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string ListTrace(AppSession session) =>
        "list trace: " + HelpTextOf(session.Find(ControlTestSupport.ListControl));

    private static string ForegroundOf(AppSession session)
    {
        ForegroundState state = ForegroundGuard.Observe(session.MainHwnd, out string text);
        return $"foreground {state}: {text}";
    }

    internal static bool FocusEditor(
        AppSession session,
        AutomationElement? firstRow,
        AutomationElement? editor)
    {
        // EnsureForeground would re-focus the disabled main window and take keyboard input away
        // from the modal dialog. UIA focus on the row activates the dialog and focuses the editor.
        _ = session;
        try
        {
            firstRow?.Focus();
        }
        catch (Exception)
        {
            // The focus check below records the result.
        }

        return ControlTestSupport.WaitUntil(
            () => HasKeyboardFocus(editor),
            TimeSpan.FromSeconds(5));
    }

    internal static AutomationElement? FindRow(AppSession session, string name) =>
        ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            name);

    internal static bool SelectRow(AppSession session, string name)
    {
        AutomationElement? row = FindRow(session, name);
        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(FindRow(session, name)),
            TimeSpan.FromSeconds(5));
    }

    private static byte[] CreatePattern()
    {
        byte[] pattern = new byte[PatternLength];
        for (var index = 0; index < pattern.Length; index++)
        {
            pattern[index] = (byte)index;
        }

        return pattern;
    }

    /// <summary>The pattern after the edit in CheckOpenAndEdit: the first two bytes become AA BB.</summary>
    private static byte[] EditedPattern()
    {
        byte[] expected = CreatePattern();
        expected[0] = 0xAA;
        expected[1] = 0xBB;
        return expected;
    }

    private static string HexText(byte[] bytes) =>
        string.Join(" ", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static void Seed(SandboxRegistry sandbox)
    {
        using var root = sandbox.CreateSubKey(string.Empty);
        root.SetValue(BinaryValueName, CreatePattern(), RegistryValueKind.Binary);
        root.SetValue(StringValueName, StringText, RegistryValueKind.String);
    }

    private static RegistryValueKind ValueKindOf(SandboxRegistry sandbox, string name)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(sandbox.RunRootPath, writable: false);
            return key?.GetValueKind(name) ?? RegistryValueKind.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RegistryValueKind.Unknown;
        }
    }

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The classic Edit Binary Value has no status line. The length and the mode come from the editor's
    /// E2E help text, and the text reads like the old status: "48 bytes Overwrite".
    /// </summary>
    private static string StatusOf(EditingDriver driver)
    {
        string help = HelpTextOf(driver.WaitForElement("HexEditor"));
        string length = HelpField(help, "Length");
        string mode = HelpField(help, "Mode");
        return length.Length == 0 ? string.Empty : $"{length} bytes {mode}";
    }

    private static string HelpField(string help, string name)
    {
        foreach (string part in help.Split(';'))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(part[..equals], name, StringComparison.Ordinal))
            {
                return part[(equals + 1)..];
            }
        }

        return string.Empty;
    }

    internal static string HelpTextOf(AutomationElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        try
        {
            return element.Properties.HelpText.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool HasKeyboardFocus(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.Properties.HasKeyboardFocus.Value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteDataRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
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
            // The run subtree is already gone. A settings file held briefly by the exiting
            // application is left for the next scoped run.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Hex editing checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
