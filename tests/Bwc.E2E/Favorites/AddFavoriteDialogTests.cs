using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Favorites;

/// <summary>
/// Add to Favorites is the classic RT_DIALOG 112 prompt (DLG-024, DLG-025). The caption, the label, the tab
/// order, the duplicate error (RT_STRING 171 in a box captioned RT_STRING 172), and the empty-name refusal
/// are checked through UI Automation. The test is mutating and runs only on the e2e flavor.
/// </summary>
[TestClass]
public sealed class AddFavoriteDialogTests
{
    private const int MenuFavorites = 517;
    private const int MenuFavoritesAdd = 1281;
    private const string ExistingName = "Existing";
    private const string TargetKeyName = "FavoriteTarget";
    private const string NameInput = "DialogTextInput";
    private const string PrimaryId = "DialogPrimary";
    private const string SecondaryId = "DialogSecondary";
    private const string LabelId = "FavoriteNameLabel";
    private const string ErrorTextId = "DialogErrorText";
    private const string ClassicCaption = "Add to Favorites";
    private const string ClassicLabel = "Favorite name:";
    private const string ClassicDuplicate = "There is already a favorite with that name.";
    private const string ClassicDuplicateCaption = "Error Adding Favorite";
    private const string ClassicEmptyName = "Enter a favorite name.";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Favorites")]
    [TestCategory("Mutating")]
    public void AddFavoriteDialog_ClassicLayoutTabOrderAndDuplicateError()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);
        config.EnsureExecutablesExist();

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            string targetDisplay = $@"Computer\HKEY_CURRENT_USER\{sandbox.RunRootPath}\{TargetKeyName}";
            sandbox.SetString(string.Empty, "RootMarker", "root");
            sandbox.CreateKey(TargetKeyName);
            sandbox.SetString(TargetKeyName, "Marker", "favorite-target");

            sandbox.SetString("Favorites", ExistingName, targetDisplay);

            string dataRoot = config.DataRootFor("favorites-add-dialog");
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }

            Directory.CreateDirectory(dataRoot);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                ["BWC_E2E_FAVORITES_ROOT"] = sandbox.RunRootDisplayPath + @"\Favorites",
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey: TargetKeyName);
            bool selected = ControlTestSupport.WaitForTreeSelection(session, TargetKeyName, UiTimeout);
            E2EResults.Record("addfav.navigate", navigated && selected, $"navigated={navigated}, selected={selected}.");
            E2EGuards.RequireSandboxSelection(session, sandbox, "addfav.sandbox.select");

            CheckLayoutAndFocus(driver, automation);
            CheckDuplicateAndEmptyErrors(driver);
            CheckCancelWritesNothing(driver, sandbox);

            FailIfNeeded(checkStart);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static void CheckLayoutAndFocus(EditingDriver driver, UIA3Automation automation)
    {
        driver.OpenMenu(MenuFavorites, MenuFavoritesAdd);
        AutomationElement? root = driver.WaitForElement("DialogRoot", UiTimeout);
        E2EResults.Record("addfav.open", root is not null, "Favorites > Add to Favorites opened the prompt.");
        if (root is null)
        {
            return;
        }

        IntPtr hwnd = ClassicDialogTests.DialogHandle(root);
        string caption = ClassicDialogTests.CaptionOf(automation, hwnd);
        E2EResults.Record(
            "addfav.caption",
            string.Equals(caption, ClassicCaption, StringComparison.Ordinal),
            $"window caption='{caption}', expected '{ClassicCaption}'.");

        string label = ControlTestSupport.SafeName(driver.WaitForElement(LabelId, TimeSpan.FromSeconds(2)))
            .Replace("&", string.Empty, StringComparison.Ordinal);
        E2EResults.Record(
            "addfav.label",
            string.Equals(label, ClassicLabel, StringComparison.Ordinal),
            $"label='{label}', expected '{ClassicLabel}'.");

        string suggested = driver.TextOf(NameInput);
        E2EResults.Record(
            "addfav.suggested",
            string.Equals(suggested, TargetKeyName, StringComparison.Ordinal),
            $"suggested name='{suggested}', expected '{TargetKeyName}'.");

        bool focused = ControlTestSupport.WaitUntil(() => ClassicDialogTests.FocusedId(automation) == NameInput, TimeSpan.FromSeconds(3));
        string tabs = ClassicDialogTests.TabSequence(automation, 3);
        string expectedTabs = $"{PrimaryId},{SecondaryId},{NameInput}";
        E2EResults.Record(
            "addfav.tab-order",
            focused && tabs == expectedTabs,
            $"initial focus on the edit={focused}; tab sequence='{tabs}', expected '{expectedTabs}'.");
    }

    private static void CheckDuplicateAndEmptyErrors(EditingDriver driver)
    {
        bool typed = driver.SetText(NameInput, ExistingName);
        bool clicked = typed && driver.Invoke(PrimaryId);
        AutomationElement? error = clicked ? driver.WaitForElement(ErrorTextId, UiTimeout) : null;
        string errorText = error is null ? string.Empty : driver.TextOf(ErrorTextId);
        string errorCaption = error is null
            ? string.Empty
            : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(error));
        E2EResults.Record(
            "addfav.duplicate.error",
            error is not null &&
                string.Equals(errorText, ClassicDuplicate, StringComparison.Ordinal) &&
                string.Equals(errorCaption, ClassicDuplicateCaption, StringComparison.Ordinal),
            $"text='{errorText}' (expected '{ClassicDuplicate}'), caption='{errorCaption}' (expected '{ClassicDuplicateCaption}').");

        bool dismissed = error is not null && driver.DismissErrorBox();
        string kept = driver.TextOf(NameInput);
        bool stillOpen = driver.WaitForElement("DialogRoot", TimeSpan.FromSeconds(2)) is not null;
        E2EResults.Record(
            "addfav.duplicate.keeps-prompt",
            dismissed && stillOpen && string.Equals(kept, ExistingName, StringComparison.Ordinal),
            $"dismissed={dismissed}, prompt open={stillOpen}, typed text kept='{kept}'.");

        bool blanked = driver.SetText(NameInput, "   ");
        bool blankClicked = blanked && driver.Invoke(PrimaryId);
        AutomationElement? blankError = blankClicked ? driver.WaitForElement(ErrorTextId, UiTimeout) : null;
        string blankText = blankError is null ? string.Empty : driver.TextOf(ErrorTextId);
        string blankCaption = blankError is null
            ? string.Empty
            : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(blankError));
        E2EResults.Record(
            "addfav.empty.error",
            blankError is not null && string.Equals(blankText, ClassicEmptyName, StringComparison.Ordinal),
            $"text='{blankText}' (expected '{ClassicEmptyName}'), caption='{blankCaption}'. " +
            "The empty-name caption is the prompt title, a declared difference (docs/failure-modes/favorites.md).");
        if (blankError is not null)
        {
            _ = driver.DismissErrorBox();
        }
    }

    private static void CheckCancelWritesNothing(EditingDriver driver, SandboxRegistry sandbox)
    {
        bool cancelled = driver.Invoke(SecondaryId) && driver.WaitForNoDialog(TimeSpan.FromSeconds(10));
        string? stored = sandbox.ReadString("Favorites", ExistingName);
        bool onlyExisting = sandbox.ReadString("Favorites", TargetKeyName) is null;
        E2EResults.Record(
            "addfav.cancel",
            cancelled && onlyExisting && stored is not null,
            $"cancelled={cancelled}, existing favorite still stored={stored is not null}, no extra value={onlyExisting}.");
    }

    private static void FailIfNeeded(int startIndex)
    {
        var failures = E2EResults.FailedChecksSince(startIndex);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Add to Favorites checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
