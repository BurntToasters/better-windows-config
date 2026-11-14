using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

namespace Bwc.E2E.Find;

/// <summary>
/// Drives the classic Find sequence against an isolated sandbox subtree: Find opens, Find Next
/// closes it and shows the separate progress dialog, the search runs behind that dialog, and the
/// result is either a selection or the finished message box. The run root is the only search root
/// because the process receives BWC_E2E_SANDBOX_ROOT, so the checks do not depend on unrelated
/// machine registry contents and the no-hit search finishes in bounded time.
/// </summary>
[TestClass]
public sealed class FindTests
{
    private const string ValueHitName = "DataNeedle";
    private const string DataHitName = "DataNeedle";
    private const string DataSearchText = "needle";
    private const string MissingText = "NoSuchText";
    private const string PersistText = "PersistMe";
    private const string FinishedMessage = "Finished searching through the registry.";
    private const string SearchingMessage = "Searching the registry...";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Find")]
    [TestCategory("E2EOnly")]
    public void Find_FollowsTheClassicTwoDialogSequence()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = SeedSandbox(config);
        string dataRoot = CreateFreshDataRoot(config, "find");
        string visualDir = Path.Combine(config.ArtifactsDir, "visual", "find");
        Directory.CreateDirectory(visualDir);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        AppletFindFlags beforeFindFlags = ReadFindFlags();

