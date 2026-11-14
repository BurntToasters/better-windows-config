using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Favorites;

/// <summary>
/// Drives the Registry Editor Favorites menu against a run-scoped favorites key. The classic
/// Favorites key is read before and after to prove the isolated run never touches it. The test is
/// mutating and runs only on the e2e flavor, because the ship application writes the real classic
/// key.
/// </summary>
[TestClass]
public sealed class FavoritesTests
{
    private const string FavoriteName = "E2EFavorite";
    private const string MissingFavoriteName = "MissingFavorite";
    private const string TargetKeyName = "FavoriteTarget";
    private const string MissingKeyName = "MissingFavoriteTarget";
    private const string FavoritesAutomationPrefix = "MenuCommand_Favorite_";
    private const string ClassicFavoritesKey =
        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\Favorites";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

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
    public void Favorites_AddRemoveNavigateAndReportMissingPath()
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
            E2EResults.Record("favorites.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        string? dataRoot = null;
        string[] classicBefore = ReadClassicFavoriteNames();

        string runRootDisplay = $@"Computer\HKEY_CURRENT_USER\Software\BWC-E2E\{config.RunId}";
        string targetDisplay = runRootDisplay + @"\" + TargetKeyName;
        string missingDisplay = runRootDisplay + @"\" + MissingKeyName;
        string favoritesRoot = sandbox.RunRootDisplayPath + @"\Favorites";

        try
        {
            sandbox.Cleanup();
            Seed(sandbox, missingDisplay);
            dataRoot = CreateFreshDataRoot(config, "favorites");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                ["BWC_E2E_FAVORITES_ROOT"] = favoritesRoot,
            };

            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);
            using var automation = new UIA3Automation();

            bool navigated = ControlTestSupport.NavigateToSandboxKey(
                session,
                config,
                childKey: TargetKeyName);
            bool selected = ControlTestSupport.WaitForTreeSelection(session, TargetKeyName, UiTimeout);
            string address = session.WaitForText("AddressBar", targetDisplay, UiTimeout) ?? "(missing)";
            E2EResults.Record(
                "favorites.navigate.target",
                navigated && selected && string.Equals(address, targetDisplay, StringComparison.OrdinalIgnoreCase),
                $"navigated={navigated}, selected={selected}, expected='{targetDisplay}', got='{address}'.");
            E2EGuards.RequireSandboxNavigation(session, sandbox, navigated && selected, "favorites.sandbox.navigate");

            E2EGuards.RequireSandboxSelection(session, sandbox, "favorites.sandbox.add");
            bool addMenu = TryOpenMenu(driver, 517, 1281);
            bool addDialog = addMenu && driver.WaitForDialog();
            // The classic prompt has no heading. Its window title is the caption.
            AutomationElement? promptRoot = addDialog ? driver.WaitForElement("DialogRoot") : null;
            string heading = promptRoot is null ? string.Empty : ControlTestSupport.SafeName(ClassicDialogTests.WindowOf(promptRoot));
            string suggested = addDialog ? driver.TextOf("DialogTextInput") : string.Empty;
            bool typed = addDialog && driver.SetText("DialogTextInput", FavoriteName);
            bool accepted = typed && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();
            string? stored = accepted ? sandbox.ReadString("Favorites", FavoriteName) : null;

