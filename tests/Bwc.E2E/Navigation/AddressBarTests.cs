using Bwc.E2E.Controls;
using Bwc.E2E.Editing;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.Navigation;

/// <summary>
/// Proves the address bar is editable and drives navigation: a full sandbox path resolves to the
/// canonical display path and loads the seeded value, an abbreviated path with a trailing slash
/// reaches the same node, an unknown path shows the error dialog without changing the selection,
/// the bare Computer text selects the virtual root, and Escape restores the committed text and
/// returns focus to the tree.
/// </summary>
[TestClass]
public sealed class AddressBarTests
{
    private const string SeededValueName = "AddressValue";
    private const string SeededValueData = "address-bar";

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Navigation")]
    public void AddressBar_NavigatesParsesAndRejectsPaths()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("addressbar.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        sandbox.Cleanup();
        using (var seeded = sandbox.CreateSubKey(string.Empty))
        {
            seeded.SetValue(SeededValueName, SeededValueData, RegistryValueKind.String);
        }

        string dataRoot = CreateFreshDataRoot(config, "address-bar");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = dataRoot,
        };

        string canonicalPath = $@"Computer\HKEY_CURRENT_USER\Software\BWC-E2E\{config.RunId}";
        string typedPath = $@"HKEY_CURRENT_USER\Software\BWC-E2E\{config.RunId}";
        string abbreviatedPath = $@"HKCU\Software\BWC-E2E\{config.RunId}\";
        string missingPath = $@"HKCU\Software\ThisDoesNotExist-{config.RunId}";

        try
        {
            using var session = new AppSession(config, config.RegeditExePath, environment: environment);
            using var driver = new EditingDriver(session);

            string initial = session.WaitForText("AddressBar", "Computer", TimeSpan.FromSeconds(15))
                ?? "(missing)";
            E2EResults.Record(
                "addressbar.initial",
                string.Equals(initial, "Computer", StringComparison.OrdinalIgnoreCase),
                $"expected 'Computer', got '{initial}'.");

            bool initialComputer = ControlTestSupport.WaitForTreeSelection(session, "Computer");
            E2EResults.Record(
                "addressbar.initial.computer",
                initialComputer,
                $"Computer selected={initialComputer}.");

            bool typed = EditAddress(session, typedPath);
            E2EResults.Record(
                "addressbar.valid.typed",
                typed,
                $"typed='{typedPath}' result='{AddressText(session)}'.");

            bool submitted = Submit(session);
            string canonical = session.WaitForText("AddressBar", canonicalPath, TimeSpan.FromSeconds(15))
                ?? "(missing)";
            E2EResults.Record(
                "addressbar.valid.address",
                submitted && string.Equals(canonical, canonicalPath, StringComparison.OrdinalIgnoreCase),
                $"expected '{canonicalPath}', got '{canonical}'.");

            bool selected = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.IsSelected(
                    ControlTestSupport.FindRowByName(
                        session,
                        ControlTestSupport.TreeControl,
                        ControlType.TreeItem,
                        config.RunId)),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "addressbar.valid.selection",
                selected,
                $"{config.RunId} selected={selected}.");