        try
        {
            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            using (var driver = new FindDialogDriver(session))
            {
                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
                Record("find.navigate.runRoot", navigated, $"navigated to run root={navigated}.");

                // F3 with no prior query opens Find. A first Find Next must not start an invisible search.
                bool f3Opened = SendAccelerator(session, "F3") && driver.WaitForFindOpen();
                Record("find.f3.noPriorOpens", f3Opened, $"Find after F3 opened={f3Opened}.");
                if (!f3Opened)
                {
                    FailIfNeeded();
                    return;
                }

                bool ownerDisabled = ControlTestSupport.WaitUntil(
                    () => !session.IsEnabled,
                    TimeSpan.FromSeconds(3));
                bool stillOpen = !ControlTestSupport.WaitUntil(
                    () => !driver.FindOpen,
                    TimeSpan.FromMilliseconds(700));
                Record(
                    "find.open.staysOpen",
                    ownerDisabled && stillOpen,
                    $"owner disabled={ownerDisabled}, Find still open={stillOpen}.");

                bool controls = driver.ControlPresent("FindText") &&
                    driver.ControlPresent("FindKeys") &&
                    driver.ControlPresent("FindValues") &&
                    driver.ControlPresent("FindData") &&
                    driver.ControlPresent("FindWholeString") &&
                    driver.ControlPresent("FindSubtree") &&
                    driver.ControlPresent("DialogPrimary") &&
                    driver.ControlPresent("DialogSecondary");
                bool defaults = driver.IsChecked("FindKeys") &&
                    driver.IsChecked("FindValues") &&
                    driver.IsChecked("FindData") &&
                    !driver.IsChecked("FindWholeString") &&
                    driver.IsChecked("FindSubtree") &&
                    !driver.IsOffscreen("FindSubtree");
                Record(
                    "find.controls.present",
                    controls && defaults,
                    $"controls={controls}, Keys/Values/Data/Subtree defaults={defaults}.");

                string findShot = SaveScreenshot(
                    driver.FindWindowElement,
                    Path.Combine(visualDir, "find-dialog.png"));
                E2EResults.Note("find.visual.dialog", $"Find screenshot='{findShot}'.");

                // Alt+K reaches the Keys checkbox through its template mnemonic, then again restores it.
                bool mnemonicToggle = driver.SendAltMnemonic('K') &&
                    driver.WaitForChecked("FindKeys", false);
                bool mnemonicRestore = mnemonicToggle &&
                    driver.SendAltMnemonic('K') &&
                    driver.WaitForChecked("FindKeys", true);
                Record(
                    "find.mnemonics.keys",
                    mnemonicToggle && mnemonicRestore,
                    $"Alt+K toggled={mnemonicToggle}, restored={mnemonicRestore}.");

                bool escapeSent = driver.SendFindKey(VirtualKeyShort.ESCAPE);
                bool escapeClosed = escapeSent && driver.WaitForFindClosed();
                bool ownerRestored = escapeClosed && WaitForOwnerEnabled(session);
                Record(
                    "find.open.cancelCloses",
                    escapeClosed && ownerRestored,
                    $"Escape closed={escapeClosed}, owner restored={ownerRestored}.");

                // Keys search through real typing and a real mouse click on Find Next.
                bool ctrlFOpened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForFindOpen();
                Record("find.ctrlF.open", ctrlFOpened, $"Ctrl+F opened={ctrlFOpened}.");
                if (!ctrlFOpened)
                {
                    FailIfNeeded();
                    return;
                }

                bool preparedKeys = driver.SetChecked("FindValues", false) &&
                    driver.SetChecked("FindData", false) &&
                    driver.SetChecked("FindWholeString", false) &&
                    driver.TypeFindText("Alpha");
                Record("find.keys.prepare", preparedKeys, $"FindText='{driver.TextOf("FindText")}'.");

                // The guard makes Find the foreground window before the click, and records a failed check itself
                // when it cannot. This line is harness state, so it is a note.
                bool foreground = driver.EnsureFindForeground();
                E2EResults.Note("find.keys.foreground", $"Find foreground={foreground} before Find Next.");

                bool nextClicked = foreground && driver.Click("DialogPrimary");
                bool findClosed = nextClicked && driver.WaitForFindClosed();
                bool progressOpened = findClosed && driver.WaitForProgressOpen();
                bool progressText = progressOpened &&
                    string.Equals(driver.ProgressText(), SearchingMessage, StringComparison.Ordinal);
                Record(
                    "find.next.closes",
                    nextClicked && findClosed,
                    $"Find Next clicked={nextClicked}, Find closed={findClosed}.");

                string progressShot = progressOpened
                    ? SaveScreenshot(
                        driver.ProgressWindowElement,
                        Path.Combine(visualDir, "find-progress.png"))
                    : string.Empty;
                Record(
                    "find.progress.opens",
                    progressOpened && progressText && driver.ControlPresent("DialogSecondary"),
                    $"progress opened={progressOpened}, text='{driver.ProgressText()}'.");
                E2EResults.Note("find.visual.progress", $"progress screenshot='{progressShot}'.");

                bool progressClosed = driver.WaitForProgressClosed(TimeSpan.FromSeconds(10));
                bool alphaSelected = ControlTestSupport.WaitForTreeSelection(
                    session,
                    "Alpha",
                    TimeSpan.FromSeconds(30));
                bool findStayedClosed = !ControlTestSupport.WaitUntil(
                    () => driver.FindOpen,
                    TimeSpan.FromMilliseconds(700));
                Record(
                    "find.keys.first",
                    progressClosed && alphaSelected && findStayedClosed,
                    $"progress closed={progressClosed}, Alpha selected={alphaSelected}, Find reopened={!findStayedClosed}.");

                // F3 repeats the last query without reopening Find.
                bool f3Sent = WaitForOwnerEnabled(session) && SendAccelerator(session, "F3");
                bool f3Progress = f3Sent && driver.WaitForProgressOpen();
                bool findHiddenDuringF3 = f3Progress && !driver.FindOpen;
                Record(
                    "find.f3.progress",
                    f3Progress && findHiddenDuringF3,
                    $"F3 progress={f3Progress}, Find hidden={findHiddenDuringF3}.");

                bool f3ProgressClosed = driver.WaitForProgressClosed(TimeSpan.FromSeconds(10));
                bool alphabetSelected = ControlTestSupport.WaitForTreeSelection(
                    session,
                    "Alphabet",
                    TimeSpan.FromSeconds(30));
                Record(
                    "find.f3.next",
                    f3ProgressClosed && alphabetSelected,
                    $"progress closed={f3ProgressClosed}, Alphabet selected={alphabetSelected}.");

                // Values search: the value name matches and its row is selected.
                bool resetForValue = WaitForOwnerEnabled(session) && SelectRunRoot(session, config);
                bool valueOpened = resetForValue &&
                    SendAccelerator(session, "CONTROL+KEY_F") &&
                    driver.WaitForFindOpen();
                bool preparedValue = valueOpened &&
                    driver.SetChecked("FindKeys", false) &&
                    driver.SetChecked("FindValues", true) &&
                    driver.SetChecked("FindData", false) &&
                    driver.TypeFindText(ValueHitName);
                bool valueClicked = preparedValue && driver.Click("DialogPrimary");
                bool valueFindClosed = valueClicked && driver.WaitForFindClosed();
                bool valueProgressClosed = valueFindClosed &&
                    driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
                bool valueRowSelected = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.IsSelected(
                        ControlTestSupport.FindRowByName(
                            session,
                            ControlTestSupport.ListControl,
                            ControlType.DataItem,
                            ValueHitName)),
                    TimeSpan.FromSeconds(30));
                Record(
                    "find.values.hit",
                    valueFindClosed && valueProgressClosed && valueRowSelected,
                    $"Find closed={valueFindClosed}, progress closed={valueProgressClosed}, row '{ValueHitName}' selected={valueRowSelected}.");

                // Data search: the value data matches and maps back to its row.
                bool resetForData = SelectRunRoot(session, config);
                bool dataOpened = resetForData &&
                    SendAccelerator(session, "CONTROL+KEY_F") &&
                    driver.WaitForFindOpen();
                bool preparedData = dataOpened &&
                    driver.SetChecked("FindKeys", false) &&
                    driver.SetChecked("FindValues", false) &&
                    driver.SetChecked("FindData", true) &&
                    driver.TypeFindText(DataSearchText);
                bool dataClicked = preparedData && driver.SendFindKey(VirtualKeyShort.RETURN);
                bool dataFindClosed = dataClicked && driver.WaitForFindClosed();
                bool dataProgressClosed = dataFindClosed &&
                    driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
                bool dataRowSelected = ControlTestSupport.WaitUntil(
                    () => ControlTestSupport.IsSelected(
                        ControlTestSupport.FindRowByName(
                            session,
                            ControlTestSupport.ListControl,
                            ControlType.DataItem,
                            DataHitName)),
                    TimeSpan.FromSeconds(30));
                Record(
                    "find.data.hit",
                    dataFindClosed && dataProgressClosed && dataRowSelected,
                    $"Find closed={dataFindClosed}, progress closed={dataProgressClosed}, row '{DataHitName}' selected={dataRowSelected}.");

                // Cancel while the progress dialog is open suppresses the hit and restores the owner.
                bool resetForCancel = SelectRunRoot(session, config);
                bool cancelOpened = resetForCancel &&
                    SendAccelerator(session, "CONTROL+KEY_F") &&
                    driver.WaitForFindOpen();
                bool preparedCancel = cancelOpened &&
                    driver.SetChecked("FindKeys", true) &&
                    driver.SetChecked("FindValues", false) &&
                    driver.SetChecked("FindData", false) &&
                    driver.TypeFindText("Alpha");
                bool cancelClicked = preparedCancel && driver.Click("DialogPrimary");
                bool cancelProgressOpened = cancelClicked && driver.WaitForProgressOpen();
                bool cancelForeground = cancelProgressOpened && driver.EnsureProgressForeground();
                bool cancelInvoked = cancelForeground && driver.Click("DialogSecondary");
                bool cancelProgressClosed = cancelInvoked && driver.WaitForProgressClosed();
                bool cancelOwnerRestored = cancelProgressClosed && WaitForOwnerEnabled(session);
                bool cancelNoHit = cancelOwnerRestored &&
                    ControlTestSupport.WaitForTreeSelection(session, config.RunId, TimeSpan.FromSeconds(5));
                bool cancelNoMessage = !driver.MessageOpen;
                Record(
                    "find.cancel",
                    cancelProgressOpened && cancelInvoked && cancelProgressClosed && cancelOwnerRestored &&
                        cancelNoHit && cancelNoMessage,
                    $"progress={cancelProgressOpened}, cancel={cancelInvoked}, closed={cancelProgressClosed}, owner={cancelOwnerRestored}, hit suppressed={cancelNoHit}, no message={cancelNoMessage}.");

                // No hit: progress closes, then the classic finished message box appears.
                bool resetForMiss = SelectRunRoot(session, config);
                bool missOpened = resetForMiss &&
                    SendAccelerator(session, "CONTROL+KEY_F") &&
                    driver.WaitForFindOpen();
                bool preparedMiss = missOpened &&
                    driver.SetChecked("FindKeys", true) &&
                    driver.SetChecked("FindValues", false) &&
                    driver.SetChecked("FindData", false) &&
                    driver.TypeFindText(MissingText);
                bool missClicked = preparedMiss && driver.Click("DialogPrimary");
                bool missFindClosed = missClicked && driver.WaitForFindClosed();
                bool missProgressOpened = missFindClosed && driver.WaitForProgressOpen();
                bool missProgressClosed = missProgressOpened &&
                    driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
                bool missMessage = missProgressClosed && driver.WaitForMessageText(
                    FinishedMessage,
                    TimeSpan.FromSeconds(15));
                string caption = missMessage ? driver.MessageCaption() : "(missing)";
                Record(
                    "find.miss.finished",
                    missFindClosed && missProgressOpened && missProgressClosed && missMessage &&
                        string.Equals(caption, "Find", StringComparison.Ordinal),
                    $"Find closed={missFindClosed}, progress={missProgressOpened}, closed={missProgressClosed}, message='{driver.MessageText()}', caption='{caption}'.");

                string finishedShot = missMessage
                    ? SaveScreenshot(
                        driver.MessageWindowElement,
                        Path.Combine(visualDir, "find-finished.png"))
                    : string.Empty;
                E2EResults.Note("find.visual.finished", $"finished screenshot='{finishedShot}'.");

                bool dismissed = missMessage && driver.DismissMessage();
                Record("find.miss.dismiss", dismissed, $"message dismissed={dismissed}.");

                // A second Find Next with the exhausted query reports the same finished message. No wrap.
                bool repeatSent = dismissed && SendAccelerator(session, "F3");
                bool repeatFinished = repeatSent && driver.WaitForMessageText(
                    FinishedMessage,
                    TimeSpan.FromSeconds(10));
                bool repeatCaption = repeatFinished && string.Equals(
                    driver.MessageCaption(),
                    "Find",
                    StringComparison.Ordinal);
                Record(
                    "find.miss.noWrap",
                    repeatFinished && repeatCaption,
                    $"second finished message={repeatFinished}, caption='{driver.MessageCaption()}'.");
                _ = driver.DismissMessage();

                // Persist a draft and its flags, then cancel without searching.
                bool persistOpened = SendAccelerator(session, "CONTROL+KEY_F") &&
                    driver.WaitForFindOpen();
                bool persistPrepared = persistOpened &&
                    driver.SetChecked("FindKeys", false) &&
                    driver.SetChecked("FindValues", true) &&
                    driver.SetChecked("FindData", false) &&
                    driver.SetChecked("FindWholeString", true) &&
                    driver.SetChecked("FindSubtree", false) &&
                    driver.TypeFindText(PersistText);
                bool persistCancel = persistPrepared && driver.Click("DialogSecondary");
                bool persistClosed = persistCancel && driver.WaitForFindClosed();
                Record(
                    "find.persist.draft",
                    persistPrepared && persistClosed,
                    $"draft text='{driver.TextOf("FindText")}', Find closed={persistClosed}.");
            }

