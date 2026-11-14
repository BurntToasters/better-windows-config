using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Hex;

/// <summary>
/// Drives the double-click hex editor with real keys: typing into an empty value, typing past the last
/// byte, Backspace before the caret, and overwrite-mode paste. Each value is accepted and read back.
/// </summary>
[TestClass]
public sealed class HexEditingFixesTests
{
    private const string EmptyName = "FixEmptyBinary";
    private const string AppendName = "FixAppendBinary";
    private const string BackspaceName = "FixBackspaceBinary";
    private const string PasteName = "FixPasteBinary";
    private const string PasteSource = "11 22 33 44";

    private static int _checkStart;

    private sealed record EditCase(
        string RowName,
        string CheckId,
        byte[] Initial,
        byte[] Expected,
        Func<bool> Keys,
        string Behavior);

    private static readonly EditCase[] Cases =
    {
        new(
            EmptyName,
            "hexfix.empty",
            Array.Empty<byte>(),
            new byte[] { 0xAB },
            TypeIntoEmpty,
            "Typing AB into a zero length value appends one byte 0xAB."),
        new(
            AppendName,
            "hexfix.append",
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0x01, 0x02, 0xFF, 0xEE },
            TypeAfterLastByte,
            "Ctrl+End then FFEE overwrites the last byte with FF and appends EE."),
        new(
            BackspaceName,
            "hexfix.backspace",
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0x01, 0x03 },
            BackspaceBeforeCaret,
            "Ctrl+End then Backspace removes the byte before the caret, 0x02, not the byte under it."),
        new(
            PasteName,
            "hexfix.paste",
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
            new byte[] { 0x11, 0x11, 0x22, 0x33, 0x44 },
            CopyAllThenPasteAtOffsetOne,
            "Overwrite mode paste of 4 bytes at offset 1 overwrites in place, giving 5 bytes; insert gives 8."),
    };

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
    public void HexEditingFixes_TypeBackspaceAndPasteUseRealKeys()
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
            E2EResults.Record("hexfix.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;

        try
        {
            sandbox.Cleanup();
            Seed(sandbox);
            dataRoot = HexEditingTests.CreateFreshDataRoot(config, "hexfix");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            bool seededRows = ControlTestSupport.WaitUntil(
                () => Cases.All(c => ControlTestSupport.ListRowNames(session).Contains(c.RowName, StringComparer.Ordinal)),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "hexfix.navigate",
                navigated && seededRows,
                $"navigated={navigated}, seededRows={seededRows}, rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && seededRows, "hexfix.sandbox.navigate");

            foreach (EditCase editCase in Cases)
            {
                RunCase(session, driver, sandbox, editCase);
            }

            var failures = E2EResults.FailedChecksSince(_checkStart);
            Assert.AreEqual(
                0,
                failures.Count,
                $"Hex editing fix checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            HexEditingTests.DeleteDataRoot(dataRoot);
        }
    }

    private static void RunCase(AppSession session, EditingDriver driver, SandboxRegistry sandbox, EditCase editCase)
    {
        _ = HexEditingTests.SelectRow(session, editCase.RowName);
        E2EGuards.RequireSandboxSelection(session, sandbox, editCase.CheckId + ".sandbox.open");

        bool opened = HexEditingTests.OpenEditor(session, driver, editCase.RowName);
        AutomationElement? editor = driver.WaitForElement("HexEditor");
        bool focused = false;
        if (opened)
        {
            try
            {
                editor?.Focus();
            }
            catch (Exception)
            {
                // The focus check below records the result.
            }

            focused = HexEditingTests.FocusEditor(session, null, editor);
        }

        bool keysSent = focused && editCase.Keys();
        bool lengthOk = keysSent && ControlTestSupport.WaitUntil(
            () => HexEditingTests.HelpTextOf(editor).Contains(
                $"Length={editCase.Expected.Length};",
                StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        string help = HexEditingTests.HelpTextOf(editor);

        bool invoked = driver.Invoke("DialogPrimary");
        bool closed = driver.WaitForNoDialog();
        bool saved = ControlTestSupport.WaitUntil(
            () => sandbox.ReadBinary(string.Empty, editCase.RowName) is { } bytes &&
                bytes.SequenceEqual(editCase.Expected),
            TimeSpan.FromSeconds(10));
        byte[]? actual = sandbox.ReadBinary(string.Empty, editCase.RowName);

        E2EResults.Record(
            editCase.CheckId,
            opened && focused && keysSent && lengthOk && invoked && closed && saved,
            $"{editCase.Behavior} opened={opened}, focused={focused}, keysSent={keysSent}, lengthOk={lengthOk}, " +
            $"invoked={invoked}, closed={closed}, saved={saved}, expected=[{Hex(editCase.Expected)}], " +
            $"actual=[{(actual is null ? "missing" : Hex(actual))}], help='{help}'.");
    }

    private static bool TypeIntoEmpty()
    {
        Keyboard.Type("AB");
        return true;
    }

    private static bool TypeAfterLastByte()
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Type("FFEE");
        return true;
    }

    private static bool BackspaceBeforeCaret()
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.END);
        Keyboard.Press(VirtualKeyShort.BACK);
        return true;
    }

    /// <summary>
    /// Copies the 4 byte value with Ctrl+A and Ctrl+C, then pastes at offset 1 in overwrite mode.
    /// The clipboard must hold the copied text first, so a missed copy fails the check.
    /// </summary>
    private static bool CopyAllThenPasteAtOffsetOne()
    {
        _ = ClipboardProbe.Clear();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        ClipboardProbe.Reading copied = ClipboardProbe.WaitForText(PasteSource, TimeSpan.FromSeconds(3));
        if (ClipboardProbe.Normalize(copied.Text) != PasteSource)
        {
            return false;
        }

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.HOME);
        Keyboard.Press(VirtualKeyShort.RIGHT);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
        return true;
    }

    private static void Seed(SandboxRegistry sandbox)
    {
        using var root = sandbox.CreateSubKey(string.Empty);
        foreach (EditCase editCase in Cases)
        {
            root.SetValue(editCase.RowName, editCase.Initial, RegistryValueKind.Binary);
        }
    }

    private static string Hex(byte[] bytes) =>
        string.Join(" ", bytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