            E2EResults.Record(
                "favorites.add",
                addDialog &&
                    string.Equals(heading, "Add to Favorites", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(suggested, TargetKeyName, StringComparison.OrdinalIgnoreCase) &&
                    accepted &&
                    string.Equals(stored, targetDisplay, StringComparison.OrdinalIgnoreCase),
                $"menu={addMenu}, dialog={addDialog}, heading='{heading}', suggested='{suggested}', accepted={accepted}, stored='{stored}', expected='{targetDisplay}'.");

            string[] namesAfterAdd = ReadFavoriteEntryNames(session, driver, automation, expectedCount: 2);
            E2EResults.Record(
                "favorites.menu.added",
                ContainsInOrder(namesAfterAdd, FavoriteName, MissingFavoriteName),
                $"entries=[{string.Join(",", namesAfterAdd)}].");

            bool selectedRunRoot = SelectTreeRow(session, config.RunId);
            bool atRunRoot = selectedRunRoot &&
                session.WaitForText("AddressBar", runRootDisplay, UiTimeout) is { } runRootAddress &&
                string.Equals(runRootAddress, runRootDisplay, StringComparison.OrdinalIgnoreCase);
            E2EResults.Record(
                "favorites.prepare.click",
                atRunRoot,
                $"selectedRunRoot={selectedRunRoot}, address='{AddressText(session)}', expected='{runRootDisplay}'.");

            E2EGuards.RequireSandboxSelection(session, sandbox, "favorites.sandbox.click");
            bool clicked = InvokeFavoriteEntry(session, driver, automation, FavoriteName);
            bool navigatedByFavorite = clicked &&
                ControlTestSupport.WaitUntil(
                    () => string.Equals(AddressText(session), targetDisplay, StringComparison.OrdinalIgnoreCase) &&
                        ControlTestSupport.IsSelected(
                            ControlTestSupport.FindRowByName(
                                session,
                                ControlTestSupport.TreeControl,
                                ControlType.TreeItem,
                                TargetKeyName)),
                    UiTimeout);
            E2EResults.Record(
                "favorites.click.navigate",
                navigatedByFavorite,
                $"clicked={clicked}, address='{AddressText(session)}', expected='{targetDisplay}', selected={ControlTestSupport.IsSelected(ControlTestSupport.FindRowByName(session, ControlTestSupport.TreeControl, ControlType.TreeItem, TargetKeyName))}.");

            E2EGuards.RequireSandboxSelection(session, sandbox, "favorites.sandbox.duplicate");
            bool duplicateMenu = TryOpenMenu(driver, 517, 1281);
            bool duplicateDialog = duplicateMenu && driver.WaitForDialog();
            bool duplicateTyped = duplicateDialog && driver.SetText("DialogTextInput", FavoriteName);
            bool duplicateSubmitted = duplicateTyped && driver.Invoke("DialogPrimary");
            bool duplicateMessage = duplicateSubmitted && ControlTestSupport.WaitUntil(
                () => string.Equals(
                    driver.TextOf("DialogErrorText"),
                    "There is already a favorite with that name.",
                    StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));
            string duplicateText = duplicateMessage ? driver.TextOf("DialogErrorText") : string.Empty;
            // The error is its own message box with OK focused. Enter closes it, and the prompt stays open.
            bool duplicateErrorDismissed = duplicateMessage && driver.DismissErrorBox();
            bool duplicateCancelled = duplicateErrorDismissed &&
                driver.Invoke("DialogSecondary") &&
                driver.WaitForNoDialog();
            string? stillStored = duplicateCancelled ? sandbox.ReadString("Favorites", FavoriteName) : null;
            E2EResults.Record(
                "favorites.add.duplicate",
                duplicateDialog && duplicateMessage && duplicateCancelled &&
                    string.Equals(stillStored, targetDisplay, StringComparison.OrdinalIgnoreCase),
                $"dialog={duplicateDialog}, message='{duplicateText}', cancelled={duplicateCancelled}, stillStored='{stillStored}'.");

            E2EGuards.RequireSandboxSelection(session, sandbox, "favorites.sandbox.remove");
            bool removeMenu = TryOpenMenu(driver, 517, 1280);
            bool removeDialog = removeMenu && driver.WaitForDialog();
            AutomationElement? list = removeDialog ? driver.WaitForElement("FavoritesList") : null;
            string[] removeNames = ListEntryNames(list);
            bool listSelected = SelectListEntry(list, FavoriteName);
            bool removed = listSelected && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();
            bool favoriteGone = removed && sandbox.ReadString("Favorites", FavoriteName) is null;
            bool missingRemains = removed && sandbox.ReadString("Favorites", MissingFavoriteName) is not null;
            E2EResults.Record(
                "favorites.remove",
                removeDialog &&
                    ContainsInOrder(removeNames, FavoriteName, MissingFavoriteName) &&
                    listSelected &&
                    removed &&
                    favoriteGone &&
                    missingRemains,
                $"dialog={removeDialog}, entries=[{string.Join(",", removeNames)}], selected={listSelected}, removed={removed}, favoriteGone={favoriteGone}, missingRemains={missingRemains}.");

            string[] namesAfterRemove = ReadFavoriteEntryNames(session, driver, automation, expectedCount: 1);
            E2EResults.Record(
                "favorites.menu.removed",
                namesAfterRemove.Length == 1 &&
                    string.Equals(namesAfterRemove[0], MissingFavoriteName, StringComparison.OrdinalIgnoreCase),
                $"entries=[{string.Join(",", namesAfterRemove)}].");

            string addressBeforeMissing = AddressText(session);
            E2EGuards.RequireSandboxSelection(session, sandbox, "favorites.sandbox.missing");
            bool missingClicked = InvokeFavoriteEntry(session, driver, automation, MissingFavoriteName);
            bool errorDialog = missingClicked && driver.WaitForDialog();
            string errorText = errorDialog ? driver.TextOf("DialogErrorText") : string.Empty;
            bool errorNamesPath = errorText.Contains("Path:", StringComparison.OrdinalIgnoreCase) &&
                errorText.Contains(missingDisplay, StringComparison.OrdinalIgnoreCase);
            bool errorDismissed = errorDialog &&
                driver.Invoke("DialogPrimary") &&
                driver.WaitForNoDialog();
            bool stillAlive = session.Find(ControlTestSupport.TreeControl) is not null && session.IsEnabled;
            string addressAfterMissing = AddressText(session);
            E2EResults.Record(
                "favorites.click.missing",
                missingClicked &&
                    errorDialog &&
                    errorNamesPath &&
                    errorDismissed &&
                    stillAlive &&
                    string.Equals(addressAfterMissing, addressBeforeMissing, StringComparison.OrdinalIgnoreCase),
                $"clicked={missingClicked}, dialog={errorDialog}, namesPath={errorNamesPath}, dismissed={errorDismissed}, alive={stillAlive}, addressBefore='{addressBeforeMissing}', addressAfter='{addressAfterMissing}', error='{errorText}'.");

            // Deliberate exemption from the sandbox proof: Computer is not a registry key, and the Add
            // command refuses it before any write. Selecting it cannot reach the registry.
            bool computerSelected = SelectTreeRow(session, "Computer");
            bool computerAddMenu = computerSelected && TryOpenMenu(driver, 517, 1281);
            bool computerDialog = computerAddMenu && driver.WaitForDialog();
            string computerText = computerDialog ? driver.TextOf("DialogErrorText") : string.Empty;
            bool computerMessage = computerDialog &&
                computerText.Contains("Computer cannot be added to favorites", StringComparison.OrdinalIgnoreCase);
            bool computerDismissed = computerMessage &&
                driver.Invoke("DialogPrimary") &&
                driver.WaitForNoDialog();
            E2EResults.Record(
                "favorites.add.computer",
                computerMessage && computerDismissed,
                $"selected={computerSelected}, dialog={computerDialog}, message={computerMessage}, dismissed={computerDismissed}, text='{computerText}'.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
            DeleteDataRoot(dataRoot);

            string[] classicAfter = ReadClassicFavoriteNames();
            E2EResults.Record(
                "favorites.classic.unchanged",
                NamesEqual(classicBefore, classicAfter),
                $"before=[{string.Join(",", classicBefore)}], after=[{string.Join(",", classicAfter)}].");
        }
    }

