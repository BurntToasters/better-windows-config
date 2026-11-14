using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.AppServices;

/// <summary>
/// A key whose values cannot be enumerated shows the classic Cannot open key message instead of an
/// empty list. The failure is driven by the E2E-only BWC_E2E_FAIL_VALUE_ENUMERATION variable, which the
/// ship build never reads. The test writes only a seed value under the run sandbox.
/// </summary>
[TestClass]
public sealed class ValueEnumerationTests
{
    private const string Caption = "Error Opening Key";
    private const string SeedValue = "Seed";
    private const string FailEnvironment = "BWC_E2E_FAIL_VALUE_ENUMERATION";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(10);

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("AppServices")]
    [TestCategory("Mutating")]
    public void AppServices_ValueEnumerationFailureShowsClassicError()
    {
        int checkStart = E2EResults.CheckCount;
        E2EConfig config = GuiGate.Config;
        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("enum.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        E2EGuards.RequireMutatingFlavor(config);
        var sandbox = new SandboxRegistry(config);
        string dataRoot = AppServicesSupport.FreshDataRoot(config, "enumfail");
        string keyPath = @"HKEY_CURRENT_USER\" + sandbox.RunRootPath + @"\Enumerate";

        try
        {
            sandbox.Cleanup();
            sandbox.CreateKey("Enumerate");
            sandbox.SetString("Enumerate", SeedValue, "seed");

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
                [FailEnvironment] = keyPath,
            };

            using RegeditProcess app = RegeditProcess.Start(config, dataRoot, Array.Empty<string>(), environment: environment);
            Navigate(app, keyPath);

            IntPtr dialog = AppServicesSupport.WaitForOwnedWindow(app.MainHwnd, null, Caption, DialogTimeout);
            E2EResults.Record("enum.dialog.shown", dialog != IntPtr.Zero, $"Error Opening Key dialog found={dialog != IntPtr.Zero}.");
            if (dialog != IntPtr.Zero)
            {
                string text = AppServicesSupport.DialogText(dialog);
                bool classic = text.Contains("cannot be opened", StringComparison.Ordinal) &&
                    text.Contains(keyPath, StringComparison.Ordinal) &&
                    text.Contains("Not enough memory", StringComparison.OrdinalIgnoreCase);
                E2EResults.Record(
                    "enum.dialog.text",
                    classic,
                    "The message names the key and carries the system text for the enumeration failure.");
                AppServicesSupport.ClickButton(app, dialog, "OK");
                _ = AppServicesSupport.WaitUntil(() => !AppServicesSupport.IsWindow(dialog), DialogTimeout);
            }

            bool listEmpty = app.Find("ListRow_0") is null;
            E2EResults.Record("enum.list.empty", listEmpty, "The value list shows no rows for the key that could not be enumerated.");
            app.Close();
        }
        finally
        {
            sandbox.Cleanup();
        }

        FailIfNeeded(checkStart);
    }

    private static void Navigate(RegeditProcess app, string path)
    {
        app.Focus();
        AutomationElement? address = app.WaitFor("AddressBar", UiTimeout);
        if (address is null)
        {
            throw new InvalidOperationException("The address bar was not found.");
        }

        address.Focus();
        address.AsTextBox().Text = path;
        Keyboard.Press(VirtualKeyShort.ENTER);
        Thread.Sleep(300);
    }

    private static void FailIfNeeded(int startIndex)
    {
        IReadOnlyList<string> failures = E2EResults.FailedChecksSince(startIndex);
        if (failures.Count > 0)
        {
            Assert.Fail($"Checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
        }
    }
}