            AppletFindFlags afterFirstRun = ReadFindFlags();
            Record(
                "find.findFlags.notWritten",
                beforeFindFlags.Equals(afterFirstRun),
                $"FindFlags before={beforeFindFlags}, after={afterFirstRun}.");

            // Restart the app against the same settings root. The dialog text and flags must return.
            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            using (var driver = new FindDialogDriver(session))
            {
                _ = ControlTestSupport.NavigateToSandboxKey(session, config);

                bool reopened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForFindOpen();
                string restoredText = reopened ? driver.TextOf("FindText") : "(missing)";
                bool restoredFlags = reopened &&
                    !driver.IsChecked("FindKeys") &&
                    driver.IsChecked("FindValues") &&
                    !driver.IsChecked("FindData") &&
                    driver.IsChecked("FindWholeString") &&
                    !driver.IsChecked("FindSubtree");
                Record(
                    "find.persist.restored",
                    reopened &&
                        string.Equals(restoredText, PersistText, StringComparison.Ordinal) &&
                        restoredFlags,
                    $"text='{restoredText}', flags restored={restoredFlags}.");

                bool restartClosed = reopened && driver.SendFindKey(VirtualKeyShort.ESCAPE) &&
                    driver.WaitForFindClosed();
                Record(
                    "find.persist.close",
                    restartClosed,
                    $"Find closed after restart={restartClosed}.");
            }

