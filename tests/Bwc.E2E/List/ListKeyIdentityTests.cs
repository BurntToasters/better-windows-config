using System.Runtime.InteropServices;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.List;

/// <summary>
/// The value list is bound to one key. A rename that is open when the tree moves to another key commits to the key it
/// started in. A list that is loading a new key shows no rows of the old key, so no command can reach them. A value
/// whose name has an embedded NUL is listed but refused by every name-based command (list.md section 10 and 11).
/// Each run key is removed in finally.
/// </summary>
[TestClass]
public sealed class ListKeyIdentityTests
{
    private const string KeyA = "KeyIdentityA";
    private const string KeyB = "KeyIdentityB";
    private const string KeyNul = "KeyIdentityNul";
    private const string ValueX = "X";
    private const string RenamedX = "Renamed";
    private const string RenameBoxId = "ListRenameBox";
    private const string NulNamePrefix = "abc";
    private const int NulRowIndex = 2;
    private const string PlainAbc = "abc";
    private const string Keep1 = "Keep1";
    private const string Keep2 = "Keep2";
    private const string ErrorTextId = "DialogErrorText";
    private const string EditorInputId = "ValueEditorInput";
    private const string NulRefusalFragment = "NUL";
    private const int DelayedLoadMilliseconds = 6000;
    private const uint RegDword = 4;
    private const uint DwordValue = 0x1388;
    private const int NtSuccess = 0;

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void ListKeyIdentity_RenameCommitsToItsOwnKey()
    {
        _checkStart = E2EResults.CheckCount;
        RunSandbox(
            seed: sandbox =>
            {
                sandbox.SetString(KeyA, ValueX, "a-data");
                sandbox.SetString(KeyB, ValueX, "b-data");
            },
            environmentExtra: null,
            checkId: "listkey.rename",
            check: (session, sandbox, config) =>
            {
                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, KeyA);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "listkey.rename.navigate");
                bool listed = ControlTestSupport.WaitUntil(() => FindRow(session, ValueX) is not null, TimeSpan.FromSeconds(15));
                E2EGuards.RequireSandboxNavigation(session, sandbox, listed, "listkey.rename.list");

                AutomationElement? row = FindRow(session, ValueX);
                ControlTestSupport.Select(row);
                bool selected = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.IsSelected(FindRow(session, ValueX)),
                    TimeSpan.FromSeconds(5));

                using var driver = new EditingDriver(session);
                bool renameOpen = selected && driver.TypeKeys("F2") && ControlTestSupport.WaitUntil(
                    () => session.Find(RenameBoxId) is not null,
                    TimeSpan.FromSeconds(5));
                bool typed = renameOpen && driver.TypeKeys(RenamedX);

                // The tree row is selected by the right-click before focus moves, so the list leaves key A at once.
                AutomationElement? treeB = ControlTestSupport.FindRowByName(
                    session,
                    ControlTestSupport.TreeControl,
                    ControlType.TreeItem,
                    KeyB);
                bool rightClicked = typed && treeB is not null;
                if (rightClicked)
                {
                    treeB!.RightClick();
                }

                bool committed = rightClicked && ControlTestSupport.WaitUntil(
                    () => sandbox.ReadString(KeyA, RenamedX) == "a-data" && sandbox.ReadString(KeyA, ValueX) is null,
                    TimeSpan.FromSeconds(10));
                _ = driver.TypeKeys("Escape");

