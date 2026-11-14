using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using Bwc.E2E.Hex;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Favorites;

/// <summary>
/// Favorites regressions from docs/failure-modes/favorites.md: a default value under the Favorites key is
/// not a favorite, a whitespace-only name is refused on add, and a soft-hyphen name is not a duplicate.
/// </summary>
[TestClass]
public sealed class FavoritesFixesTests
{
    private const string ExistingFavorite = "ab";
    private const string SoftHyphenFavorite = "a­b";
    private const string WhitespaceFavorite = "   ";
    private const string DefaultPath = @"Computer\HKEY_CURRENT_USER\Software";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Favorites")]
    [TestCategory("Mutating")]
    [TestCategory("E2EOnly")]
    public void FavoritesFixes_DefaultValueWhitespaceAndCulture()
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
            E2EResults.Record("favfix.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        string runRootDisplay = $@"Computer\HKEY_CURRENT_USER\Software\BWC-E2E\{config.RunId}";
        string favoritesRoot = sandbox.RunRootDisplayPath + @"\Favorites";

        try
        {
            sandbox.Cleanup();
            using (var favorites = sandbox.CreateSubKey("Favorites"))
            {
                favorites.SetValue(string.Empty, DefaultPath, RegistryValueKind.String);
                favorites.SetValue(ExistingFavorite, runRootDisplay, RegistryValueKind.String);
            }

            dataRoot = HexEditingTests.CreateFreshDataRoot(config, "favfix");
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                ["BWC_E2E_FAVORITES_ROOT"] = favoritesRoot,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config);
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, "favfix.sandbox.navigate");

            string[] names = FavoritesTests.ReadFavoriteEntryNames(session, driver, automation, expectedCount: 1);
            E2EResults.Record(
                "favfix.default",
                names.Length == 1 && names[0] == ExistingFavorite,
                $"menu entries=[{string.Join(",", names)}], expected only '{ExistingFavorite}'.");

            CheckAdd(session, driver, sandbox, WhitespaceFavorite, runRootDisplay, "favfix.whitespace", expectStored: false);
            CheckAdd(session, driver, sandbox, SoftHyphenFavorite, runRootDisplay, "favfix.culture", expectStored: true);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            HexEditingTests.DeleteDataRoot(dataRoot);
        }

        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Favorites fix checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void CheckAdd(
        AppSession session,
        EditingDriver driver,
        SandboxRegistry sandbox,
        string name,
        string targetDisplay,
        string checkId,
        bool expectStored)
    {
        E2EGuards.RequireSandboxSelection(session, sandbox, checkId + ".sandbox");
        bool menu = FavoritesTests.TryOpenMenu(driver, 517, 1281);
        bool dialog = menu && driver.WaitForDialog();
        bool typed = dialog && driver.SetText("DialogTextInput", name);
        bool invoked = typed && driver.Invoke("DialogPrimary");
        bool closed = invoked && driver.WaitForNoDialog(TimeSpan.FromSeconds(5));
        if (!closed)
        {
            // A refused name shows an error box, or leaves the prompt open. Dismiss whichever is shown.
            _ = driver.DismissErrorBox(TimeSpan.FromSeconds(3));
            if (driver.WaitForDialog(TimeSpan.FromSeconds(1)))
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
            }

            _ = driver.WaitForNoDialog(TimeSpan.FromSeconds(5));
        }

        string? stored = sandbox.ReadString("Favorites", name);
        bool outcome = expectStored
            ? stored == targetDisplay
            : stored is null;

        E2EResults.Record(
            checkId,
            dialog && typed && invoked && outcome,
            $"name='{name}', menu={menu}, dialog={dialog}, typed={typed}, invoked={invoked}, closed={closed}, " +
            $"stored='{stored ?? "(missing)"}', expectStored={expectStored}.");
    }
}