            AppletFindFlags afterRestart = ReadFindFlags();
            Record(
                "find.findFlags.notWrittenAfterRestart",
                beforeFindFlags.Equals(afterRestart),
                $"FindFlags before={beforeFindFlags}, after={afterRestart}.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    /// <summary>
    /// Edit &gt; Find (menu command 662) is opened with real keyboard input only: Alt+E opens the Edit menu, the F
    /// mnemonic invokes Find, and the seeded value is found through the Find dialog. The menu item must exist with
    /// command id 662 before the mnemonic is sent, so a menu that lost the command cannot pass.
    /// </summary>
    [TestMethod]
    [TestCategory("Find")]
    [TestCategory("E2EOnly")]
    public void Find_EditMenuFindRunsFromTheKeyboard()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.menu.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = SeedSandbox(config);
        string dataRoot = CreateFreshDataRoot(config, "findmenu");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        try
        {
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new FindDialogDriver(session);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            Record("find.menu.navigate", navigated, $"navigated to run root={navigated}.");
            if (!navigated)
            {
                FailIfNeeded();
                return;
            }

            // Alt+E with real keys opens the Edit menu. The Find command must be in it with id 662.
            bool editOpened = SendAccelerator(session, "ALT+KEY_E");
            bool findItem = editOpened && ControlTestSupport.WaitUntil(
                () => session.Find("MenuCommand_662") is not null,
                TimeSpan.FromSeconds(5));
            Record(
                "find.menu.item",
                findItem,
                $"Alt+E sent={editOpened}; Edit menu item with command id 662 (Find) present={findItem}.");

            // The F mnemonic invokes Find, which opens the Find dialog from the menu, not from Ctrl+F.
            bool mnemonicSent = findItem && SendAccelerator(session, "KEY_F");
            bool findOpened = mnemonicSent && driver.WaitForFindOpen();
            Record(
                "find.menu.mnemonic",
                findOpened,
                $"F mnemonic sent={mnemonicSent}; Find dialog opened from the Edit menu={findOpened}.");
            if (!findOpened)
            {
                FailIfNeeded();
                return;
            }

            bool prepared = driver.SetChecked("FindKeys", false) &&
                driver.SetChecked("FindValues", true) &&
                driver.SetChecked("FindData", false) &&
                driver.TypeFindText(ValueHitName);
            bool clicked = prepared && driver.Click("DialogPrimary");
            bool findClosed = clicked && driver.WaitForFindClosed();
            bool progressClosed = findClosed && driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
            bool rowSelected = progressClosed && ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.ListControl,
                        ControlType.DataItem,
                        ValueHitName)),
                TimeSpan.FromSeconds(30));
            Record(
                "find.menu.hit",
                prepared && clicked && findClosed && progressClosed && rowSelected,
                $"prepared={prepared}, clicked={clicked}, Find closed={findClosed}, progress closed={progressClosed}, " +
                $"row '{ValueHitName}' selected={rowSelected}.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    /// <summary>
    /// A key search that reaches the end shows Finished. F3 repeats Finished only while the selection stays where
    /// the search ended. After the selection moves to an earlier key, F3 searches again and finds the next hit.
    /// The tree keeps the keyboard after the progress dialog closes.
    /// </summary>
    [TestMethod]
    [TestCategory("Find")]
    [TestCategory("E2EOnly")]
    public void Find_FinishedIsStickyUntilTheSelectionMoves()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.sticky.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = SeedSandbox(config);
        string dataRoot = CreateFreshDataRoot(config, "findsticky");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
            ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
        };

        try
        {
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new FindDialogDriver(session);
            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            Record("find.sticky.navigate", navigated, $"navigated to run root={navigated}.");
            if (!navigated)
            {
                FailIfNeeded();
                return;
            }

            // Keys named Alpha in the run root: Alpha, then Alphabet, then no more hits.
            bool opened = SendAccelerator(session, "CONTROL+KEY_F") && driver.WaitForFindOpen();
            bool prepared = opened &&
                driver.SetChecked("FindKeys", true) &&
                driver.SetChecked("FindValues", false) &&
                driver.SetChecked("FindData", false) &&
                driver.TypeFindText("Alpha");
            bool clicked = prepared && driver.Click("DialogPrimary");
            bool firstFindClosed = clicked && driver.WaitForFindClosed();
            bool firstProgressClosed = firstFindClosed && driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
            bool alphaSelected = firstProgressClosed &&
                ControlTestSupport.WaitForTreeSelection(session, "Alpha", TimeSpan.FromSeconds(30));
            bool treeFocused = alphaSelected &&
                Win32.IsForeground(session.MainHwnd) &&
                ControlTestSupport.WaitForTreeKeyboardFocus(session, "Alpha", TimeSpan.FromSeconds(5));
            Record(
                "find.sticky.first",
                firstFindClosed && firstProgressClosed && alphaSelected,
                $"Find closed={firstFindClosed}, progress closed={firstProgressClosed}, Alpha selected={alphaSelected}.");
            Record(
                "find.focus.treeAfterProgress",
                treeFocused,
                $"main window foreground and tree row 'Alpha' has keyboard focus={treeFocused}.");

            // F3 finds Alphabet, then F3 runs out of hits and shows Finished with the progress dialog first.
            bool f3Alphabet = alphaSelected && WaitForOwnerEnabled(session) && SendAccelerator(session, "F3") &&
                driver.WaitForProgressOpen() &&
                driver.WaitForProgressClosed(TimeSpan.FromSeconds(15)) &&
                ControlTestSupport.WaitForTreeSelection(session, "Alphabet", TimeSpan.FromSeconds(30));
            bool f3Finished = f3Alphabet && WaitForOwnerEnabled(session) && SendAccelerator(session, "F3") &&
                driver.WaitForProgressOpen() &&
                driver.WaitForProgressClosed(TimeSpan.FromSeconds(15)) &&
                driver.WaitForMessageText(FinishedMessage, TimeSpan.FromSeconds(15));
            bool dismissed = f3Finished && driver.DismissMessage();
            Record(
                "find.sticky.finished",
                f3Alphabet && f3Finished && dismissed,
                $"F3 found Alphabet={f3Alphabet}, F3 showed Finished={f3Finished}, dismissed={dismissed}.");

            // The selection is still on Alphabet, where the search finished. F3 shows Finished again, with no progress.
            bool repeatSent = dismissed && WaitForOwnerEnabled(session) && SendAccelerator(session, "F3");
            bool repeatFinished = repeatSent && driver.WaitForMessageText(FinishedMessage, TimeSpan.FromSeconds(10));
            bool repeatDismissed = repeatFinished && driver.DismissMessage();
            Record(
                "find.sticky.repeat",
                repeatFinished && repeatDismissed,
                $"F3 at the finish point showed Finished={repeatFinished}, dismissed={repeatDismissed}.");

            // Move the selection to the earlier key Alpha. F3 now searches again from there and finds Alphabet.
            bool moved = repeatDismissed && ClickTreeRow(session, "Alpha");
            bool searchedAgain = moved && WaitForOwnerEnabled(session) && SendAccelerator(session, "F3") &&
                driver.WaitForProgressOpen() &&
                driver.WaitForProgressClosed(TimeSpan.FromSeconds(15));
            bool nextHit = searchedAgain &&
                ControlTestSupport.WaitForTreeSelection(session, "Alphabet", TimeSpan.FromSeconds(30));
            bool messageAbsent = !driver.MessageOpen;
            Record(
                "find.sticky.movedSelection",
                moved && searchedAgain && nextHit && messageAbsent,
                $"Alpha selected={moved}, F3 searched again={searchedAgain}, Alphabet selected={nextHit}, no message={messageAbsent}.");
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    private static SandboxRegistry SeedSandbox(E2EConfig config)
    {
        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();

        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue("AlphaValue", "alpha value", RegistryValueKind.String);
            root.SetValue(ValueHitName, "needle", RegistryValueKind.String);
            root.SetValue(
                "MultiNeedle",
                new[] { "haystack", "needle in haystack" },
                RegistryValueKind.MultiString);
            root.SetValue("Dword42", 42, RegistryValueKind.DWord);
        }

        using (var alpha = sandbox.CreateSubKey("Alpha"))
        {
            alpha.SetValue("Inside", "alpha child", RegistryValueKind.String);
        }

        using (sandbox.CreateSubKey("Alphabet"))
        {
        }

        using (sandbox.CreateSubKey("Beta"))
        {
        }

        return sandbox;
    }

    /// <summary>
    /// Selects Beta and then the run root with real mouse clicks. The two selections force
    /// DetailsList.SetRows, which clears any selected value row so the next search starts at the key.
    /// </summary>
    private static bool SelectRunRoot(AppSession session, E2EConfig config)
    {
        if (!ClickTreeRow(session, "Beta"))
        {
            var beta = ControlTestSupport.FindRowByName(
                session,
                ControlTestSupport.TreeControl,
                ControlType.TreeItem,
                "Beta");
            ControlTestSupport.Select(beta);
            _ = ControlTestSupport.WaitForTreeSelection(session, "Beta");
        }

        return ClickTreeRow(session, config.RunId);
    }

    private static bool ClickTreeRow(AppSession session, string name)
    {
        AutomationElement? row = ControlTestSupport.FindRowByName(
            session,
            ControlTestSupport.TreeControl,
            ControlType.TreeItem,
            name);
        if (row is null)
        {
            return false;
        }

        try
        {
            row.Click();
        }
        catch (Exception)
        {
            return false;
        }

        return ControlTestSupport.WaitForTreeSelection(session, name);
    }

    private static bool WaitForOwnerEnabled(AppSession session) =>
        ControlTestSupport.WaitUntil(() => session.IsEnabled, TimeSpan.FromSeconds(5));

    private static bool SendAccelerator(AppSession session, string key)
    {
        try
        {
            ControlTestSupport.SendKeys(session, key);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("find.accelerator", false, $"'{key}' failed: {ex.Message}");
            return false;
        }
    }

    private static string SaveScreenshot(AutomationElement? window, string path)
    {
        // A screenshot is an artifact, not a pass or fail claim, so a failure is a note.
        if (window is null)
        {
            E2EResults.Note("find.screenshot", $"No window to capture for '{path}'.");
            return string.Empty;
        }

        try
        {
            return ClassicDialogTests.SaveScreenshot(window, path);
        }
        catch (Exception ex)
        {
            E2EResults.Note("find.screenshot", $"'{path}' failed: {ex.Message}");
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

    private static AppletFindFlags ReadFindFlags()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit",
            writable: false);
        object? value = key?.GetValue("FindFlags");
        return value is int flags
            ? new AppletFindFlags(true, flags)
            : new AppletFindFlags(false, 0);
    }

    private static void Record(string id, bool pass, string notes) =>
        _ = E2EResults.Record(id, pass, notes);

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Find checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private readonly record struct AppletFindFlags(bool Present, int Value);

    /// <summary>
    /// Drives the modal Find dialog, the progress dialog, and the finished message box. Each dialog
    /// is found by a control id that only it has, so the shared "Find" caption is not ambiguous.
    /// Keyboard input goes to the real Find HWND and every keyboard or mouse action checks that its
    /// window is foreground first.
    /// </summary>
    private sealed class FindDialogDriver : IDisposable
    {
        private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

        private readonly AppSession _session;
        private readonly UIA3Automation _automation;
        private nint _findHwnd;
        private nint _progressHwnd;
        private nint _messageHwnd;

        internal FindDialogDriver(AppSession session)
        {
            _session = session;
            _automation = new UIA3Automation();
        }

        internal bool FindOpen => FindWindowNow("FindText") is not null;

        internal bool MessageOpen => FindWindowNow("DialogMessage") is not null;

        internal AutomationElement? FindWindowElement =>
            ElementForHandle(_findHwnd) ?? FindWindowNow("FindText");

        internal AutomationElement? ProgressWindowElement =>
            ElementForHandle(_progressHwnd) ?? FindWindowNow("FindProgressText");

        internal AutomationElement? MessageWindowElement =>
            ElementForHandle(_messageHwnd) ?? FindWindowNow("DialogMessage");

        internal bool WaitForFindOpen(TimeSpan? timeout = null)
        {
            AutomationElement? found = WaitForWindow("FindText", timeout ?? UiTimeout);
            if (found is null)
            {
                return false;
            }

            _findHwnd = FindWin32DialogWindow();
            if (_findHwnd == 0)
            {
                _findHwnd = HwndOf(found);
            }

            if (_findHwnd != 0)
            {
                // The z-order and foreground checks read the Find dialog as it opened, before any input.
                OwnedWindowChecks.Record(_findHwnd);
            }

            return WaitForElement("FindText", UiTimeout) is not null && _findHwnd != 0;
        }

        internal bool WaitForFindClosed(TimeSpan? timeout = null) =>
            ControlTestSupport.WaitUntil(
                () => FindWindowNow("FindText") is null,
                timeout ?? TimeSpan.FromSeconds(10));

        internal bool WaitForProgressOpen(TimeSpan? timeout = null)
        {
            AutomationElement? found = WaitForWindow("FindProgressText", timeout ?? UiTimeout);
            if (found is null)
            {
                return false;
            }

            _progressHwnd = FindWin32DialogWindow();
            if (_progressHwnd == 0)
            {
                _progressHwnd = HwndOf(found);
            }

            if (_progressHwnd != 0)
            {
                // The Find progress window is checked for z order and foreground as it appeared.
                OwnedWindowChecks.Record(_progressHwnd);
            }

            return WaitForElement("FindProgressText", UiTimeout) is not null;
        }

        internal bool WaitForProgressClosed(TimeSpan? timeout = null) =>
            ControlTestSupport.WaitUntil(
                () => FindWindowNow("FindProgressText") is null,
                timeout ?? TimeSpan.FromSeconds(10));

        internal bool WaitForMessageText(string expected, TimeSpan timeout)
        {
            AutomationElement? found = null;
            bool matched = ControlTestSupport.WaitUntil(
                () =>
                {
                    found = FindWindowNow("DialogMessage");
                    return found is not null &&
                        string.Equals(MessageElementText(found), expected, StringComparison.Ordinal);
                },
                timeout);
            if (!matched || found is null)
            {
                return false;
            }

            _messageHwnd = FindWin32DialogWindow();
            if (_messageHwnd == 0)
            {
                _messageHwnd = HwndOf(found);
            }

            if (_messageHwnd != 0)
            {
                OwnedWindowChecks.Record(_messageHwnd);
            }

            return _messageHwnd != 0;
        }

        internal string MessageText()
        {
            AutomationElement? message = FindWindowNow("DialogMessage");
            return message is null ? string.Empty : MessageElementText(message);
        }

        internal string MessageCaption()
        {
            if (_messageHwnd == 0 || !Win32.IsWindow(_messageHwnd))
            {
                return string.Empty;
            }

            var title = new StringBuilder(128);
            _ = GetWindowText(_messageHwnd, title, title.Capacity);
            return title.ToString();
        }

        internal string ProgressText()
        {
            AutomationElement? text = WaitForElement("FindProgressText", UiTimeout);
            return text is null ? string.Empty : ControlTestSupport.SafeName(text);
        }

        internal bool DismissMessage()
        {
            AutomationElement? message = FindWindowNow("DialogMessage");
            if (message is null)
            {
                return false;
            }

            AutomationElement? button = FindInWindow(message, "DialogPrimary");
            if (button is null || !ForegroundGuard.Ensure(_messageHwnd, "dialog.find.message.foreground", TimeSpan.FromSeconds(3)))
            {
                return false;
            }

            try
            {
                button.Click();
            }
            catch (Exception)
            {
                return false;
            }

            return ControlTestSupport.WaitUntil(
                () => FindWindowNow("DialogMessage") is null,
                TimeSpan.FromSeconds(10));
        }

        internal bool ControlPresent(string automationId) =>
            WaitForElement(automationId, TimeSpan.FromSeconds(1)) is not null;

        internal bool IsChecked(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, TimeSpan.FromSeconds(1));
            if (element is null)
            {
                return false;
            }

            try
            {
                if (element.Patterns.Toggle.IsSupported &&
                    element.Patterns.Toggle.Pattern.ToggleState.Value is { } state)
                {
                    return state == ToggleState.On;
                }
            }
            catch (Exception)
            {
                // Fall through to false.
            }

            return false;
        }

        internal bool IsOffscreen(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, TimeSpan.FromSeconds(1));
            if (element is null)
            {
                return true;
            }

            try
            {
                return element.Properties.IsOffscreen.Value;
            }
            catch (Exception)
            {
                return true;
            }
        }

        internal string TextOf(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, TimeSpan.FromSeconds(1));
            if (element is null)
            {
                return string.Empty;
            }

            try
            {
                if (element.Patterns.Value.IsSupported)
                {
                    return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // The element has no value pattern.
            }

            try
            {
                return element.Name ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal bool SetChecked(string automationId, bool isChecked)
        {
            if (WaitForChecked(automationId, isChecked, TimeSpan.FromMilliseconds(300)))
            {
                return true;
            }

            return Click(automationId) &&
                WaitForChecked(automationId, isChecked, TimeSpan.FromSeconds(3));
        }

        internal bool WaitForChecked(string automationId, bool expected, TimeSpan? timeout = null) =>
            ControlTestSupport.WaitUntil(
                () => IsChecked(automationId) == expected,
                timeout ?? TimeSpan.FromSeconds(3));

        internal bool TypeFindText(string text)
        {
            AutomationElement? edit = WaitForElement("FindText", UiTimeout);
            if (edit is null || !EnsureFindForeground())
            {
                return false;
            }

            try
            {
                edit.Click();
            }
            catch (Exception)
            {
                return false;
            }

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(text);
            return ControlTestSupport.WaitUntil(
                () => string.Equals(TextOf("FindText"), text, StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
        }

        internal bool Click(string automationId)
        {
            AutomationElement? element = WaitForElement(automationId, UiTimeout);
            if (element is null)
            {
                return false;
            }

            nint hwnd = HwndOfWindow(element);
            if (!ForegroundGuard.Ensure(hwnd, "dialog.find.foreground", TimeSpan.FromSeconds(3)))
            {
                return false;
            }

            try
            {
                element.Click();
                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (element.Patterns.Invoke.IsSupported)
                    {
                        element.Patterns.Invoke.Pattern.Invoke();
                        return true;
                    }
                }
                catch (Exception)
                {
                    // The control is gone.
                }

                return false;
            }
        }

        internal bool SendFindKey(VirtualKeyShort key)
        {
            if (!EnsureFindForeground())
            {
                return false;
            }

            Keyboard.Press(key);
            return true;
        }

        internal bool SendAltMnemonic(char letter)
        {
            if (!EnsureFindForeground())
            {
                return false;
            }

            Keyboard.TypeSimultaneously(
                VirtualKeyShort.ALT,
                (VirtualKeyShort)char.ToUpperInvariant(letter));
            return true;
        }

        internal bool EnsureFindForeground() =>
            ForegroundGuard.Ensure(_findHwnd, "dialog.find.foreground", TimeSpan.FromSeconds(3));

        internal bool EnsureProgressForeground() =>
            ForegroundGuard.Ensure(_progressHwnd, "dialog.progress.foreground", TimeSpan.FromSeconds(3));

        public void Dispose()
        {
            _automation.Dispose();
        }

        private AutomationElement? WaitForElement(string automationId, TimeSpan timeout)
        {
            AutomationElement? result = null;
            _ = ControlTestSupport.WaitUntil(
                () => (result = FindAcrossWindows(automationId)) is not null,
                timeout);
            return result;
        }

        private AutomationElement? WaitForWindow(string automationId, TimeSpan timeout)
        {
            AutomationElement? result = null;
            _ = ControlTestSupport.WaitUntil(
                () => (result = FindWindowNow(automationId)) is not null,
                timeout);
            return result;
        }

        private AutomationElement? FindAcrossWindows(string automationId)
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();
                foreach (AutomationElement window in desktop.FindAllChildren(
                    cf => cf.ByControlType(ControlType.Window)))
                {
                    try
                    {
                        if (window.Properties.ProcessId.Value != _session.ProcessId)
                        {
                            continue;
                        }

                        AutomationElement? found = FindInWindow(window, automationId);
                        if (found is not null)
                        {
                            return found;
                        }
                    }
                    catch (Exception)
                    {
                        // A window that disappeared during enumeration is skipped.
                    }
                }
            }
            catch (Exception)
            {
                // UIA can throw while a window is created or destroyed.
            }

            return null;
        }

        private AutomationElement? FindWindowNow(string automationId)
        {
            try
            {
                AutomationElement desktop = _automation.GetDesktop();
                foreach (AutomationElement window in desktop.FindAllChildren(
                    cf => cf.ByControlType(ControlType.Window)))
                {
                    try
                    {
                        if (window.Properties.ProcessId.Value != _session.ProcessId)
                        {
                            continue;
                        }

                        if (FindInWindow(window, automationId) is not null)
                        {
                            return window;
                        }
                    }
                    catch (Exception)
                    {
                        // A window that disappeared during enumeration is skipped.
                    }
                }
            }
            catch (Exception)
            {
                // UIA can throw while a window is created or destroyed.
            }

            return null;
        }

        private static AutomationElement? FindInWindow(AutomationElement window, string automationId)
        {
            try
            {
                return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string MessageElementText(AutomationElement window)
        {
            AutomationElement? message = FindInWindow(window, "DialogMessage");
            return message is null ? string.Empty : ControlTestSupport.SafeName(message);
        }

        private AutomationElement? ElementForHandle(nint hwnd)
        {
            if (hwnd == 0 || !Win32.IsWindow(hwnd))
            {
                return null;
            }

            try
            {
                return _automation.FromHandle(hwnd);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private nint HwndOf(AutomationElement element)
        {
            try
            {
                AutomationElement window = ClassicDialogTests.WindowOf(element);
                return (nint)window.Properties.NativeWindowHandle.Value;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private nint HwndOfWindow(AutomationElement element)
        {
            nint hwnd = HwndOf(element);
            if (hwnd == 0 || hwnd == _session.MainHwnd)
            {
                // UIA can report the owned dialog content under the main window element. The real
                // modal HWND carries the shared "Find" caption and is the window that takes input.
                hwnd = FindWin32DialogWindow();
            }

            return hwnd;
        }

        private nint FindWin32DialogWindow()
        {
            nint found = 0;
            try
            {
                _ = EnumWindows(
                    (hwnd, lParam) =>
                    {
                        _ = lParam;
                        _ = GetWindowThreadProcessId(hwnd, out uint processId);
                        if (processId != (uint)_session.ProcessId || !IsWindowVisible(hwnd))
                        {
                            return true;
                        }

                        var title = new StringBuilder(128);
                        _ = GetWindowText(hwnd, title, title.Capacity);
                        if (!string.Equals(title.ToString(), "Find", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        found = hwnd;
                        return false;
                    },
                    IntPtr.Zero);
            }
            catch (Exception)
            {
                return 0;
            }

            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

#pragma warning disable SYSLIB1054 // The small user32 surface does not justify LibraryImport plumbing.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);
#pragma warning restore SYSLIB1054
    }
}