                string? bX = sandbox.ReadString(KeyB, ValueX);
                string? bRenamed = sandbox.ReadString(KeyB, RenamedX);
                E2EResults.Record(
                    "listkey.rename.commits-to-source",
                    selected && renameOpen && typed && rightClicked && committed,
                    $"selected={selected}, renameOpen={renameOpen}, typed={typed}, rightClicked={rightClicked}, " +
                    $"committed to A={committed}.");
                E2EResults.Record(
                    "listkey.rename.key-b-untouched",
                    bX == "b-data" && bRenamed is null,
                    $"B's X='{bX ?? "(missing)"}' (expected 'b-data'), B's {RenamedX}={(bRenamed is null ? "absent" : "present")}.");
            });

        FailIfNeeded("List key identity rename checks failed");
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void ListKeyIdentity_DeleteWhileKeyBLoadsTouchesNothing()
    {
        _checkStart = E2EResults.CheckCount;
        string delay = $@"HKEY_CURRENT_USER\Software\BWC-E2E\{GuiGate.Config.RunId}\{KeyB}|{DelayedLoadMilliseconds}";
        RunSandbox(
            seed: sandbox =>
            {
                sandbox.SetString(KeyA, ValueX, "a-data");
                sandbox.SetString(KeyB, ValueX, "b-data");
            },
            environmentExtra: ("BWC_E2E_DELAY_VALUE_LOAD", delay),
            checkId: "listkey.delete",
            check: (session, sandbox, config) =>
            {
                bool navigatedA = ControlTestSupport.NavigateToSandboxKey(session, config, KeyA);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigatedA, "listkey.delete.navigate-a");
                bool listedA = ControlTestSupport.WaitUntil(() => FindRow(session, ValueX) is not null, TimeSpan.FromSeconds(15));
                E2EGuards.RequireSandboxNavigation(session, sandbox, listedA, "listkey.delete.list-a");
                ControlTestSupport.Select(FindRow(session, ValueX));

                // Key B's rows are held back by the E2E delay. The tree has moved, so the rows of A must be gone.
                bool navigatedB = ControlTestSupport.NavigateToSandboxKey(session, config, KeyB);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigatedB, "listkey.delete.navigate-b");
                string[] duringLoad = ControlTestSupport.ListRowNames(session);
                bool aRowsGone = !duringLoad.Contains(ValueX, StringComparer.Ordinal);

                // Focus the list on its empty area (which clears the selection) and press Delete. Nothing is selected.
                AutomationElement? list = session.Find(ControlTestSupport.ListControl);
                list?.Click();
                using var driver = new EditingDriver(session);
                // Delete is sent only when the list has the focus. With the tree focused it would delete key B itself.
                bool deleteSent = !ControlTestSupport.IsTreeKeyboardFocused(session, KeyB) && driver.TypeKeys("DELETE");
                _ = driver.WaitForNoDialog(TimeSpan.FromSeconds(2));

                bool bLoaded = ControlTestSupport.WaitUntil(
                    () => FindRow(session, ValueX) is not null,
                    TimeSpan.FromSeconds(15));
                string? bX = sandbox.ReadString(KeyB, ValueX);
                string? aX = sandbox.ReadString(KeyA, ValueX);
                E2EResults.Record(
                    "listkey.delete.key-b-survives",
                    aRowsGone && deleteSent && bLoaded && bX == "b-data" && aX == "a-data",
                    $"A's rows hidden while B loads={aRowsGone}, Delete sent={deleteSent}, B loaded={bLoaded}, " +
                    $"B's X='{bX ?? "(missing)"}', A's X='{aX ?? "(missing)"}'.");
            });

        FailIfNeeded("List key identity delete checks failed");
    }

    [TestMethod]
    [TestCategory("List")]
    [TestCategory("Mutating")]
    public void ListKeyIdentity_NulNamedValueIsRefused()
    {
        _checkStart = E2EResults.CheckCount;
        RunSandbox(
            seed: sandbox =>
            {
                sandbox.SetString(KeyNul, Keep1, "one");
                sandbox.SetString(KeyNul, PlainAbc, "plain");
                sandbox.SetString(KeyNul, Keep2, "two");
                SeedNulNamedDword(sandbox, KeyNul, NulNamePrefix + "\0def", DwordValue);
            },
            environmentExtra: null,
            checkId: "listkey.nul",
            check: (session, sandbox, config) =>
            {
                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, KeyNul);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "listkey.nul.navigate");
                bool listed = ControlTestSupport.WaitUntil(() => FindRow(session, Keep2) is not null, TimeSpan.FromSeconds(15));
                E2EGuards.RequireSandboxNavigation(session, sandbox, listed, "listkey.nul.list");

                using var driver = new EditingDriver(session);
                AutomationElement? nulRow = FindNulRow(session);
                ControlTestSupport.Select(nulRow);
                bool selected = nulRow is not null && ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.IsSelected(FindNulRow(session)),
                    TimeSpan.FromSeconds(5));

                // Modify is refused with the invalid-name text, and no editor opens.
                bool modifyOpened = selected && driver.TypeKeys("Enter") && driver.WaitForDialog();
                string modifyText = modifyOpened ? driver.TextOf(ErrorTextId) : string.Empty;
                bool modifyRefused = modifyOpened && modifyText.Contains(NulRefusalFragment, StringComparison.Ordinal);
                if (modifyOpened)
                {
                    _ = driver.DismissErrorBox();
                }

                bool editorAbsent = session.Find(EditorInputId) is null;
                string? plainBefore = sandbox.ReadString(KeyNul, PlainAbc);

                // A multi-delete of Keep1, the NUL-named value and Keep2 (a contiguous range in classic order) deletes
                // Keep1 and Keep2, refuses the NUL-named value with the same text, and refreshes the list.
                ControlTestSupport.Select(FindRow(session, Keep1));
                bool rangeSelected = false;
                if (selected)
                {
                    ControlTestSupport.Select(FindNulRow(session));
                    RawKeyboard.WithHeld(RawKeyboard.Shift, () =>
                    {
                        RawKeyboard.Tap(RawKeyboard.Down);
                        RawKeyboard.Tap(RawKeyboard.Down);
                    });
                    rangeSelected = ControlTestSupport.WaitUntil(() => SelectedCount(session) == 3, TimeSpan.FromSeconds(5));
                }

                bool deleteSent = rangeSelected && !ControlTestSupport.IsTreeKeyboardFocused(session, KeyNul) && driver.TypeKeys("DELETE");
                bool confirmed = deleteSent && driver.WaitForDialog() && driver.Invoke("DialogPrimary");
                bool refusalShown = confirmed && ControlTestSupport.WaitUntil(
                    () => driver.TextOf(ErrorTextId).Contains(NulRefusalFragment, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10));
                if (refusalShown)
                {
                    _ = driver.DismissErrorBox();
                }

                bool refreshed = ControlTestSupport.WaitUntil(
                    () => FindRow(session, Keep1) is null && FindRow(session, Keep2) is null,
                    TimeSpan.FromSeconds(10));
                string? keep1 = sandbox.ReadString(KeyNul, Keep1);
                string? keep2 = sandbox.ReadString(KeyNul, Keep2);
                string? plainAfter = sandbox.ReadString(KeyNul, PlainAbc);
                bool nulKept = NulNamedValueExists(sandbox, KeyNul);
                E2EResults.Record(
                    "listkey.nul.modify-refused",
                    selected && modifyRefused && editorAbsent && plainBefore == "plain",
                    $"selected={selected}, Modify dialog={modifyOpened}, NUL text={modifyRefused}, editor absent={editorAbsent}, " +
                    $"plain abc='{plainBefore ?? "(missing)"}'.");
                E2EResults.Record(
                    "listkey.nul.multidelete-continues",
                    rangeSelected && deleteSent && confirmed && refusalShown && keep1 is null && keep2 is null && nulKept &&
                        plainAfter == "plain" && refreshed,
                    $"range of 3 selected={rangeSelected}, confirmed={confirmed}, refusal shown={refusalShown}, " +
                    $"Keep1={(keep1 is null ? "absent" : "present")}, Keep2={(keep2 is null ? "absent" : "present")}, " +
                    $"NUL-named value kept={nulKept}, plain abc='{plainAfter ?? "(missing)"}', list refreshed={refreshed}.");
            });

        FailIfNeeded("List key identity NUL checks failed");
    }

    /// <summary>
    /// Seeds one key, opens Registry Editor on the run, runs the check, and removes the run key in finally.
    /// The optional environment entry is passed to the app for one test.
    /// </summary>
    private static void RunSandbox(
        Action<SandboxRegistry> seed,
        (string Name, string Value)? environmentExtra,
        string checkId,
        Action<AppSession, SandboxRegistry, E2EConfig> check)
    {
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record(checkId + ".publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            seed(sandbox);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("list-key-identity"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };
            if (environmentExtra is { } extra)
            {
                environment[extra.Name] = extra.Value;
            }

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            check(session, sandbox, config);
            session.Close();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    /// <summary>The list row with this name, read through UI Automation, so the name is the displayed one.</summary>
    private static AutomationElement? FindRow(AppSession session, string name) =>
        ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .FirstOrDefault(row => string.Equals(ControlTestSupport.SafeName(row), name, StringComparison.Ordinal));

    /// <summary>
    /// The list row of the NUL-named value. The list sorts (Default), abc, the NUL-named value, Keep1, Keep2, so it is the
    /// row with index 2. The index is used because UI Automation may report the name without the NUL.
    /// </summary>
    private static AutomationElement? FindNulRow(AppSession session) =>
        ControlTestSupport.FindRowById(session, ControlTestSupport.ListRowPrefix + NulRowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static int SelectedCount(AppSession session) =>
        ControlTestSupport.Rows(session, ControlTestSupport.ListControl, ControlType.DataItem)
            .Count(ControlTestSupport.IsSelected);

    /// <summary>
    /// Creates a value whose name has an embedded NUL. RegSetValueExW cannot pass one, so this calls
    /// NtSetValueKey with a counted name, on a key under the run root only.
    /// </summary>
    private static void SeedNulNamedDword(SandboxRegistry sandbox, string relativeKey, string name, uint value)
    {
        using var key = sandbox.CreateSubKey(relativeKey);
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr dataBuffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(dataBuffer, unchecked((int)value));
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)(name.Length * 2 + 2)),
                Buffer = nameBuffer,
            };
            int status = NtSetValueKey(key.Handle.DangerousGetHandle(), ref unicodeName, 0, RegDword, dataBuffer, sizeof(uint));
            if (status != NtSuccess)
            {
                throw new InvalidOperationException($"NtSetValueKey failed with status 0x{status:X8}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(dataBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>True when the key holds a value whose name still contains a NUL.</summary>
    private static bool NulNamedValueExists(SandboxRegistry sandbox, string relativeKey)
    {
        using var key = sandbox.CreateSubKey(relativeKey);
        return key.GetValueNames().Any(name => name.Contains('\0'));
    }

    private static void FailIfNeeded(string title)
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(0, failures.Count, $"{title}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetValueKey(
        IntPtr keyHandle,
        ref UnicodeString valueName,
        uint titleIndex,
        uint type,
        IntPtr data,
        uint dataSize);
}