            bool values = ControlTestSupport.WaitUntil(
                () => ControlTestSupport.ListRowNames(session).Contains(SeededValueName, StringComparer.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "addressbar.valid.values",
                values,
                $"list rows=[{string.Join(",", ControlTestSupport.ListRowNames(session))}].");

            bool abbreviationTyped = EditAddress(session, abbreviatedPath);
            _ = Submit(session);
            string abbreviationAddress = session.WaitForText(
                "AddressBar",
                canonicalPath,
                TimeSpan.FromSeconds(15)) ?? "(missing)";
            bool abbreviationSelected = ControlTestSupport.WaitForTreeSelection(
                session,
                config.RunId,
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "addressbar.abbreviation",
                abbreviationTyped &&
                    string.Equals(abbreviationAddress, canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                    abbreviationSelected,
                $"typed='{abbreviatedPath}' expected '{canonicalPath}', got '{abbreviationAddress}', selected={abbreviationSelected}.");

            bool missingTyped = EditAddress(session, missingPath);
            E2EResults.Record(
                "addressbar.invalid.typed",
                missingTyped,
                $"typed='{missingPath}' result='{AddressText(session)}'.");

            _ = Submit(session);
            bool dialog = driver.WaitForDialog(TimeSpan.FromSeconds(15));
            E2EResults.Record(
                "addressbar.invalid.dialog",
                dialog,
                $"DialogRoot appeared={dialog}.");

            string restored = session.WaitForText("AddressBar", canonicalPath, TimeSpan.FromSeconds(5))
                ?? "(missing)";
            E2EResults.Record(
                "addressbar.invalid.restored",
                string.Equals(restored, canonicalPath, StringComparison.OrdinalIgnoreCase),
                $"expected '{canonicalPath}', got '{restored}'.");

            bool stillSelected = ControlTestSupport.WaitForTreeSelection(
                session,
                config.RunId,
                TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "addressbar.invalid.selection",
                stillSelected,
                $"{config.RunId} still selected={stillSelected}.");

            bool dismissed = dialog && driver.Invoke("DialogPrimary") && driver.WaitForNoDialog();
            E2EResults.Record(
                "addressbar.invalid.dismissed",
                dismissed,
                $"primary invoked and dialog closed={dismissed}.");

            bool computerTyped = EditAddress(session, "Computer");
            _ = Submit(session);
            string computerAddress = session.WaitForText("AddressBar", "Computer", TimeSpan.FromSeconds(15))
                ?? "(missing)";
            bool computerSelected = ControlTestSupport.WaitForTreeSelection(session, "Computer");
            E2EResults.Record(
                "addressbar.computer.navigate",
                computerTyped &&
                    string.Equals(computerAddress, "Computer", StringComparison.OrdinalIgnoreCase) &&
                    computerSelected,
                $"typed={computerTyped} address='{computerAddress}' Computer selected={computerSelected}.");

            bool escapeTyped = EditAddress(session, @"HKCU\Software");
            ControlTestSupport.SendKeys(session, "Escape");
            string escaped = session.WaitForText("AddressBar", "Computer", TimeSpan.FromSeconds(5))
                ?? "(missing)";
            E2EResults.Record(
                "addressbar.escape.restore",
                escapeTyped && string.Equals(escaped, "Computer", StringComparison.OrdinalIgnoreCase),
                $"typed={escapeTyped} expected 'Computer', got '{escaped}'.");

            bool treeFocus = ControlTestSupport.WaitUntil(
                () => HasKeyboardFocus(session, ControlTestSupport.TreeControl),
                TimeSpan.FromSeconds(3));
            E2EResults.Record(
                "addressbar.escape.focus",
                treeFocus,
                $"TreeControl keyboard focus={treeFocus}.");

            FailIfNeeded();
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }
    }

    private static bool EditAddress(AppSession session, string text)
    {
        if (!FocusAddressBar(session))
        {
            return false;
        }

        AutomationElement? address = session.Find("AddressBar");
        if (address is null)
        {
            return false;
        }

        try
        {
            address.AsTextBox().Text = string.Empty;
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            ControlTestSupport.SendKeys(session, text);
        }
        catch (Exception)
        {
            return false;
        }

        return ControlTestSupport.WaitUntil(
            () => string.Equals(AddressText(session), text, StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5));
    }

    private static bool Submit(AppSession session)
    {
        try
        {
            ControlTestSupport.SendKeys(session, "Enter");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool FocusAddressBar(AppSession session)
    {
        if (!ControlTestSupport.EnsureForeground(session))
        {
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            AutomationElement? address = session.Find("AddressBar");
            if (address is null)
            {
                continue;
            }

            try
            {
                address.Focus();
            }
            catch (Exception)
            {
                try
                {
                    address.Click();
                }
                catch (Exception)
                {
                    // The next attempt reacquires the element.
                }
            }

            if (ControlTestSupport.WaitUntil(
                () => HasKeyboardFocus(session, "AddressBar"),
                TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasKeyboardFocus(AppSession session, string automationId)
    {
        AutomationElement? element = session.Find(automationId);
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

    private static string AddressText(AppSession session)
    {
        AutomationElement? address = session.Find("AddressBar");
        return address is null ? "(missing)" : session.TextOf(address);
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

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Address bar checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