    private static void Seed(SandboxRegistry sandbox, string missingDisplayPath)
    {
        using (var root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue("RootMarker", "root", RegistryValueKind.String);
        }

        using (var target = sandbox.CreateSubKey(TargetKeyName))
        {
            target.SetValue("Marker", "favorite-target", RegistryValueKind.String);
        }

        sandbox.SetString("Favorites", MissingFavoriteName, missingDisplayPath);
    }

    internal static bool TryOpenMenu(EditingDriver driver, params int[] commandIds)
    {
        try
        {
            driver.OpenMenu(commandIds);
            return true;
        }
        catch (Exception ex)
        {
            E2EResults.Record("favorites.menu.open", false, ex.Message);
            return false;
        }
    }

    internal static string[] ReadFavoriteEntryNames(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        int expectedCount)
    {
        if (!TryOpenMenu(driver, 517))
        {
            return Array.Empty<string>();
        }

        _ = ControlTestSupport.WaitUntil(
            () => FindFavoriteEntries(session, automation).Count >= expectedCount,
            UiTimeout);

        string[] names = OrderedFavoriteEntries(session, automation)
            .Select(ControlTestSupport.SafeName)
            .ToArray();
        CloseMenu(session, automation);
        return names;
    }

    private static bool InvokeFavoriteEntry(
        AppSession session,
        EditingDriver driver,
        UIA3Automation automation,
        string name)
    {
        if (!TryOpenMenu(driver, 517))
        {
            return false;
        }

        AutomationElement? entry = null;
        _ = ControlTestSupport.WaitUntil(
            () =>
            {
                entry = OrderedFavoriteEntries(session, automation)
                    .FirstOrDefault(element => string.Equals(
                        ControlTestSupport.SafeName(element),
                        name,
                        StringComparison.OrdinalIgnoreCase));
                return entry is not null;
            },
            UiTimeout);

        if (entry is null)
        {
            E2EResults.Record(
                "favorites.menu.missing",
                false,
                $"Favorite '{name}' was not in the open menu. entries=[{string.Join(",", OrderedFavoriteEntries(session, automation).Select(ControlTestSupport.SafeName))}].");
            CloseMenu(session, automation);
            return false;
        }

        try
        {
            if (entry.Patterns.Invoke.IsSupported)
            {
                entry.Patterns.Invoke.Pattern.Invoke();
            }
            else
            {
                entry.Click();
            }

            return true;
        }
        catch (Exception)
        {
            try
            {
                entry.Click();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static List<AutomationElement> OrderedFavoriteEntries(
        AppSession session,
        UIA3Automation automation) =>
        FindFavoriteEntries(session, automation)
            .OrderBy(element => SafeTop(element))
            .ThenBy(element => SafeLeft(element))
            .ToList();

    private static List<AutomationElement> FindFavoriteEntries(
        AppSession session,
        UIA3Automation automation)
    {
        var found = new List<AutomationElement>();
        var automationIds = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<AutomationElement>();

        try
        {
            roots.Add(automation.FromHandle(session.MainHwnd));
        }
        catch (Exception)
        {
            // The next root may still be usable.
        }

        try
        {
            roots.Add(automation.GetDesktop());
        }
        catch (Exception)
        {
            // The main window root above is enough when the desktop is unavailable.
        }

        foreach (AutomationElement root in roots)
        {
            AutomationElement[] elements;
            try
            {
                elements = root.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem));
            }
            catch (Exception)
            {
                continue;
            }

            foreach (AutomationElement element in elements)
            {
                try
                {
                    string automationId = element.AutomationId ?? string.Empty;
                    if (element.Properties.ProcessId.Value != session.ProcessId ||
                        !automationId.StartsWith(FavoritesAutomationPrefix, StringComparison.OrdinalIgnoreCase) ||
                        element.Properties.IsOffscreen.Value)
                    {
                        continue;
                    }

                    System.Drawing.Rectangle bounds = element.BoundingRectangle;
                    if (bounds.Width <= 0 || bounds.Height <= 0 || !automationIds.Add(automationId))
                    {
                        continue;
                    }

                    found.Add(element);
                }
                catch (Exception)
                {
                    // A stale element is skipped; the next poll re-reads the tree.
                }
            }
        }

        return found;
    }

    private static void CloseMenu(AppSession session, UIA3Automation automation)
    {
        try
        {
            ControlTestSupport.SendKeys(session, "ESCAPE");
        }
        catch (Exception)
        {
            return;
        }

        _ = ControlTestSupport.WaitUntil(
            () => FindFavoriteEntries(session, automation).Count == 0,
            TimeSpan.FromSeconds(3));
    }

    private static bool SelectTreeRow(AppSession session, string name)
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

        ControlTestSupport.Select(row);
        return ControlTestSupport.WaitForTreeSelection(session, name);
    }

