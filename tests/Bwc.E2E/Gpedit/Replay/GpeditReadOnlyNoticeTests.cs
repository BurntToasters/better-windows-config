using System.Globalization;
using System.Security.Principal;
using Bwc.E2E.ConsoleFrame;
using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Gpedit.Replay;

/// <summary>
/// The read-only notice of the editor, on the machine's own policy (piece G4, dispatch T). It runs in the ship E2E
/// run: the
/// ship app is started by a process that is not elevated and opens the real policy object, which that process
/// cannot write,
/// so the window must say so (<c>GpeditReadOnlyNotice</c>) and the setting dialog of any policy must say so too
/// (<c>DialogReadOnlyNotice</c>) or have OK and Apply disabled. The test only reads: it never presses OK or Apply,
/// closes the
/// dialog with Cancel, and compares the real policy folder before and after (the run also records
/// <c>gpo.real-unchanged</c>). In the E2E flavor the same test runs with no sandbox root, which the product denies. The
/// failure modes are in docs/failure-modes/gpedit-dialog.md, section 15.
/// </summary>
[TestClass]
public sealed class GpeditReadOnlyNoticeTests
{
    private const string WindowNoticeId = "GpeditReadOnlyNotice";
    private const string DialogNoticeId = "DialogReadOnlyNotice";
    private const string RowPrefix = "ConsoleListRow_";

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Gpedit")]
    public void Gpedit_ReadOnly_Notice()
    {
        E2EConfig config = GuiGate.Config;
        var checks = new GpeditChecks("readonly", "notice");
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        checks.Record("notElevated", !elevated, "the test process is not elevated, so the real policy folder is read-only for it.");
        if (elevated)
        {
            checks.AssertAllPassed();
            return;
        }

        GpoGuard.Snapshot before = GpoGuard.Capture(GpoGuard.RealRoot);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWC_E2E_GPEDIT_HOST"] = "editor" };
        if (config.Flavor == "e2e")
        {
            // The E2E flavor needs a template folder and an import store; it gets no sandbox policy root, which it denies.
            environment["BWC_E2E_ADMX_ROOT"] = GpeditSandbox.FixtureRoot;
            environment["BWC_E2E_IMPORT_STORE"] = Path.Combine(config.DataRootFor("console-gpedit-readonly-notice"), "template-store");
        }

        using ShellRun shell = ShellRun.Open("gpedit-readonly-notice", environment);
        shell.PlaceWindow(20, 20, Math.Min(1900, ShellRun.ScreenWidth - 60), 1000);

        bool noticeShown = ShellRun.WaitUntil(() => shell.Find(WindowNoticeId) is not null, TimeSpan.FromSeconds(90));
        string noticeText = noticeShown ? ShellRun.SafeName(shell.Find(WindowNoticeId)) : string.Empty;
        checks.Record("window.notice", noticeShown && noticeText.Trim().Length > 0, $"the window shows '{WindowNoticeId}' with the text '{noticeText}'.");

        using var driver = new EditingDriver(shell.Session);
        bool opened = OpenFirstPolicy(shell, driver, checks);
        if (opened)
        {
            bool dialogNotice = driver.WaitForElement(DialogNoticeId, TimeSpan.FromSeconds(2)) is not null;
            bool okDisabled = driver.WaitForElement("buttonOK")?.IsEnabled == false;
            bool applyDisabled = driver.WaitForElement("buttonApply")?.IsEnabled == false;
            checks.Record("dialog.noticeOrOkDisabled", dialogNotice || okDisabled, $"notice shown={dialogNotice}, OK disabled={okDisabled}: OK must not save on a read-only policy object.");
            checks.Record("dialog.noticeOrApplyDisabled", dialogNotice || applyDisabled, $"notice shown={dialogNotice}, Apply disabled={applyDisabled}.");
            checks.Record("dialog.noticeText", !dialogNotice || ShellRun.SafeName(driver.WaitForElement(DialogNoticeId)).Trim().Length > 0, "a dialog notice has a text.");

            // Cancel is the only button the test presses.
            _ = driver.Invoke("buttonCancel");
            checks.Record("dialog.cancelCloses", driver.WaitForNoDialog(), "Cancel closes the dialog.");
        }

        GpoGuard.Snapshot after = GpoGuard.Capture(GpoGuard.RealRoot);
        IReadOnlyList<string> changes = GpoGuard.Compare(before, after);
        checks.Record("realPolicyUnchanged", changes.Count == 0, $"the real policy folder is unchanged: {string.Join("; ", changes.Take(5))}.");
        checks.AssertAllPassed();
    }

    /// <summary>
    /// Opens the setting dialog of the first policy found by opening the first row at every level (the Computer Configuration
    /// and Administrative Templates nodes first). A double click on a folder row opens the folder, on a policy row the
    /// dialog.
    /// </summary>
    private static bool OpenFirstPolicy(ShellRun shell, EditingDriver driver, GpeditChecks checks)
    {
        string[] preferred = ["Computer Configuration", "Administrative Templates"];
        for (int depth = 0; depth < 12; depth++)
        {
            List<(int Index, string Name)> rows = [];
            bool listed = ShellRun.WaitUntil(
                () =>
                {
                    rows = RowsOf(shell);
                    return rows.Count > 0;
                },
                TimeSpan.FromSeconds(30));
            if (!listed)
            {
                checks.Record("dialog.opens", false, $"no list row appeared at depth {depth}.");
                return false;
            }

            (int Index, string Name) row = depth < preferred.Length ? rows.FirstOrDefault(r => r.Name == preferred[depth], rows[0]) : rows[0];
            string[] names = rows.Select(r => r.Name).ToArray();
            _ = ControlTestSupport.EnsureForeground(shell.Session);
            try
            {
                shell.Find(RowPrefix + row.Index.ToString(CultureInfo.InvariantCulture))?.DoubleClick();
            }
            catch (FlaUI.Core.Exceptions.NoClickablePointException)
            {
                Thread.Sleep(500);
                continue;
            }

            if (driver.WaitForElement("DialogRoot", TimeSpan.FromSeconds(3)) is not null)
            {
                checks.Record("dialog.opens", true, $"the dialog of '{row.Name}' opened at depth {depth}.");
                return true;
            }

            if (!ShellRun.WaitUntil(() => !RowsOf(shell).Select(r => r.Name).SequenceEqual(names), TimeSpan.FromSeconds(8)))
            {
                checks.Record("dialog.opens", false, $"opening '{row.Name}' neither opened a dialog nor changed the list.");
                return false;
            }
        }

        checks.Record("dialog.opens", false, "no policy row was found within 12 levels.");
        return false;
    }

    private static List<(int Index, string Name)> RowsOf(ShellRun shell)
    {
        var rows = new List<(int, string)>();
        try
        {
            AutomationElement? list = shell.Find(GpeditRun.ListId);
            if (list is null)
            {
                return rows;
            }

            foreach (AutomationElement element in list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
            {
                string id = ShellRun.SafeId(element);
                if (id.StartsWith(RowPrefix, StringComparison.Ordinal)
                    && int.TryParse(id[RowPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    rows.Add((index, ShellRun.SafeName(element)));
                }
            }
        }
        catch (Exception)
        {
            // The list is being rebuilt; the caller polls again.
        }

        rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return rows;
    }
}
