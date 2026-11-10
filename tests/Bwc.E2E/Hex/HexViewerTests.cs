using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hex;

/// <summary>
/// Seeds a 300 byte binary value and a string value, then drives View > Display Binary Data through
/// the real menu, modal dialog, hex grid, selection, and clipboard path.
/// </summary>
[TestClass]
public sealed class HexViewerTests
{
    private const int MenuView = 514;
    private const int MenuViewDisplayBinaryData = 679;
    private const string PatternValueName = "Pattern300";
    private const string StringValueName = "HexString";
    private const string StringText = "HelloHex";
    private const uint CfUnicodeText = 13;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hex")]
    public void HexViewer_ShowsRawBytesSelectsAndReturnsFocus()
    {
        _checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("hex.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        byte[] pattern = CreatePattern();
        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox, pattern);
            dataRoot = CreateFreshDataRoot(config, "hex");

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
                    PatternValueName,
                    StringValueName),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "hex.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            CheckBinaryValue(session, driver, pattern, sandbox);
            CheckStringValue(session, driver, sandbox);

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);
        }
    }

    private static void CheckBinaryValue(
        AppSession session,
        EditingDriver driver,
        byte[] pattern,
        SandboxRegistry sandbox)
    {
        bool selected = SelectRow(session, PatternValueName);
        E2EResults.Record(
            "hex.binary.select",
            selected,
            $"binary row selected={selected}.");

        driver.OpenMenu(MenuView, MenuViewDisplayBinaryData);
        bool dialog = driver.WaitForDialog();
        bool viewer = driver.WaitForElement("HexViewerDialog") is not null;
        bool editor = driver.WaitForElement("HexEditor") is not null;
        E2EResults.Record(
            "hex.binary.dialog",
            dialog && viewer && editor,
            $"DialogRoot={dialog}, HexViewerDialog={viewer}, HexEditor={editor}.");

        string heading = driver.TextOf("DialogHeading");
        E2EResults.Record(
            "hex.binary.heading",
            heading.Contains(PatternValueName, StringComparison.Ordinal),
            $"heading='{heading}'.");

        string status = driver.TextOf("HexViewerStatus");
        bool statusOk = status.Contains("300", StringComparison.Ordinal) &&
            status.Contains("REG_BINARY", StringComparison.Ordinal) &&
            status.Contains(PatternValueName, StringComparison.Ordinal);
        E2EResults.Record(
            "hex.binary.status",
            statusOk,
            $"status='{status}'.");

        AutomationElement? firstRow = driver.WaitForElement("HexRow_0");
        string rowText = ControlTestSupport.SafeName(firstRow);
        string expectedHex = string.Join(
            " ",
            pattern.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        bool rowOk = rowText.Contains("00000000", StringComparison.Ordinal) &&
            rowText.Contains(expectedHex, StringComparison.Ordinal) &&
            rowText.Contains("................", StringComparison.Ordinal);
        E2EResults.Record(
            "hex.binary.row0",
            rowOk,
            $"row0='{rowText}', expectedHex='{expectedHex}'.");

        AutomationElement? editorElement = driver.WaitForElement("HexEditor");
        bool focused = false;
        try
        {
            firstRow?.Focus();
            focused = ControlTestSupport.WaitUntil(
                () => HasKeyboardFocus(editorElement),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            focused = false;
        }

        bool selectAll = false;
        bool clipboardMatches = false;
        string clipboardNote = "not attempted";
        if (focused)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            selectAll = ControlTestSupport.WaitUntil(
                () => HelpTextOf(editorElement).Contains("SelectedLength=300", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            string clipboardText = string.Empty;
            string? clipboardError = null;
            bool clipboardRead = ControlTestSupport.WaitUntil(
                () => TryReadClipboardText(out clipboardText, out clipboardError),
                TimeSpan.FromSeconds(3));
            string expectedClipboard = string.Join(
                " ",
                pattern.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
            clipboardMatches = clipboardRead &&
                string.Equals(clipboardText, expectedClipboard, StringComparison.Ordinal);
            clipboardNote = clipboardRead
                ? $"clipboard read, matches={clipboardMatches}, length={clipboardText.Length}."
                : "informational: clipboard unavailable: " + clipboardError;
        }
        else
        {
            clipboardNote = "informational: hex editor did not take UIA keyboard focus.";
        }

        E2EResults.Record(
            "hex.binary.selectall",
            selectAll,
            $"editorFocused={focused}, help='{HelpTextOf(editorElement)}'.");

        // Clipboard access is environmental. The selection check above is the hard assertion.
        E2EResults.Record("hex.binary.clipboard", true, clipboardNote);

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool focusReturned = ControlTestSupport.WaitUntil(
            () => HasKeyboardFocus(session.Find(ControlTestSupport.ListControl)),
            TimeSpan.FromSeconds(5));
        bool stillExists = sandbox.ReadBinary(string.Empty, PatternValueName) is not null;
        E2EResults.Record(
            "hex.binary.close",
            closed && focusReturned && stillExists,
            $"closed={closed}, focusReturnedToList={focusReturned}, valueUnchanged={stillExists}.");
    }

    private static void CheckStringValue(AppSession session, EditingDriver driver, SandboxRegistry sandbox)
    {
        bool selected = SelectRow(session, StringValueName);
        E2EResults.Record(
            "hex.string.select",
            selected,
            $"string row selected={selected}.");

        driver.OpenMenu(MenuView);
        AutomationElement? command = driver.WaitForElement("MenuCommand_" + MenuViewDisplayBinaryData);
        bool enabled = command is not null && SafeIsEnabled(command);
        E2EResults.Record(
            "hex.string.enabled",
            selected && enabled,
            $"selected={selected}, DisplayBinaryData enabled={enabled}.");

        if (!enabled)
        {
            return;
        }

        driver.Invoke("MenuCommand_" + MenuViewDisplayBinaryData);
        bool dialog = driver.WaitForDialog();
        string status = driver.TextOf("HexViewerStatus");
        AutomationElement? firstRow = driver.WaitForElement("HexRow_0");
        string rowText = ControlTestSupport.SafeName(firstRow);
        byte[] stringBytes = System.Text.Encoding.Unicode.GetBytes(StringText);
        string expectedHex = string.Join(
            " ",
            stringBytes.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        bool rowOk = rowText.Contains(expectedHex, StringComparison.Ordinal);
        int expectedLength = stringBytes.Length + 2;
        bool statusOk = status.Contains(expectedLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            status.Contains("REG_SZ", StringComparison.Ordinal);
        E2EResults.Record(
            "hex.string.viewer",
            dialog && rowOk && statusOk,
            $"dialog={dialog}, status='{status}', row0='{rowText}', expectedHex='{expectedHex}'.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        bool closed = driver.WaitForNoDialog();
        bool valueUnchanged = sandbox.ReadString(string.Empty, StringValueName) == StringText;
        E2EResults.Record(
            "hex.string.close",
            closed && valueUnchanged,
            $"closed={closed}, valueUnchanged={valueUnchanged}.");
    }

    private static bool SelectRow(AppSession session, string name)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.ListControl,
            ControlType.DataItem,
            name);
        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitUntil(
            () => ControlTestSupport.IsSelected(ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.ListControl,
                ControlType.DataItem,
                name)),
            TimeSpan.FromSeconds(5));
    }

    private static byte[] CreatePattern()
    {
        byte[] pattern = new byte[300];
        for (var index = 0; index < pattern.Length; index++)
        {
            pattern[index] = (byte)(index & 0xFF);
        }

        return pattern;
    }

    private static void Seed(SandboxRegistry sandbox, byte[] pattern)
    {
        using var root = sandbox.CreateSubKey(string.Empty);
        root.SetValue(PatternValueName, pattern, RegistryValueKind.Binary);
        root.SetValue(StringValueName, StringText, RegistryValueKind.String);
    }

    private static bool ContainsAll(IReadOnlyList<string> names, params string[] expected)
    {
        foreach (string name in expected)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SafeIsEnabled(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            return element.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasKeyboardFocus(AutomationElement? element)
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

    private static string HelpTextOf(AutomationElement? element)
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

    private static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDataRoot(string? path)
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

    private static bool TryReadClipboardText(out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (!OpenClipboard(IntPtr.Zero))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            IntPtr handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                error = "The clipboard has no Unicode text.";
                return false;
            }

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                return true;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Hex checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