    private static bool SelectListEntry(AutomationElement? list, string name)
    {
        if (list is null)
        {
            return false;
        }

        try
        {
            AutomationElement? item = list
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .FirstOrDefault(element => string.Equals(
                    ControlTestSupport.SafeName(element),
                    name,
                    StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                return false;
            }

            ControlTestSupport.Select(item);
            return ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(item),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string[] ListEntryNames(AutomationElement? list)
    {
        if (list is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return list
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .OrderBy(SafeTop)
                .Select(ControlTestSupport.SafeName)
                .Where(name => name.Length > 0)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static string AddressText(AppSession session)
    {
        AutomationElement? address = session.Find("AddressBar");
        return address is null ? "(missing)" : session.TextOf(address);
    }

    private static double SafeTop(AutomationElement element)
    {
        try
        {
            return element.BoundingRectangle.Top;
        }
        catch (Exception)
        {
            return double.MaxValue;
        }
    }

    private static double SafeLeft(AutomationElement element)
    {
        try
        {
            return element.BoundingRectangle.Left;
        }
        catch (Exception)
        {
            return double.MaxValue;
        }
    }

    private static bool ContainsInOrder(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index += 1)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] ReadClassicFavoriteNames()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ClassicFavoritesKey, writable: false);
            string[] names = key?.GetValueNames() ?? Array.Empty<string>();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool NamesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index += 1)
        {
            if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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
            // The scoped settings folder is left for the next run if the exiting application
            // still holds a file for a moment.
        }
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Favorites checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
