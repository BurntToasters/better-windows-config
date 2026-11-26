using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Hub;

/// <summary>
/// Drives the Hub from its published output. Tests that use no E2E seam run in the ship flavor: the classic
/// window and tool list, Enter and the Alt+H keyboard path, About text, and the update settings file. Tests that
/// force the palette (BWC_E2E_THEME) or the update source (BWC_E2E_UPDATE_*) are tagged E2EOnly: the light and
/// dark captures, the pixel contrast of every dialog (<see cref="DialogContrast"/>), the not-configured message
/// (manifest URL forced to "none"), and the update checks in three states (up to date, update available, cancel).
/// The install tests press Download and Install with the mouse. A stand-in installer (tests/Bwc.E2E.FakeInstaller)
/// records the command line it is started with, and a tampered download must be refused before it runs.
/// Update checks run against a loopback server that serves signed fixtures generated for the run; nothing
/// reaches the network. Captures are written to artifacts/e2e/visual/hub.
/// </summary>
[TestClass]
public sealed class HubTests
{
    private const string HubTitle = "Better Windows Config";
    private const string RegeditTitle = "Registry Editor";
    private const string RegeditProcessName = "BwcRegedit";
    private const string GpeditTitle = "Local Group Policy Editor";
    private const string GpeditProcessName = "BwcGpedit";
    private const string UpdateProcessName = "BwcUpdate";
    private const string HelperExeName = "BwcUpdate.exe";
    private const string HubProcessName = "BetterWindowsConfig";
    private const string AboutTitle = "About Better Windows Config";
    private const string CheckTitle = "Check for Updates";
    private const string AvailableTitle = "Update Available";
    private const string SettingsTitle = "Update Settings";
    private const string FailedTitle = "Update Check Failed";
    private const string DownloadFailedTitle = "Update Download Failed";
    private const string DownloadFailedStatus = "Update download failed.";
    private const string DownloadNotVerifiedText = "did not match the signed release";
    private const string NotInstalledTitle = "Update Not Installed";
    private const string InstallScope = "hub-install";
    private const string ElevatedGateScope = "hub-elevated-gate";
    private const string GpeditLaunchScope = "hub-gpedit-launch";
    private const string RefuseScope = "hub-refuse";
    private const string MigrateScope = "hub-migrate";
    private const string KeepOffScope = "hub-keepoff";
    private const string StartupScope = "hub-startup";
    private const string ModalAboutScope = "hub-modal-about";
    private const string ModalSettingsScope = "hub-modal-settings";
    private const string ModalResultScope = "hub-modal-result";
    private const string HandoverScope = "hub-modal-handover";
    private const string CloseCheckScope = "hub-close-check";
    private const string CloseDownloadScope = "hub-close-download";
    private const string ResultScope = "hub-result";
    private const string InstalledHubCapture = "update-installed-hub.png";
    private const string FakeInstallerRecordVariable = "BWC_E2E_INSTALLER_RECORD";
    private const string RefusedCapture = "update-install-refused.png";
    private const string HashMismatchName = "InstallerHashMismatch";
    private const string InstallArgumentSwitch = "/S";
    private const string InstallRelaunchSwitch = "/RELAUNCH";
    private const string InstallParentPidPrefix = "/PARENTPID=";
    private const string InstallDirectoryArgumentPrefix = "/D=";
    private const string UpdateResultArgument = "--update-result";
    private const string InstallerFailedCode = "InstallerFailed";
    private const string UnexpectedCode = "Unexpected";
    private const uint WindowCloseMessage = 0x0010;
    private const string BoundsScope = "hub-bounds";
    private const string CheckFinishedLine = "Update check finished: UpToDate.";
    private const string SettingsFileName = "settings.json";
    private const string LegacySettingsJson = "{\n  \"UpdateChannel\": \"stable\",\n  \"CheckForUpdatesOnStartup\": false\n}";
    private const string VersionTwoOffJson = "{\n  \"CheckForUpdatesOnStartup\": false,\n  \"SettingsVersion\": 2\n}";
    private const string BetaSavedJson = "{\n  \"UpdateChannel\": \"beta\",\n  \"CheckForUpdatesOnStartup\": false,\n  \"SettingsVersion\": 2\n}";
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NoRunWindow = TimeSpan.FromSeconds(2);
    private const string LightTheme = "Light";
    private const string DarkTheme = "Dark";
    private const string NoticesScope = "hub-main";
    private const string ToolOrderScope = "hub-tool-order";
    private const string UnconfiguredScope = "hub-unconfigured";
    private const string SettingsScope = "hub-settings";
    private const string SettingsRollbackScope = "hub-settings-rollback";
    private const string LightScope = "hub-light";
    private const string UpdateScope = "hub-update";
    private const string DarkScope = "hub-dark";
    private const double LightBrightnessFloor = 150;
    private const double DarkBrightnessCeiling = 90;
    private const int HeldManifestMilliseconds = 60_000;
    private const int AboutOpenAttempts = 2;
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoDialogWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex FullCommitPattern = new("[0-9a-fA-F]{40}", RegexOptions.CultureInvariant);

    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;

        // Window rectangles and screen captures are only exact in a per-monitor DPI aware process.
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_ShowsToolListAndLaunchesRegedit()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);
        string semVer = ReadSemVer(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, NoticesScope, null));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        E2EResults.Record(
            "hub.title",
            string.Equals(hub.Window.Title, HubTitle, StringComparison.Ordinal),
            $"expected '{HubTitle}', got '{hub.Window.Title}'.");

        AutomationElement? root = WaitFor(hub, "HubRoot");
        E2EResults.Record("hub.root", root is not null, root is null ? "HubRoot was not found." : "HubRoot was found.");

        string version = WaitForText(hub, "HubVersion");
        bool shortVersion = version.StartsWith("Version " + semVer, StringComparison.Ordinal) &&
            !FullCommitPattern.IsMatch(version);
        E2EResults.Record(
            "hub.version.short",
            shortVersion,
            $"HubVersion text was '{version}'. Expected a short version starting with '{semVer}' and no 40-character hash.");

        AutomationElement? toolList = WaitFor(hub, "ToolList");
        E2EResults.Record("hub.toolList", toolList is not null, toolList is null ? "ToolList was not found." : "ToolList was found.");

        AutomationElement? row = WaitFor(hub, "ToolRow_regedit");
        E2EResults.Record("hub.toolRow", row is not null, row is null ? "ToolRow_regedit was not found." : "ToolRow_regedit was found.");

        if (row is not null)
        {
            bool installed = row.FindFirstDescendant(cf => cf.ByName("Installed")) is not null;
            E2EResults.Record(
                "hub.toolRow.status",
                installed,
                installed ? "The Registry Editor row shows Installed." : "The Registry Editor row does not show Installed.");
        }

        AutomationElement? launch = WaitFor(hub, "LaunchSelected");
        bool launchEnabled = launch is not null && launch.AsButton().IsEnabled;
        E2EResults.Record(
            "hub.launchButton.enabled",
            launchEnabled,
            launchEnabled ? "The Launch button is enabled with a tool selected." : "The Launch button is missing or disabled.");

        string status = WaitForText(hub, "HubStatus");
        E2EResults.Record("hub.status", !string.IsNullOrWhiteSpace(status), $"HubStatus text was '{status}'.");

        // The smoke test leaves its Registry Editor running, and that window can keep the foreground from the Hub.
        CloseRegeditLeftovers();
        CheckAboutDialog(hub, automation, semVer, null, "about.png", "hub.about");

        if (row is not null)
        {
            VerifyToolLaunchWithEnter(hub, row, RegeditTool(config));
        }

        _ = HubUi.Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
        CheckHelpMenuOpensWithAltH(hub, automation);

        hub.Close();
        E2EResults.Record("hub.closed", !IsRunning(hub.ProcessId), $"Hub pid {hub.ProcessId} was closed by the test.");

        AssertNoFailures("Hub window checks");
    }

    /// <summary>
    /// The Hub starts the Local Group Policy Editor from its row: the row is selected with the mouse, Enter starts the
    /// tool, a new BwcGpedit process from the publish folder shows its main window, and the test closes it. The Hub
    /// and the harness never ask for elevation: the child inherits __COMPAT_LAYER=RunAsInvoker from the Hub, which the
    /// harness sets on every process it starts, so the highestAvailable manifest of the ship tool shows no prompt
    /// (docs/failure-modes/e2e-harness.md, sections 3 and 45). The test starts no stand-in and writes no policy.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_LaunchesGpeditFromItsRow()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, GpeditLaunchScope, null));
        E2EResults.CurrentSession = hub;

        AutomationElement? row = WaitFor(hub, "ToolRow_gpedit");
        E2EResults.Record("hub.gpedit.toolRow", row is not null, row is null ? "ToolRow_gpedit was not found." : "ToolRow_gpedit was found.");
        if (row is not null)
        {
            VerifyToolLaunchWithEnter(hub, row, GpeditTool(config));
        }

        hub.Close();
        E2EResults.Record("hub.gpedit.hubClosed", !IsRunning(hub.ProcessId), $"Hub pid {hub.ProcessId} was closed by the test.");
        AssertNoFailures("Hub policy editor launch checks");
    }

    /// <summary>
    /// The Hub renders ToolCatalog.Tools in catalog order and keeps no second list. Both tools are listed, the Registry
    /// Editor first and the Local Group Policy Editor second, each row with its display name, its description and the
    /// Installed status (docs/failure-modes/gpedit-scaffold.md, Hub and catalog).
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_ListsBothToolsInCatalogOrder()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, ToolOrderScope, null));
        E2EResults.CurrentSession = hub;

        AutomationElement? toolList = WaitFor(hub, "ToolList");
        E2EResults.Record("hub.rows.list", toolList is not null, toolList is null ? "ToolList was not found." : "ToolList was found.");

        // Full row ids: a bare "regedit" literal reads as a launch of the classic tool to the registry-writer scan.
        string[] expectedIds = new[] { "ToolRow_regedit", "ToolRow_gpedit" }.Select(row => row["ToolRow_".Length..]).ToArray();
        string[] expectedNames = { "Registry Editor", "Local Group Policy Editor" };
        string[] expectedDescriptions = { "Browse and edit the Windows registry.", "Browse and edit local group policy." };
        List<AutomationElement> rows = new();
        if (toolList is not null)
        {
            // The rows are tool rows by id. The list is read after the first row exists, so the tree is built.
            _ = WaitFor(hub, "ToolRow_" + expectedIds[0]);
            rows = toolList.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                .Where(row => (row.AutomationId ?? string.Empty).StartsWith("ToolRow_", StringComparison.Ordinal))
                .ToList();
        }

        string[] actualIds = rows.Select(row => (row.AutomationId ?? string.Empty)["ToolRow_".Length..]).ToArray();
        E2EResults.Record(
            "hub.rows.order",
            actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal),
            $"Tool rows in tree order were [{string.Join(", ", actualIds)}]. Expected [{string.Join(", ", expectedIds)}].");

        int[] tops = rows.Select(row => row.BoundingRectangle.Top).ToArray();
        bool topsAscending = tops.Length == expectedIds.Length && tops.Zip(tops.Skip(1), (a, b) => a < b).All(ascending => ascending);
        E2EResults.Record(
            "hub.rows.visualOrder",
            topsAscending,
            $"Row tops on screen were [{string.Join(", ", tops)}]. Expected strictly increasing for {expectedIds.Length} rows.");

        for (int index = 0; index < Math.Min(rows.Count, expectedIds.Length); index++)
        {
            AutomationElement row = rows[index];
            string id = expectedIds[index];
            string[] texts = row.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Select(text => text.Name ?? string.Empty)
                .ToArray();
            E2EResults.Record(
                "hub.rows." + id + ".name",
                string.Equals(row.Name, expectedNames[index], StringComparison.Ordinal) && texts.Contains(expectedNames[index], StringComparer.Ordinal),
                $"Row {id} has name '{row.Name}' and cells [{string.Join(" | ", texts)}]. Expected the display name '{expectedNames[index]}'.");
            E2EResults.Record(
                "hub.rows." + id + ".description",
                texts.Contains(expectedDescriptions[index], StringComparer.Ordinal),
                $"Row {id} cells were [{string.Join(" | ", texts)}]. Expected the description '{expectedDescriptions[index]}'.");
            E2EResults.Record(
                "hub.rows." + id + ".status",
                texts.Contains("Installed", StringComparer.Ordinal),
                $"Row {id} cells were [{string.Join(" | ", texts)}]. Expected the status 'Installed'.");
        }

        hub.Close();
        AssertNoFailures("Hub tool order checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_CheckForUpdates_ReportsNotConfiguredWithoutServer()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);

        // The shipped build carries the GitHub manifest URL and the release key, so "no server" is forced through the
        // E2E-only override that Environment sets when no fixture server is given. Nothing reaches the network.
        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, UnconfiguredScope, null));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        CheckNotConfiguredDialog(hub, automation, null, "update-not-configured.png", "hub.update.unconfigured");

        hub.Close();
        AssertNoFailures("Hub not-configured checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_UpdateSettings_WritesChannelAndStartupCheck()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);

        // A saved beta channel must read as stable while no beta manifest is built in. The startup check starts off,
        // so the toggle below turns it on and the saved value must show that.
        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, SettingsScope, null, settingsJson: BetaSavedJson));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        CheckUpdateSettings(hub, automation, config, SettingsScope, null);

        hub.Close();
        AssertNoFailures("Hub update settings checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_UpdateSettings_SaveFailureRestoresPreviousChoices()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);
        string settingsPath = SettingsPath(config, SettingsRollbackScope);

        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, SettingsRollbackScope, null, settingsJson: BetaSavedJson));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        // Beta is hidden in this build, so the seeded beta value resolves to stable on Save.
        // SettingsStore replaces the file atomically; this read handle denies delete/replace sharing.
        using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            HubUi.OpenHelpMenuByMouse(hub, automation);
            HubUi.ClickMenuItem(HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_UpdateSettings", ElementTimeout));
            AutomationElement? settings = HubUi.FindWindowInProcess(automation, hub.ProcessId, SettingsTitle, DialogTimeout);
            AutomationElement? startupCheck = HubUi.WaitWithin(settings, "UpdateStartupCheck", DialogTimeout);
            bool dialogRaised = settings is not null && HubUi.RaiseDialog(
                (IntPtr)settings.Properties.NativeWindowHandle.ValueOrDefault,
                TimeSpan.FromSeconds(3));
            bool startupChanged = dialogRaised && startupCheck is not null && startupCheck.AsCheckBox().IsChecked == false;
            if (startupChanged)
            {
                startupCheck!.Click();
                startupChanged = startupCheck.AsCheckBox().IsChecked == true;
            }

            E2EResults.Record("hub.settings.rollback.startupChanged", startupChanged, "The startup choice changed from off to on before Save.");
            HubUi.InvokeButton(HubUi.WaitWithin(settings, "UpdateSettingsOk", DialogTimeout));
            AutomationElement? error = HubUi.FindWindowWithChild(automation, hub.ProcessId, SettingsTitle, "DialogMessage", DialogTimeout);
            string errorText = MessageText(error);
            bool saveFailureShown = errorText.Contains("settings could not be saved", StringComparison.OrdinalIgnoreCase);
            E2EResults.Record(
                "hub.settings.rollback.saveFailure",
                saveFailureShown,
                saveFailureShown ? errorText : $"Expected the settings-save error; observed '{errorText}'.");
            HubUi.InvokeButton(HubUi.WaitWithin(error, "DialogPrimary", DialogTimeout));
            bool errorDismissed = error is not null && HubUi.WaitForWindowGone(automation, hub.ProcessId, SettingsTitle, DialogTimeout);
            E2EResults.Record(
                "hub.settings.rollback.errorDismissed",
                errorDismissed,
                errorDismissed ? "The settings-save error was dismissed." : "The settings-save error did not close.");
        }

        hub.Close();
        (bool startup, int version, string channel) = ReadSettings(settingsPath) ?? (true, 0, string.Empty);
        bool fileRestored = !startup && version == 2 && string.Equals(channel, "beta", StringComparison.Ordinal);
        E2EResults.Note("hub.settings.rollback.file", settingsPath);
        E2EResults.Record(
            "hub.settings.rollback.fileRestored",
            fileRestored,
            $"After Hub close, scoped settings have {channel}, startup={startup}, version={version}; expected beta, false, 2.");

        AssertNoFailures("Hub update settings rollback checks");
    }

    /// <summary>
    /// A settings file written by 0.1.0 has no SettingsVersion and saved the startup check off. The first launch
    /// turns the check on and writes version 2, both at load and again at close.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_SettingsMigration_LegacyStartupCheckTurnsOnOnce()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string path = SettingsPath(config, MigrateScope);
        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, MigrateScope, null, settingsJson: LegacySettingsJson));
        E2EResults.CurrentSession = hub;
        (bool startupAtLoad, int versionAtLoad, string _) = ReadSettings(path) ?? (false, 0, string.Empty);
        E2EResults.Record(
            "hub.settings.migrate.on",
            startupAtLoad && versionAtLoad == 2,
            $"After load the settings file has CheckForUpdatesOnStartup={startupAtLoad} and SettingsVersion={versionAtLoad}. Expected true and 2.");

        hub.Close();
        (bool startupAtClose, int versionAtClose, string _) = ReadSettings(path) ?? (false, 0, string.Empty);
        E2EResults.Record(
            "hub.settings.migrate.saved",
            startupAtClose && versionAtClose == 2,
            $"After close the settings file has CheckForUpdatesOnStartup={startupAtClose} and SettingsVersion={versionAtClose}.");
        AssertNoFailures("Hub settings migration checks");
    }

    /// <summary>
    /// A version 2 file with the startup check off is a user choice. The migration must not run again and turn it on.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_SettingsVersionTwo_StartupCheckOffStaysOff()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string path = SettingsPath(config, KeepOffScope);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, KeepOffScope, null));
        E2EResults.CurrentSession = hub;
        hub.Close();

        (bool startup, int version, string _) = ReadSettings(path) ?? (true, 0, string.Empty);
        E2EResults.Record(
            "hub.settings.migrate.keepsOff",
            !startup && version == 2,
            $"After a launch and close the settings file has CheckForUpdatesOnStartup={startup} and SettingsVersion={version}. Expected false and 2.");
        AssertNoFailures("Hub settings keep-off checks");
    }

    /// <summary>
    /// The startup check runs with no settings file (the default is on). The server offers the same version, so the
    /// check finds nothing new. No window may appear: the check is silent unless an update is offered.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_StartupCheck_NoUpdateShowsNoDialog()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string installed = ReadSemVer(config);
        string dataRoot = config.DataRootFor(StartupScope);
        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] sameManifest, byte[] sameSignature) = fixture.BuildManifest(installed, server.BaseUrl);
        server.Offer(sameManifest, sameSignature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));

        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, StartupScope, null, server, fixture, startupCheckOn: true));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        bool ran = WaitForHubLog(dataRoot, CheckFinishedLine, DialogTimeout);
        E2EResults.Record(
            "hub.startup.default",
            ran,
            ran ? "With no settings file the startup check ran and finished." : $"The log had no '{CheckFinishedLine}' line within 20 s.");

        // The check has finished, so a dialog it would show appears within the window. The whole window is polled.
        List<string> shown = DialogsThatAppear(
            hub,
            automation,
            new[] { CheckTitle, AvailableTitle, FailedTitle, DownloadFailedTitle },
            NoDialogWindow);

        E2EResults.Record(
            "hub.startup.noDialog",
            ran && shown.Count == 0,
            shown.Count == 0 ? "No update window appeared after the startup check." : "A window appeared: " + string.Join(", ", shown) + ".");
        string status = WaitForText(hub, "HubStatus");
        E2EResults.Record(
            "hub.startup.status",
            string.Equals(status, "Ready.", StringComparison.Ordinal),
            $"HubStatus after a silent check was '{status}'.");

        hub.Close();
        AssertNoFailures("Hub startup check checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_StartupOfferWaitsForAboutAndSettings()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);

        CheckStartupOfferWaitsForDialog(
            config,
            ModalAboutScope,
            "MenuItem_About",
            AboutTitle,
            "AboutOk",
            "hub.modal.about");
        CheckStartupOfferWaitsForDialog(
            config,
            ModalSettingsScope,
            "MenuItem_UpdateSettings",
            SettingsTitle,
            "UpdateSettingsCancel",
            "hub.modal.settings");

        AssertNoFailures("Hub startup modal serialization checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_StartupManualHandoverShowsOneResultAfterProgress()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string installed = ReadSemVer(config);
        string dataRoot = config.DataRootFor(HandoverScope);

        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
        server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        server.HoldManifest();

        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, HandoverScope, null, server, fixture, startupCheckOn: true));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        bool startupRequested = server.WaitForManifestRequests(1, DialogTimeout);
        E2EResults.Record(
            "hub.modal.handover.startupRequest",
            startupRequested,
            startupRequested ? "The startup check reached the held fixture manifest." : "The startup check never requested the fixture manifest.");

        RunCheck(hub, automation);
        AutomationElement? progress = HubUi.FindWindowWithChild(automation, hub.ProcessId, CheckTitle, "UpdateProgressText", DialogTimeout);
        E2EResults.Record(
            "hub.modal.handover.progress",
            progress is not null,
            progress is null ? "Manual check progress did not appear." : "Manual check progress appeared while the startup check was running.");
        E2EResults.Record(
            "hub.modal.handover.progressOwnerDisabled",
            !hub.IsEnabled,
            "The Hub owner stays disabled while manual progress is open.");

        server.ReleaseManifest();
        bool finished = WaitForHubLog(dataRoot, "Update check finished: Available.", DialogTimeout);
        E2EResults.Record(
            "hub.modal.handover.checkFinished",
            finished,
            finished ? "The handed-over startup check finished with an update offer." : "The startup check did not finish with an update offer.");

        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        int resultWindows = HubUi.CountWindowsInProcess(automation, hub.ProcessId, AvailableTitle);
        bool progressClosed = HubUi.FindWindowWithChild(
            automation,
            hub.ProcessId,
            CheckTitle,
            "UpdateProgressText",
            TimeSpan.FromMilliseconds(500)) is null;
        E2EResults.Record(
            "hub.modal.handover.progressClosedBeforeResult",
            progressClosed,
            "The progress modal closes before the handed-over result is shown.");
        E2EResults.Record(
            "hub.modal.handover.singleResult",
            available is not null && resultWindows == 1,
            $"After progress, Update Available windows={resultWindows}; exactly one result must appear.");
        E2EResults.Record(
            "hub.modal.handover.resultOwnerDisabled",
            !hub.IsEnabled,
            "The Hub stays disabled while the handed-over result is open.");

        AutomationElement? later = available is null ? null : HubUi.WaitWithin(available, "UpdateLater", DialogTimeout);
        if (later is not null)
        {
            HubUi.InvokeButton(later);
        }

        E2EResults.Record(
            "hub.modal.handover.ownerRestored",
            ControlTestSupport.WaitUntil(() => hub.IsEnabled, DialogTimeout),
            "The Hub is restored after the only result closes.");

        hub.Close();
        AssertNoFailures("Hub startup/manual handover checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResultWaitsForExistingModal()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string dataRoot = config.DataRootFor(ModalResultScope);

        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
        server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        server.HoldManifest();

        using Process process = StartHubWithArguments(
            config,
            Environment(config, ModalResultScope, null, server, fixture, startupCheckOn: true),
            UpdateResultArgument,
            InstallerFailedCode);
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement? main = HubUi.FindWindowInProcess(automation, process.Id, HubTitle, ElementTimeout);
            IntPtr mainHwnd = main is null ? IntPtr.Zero : (IntPtr)main.Properties.NativeWindowHandle.ValueOrDefault;
            AutomationElement? result = HubUi.FindWindowWithChild(automation, process.Id, NotInstalledTitle, "DialogMessage", DialogTimeout);
            bool startupRequested = server.WaitForManifestRequests(1, DialogTimeout);
            E2EResults.Record(
                "hub.modal.result.initial",
                main is not null && result is not null && startupRequested,
                $"main={main is not null}, update result={result is not null}, startup manifest requested={startupRequested}.");

            server.ReleaseManifest();
            bool checkFinished = WaitForHubLog(dataRoot, "Update check finished: Available.", DialogTimeout);
            E2EResults.Record(
                "hub.modal.result.checkFinished",
                checkFinished,
                checkFinished ? "The startup offer completed while the update-result dialog was open." : "The startup offer did not finish.");
            AutomationElement? prematureOffer = HubUi.FindWindowInProcess(automation, process.Id, AvailableTitle, NoDialogWindow);
            E2EResults.Record(
                "hub.modal.result.queued",
                prematureOffer is null && mainHwnd != IntPtr.Zero && !Bwc.E2E.Harness.Win32.IsWindowEnabled(mainHwnd),
                prematureOffer is null
                    ? "No update offer appeared over the update-result dialog, and the owner stayed disabled."
                    : "Update Available appeared over Update Not Installed.");

            AutomationElement? ok = result is null ? null : HubUi.WaitWithin(result, "DialogPrimary", DialogTimeout);
            if (ok is not null)
            {
                HubUi.InvokeButton(ok);
            }

            AutomationElement? available = HubUi.FindWindowInProcess(automation, process.Id, AvailableTitle, DialogTimeout);
            int offers = HubUi.CountWindowsInProcess(automation, process.Id, AvailableTitle);
            E2EResults.Record(
                "hub.modal.result.offerAfterClose",
                available is not null && offers == 1,
                $"Update Available windows after Update Not Installed closed={offers}; exactly one must appear.");
            E2EResults.Record(
                "hub.modal.result.ownerDisabled",
                mainHwnd != IntPtr.Zero && !Bwc.E2E.Harness.Win32.IsWindowEnabled(mainHwnd),
                "The Hub remains disabled while the queued update offer is open.");

            AutomationElement? later = available is null ? null : HubUi.WaitWithin(available, "UpdateLater", DialogTimeout);
            if (later is not null)
            {
                HubUi.InvokeButton(later);
            }

            E2EResults.Record(
                "hub.modal.result.ownerRestored",
                mainHwnd != IntPtr.Zero && ControlTestSupport.WaitUntil(() => Bwc.E2E.Harness.Win32.IsWindowEnabled(mainHwnd), DialogTimeout),
                "The Hub owner is restored after the queued offer closes.");
        }
        finally
        {
            server.ReleaseManifest();
            InstallEvidence.StopProcess(process);
        }

        AssertNoFailures("Hub update-result modal serialization checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_CloseCancelsStartupCheck()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        DateTime testStartUtc = DateTime.UtcNow;

        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
        server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        server.HoldManifest();
        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, CloseCheckScope, null, server, fixture, startupCheckOn: true));
        E2EResults.CurrentSession = hub;

        bool requested = server.WaitForManifestRequests(1, DialogTimeout);
        E2EResults.Record(
            "hub.close.check.active",
            requested,
            requested ? "The startup helper reached the held manifest before close." : "No active startup request was observed.");

        hub.Close();
        E2EResults.Record(
            "hub.close.check.windowExited",
            WaitForProcessExit(hub.ProcessId, DialogTimeout),
            "The Hub process exited after close.");
        E2EResults.Record(
            "hub.close.check.helperStopped",
            WaitForNoHelper(testStartUtc, DialogTimeout),
            "No update helper from the active startup check remained after Hub close.");

        server.ReleaseManifest();
        AssertNoFailures("Hub close startup-check cancellation checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_CloseCancelsDownloadWork()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        DateTime testStartUtc = DateTime.UtcNow;
        string dataRoot = config.DataRootFor(CloseDownloadScope);

        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
        server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, CloseDownloadScope, null, server, fixture));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        RunCheck(hub, automation);
        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        AutomationElement? install = available is null ? null : HubUi.WaitWithin(available, "UpdateInstall", DialogTimeout);
        bool availableReady = available is not null && install is not null;
        E2EResults.Record(
            "hub.close.download.offer",
            availableReady,
            availableReady ? "A signed update offer appeared before download." : "The signed update offer did not appear.");
        if (!availableReady)
        {
            hub.Close();
            AssertNoFailures("Hub close download cancellation checks");
            return;
        }

        server.HoldManifest();
        bool installedClicked = HubUi.ClickDialogButton(install!, available!);
        E2EResults.Record(
            "hub.close.download.installClicked",
            installedClicked,
            installedClicked ? "Download and Install was pressed." : "The install action could not be pressed.");
        bool downloadRequested = server.WaitForManifestRequests(2, DialogTimeout);
        AutomationElement? progress = HubUi.FindWindowWithChild(automation, hub.ProcessId, CheckTitle, "UpdateProgressText", DialogTimeout);
        E2EResults.Record(
            "hub.close.download.active",
            downloadRequested && progress is not null,
            $"download manifest requested={downloadRequested}, progress window={progress is not null}.");

        using Process hubProcess = Process.GetProcessById(hub.ProcessId);
        bool closePosted = PostMessageW(hub.MainHwnd, WindowCloseMessage, IntPtr.Zero, IntPtr.Zero);
        E2EResults.Record(
            "hub.close.download.closePosted",
            closePosted,
            closePosted ? "A close request was posted to the Hub owner during download." : "The Hub owner rejected the close request.");
        bool exited = hubProcess.WaitForExit((int)DialogTimeout.TotalMilliseconds);
        E2EResults.Record(
            "hub.close.download.windowExited",
            exited,
            exited ? "The Hub exited while download progress was open." : "The Hub stayed open while download progress was open.");
        E2EResults.Record(
            "hub.close.download.helperStopped",
            WaitForNoHelper(testStartUtc, DialogTimeout),
            "No download helper remained after Hub close.");
        E2EResults.Record(
            "hub.close.download.noLateOffer",
            HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, TimeSpan.FromSeconds(1)) is null,
            "No late update offer appeared after the Hub closed.");

        server.ReleaseManifest();
        AssertNoFailures("Hub close download cancellation checks");
    }

    /// <summary>
    /// The helper relaunched the Hub with --update-result InstallerFailed. The Hub must say the update did not install,
    /// that the current version is still in place, and must not show the failure name.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_InstallerFailedSaysNotInstalled()
    {
        CheckUpdateResultMessage(InstallerFailedCode, "hub.updateResult.installerFailed");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>
    /// The helper reports Unexpected when it stops on an error it did not expect. The Hub must say the update did not
    /// install because of an unexpected error, name the current version, and must not show the code name.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_UnexpectedSaysNotInstalled()
    {
        CheckUpdateResultMessage(UnexpectedCode, "hub.updateResult.unexpected");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>The per-machine update: the user pressed Cancel on the Windows permission prompt.
    /// Nothing was changed.</summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_ElevationDeclinedSaysCanceled()
    {
        CheckUpdateResultMessage("ElevationDeclined", "hub.updateResult.elevationDeclined", "was canceled", "have not been changed");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>The helper could not get administrator rights, so it did nothing.</summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_NotElevatedSaysNoAdminRights()
    {
        CheckUpdateResultMessage("NotElevated", "hub.updateResult.notElevated", "did not install", "administrator rights", "still in place");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>Another Windows session has Better Windows Config open, so the update stopped before it
    /// changed anything.</summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_RunningInOtherSessionSaysCloseThere()
    {
        CheckUpdateResultMessage("RunningInOtherSession", "hub.updateResult.otherSession", "did not install", "another Windows session", "still in place");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>The safety net text for a helper that could not start the Hub again at the user's level.</summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateResult_DeElevationFailedSaysOpenFromStartMenu()
    {
        CheckUpdateResultMessage("DeElevationFailed", "hub.updateResult.deElevation", "could not be started again", "Start menu");
        AssertNoFailures("Hub update result checks");
    }

    /// <summary>
    /// Starts the Hub with --update-result code, then checks the window, the message, and OK. The message must hold every
    /// phrase (by default the 'did not install ... still in place' pair), name the current version unless the text is the
    /// canceled one, and never show the code name.
    /// </summary>
    private static void CheckUpdateResultMessage(string code, string checkId, params string[] phrases)
    {
        string[] required = phrases.Length > 0 ? phrases : ["did not install", "still in place"];
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string semVer = ReadSemVer(config);

        using Process hubProcess = StartHubWithArguments(
            config,
            Environment(config, ResultScope, null),
            UpdateResultArgument,
            code);
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement? main = HubUi.FindWindowInProcess(automation, hubProcess.Id, HubTitle, ElementTimeout);
            E2EResults.Record(
                "hub.updateResult.window",
                main is not null,
                main is null ? "The Hub started with --update-result showed no main window." : "The Hub showed its main window.");

            AutomationElement? dialog = HubUi.FindWindowWithChild(automation, hubProcess.Id, NotInstalledTitle, "DialogMessage", DialogTimeout);
            string text = MessageText(dialog);
            bool plain = required.All(phrase => text.Contains(phrase, StringComparison.Ordinal)) &&
                text.Contains(semVer, StringComparison.Ordinal) &&
                !text.Contains(code, StringComparison.Ordinal);
            E2EResults.Record(
                checkId,
                plain,
                $"The message was '{text}'. It must hold [{string.Join("; ", required)}], name {semVer}, and not show {code}.");

            if (dialog is not null)
            {
                AutomationElement? ok = HubUi.WaitWithin(dialog, "DialogPrimary", DialogTimeout);
                if (ok is not null)
                {
                    HubUi.InvokeButton(ok);
                }
            }

            E2EResults.Record(
                checkId + ".closed",
                HubUi.WaitForWindowGone(automation, hubProcess.Id, NotInstalledTitle, DialogTimeout),
                "The Update Not Installed window closed on OK.");
        }
        finally
        {
            InstallEvidence.StopProcess(hubProcess);
        }
    }

    private static void CheckStartupOfferWaitsForDialog(
        E2EConfig config,
        string scope,
        string menuItemId,
        string dialogTitle,
        string closeButtonId,
        string checkPrefix)
    {
        string installed = ReadSemVer(config);
        string dataRoot = config.DataRootFor(scope);
        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
        server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        server.HoldManifest();

        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, scope, null, server, fixture, startupCheckOn: true));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? menuItem = HubUi.FindInProcess(automation, hub.ProcessId, menuItemId, ElementTimeout);
        HubUi.ClickMenuItem(menuItem);
        AutomationElement? existing = HubUi.FindWindowInProcess(automation, hub.ProcessId, dialogTitle, DialogTimeout);
        bool requestStarted = server.WaitForManifestRequests(1, DialogTimeout);
        E2EResults.Record(
            checkPrefix + ".existing",
            existing is not null && requestStarted,
            $"Existing modal={existing is not null}, startup manifest requested={requestStarted}.");

        server.ReleaseManifest();
        bool offerCompleted = WaitForHubLog(dataRoot, "Update check finished: Available.", DialogTimeout);
        E2EResults.Record(
            checkPrefix + ".completed",
            offerCompleted,
            offerCompleted ? "The startup check found an update while the existing modal was open." : "The startup check did not finish with an offer.");

        AutomationElement? premature = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, NoDialogWindow);
        E2EResults.Record(
            checkPrefix + ".queued",
            premature is null && !hub.IsEnabled,
            premature is null
                ? "The offer stayed queued and the Hub stayed disabled under the existing modal."
                : "Update Available appeared over the existing modal.");

        AutomationElement? close = existing is null ? null : HubUi.WaitWithin(existing, closeButtonId, DialogTimeout);
        if (close is not null)
        {
            HubUi.InvokeButton(close);
        }

        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        int offerCount = HubUi.CountWindowsInProcess(automation, hub.ProcessId, AvailableTitle);
        E2EResults.Record(
            checkPrefix + ".singleOffer",
            available is not null && offerCount == 1,
            $"After the existing modal closed, Update Available windows={offerCount}; exactly one must appear.");
        E2EResults.Record(
            checkPrefix + ".ownerDisabled",
            !hub.IsEnabled,
            "The Hub owner remains disabled while the queued offer is open.");

        E2EResults.Record(
            checkPrefix + ".version",
            available is not null && HubUi.TextOf(HubUi.WaitWithin(available, "UpdateAvailableText", DialogTimeout) ?? available)
                .Contains(installed, StringComparison.Ordinal),
            $"The queued offer names installed version {installed}.");

        AutomationElement? later = available is null ? null : HubUi.WaitWithin(available, "UpdateLater", DialogTimeout);
        if (later is not null)
        {
            HubUi.InvokeButton(later);
        }

        E2EResults.Record(
            checkPrefix + ".ownerRestored",
            ControlTestSupport.WaitUntil(() => hub.IsEnabled, DialogTimeout),
            "The Hub owner is restored after the offer closes.");

        hub.Close();
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static bool WaitForNoHelper(DateTime startedUtc, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (!AnyHelperStartedSince(startedUtc, Process.GetProcessesByName(UpdateProcessName)))
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// A saved normal rectangle with WindowMaximized starts the Hub maximized. Closing it again must save the normal
    /// rectangle (not the maximized one) and keep the maximized flag.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_WindowBounds_RestoresMaximizedNormalRect()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);
        string path = SettingsPath(config, BoundsScope);
        const string json = "{\n  \"CheckForUpdatesOnStartup\": false,\n  \"SettingsVersion\": 2," +
            "\n  \"WindowBounds\": { \"X\": 120, \"Y\": 140, \"Width\": 720, \"Height\": 520 },\n  \"WindowMaximized\": true\n}";

        using Process hubProcess = StartHubWithArguments(config, Environment(config, BoundsScope, null, settingsJson: json));
        try
        {
            using var automation = new UIA3Automation();
            AutomationElement? main = HubUi.FindWindowInProcess(automation, hubProcess.Id, HubTitle, ElementTimeout);
            WindowVisualState? state = main?.Patterns.Window.PatternOrDefault?.WindowVisualState.ValueOrDefault;
            E2EResults.Record(
                "hub.bounds.maximized",
                state == WindowVisualState.Maximized,
                $"A saved WindowMaximized true must start the Hub maximized; the window state was '{state}'.");
        }
        finally
        {
            InstallEvidence.StopProcess(hubProcess);
        }

        JsonElement? saved = ReadSettingsRoot(path);
        bool maximizedKept = saved is { } root && root.TryGetProperty("WindowMaximized", out JsonElement flag) &&
            flag.ValueKind == JsonValueKind.True;
        E2EResults.Record(
            "hub.bounds.maximizedSaved",
            maximizedKept,
            "The close kept WindowMaximized true in the settings file.");

        bool normal = saved is { } normalRoot && normalRoot.TryGetProperty("WindowBounds", out JsonElement bounds) &&
            bounds.GetProperty("X").GetInt32() == 120 && bounds.GetProperty("Y").GetInt32() == 140 &&
            bounds.GetProperty("Width").GetInt32() == 720 && bounds.GetProperty("Height").GetInt32() == 520;
        E2EResults.Record(
            "hub.bounds.normalRect",
            normal,
            "The close saved the restored rectangle 120,140 720x520, not the maximized size.");

        AssertNoFailures("Hub window bounds checks");
    }

    /// <summary>Reads the settings file root, or null when the file does not exist.</summary>
    private static JsonElement? ReadSettingsRoot(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_LightTheme_CapturesAndDialogContrast()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string semVer = ReadSemVer(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, LightScope, LightTheme));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        string mainCapture = CaptureMain(hub, "main-light.png");
        CheckToolIcons(hub, automation, mainCapture, "hub.main.light.icon");
        double lightBrightness = HubUi.AverageBrightness(mainCapture);
        E2EResults.Record(
            "hub.main.light",
            lightBrightness >= LightBrightnessFloor,
            $"Light capture average brightness {lightBrightness:0.0}, expected at least {LightBrightnessFloor}.");

        CheckAboutDialog(hub, automation, semVer, LightTheme, "about.png", "hub.about");
        CheckNotConfiguredDialog(hub, automation, LightTheme, "update-not-configured.png", "hub.update.unconfigured");
        CheckUpdateSettings(hub, automation, config, LightScope, LightTheme);

        hub.Close();
        AssertNoFailures("Hub light theme checks");
    }

    /// <summary>
    /// Runs Help > Check for Updates on a Hub with no update server, checks the message and the status line,
    /// and captures the message window in the given theme. <paramref name="checkPrefix"/> names the checks.
    /// A null theme means no palette is forced, so the capture and its contrast check are skipped.
    /// </summary>
    private static void CheckNotConfiguredDialog(
        AppSession hub,
        UIA3Automation automation,
        string? theme,
        string fileName,
        string checkPrefix)
    {
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? item = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        E2EResults.Record(checkPrefix + ".menu", item is not null, "Help > Check for Updates was found.");
        HubUi.ClickMenuItem(item);

        AutomationElement? dialog = HubUi.FindWindowInProcess(automation, hub.ProcessId, CheckTitle, DialogTimeout);
        AutomationElement? message = HubUi.WaitWithin(dialog, "DialogMessage", DialogTimeout);
        string text = message is null ? string.Empty : HubUi.TextOf(message);
        bool deliberate = text.Contains("not configured for this build yet", StringComparison.Ordinal) &&
            !text.Contains("error", StringComparison.OrdinalIgnoreCase);
        E2EResults.Record(
            checkPrefix + ".dialog",
            deliberate,
            $"Check for Updates message was '{text}'.");

        if (dialog is not null)
        {
            if (theme is not null)
            {
                CaptureDialog(dialog, fileName, checkPrefix + ".contrast", theme);
            }

            AutomationElement? ok = HubUi.WaitWithin(dialog, "DialogPrimary", DialogTimeout);
            if (ok is not null)
            {
                HubUi.InvokeButton(ok);
            }
        }

        bool gone = HubUi.WaitForWindowGone(automation, hub.ProcessId, CheckTitle, DialogTimeout);
        E2EResults.Record(checkPrefix + ".closed", gone, "The message window closed.");

        string status = WaitForText(hub, "HubStatus");
        E2EResults.Record(
            checkPrefix + ".status",
            string.Equals(status, "Updates are not configured.", StringComparison.Ordinal),
            $"HubStatus after the check was '{status}'.");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_CheckForUpdates_LocalManifest_UpToDateAvailableAndCancel()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string installed = ReadSemVer(config);
        DateTime testStartUtc = DateTime.UtcNow;

        using var server = new UpdateFixtureServer();
        using var fixture = UpdateFixture.Create(config);
        (byte[] sameManifest, byte[] sameSignature) = fixture.BuildManifest(installed, server.BaseUrl);
        (byte[] newerManifest, byte[] newerSignature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);

        using var hub = new AppSession(
            config,
            config.HubExePath,
            environment: Environment(config, UpdateScope, LightTheme, server, fixture));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        // Same version: the helper refuses with SameVersion, which the Hub shows as up to date.
        server.Offer(sameManifest, sameSignature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        RunCheck(hub, automation);
        // The interactive check shows its progress window under the same title first, so the lookup waits
        // for the window that holds the result message.
        AutomationElement? upToDate = HubUi.FindWindowWithChild(automation, hub.ProcessId, CheckTitle, "DialogMessage", DialogTimeout);
        string upToDateText = MessageText(upToDate);
        E2EResults.Record(
            "hub.update.uptodate.dialog",
            upToDateText.Contains("is up to date", StringComparison.Ordinal),
            $"Up-to-date message was '{upToDateText}'.");
        if (upToDate is not null)
        {
            CaptureDialog(upToDate, "update-up-to-date.png", "hub.update.uptodate.contrast", LightTheme);
            HubUi.InvokeButton(HubUi.WaitWithin(upToDate, "DialogPrimary", DialogTimeout));
        }

        E2EResults.Record(
            "hub.update.uptodate.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, CheckTitle, DialogTimeout),
            "The up-to-date window closed.");
        E2EResults.Record(
            "hub.update.uptodate.status",
            string.Equals(WaitForText(hub, "HubStatus"), "Up to date.", StringComparison.Ordinal),
            "HubStatus reports Up to date.");

        // Newer version. The progress window closes when the check ends, so the manifest is held until the window
        // has been checked and captured. The hold is released right after the capture, whatever the machine's speed.
        server.Offer(newerManifest, newerSignature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));
        server.HoldManifest();
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? checkItem = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        HubUi.ClickMenuItem(checkItem);

        AutomationElement? progress = HubUi.FindWindowInProcess(automation, hub.ProcessId, CheckTitle, DialogTimeout);
        AutomationElement? progressText = HubUi.WaitWithin(progress, "UpdateProgressText", DialogTimeout);
        E2EResults.Record(
            "hub.update.progress.dialog",
            progressText is not null,
            progressText is null ? "The progress window did not show." : "The progress window showed while the check ran.");
        if (progress is not null)
        {
            CaptureDialog(progress, "update-progress.png", "hub.update.progress.contrast", LightTheme);
        }

        // The check may finish now: the progress window has been checked, and the manifest answer closes it.
        server.ReleaseManifest();

        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        E2EResults.Record(
            "hub.update.available.dialog",
            available is not null,
            available is null ? "The Update Available window did not show." : "The Update Available window showed.");
        if (available is not null)
        {
            string text = HubUi.TextOf(HubUi.WaitWithin(available, "UpdateAvailableText", DialogTimeout) ?? available);
            bool offersNewer = text.Contains(UpdateFixture.NewerVersion, StringComparison.Ordinal) &&
                text.Contains(installed, StringComparison.Ordinal);
            E2EResults.Record("hub.update.available.text", offersNewer, $"Update Available text was '{text}'.");
            E2EResults.Record(
                "hub.update.available.permission",
                text.Contains("Windows will ask for permission to install", StringComparison.Ordinal),
                $"Update Available text must say Windows will ask for permission to install. It was '{text}'.");

            AutomationElement? notes = HubUi.WaitWithin(available, "UpdateNotesLink", DialogTimeout);
            E2EResults.Record(
                "hub.update.available.notes",
                notes is not null,
                notes is null ? "The release notes link is missing." : "The release notes link is shown.");

            AutomationElement? install = HubUi.WaitWithin(available, "UpdateInstall", DialogTimeout);
            E2EResults.Record(
                "hub.update.available.install",
                install is not null,
                install is null ? "Download and Install is missing." : "Download and Install is shown.");

            CaptureDialog(available, "update-available.png", "hub.update.available.contrast", LightTheme);
            AutomationElement? later = HubUi.WaitWithin(available, "UpdateLater", DialogTimeout);
            if (later is not null)
            {
                HubUi.InvokeButton(later);
            }
        }

        E2EResults.Record(
            "hub.update.available.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, AvailableTitle, DialogTimeout),
            "The Update Available window closed on Later.");
        string availableStatus = WaitForText(hub, "HubStatus");
        E2EResults.Record(
            "hub.update.available.status",
            string.Equals(availableStatus, "Update " + UpdateFixture.NewerVersion + " is available.", StringComparison.Ordinal),
            $"HubStatus after Later was '{availableStatus}'.");

        // Cancel: the manifest is held, the user presses Cancel, and the helper must be stopped.
        server.ManifestDelayMilliseconds = HeldManifestMilliseconds;
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? cancelItem = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        HubUi.ClickMenuItem(cancelItem);
        AutomationElement? held = HubUi.FindWindowInProcess(automation, hub.ProcessId, CheckTitle, DialogTimeout);
        Thread.Sleep(500);
        if (held is not null)
        {
            AutomationElement? cancel = HubUi.WaitWithin(held, "DialogPrimary", DialogTimeout);
            if (cancel is not null)
            {
                HubUi.InvokeButton(cancel);
            }
        }

        E2EResults.Record(
            "hub.update.cancel.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, CheckTitle, DialogTimeout),
            "The progress window closed on Cancel.");
        string canceledStatus = WaitForStatus(hub, "The update check was canceled.", ElementTimeout);
        E2EResults.Record(
            "hub.update.cancel.status",
            string.Equals(canceledStatus, "The update check was canceled.", StringComparison.Ordinal),
            $"HubStatus after Cancel was '{canceledStatus}'.");
        E2EResults.Record(
            "hub.update.cancel.helperStopped",
            !AnyHelperStartedSince(testStartUtc, Process.GetProcessesByName(UpdateProcessName)),
            "No BwcUpdate process from this test is still running after Cancel.");

        hub.Close();
        AssertNoFailures("Hub update checks");
    }

    /// <summary>
    /// The real helper, started with the E2E-only force-elevated seam, refuses --check and --download with RunningElevated
    /// (update-elevation.md HK1, HB4): exit code 1, the failure in its JSON line, and no connection to the feed.
    /// Nothing is elevated, and the seam does not exist in a ship build.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Helper_Check_Refuses_When_Forced_Elevated()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string helperPath = Path.Combine(config.PublishDir, HelperExeName);

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            string baseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            foreach (string mode in new[] { "--check", "--download" })
            {
                var startInfo = new ProcessStartInfo(helperPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = config.PublishDir,
                };
                foreach (string argument in new[]
                {
                    mode, "--manifest-url", baseUrl + "/m.json", "--signature-url", baseUrl + "/m.json.minisig",
                    "--channel", "stable", "--current-version", "0.1.0",
                })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(ElevatedGateScope))
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }

                foreach (KeyValuePair<string, string> pair in config.TempEnvironment(ElevatedGateScope))
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }

                startInfo.Environment["BWC_E2E_UPDATE_FORCE_ELEVATED"] = "1";
                startInfo.Environment["BWC_E2E_UPDATE_ALLOW_HTTP"] = "1";

                using Process process = Process.Start(startInfo)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                bool exited = process.WaitForExit(30000);
                if (!exited)
                {
                    process.Kill(entireProcessTree: true);
                }

                string text = exited ? output.GetAwaiter().GetResult() : string.Empty;
                E2EResults.Record($"hub.elevatedGate{mode}.exit", exited && process.ExitCode == 1, $"{mode} exited {(exited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "never")}.");
                E2EResults.Record(
                    $"hub.elevatedGate{mode}.failure",
                    text.Contains("\"failure\":\"RunningElevated\"", StringComparison.Ordinal),
                    $"{mode} printed: {text.Trim()}");
            }

            E2EResults.Record(
                "hub.elevatedGate.noConnection",
                !listener.Pending(),
                "The feed was never contacted by a helper that believes it is elevated.");
        }
        finally
        {
            listener.Stop();
        }

        AssertNoFailures("Elevated gate checks");
    }

    /// <summary>
    /// The whole install path from the Hub: Help > Check for Updates finds a newer signed version,
    /// Download and Install is pressed with the mouse, and the helper verifies the stand-in and starts it
    /// with the contract's command line. The stand-in then starts the Hub again, as the installer does.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateAvailable_InstallRunsVerifiedInstaller()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string installStaging = InstallEvidence.StagingRoot(config, InstallScope);
        HashSet<string> stagingBefore = InstallEvidence.StagingFolders(installStaging);
        string installProtected = InstallEvidence.ProtectedRoot(config, InstallScope);
        HashSet<string> protectedBefore = InstallEvidence.StagingFolders(installProtected);
        string recordPath = Path.Combine(config.DataRootFor(InstallScope), InstallEvidence.RecordFileName);
        DateTime testStartUtc = DateTime.UtcNow;

        try
        {
            using var server = new UpdateFixtureServer();
            using var fixture = UpdateFixture.Create(config);
            (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
            server.Offer(manifest, signature, fixture.InstallerFor(UpdateFixture.ArchX64), fixture.InstallerFor(UpdateFixture.ArchArm64));

            using var hub = new AppSession(
                config,
                config.HubExePath,
                environment: Environment(config, InstallScope, null, server, fixture, recordPath));
            E2EResults.CurrentSession = hub;
            using var automation = new UIA3Automation();

            VerifyInstallFromHub(config, hub, automation, fixture, recordPath);
            hub.Close();
        }
        finally
        {
            // Also on a throw: a relaunched Hub left running makes every later run refuse to install.
            int leftover = InstallEvidence.StopStartedProcesses(HubProcessName, config.HubExePath, testStartUtc);
            if (leftover > 0)
            {
                E2EResults.Note("hub.install.leftoverHubs", $"Stopped {leftover} Hub process(es) this test started.");
            }

            InstallEvidence.RemoveStagingSince(installStaging, stagingBefore);
            InstallEvidence.RemoveStagingSince(installProtected, protectedBefore);
        }

        AssertNoFailures("Hub install checks");
    }

    /// <summary>
    /// The refusal through the GUI. The server sends an installer whose bytes differ from the signed hash. The helper
    /// refuses during the download, so the stand-in never runs, no Hub starts, and the Hub shows the refusal.
    /// </summary>
    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_UpdateAvailable_TamperedInstallerIsRefused()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string refuseStaging = InstallEvidence.StagingRoot(config, RefuseScope);
        HashSet<string> stagingBefore = InstallEvidence.StagingFolders(refuseStaging);
        string recordPath = Path.Combine(config.DataRootFor(RefuseScope), InstallEvidence.RecordFileName);

        try
        {
            using var server = new UpdateFixtureServer();
            using var fixture = UpdateFixture.Create(config);
            (byte[] manifest, byte[] signature) = fixture.BuildManifest(UpdateFixture.NewerVersion, server.BaseUrl);
            // Only the architecture under test is tampered: that is the installer the Hub downloads.
            bool x64 = config.Rid == UpdateFixture.ArchX64;
            server.Offer(
                manifest,
                signature,
                x64 ? fixture.TamperedInstallerFor(UpdateFixture.ArchX64) : fixture.InstallerFor(UpdateFixture.ArchX64),
                x64 ? fixture.InstallerFor(UpdateFixture.ArchArm64) : fixture.TamperedInstallerFor(UpdateFixture.ArchArm64));

            using var hub = new AppSession(
                config,
                config.HubExePath,
                environment: Environment(config, RefuseScope, LightTheme, server, fixture, recordPath));
            E2EResults.CurrentSession = hub;
            using var automation = new UIA3Automation();

            RefuseTamperedInstall(config, hub, automation, recordPath, stagingBefore);
            hub.Close();
        }
        finally
        {
            InstallEvidence.RemoveStagingSince(refuseStaging, stagingBefore);
        }

        AssertNoFailures("Hub refused install checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_DarkTheme_MainWindowCapture()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, DarkScope, DarkTheme));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        string capture = CaptureMain(hub, "main-dark.png");
        CheckToolIcons(hub, automation, capture, "hub.main.dark.icon");
        double brightness = HubUi.AverageBrightness(capture);
        E2EResults.Record(
            "hub.main.dark",
            brightness <= DarkBrightnessCeiling,
            $"Dark capture average brightness {brightness:0.0}, expected at most {DarkBrightnessCeiling}.");

        hub.Close();
        AssertNoFailures("Hub dark theme checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    [TestCategory("E2EOnly")]
    public void Hub_DarkTheme_DialogsAreReadable()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        EnsureExecutables(config);
        string semVer = ReadSemVer(config);

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, DarkScope, DarkTheme));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        CheckAboutDialog(hub, automation, semVer, DarkTheme, "about-dark.png", "hub.dark.about");
        CheckNotConfiguredDialog(hub, automation, DarkTheme, "update-not-configured-dark.png", "hub.dark.update.unconfigured");

        hub.Close();
        AssertNoFailures("Hub dark dialog checks");
    }

    /// <summary>
    /// Closes the Registry Editor processes this run started and left running, so one cannot hold the foreground
    /// from the Hub. A process is closed only when its image is the publish folder's BwcRegedit.exe and it started
    /// after this run began. A developer's Registry Editor, or one from another folder, is never touched.
    /// </summary>
    private static void CloseRegeditLeftovers()
    {
        E2EConfig config = GuiGate.Config;
        foreach (Process process in Process.GetProcessesByName(RegeditProcessName))
        {
            if (!IsRunOwnedRegedit(process, config))
            {
                process.Dispose();
                continue;
            }

            E2EResults.Note("hub.regedit.leftover", $"closing BwcRegedit pid {process.Id} this run started earlier.");
            CloseChild(process, RegeditTool(config));
        }
    }

    /// <summary>A tool the Hub starts: its check id prefix, process name, publish image and window title.</summary>
    private sealed record LaunchTool(string CheckPrefix, string ProcessName, string ExePath, string Title);

    private static LaunchTool RegeditTool(E2EConfig config) => new("hub.regedit", RegeditProcessName, config.RegeditExePath, RegeditTitle);

    private static LaunchTool GpeditTool(E2EConfig config) => new("hub.gpedit", GpeditProcessName, config.GpeditExePath, GpeditTitle);

    /// <summary>True when the process runs the publish folder's Registry Editor and started after this run began.</summary>
    private static bool IsRunOwnedRegedit(Process process, E2EConfig config) =>
        StartedSince(process, config.StartedUtc) && InstallEvidence.HasImage(process, config.RegeditExePath);

    private static void CheckHelpMenuOpensWithAltH(AppSession hub, UIA3Automation automation)
    {
        HubUi.OpenHelpMenu(hub.MainHwnd);
        AutomationElement? check = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        E2EResults.Record(
            "hub.keyboard.altH",
            check is not null,
            check is null ? "Alt+H did not open the Help menu." : "Alt+H opened the Help menu.");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
    }

    /// <summary>
    /// Opens Help > About, checks its text, captures it in the given theme, and closes it. <paramref name="checkPrefix"/>
    /// names the checks, so the light and dark runs record different ids. A null theme means no palette is forced,
    /// so the capture and its contrast check are skipped.
    /// </summary>
    private static void CheckAboutDialog(
        AppSession hub,
        UIA3Automation automation,
        string semVer,
        string? theme,
        string fileName,
        string checkPrefix)
    {
        // A foreign window can take the foreground between steps, and then the Help menu click lands nowhere. Each
        // attempt makes the Hub foreground first, and the foreground is recorded so a failure names the window that won.
        AutomationElement? window = null;
        for (int attempt = 1; attempt <= AboutOpenAttempts && window is null; attempt++)
        {
            E2EResults.Note(checkPrefix + ".foreground.attempt" + attempt, HubUi.DescribeForeground(hub.MainHwnd));
            _ = HubUi.ForceForeground(hub.MainHwnd, TimeSpan.FromSeconds(3));
            HubUi.OpenHelpMenuByMouse(hub, automation);
            AutomationElement? about = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_About", ElementTimeout);
            HubUi.ClickMenuItem(about);
            window = HubUi.FindWindowInProcess(automation, hub.ProcessId, AboutTitle, DialogTimeout);
        }

        if (window is null)
        {
            E2EResults.Note(checkPrefix + ".foreground.failed", HubUi.DescribeForeground(hub.MainHwnd));
        }

        E2EResults.Record(checkPrefix + ".open", window is not null, window is null ? "About did not open." : "About opened.");
        if (window is null)
        {
            return;
        }

        string text = HubUi.TextOf(HubUi.WaitWithin(window, "AboutText", DialogTimeout) ?? window);
        E2EResults.Record(
            checkPrefix + ".version",
            text.Contains(semVer, StringComparison.Ordinal) && !FullCommitPattern.IsMatch(text),
            $"About text does not carry the full 40-character hash and shows {semVer}.");
        E2EResults.Record(
            checkPrefix + ".license",
            text.Contains("MPL-2.0", StringComparison.Ordinal),
            "About shows the MPL-2.0 license.");

        bool noNotices = HubUi.WaitWithin(window, "AboutNotices", TimeSpan.FromSeconds(2)) is null &&
            text.Contains("does not include a third-party notices file", StringComparison.Ordinal);
        E2EResults.Record(
            checkPrefix + ".notices",
            noNotices,
            "With no THIRD-PARTY-NOTICES.txt beside the Hub, About shows no notices button and says so.");

        if (theme is not null)
        {
            CaptureDialog(window, fileName, checkPrefix + ".contrast", theme);
        }

        AutomationElement? ok = HubUi.WaitWithin(window, "AboutOk", DialogTimeout);
        if (ok is not null)
        {
            HubUi.InvokeButton(ok);
        }

        E2EResults.Record(
            checkPrefix + ".closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, AboutTitle, DialogTimeout),
            "About closed.");
    }

    private static void CheckUpdateSettings(AppSession hub, UIA3Automation automation, E2EConfig config, string scope, string? theme)
    {
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? settings = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_UpdateSettings", ElementTimeout);
        HubUi.ClickMenuItem(settings);

        AutomationElement? window = HubUi.FindWindowInProcess(automation, hub.ProcessId, SettingsTitle, DialogTimeout);
        E2EResults.Record("hub.settings.open", window is not null, window is null ? "Update Settings did not open." : "Update Settings opened.");
        if (window is null)
        {
            return;
        }

        if (theme is not null)
        {
            CaptureDialog(window, "update-settings.png", "hub.settings.contrast", theme);
        }

        // No beta manifest is built in, so the dialog must not offer Beta.
        AutomationElement? beta = HubUi.WaitWithin(window, "UpdateChannelBeta", TimeSpan.FromSeconds(2));
        E2EResults.Record(
            "hub.settings.noBeta",
            beta is null,
            beta is null ? "Update Settings does not offer Beta." : "Update Settings offers Beta with no beta manifest built in.");
        AutomationElement? startup = HubUi.WaitWithin(window, "UpdateStartupCheck", DialogTimeout);
        bool startupBefore = startup is not null && startup.AsCheckBox().IsChecked == true;

        // The toggle is a mouse click, so it is sent only once the dialog is confirmed foreground.
        // Otherwise a click lands on the Hub or on a window that covers it, and the saved-settings check fails.
        if (HubUi.RaiseDialog((IntPtr)window.Properties.NativeWindowHandle.ValueOrDefault, TimeSpan.FromSeconds(3)))
        {
            startup?.Click();
        }

        AutomationElement? save = HubUi.WaitWithin(window, "UpdateSettingsOk", DialogTimeout);
        if (save is not null)
        {
            HubUi.InvokeButton(save);
        }

        E2EResults.Record(
            "hub.settings.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, SettingsTitle, DialogTimeout),
            "Update Settings closed on OK.");

        string path = SettingsPath(config, scope);
        (bool startupOn, int _, string channel) = ReadSettings(path) ?? (false, 0, "(no file)");
        bool toggled = startupOn == !startupBefore;
        E2EResults.Record(
            "hub.settings.saved",
            toggled && channel == "stable",
            $"settings file has UpdateChannel='{channel}' CheckForUpdatesOnStartup={startupOn}; the toggle started at {startupBefore}.");
        E2EResults.Record(
            "hub.settings.betaIsStable",
            channel == "stable",
            $"A saved beta channel is saved as stable when no beta manifest is built in; the file says '{channel}'.");
    }

    private static string SettingsPath(E2EConfig config, string scope) =>
        Path.Combine(config.DataRootFor(scope), "hub", SettingsFileName);

    /// <summary>Writes the settings file a test starts the Hub with. The Hub reads it at launch.</summary>
    private static void SeedSettings(E2EConfig config, string scope, string json)
    {
        string path = SettingsPath(config, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>The fields the migration and the startup check set. Null when the file does not exist.</summary>
    private static (bool Startup, int Version, string Channel)? ReadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        bool startup = root.TryGetProperty("CheckForUpdatesOnStartup", out JsonElement startupValue) &&
            startupValue.ValueKind == JsonValueKind.True;
        int version = root.TryGetProperty("SettingsVersion", out JsonElement versionValue) &&
            versionValue.ValueKind == JsonValueKind.Number
            ? versionValue.GetInt32()
            : 0;
        string channel = root.TryGetProperty("UpdateChannel", out JsonElement channelValue)
            ? channelValue.GetString() ?? string.Empty
            : string.Empty;
        return (startup, version, channel);
    }

    /// <summary>
    /// Polls the status line until it shows the expected text or the timeout passes. Returns the last text read, so a
    /// failure names what the line showed at the end.
    /// </summary>
    private static string WaitForStatus(AppSession hub, string expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string text;
        do
        {
            text = WaitForText(hub, "HubStatus");
            if (string.Equals(text, expected, StringComparison.Ordinal))
            {
                return text;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return text;
    }

    /// <summary>Polls the Hub's log under the data root until a line holds the text, or the timeout passes.</summary>
    private static bool WaitForHubLog(string dataRoot, string text, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            if (InstallEvidence.HubLogContains(dataRoot, text))
            {
                return true;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    /// <summary>
    /// Starts the Hub with command-line arguments. AppSession passes none, so this does what it does for the
    /// environment and the compatibility layer, and waits for nothing: the caller finds the window.
    /// </summary>
    private static Process StartHubWithArguments(E2EConfig config, IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(config.HubExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = config.PublishDir,
        };
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.Environment[E2EConfig.DataRootEnvironmentVariable] = config.DataRoot;
        foreach (KeyValuePair<string, string> pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Process.Start returned no process for '{config.HubExePath}'.");
    }

    private static void RunCheck(AppSession hub, UIA3Automation automation)
    {
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? item = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        HubUi.ClickMenuItem(item);
    }

    /// <summary>
    /// Polls every title for the whole window and returns the ones that appeared. A negative check stops at the first
    /// one, because a dialog that shows at all is a failure.
    /// </summary>
    private static List<string> DialogsThatAppear(AppSession hub, UIA3Automation automation, string[] titles, TimeSpan window)
    {
        var shown = new List<string>();
        DateTime deadline = DateTime.UtcNow + window;
        do
        {
            foreach (string title in titles)
            {
                if (!shown.Contains(title) && HubUi.FindWindowInProcess(automation, hub.ProcessId, title, TimeSpan.FromMilliseconds(100)) is not null)
                {
                    shown.Add(title);
                }
            }

            if (shown.Count > 0)
            {
                break;
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        return shown;
    }

    private static void VerifyToolLaunchWithEnter(AppSession hub, AutomationElement row, LaunchTool tool)
    {
        var before = SnapshotProcessIds(tool.ProcessName);
        var startedUtc = DateTime.UtcNow;

        try
        {
            bool foreground = HubUi.Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
            row.Click();
            Thread.Sleep(200);
            bool selected = ControlTestSupport.IsSelected(row);
            ReleaseHeldKey(VkReturn);
            bool returnDown = IsKeyDown(VkReturn);
            E2EResults.Note($"{tool.CheckPrefix}.preenter", $"foreground={foreground}, row selected={selected}, Enter held={returnDown}, {HubUi.DescribeForeground(hub.MainHwnd)}");
            bool ready = foreground && selected && !returnDown && GetForegroundWindow() == hub.MainHwnd;
            E2EResults.Record(
                $"{tool.CheckPrefix}.enter.precondition",
                ready,
                ready ? "The Hub is foreground, the row is selected, and Enter is up. Enter is sent." : "The Hub was not ready for Enter. No Enter was sent.");
            if (!ready)
            {
                return;
            }

            Keyboard.Type(VirtualKeyShort.RETURN);
            E2EResults.Note($"{tool.CheckPrefix}.enter", $"Enter pressed on the {tool.Title} row.");
        }
        catch (Exception ex)
        {
            E2EResults.Record($"{tool.CheckPrefix}.enter", false, "Enter on the tool row failed: " + ex.Message);
            return;
        }

        Process? child = null;
        try
        {
            child = WaitForNewProcess(tool, before, startedUtc, ProcessTimeout);
            E2EResults.Record(
                $"{tool.CheckPrefix}.process",
                child is not null,
                child is null
                    ? $"No new {tool.ProcessName} process appeared within 15 s. " + LaunchDiagnostics(hub)
                    : $"Started {tool.ProcessName} pid {child.Id}.");
            if (child is null)
            {
                return;
            }

            var handle = WaitForMainWindow(child, ProcessTimeout);
            E2EResults.Record(
                $"{tool.CheckPrefix}.window",
                handle != IntPtr.Zero,
                handle == IntPtr.Zero
                    ? $"{tool.ProcessName} pid {child.Id} showed no main window within 15 s."
                    : $"{tool.ProcessName} pid {child.Id} main window is 0x{handle.ToInt64():X}.");
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var windowTitle = ReadWindowTitle(handle, tool.Title, TimeSpan.FromSeconds(5));
            E2EResults.Record(
                $"{tool.CheckPrefix}.title",
                string.Equals(windowTitle, tool.Title, StringComparison.Ordinal),
                $"expected '{tool.Title}', got '{windowTitle ?? "(none)"}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Record($"{tool.CheckPrefix}.error", false, ex.Message);
        }
        finally
        {
            if (child is not null)
            {
                CloseChild(child, tool);
            }
        }
    }

    /// <summary>
    /// Presses Download and Install in the Update Available window with the mouse, then checks the handoff from the
    /// evidence: the stand-in ran after the Hub exited, from the staging folder, with the signed bytes and the
    /// contract's command line. The helper that started it is the publish copy, and the Hub started again.
    /// </summary>
    private static void VerifyInstallFromHub(
        E2EConfig config,
        AppSession hub,
        UIA3Automation automation,
        UpdateFixture fixture,
        string recordPath)
    {
        using Process hubProcess = Process.GetProcessById(hub.ProcessId);
        InstallEvidence.CacheHandle(hubProcess);
        string dataRoot = config.DataRootFor(InstallScope);
        string helperPath = Path.Combine(config.PublishDir, HelperExeName);
        using var sampler = new ProcessSampler(UpdateProcessName, helperPath);

        RunCheck(hub, automation);
        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        E2EResults.Record(
            "hub.install.available.dialog",
            available is not null,
            available is null ? "The Update Available window did not show." : "The Update Available window showed.");
        AutomationElement? install = available is null ? null : HubUi.WaitWithin(available, "UpdateInstall", DialogTimeout);
        E2EResults.Record(
            "hub.install.available.button",
            install is not null,
            install is null ? "Download and Install is missing." : "Download and Install is shown.");
        if (available is null || install is null)
        {
            return;
        }

        DateTime pressedUtc = DateTime.UtcNow;
        bool pressed = HubUi.ClickDialogButton(install, available);
        E2EResults.Record(
            "hub.install.click",
            pressed,
            pressed ? "Download and Install was pressed with the mouse." : "The Update Available window could not be raised.");
        if (!pressed)
        {
            return;
        }

        E2EResults.Record(
            "hub.install.available.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, AvailableTitle, DialogTimeout),
            "The Update Available window closed on Download and Install.");

        // The helper waits for the Hub to exit, then starts the installer. So the Hub closes first.
        bool hubExited = hubProcess.WaitForExit(InstallTimeout);
        E2EResults.Record(
            "hub.install.hubClosed",
            hubExited,
            hubExited ? "The Hub closed so the update could install." : "The Hub was still running 90 s after the install press.");

        bool ran = InstallEvidence.WaitForFile(recordPath, InstallTimeout);
        E2EResults.Record(
            "hub.install.installerRan",
            ran,
            ran ? "The stand-in installer ran and wrote its record." : "No stand-in record appeared within 90 s.");
        if (!ran)
        {
            return;
        }

        int helperPid = InstallEvidence.HelperPidFromHubLog(dataRoot);
        CheckInstallerRecord(config, fixture, InstallEvidence.ReadRecord(recordPath), hubProcess, helperPid, sampler);
        bool helperSeen = helperPid > 0 && sampler.Seen.ContainsKey(helperPid);
        E2EResults.Record(
            "hub.install.helperPath",
            helperSeen,
            helperSeen
                ? $"The helper pid {helperPid} that the Hub logged ran from the publish folder."
                : $"The Hub logged helper pid {helperPid}, and it was not seen running from '{helperPath}'.");
        E2EResults.Record(
            "hub.install.helperLog",
            InstallEvidence.HelperStartedInstaller(dataRoot),
            "The helper's own log says it started the installer.");

        // The installer starts the Hub again when it finishes (update-manifest.md). The stand-in does the same.
        Process? relaunched = InstallEvidence.WaitForStartedProcess(
            HubProcessName,
            config.HubExePath,
            hubProcess.Id,
            pressedUtc,
            InstallTimeout);
        E2EResults.Record(
            "hub.install.relaunched",
            relaunched is not null,
            relaunched is null ? "The installer did not start the Hub again." : "The Hub started again from the publish folder.");
        if (relaunched is null)
        {
            return;
        }

        int relaunchedPid = relaunched.Id;
        try
        {
            AutomationElement? window = HubUi.FindWindowInProcess(automation, relaunchedPid, HubTitle, DialogTimeout);
            E2EResults.Record(
                "hub.install.relaunched.window",
                window is not null,
                window is null ? "The relaunched Hub showed no main window." : "The relaunched Hub showed its main window.");
            if (window is not null)
            {
                var hwnd = (IntPtr)window.Properties.NativeWindowHandle.ValueOrDefault;
                _ = HubUi.Foreground(hwnd, TimeSpan.FromSeconds(3));
                Thread.Sleep(500);
                string saved = HubUi.CaptureWindow(hwnd, Path.Combine(HubUi.VisualDir(config), InstalledHubCapture));
                E2EResults.Note("hub.capture.update-installed-hub", saved);
            }
        }
        finally
        {
            InstallEvidence.StopProcess(relaunched);
        }

        E2EResults.Record(
            "hub.install.relaunched.closed",
            !IsRunning(relaunchedPid),
            $"Closed the Hub that the installer started, pid {relaunchedPid}.");
    }

    /// <summary>
    /// Checks the stand-in's record: the exact command line, the bytes that ran (the signed bytes), the protected copy
    /// path, and that the Hub had exited before the installer ran. The installer's parent is the elevated helper that the
    /// medium helper started, not the medium helper itself.
    /// </summary>
    private static void CheckInstallerRecord(
        E2EConfig config,
        UpdateFixture fixture,
        Dictionary<string, string> record,
        Process hubProcess,
        int helperPid,
        ProcessSampler sampler)
    {
        string installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.PublishDir));
        string argc = RecordField(record, "argc");
        string arg0 = RecordField(record, "arg0");
        string arg1 = RecordField(record, "arg1");
        string arg2 = RecordField(record, "arg2");
        string arg3 = RecordField(record, "arg3");
        bool parentWellFormed = arg2.StartsWith(InstallParentPidPrefix, StringComparison.Ordinal) &&
            int.TryParse(arg2[InstallParentPidPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int parentPid) &&
            parentPid > 0;
        bool exact = argc == "4" &&
            arg0 == InstallArgumentSwitch &&
            arg1 == InstallRelaunchSwitch &&
            parentWellFormed &&
            string.Equals(arg3, InstallDirectoryArgumentPrefix + installDir, StringComparison.OrdinalIgnoreCase);
        E2EResults.Record(
            "hub.install.commandLine",
            exact,
            $"The installer got {argc} argument(s): '{arg0}', '{arg1}', '{arg2}', '{arg3}'. The contract is " +
            $"'{InstallArgumentSwitch} {InstallRelaunchSwitch} {InstallParentPidPrefix}<n> {InstallDirectoryArgumentPrefix}{installDir}', unquoted and last.");

        string parentText = arg2.Length >= InstallParentPidPrefix.Length ? arg2[InstallParentPidPrefix.Length..] : string.Empty;
        bool parentIsElevatedHelper = helperPid > 0 &&
            int.TryParse(parentText, NumberStyles.None, CultureInfo.InvariantCulture, out int parentId) &&
            parentId > 0 &&
            parentId != helperPid;
        bool parentSeen = parentIsElevatedHelper && sampler.Seen.ContainsKey(int.Parse(parentText, CultureInfo.InvariantCulture));
        E2EResults.Record(
            "hub.install.parentPid",
            parentIsElevatedHelper,
            $"The installer's parent pid is '{parentText}', the elevated helper, and not the medium helper pid {helperPid} " +
            $"that the Hub logged. The sampler {(parentSeen ? "saw" : "did not catch")} it running from the publish folder.");

        string commandLine = RecordField(record, "commandLine");
        string tail = " " + InstallArgumentSwitch + " " + InstallRelaunchSwitch + " " + arg2 + " " + InstallDirectoryArgumentPrefix + installDir;
        E2EResults.Record(
            "hub.install.commandLine.raw",
            commandLine.EndsWith(tail, StringComparison.OrdinalIgnoreCase),
            $"The raw command line was '{commandLine}'.");

        string signedHash = fixture.Sha256For(config.Rid);
        string ranHash = RecordField(record, "sha256");
        E2EResults.Record(
            "hub.install.signedBytes",
            string.Equals(ranHash, signedHash, StringComparison.Ordinal),
            $"The installer that ran hashes to {ranHash}. The signed SHA-256 is {signedHash}.");

        string processPath = RecordField(record, "processPath");
        E2EResults.Record(
            "hub.install.protectedPath",
            InstallEvidence.IsStagedInstaller(processPath, InstallEvidence.ProtectedRoot(GuiGate.Config, InstallScope)),
            $"The installer ran from '{processPath}', a copy under the protected root, and not from the staging folder.");

        DateTime? hubExit = InstallEvidence.ExitTimeUtc(hubProcess);
        string stamp = RecordField(record, "timestampUtc");
        bool stamped = DateTime.TryParse(
            stamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTime started);
        bool exitedFirst = stamped && hubExit is { } exited && exited <= started;
        E2EResults.Record(
            "hub.install.hubExitedFirst",
            exitedFirst,
            $"The Hub exited at {hubExit:O} UTC and the installer started at {stamp}.");
    }

    /// <summary>
    /// Presses Download and Install on a tampered installer. The download is refused, so the stand-in never runs,
    /// no Hub starts, the refusal message names the failure, and no staging folder of this test keeps a receipt.
    /// </summary>
    private static void RefuseTamperedInstall(
        E2EConfig config,
        AppSession hub,
        UIA3Automation automation,
        string recordPath,
        HashSet<string> stagingBefore)
    {
        using Process hubProcess = Process.GetProcessById(hub.ProcessId);
        InstallEvidence.CacheHandle(hubProcess);
        string dataRoot = config.DataRootFor(RefuseScope);

        RunCheck(hub, automation);
        AutomationElement? available = HubUi.FindWindowInProcess(automation, hub.ProcessId, AvailableTitle, DialogTimeout);
        E2EResults.Record(
            "hub.refuse.available.dialog",
            available is not null,
            available is null ? "The Update Available window did not show." : "The Update Available window showed.");
        AutomationElement? install = available is null ? null : HubUi.WaitWithin(available, "UpdateInstall", DialogTimeout);
        if (available is null || install is null)
        {
            return;
        }

        DateTime pressedUtc = DateTime.UtcNow;
        bool pressed = HubUi.ClickDialogButton(install, available);
        E2EResults.Record(
            "hub.refuse.click",
            pressed,
            pressed ? "Download and Install was pressed with the mouse." : "The Update Available window could not be raised.");
        if (!pressed)
        {
            return;
        }

        AutomationElement? failed = HubUi.FindWindowWithChild(automation, hub.ProcessId, DownloadFailedTitle, "DialogMessage", DialogTimeout);
        E2EResults.Record(
            "hub.refuse.title",
            failed is not null,
            failed is null ? "No 'Update Download Failed' window showed for the refused download." : "The refused download is titled Update Download Failed.");
        string text = MessageText(failed);
        bool plain = text.Contains(DownloadNotVerifiedText, StringComparison.Ordinal) &&
            !text.Contains(HashMismatchName, StringComparison.Ordinal);
        E2EResults.Record(
            "hub.refuse.message",
            plain,
            $"The Hub showed '{text}'. It must say the download did not match the signed release, and not show {HashMismatchName}.");
        if (failed is not null)
        {
            CaptureDialog(failed, RefusedCapture, "hub.refuse.contrast", LightTheme);
            AutomationElement? ok = HubUi.WaitWithin(failed, "DialogPrimary", DialogTimeout);
            if (ok is not null)
            {
                _ = HubUi.ClickDialogButton(ok, failed);
            }
        }

        E2EResults.Record(
            "hub.refuse.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, DownloadFailedTitle, DialogTimeout),
            "The refusal message closed on OK.");

        // The helper reported the refusal and exited before the message appeared. Nothing starts after that, so a
        // short wait is enough to see a record or a Hub that should not exist.
        Thread.Sleep(NoRunWindow);
        E2EResults.Record(
            "hub.refuse.neverRan",
            !File.Exists(recordPath),
            "No stand-in record exists, so the refused installer never ran.");
        E2EResults.Record(
            "hub.refuse.helperLog",
            !InstallEvidence.HelperStartedInstaller(dataRoot),
            "The helper's log does not say it started an installer.");

        Process? started = InstallEvidence.WaitForStartedProcess(HubProcessName, config.HubExePath, hubProcess.Id, pressedUtc, TimeSpan.Zero);
        E2EResults.Record(
            "hub.refuse.noRelaunch",
            started is null,
            started is null ? "No Hub started after the refusal." : "A Hub started after the refusal.");
        if (started is not null)
        {
            InstallEvidence.StopProcess(started);
        }

        E2EResults.Record("hub.refuse.hubStays", !hubProcess.HasExited, "The Hub stays open after the refusal.");
        string status = WaitForText(hub, "HubStatus");
        E2EResults.Record(
            "hub.refuse.status",
            string.Equals(status, DownloadFailedStatus, StringComparison.Ordinal),
            $"HubStatus after the refusal was '{status}'.");
        E2EResults.Record(
            "hub.refuse.staging",
            !InstallEvidence.AnyReceiptSince(InstallEvidence.StagingRoot(GuiGate.Config, RefuseScope), stagingBefore),
            "No staging folder made by this test keeps a receipt for the refused installer.");
    }

    private static string RecordField(Dictionary<string, string> record, string key) =>
        record.TryGetValue(key, out string? value) ? value : string.Empty;

    private static void CheckToolIcons(AppSession hub, UIA3Automation automation, string capturePath, string checkPrefix)
    {
        var tools = new[] { ("regedit", "Registry Editor"), ("gpedit", "Local Group Policy Editor") };
        if (string.IsNullOrWhiteSpace(capturePath) || !File.Exists(capturePath))
        {
            foreach ((string id, string name) in tools)
            {
                E2EResults.Record(checkPrefix + "." + id, false, $"{name} icon capture was missing.");
            }

            return;
        }

        using Bitmap capture = new(capturePath);
        Rectangle frame = HubUi.CaptureFrameRect(hub.MainHwnd);
        var imageBounds = new Rectangle(0, 0, capture.Width, capture.Height);
        bool frameMatches = frame.Width == capture.Width && frame.Height == capture.Height;
        foreach ((string id, string name) in tools)
        {
            AutomationElement? icon = HubUi.FindInProcess(automation, hub.ProcessId, "ToolIcon_" + id, ElementTimeout);
            AutomationElement? row = HubUi.FindInProcess(automation, hub.ProcessId, "ToolRow_" + id, ElementTimeout);
            Rectangle screenIcon = icon?.BoundingRectangle ?? Rectangle.Empty;
            var iconPixels = new Rectangle(screenIcon.Left - frame.Left, screenIcon.Top - frame.Top, screenIcon.Width, screenIcon.Height);
            var backgroundScreen = new Point(screenIcon.Left - 2, screenIcon.Top + screenIcon.Height / 2);
            var backgroundPixel = new Point(backgroundScreen.X - frame.Left, backgroundScreen.Y - frame.Top);
            if (!frameMatches || icon is null || row is null || screenIcon.Width <= 0 || screenIcon.Height <= 0
                || Rectangle.Intersect(iconPixels, imageBounds) != iconPixels
                || !imageBounds.Contains(backgroundPixel)
                || !row.BoundingRectangle.Contains(backgroundScreen))
            {
                E2EResults.Record(checkPrefix + "." + id, false, $"{name} UIA icon rectangle {screenIcon} was outside the saved Hub capture.");
                continue;
            }

            Color background = capture.GetPixel(backgroundPixel.X, backgroundPixel.Y);
            var colors = new HashSet<int>();
            int nonBackgroundPixels = 0;
            for (int y = iconPixels.Top; y < iconPixels.Bottom; y++)
            {
                for (int x = iconPixels.Left; x < iconPixels.Right; x++)
                {
                    Color pixel = capture.GetPixel(x, y);
                    int distance = Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B);
                    if (distance >= 24)
                    {
                        nonBackgroundPixels++;
                        colors.Add(pixel.ToArgb());
                    }
                }
            }

            bool visible = nonBackgroundPixels >= 8 && colors.Count >= 2;
            E2EResults.Record(
                checkPrefix + "." + id,
                visible,
                $"{name} UIA icon rectangle {screenIcon} contains {nonBackgroundPixels} nonbackground pixels across {colors.Count} colors; row background is {background}.");
        }
    }

    private static string CaptureMain(AppSession hub, string fileName)
    {
        string path = Path.Combine(HubUi.VisualDir(GuiGate.Config), fileName);
        _ = HubUi.Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
        Thread.Sleep(750);
        string saved = HubUi.CaptureWindow(hub.MainHwnd, path);
        E2EResults.Note("hub.capture." + Path.GetFileNameWithoutExtension(fileName), saved);
        return saved;
    }

    /// <summary>
    /// Saves the frame capture of a dialog, then checks its pixels for readable text in the theme the test ran with.
    /// The contrast check reads the client area of the same window, so the failure names the dialog that lost its text.
    /// </summary>
    private static void CaptureDialog(AutomationElement window, string fileName, string checkId, string theme)
    {
        IntPtr hwnd = (IntPtr)window.Properties.NativeWindowHandle.ValueOrDefault;
        _ = HubUi.RaiseDialog(hwnd, TimeSpan.FromSeconds(3));
        Thread.Sleep(500);
        string saved = HubUi.CaptureWindow(hwnd, Path.Combine(HubUi.VisualDir(GuiGate.Config), fileName));
        E2EResults.Note("hub.capture." + Path.GetFileNameWithoutExtension(fileName), saved);
        DialogContrast.Check(checkId, window, hwnd, DialogContrast.FaceFor(theme == DarkTheme));
    }

    private static string MessageText(AutomationElement? window)
    {
        if (window is null)
        {
            return string.Empty;
        }

        AutomationElement? message = HubUi.WaitWithin(window, "DialogMessage", DialogTimeout);
        return message is null ? string.Empty : HubUi.TextOf(message);
    }

    private static bool AnyHelperStartedSince(DateTime sinceUtc, Process[] helpers)
    {
        bool any = false;
        foreach (Process helper in helpers)
        {
            using (helper)
            {
                try
                {
                    if (!helper.HasExited && helper.StartTime.ToUniversalTime() >= sinceUtc.AddSeconds(-2))
                    {
                        any = true;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The helper exited while it was checked.
                }
            }
        }

        return any;
    }

    /// <summary>
    /// The child environment for one Hub run. The palette seam is set only when a theme is given, and the update
    /// seams only when a fixture server is given, so a null theme with no server is the ship-equivalent run.
    /// The stand-in installer reads its record path from the same environment, because the helper and the installer
    /// inherit it from the Hub. The Hub itself never reads it.
    /// </summary>
    private static Dictionary<string, string> Environment(
        E2EConfig config,
        string scope,
        string? theme,
        UpdateFixtureServer? server = null,
        UpdateFixture? fixture = null,
        string? installerRecordPath = null,
        string? settingsJson = null,
        bool startupCheckOn = false)
    {
        // Every launch starts from a known settings file: version 2 with the startup check off, so no test reaches the
        // feed through the startup check. A test gives its own JSON, or opts into the default (on) with no file.
        string settingsPath = SettingsPath(config, scope);
        if (startupCheckOn)
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
        else
        {
            SeedSettings(config, scope, settingsJson ?? VersionTwoOffJson);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (theme is not null)
        {
            environment["BWC_E2E_THEME"] = theme;
        }

        foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        foreach (KeyValuePair<string, string> pair in config.TempEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        if (server is not null && fixture is not null)
        {
            environment["BWC_E2E_UPDATE_MANIFEST_URL"] = server.ManifestUrl;
            environment["BWC_E2E_UPDATE_TRUSTED_KEY"] = fixture.PublicKeyBase64;
            environment["BWC_E2E_UPDATE_ALLOW_HTTP"] = "1";

            // The elevated helper runs unelevated in this flavor, and copies the installer under this folder of the run's temp.
            environment["BWC_E2E_UPDATE_PROTECTED_ROOT"] = InstallEvidence.ProtectedRoot(config, scope);
        }
        else
        {
            // No fixture server: force "no manifest URL" so no test reaches the real GitHub feed, which the build
            // now carries along with the release key. The ship flavor ignores this E2E-only variable.
            environment["BWC_E2E_UPDATE_MANIFEST_URL"] = "none";
        }

        if (installerRecordPath is not null)
        {
            environment[FakeInstallerRecordVariable] = installerRecordPath;
        }

        return environment;
    }

    private static string ReadSemVer(E2EConfig config)
    {
        string productVersion = FileVersionInfo.GetVersionInfo(config.HubExePath).ProductVersion ?? string.Empty;
        int plus = productVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? productVersion.Trim() : productVersion[..plus].Trim();
    }

    private static void EnsureExecutables(E2EConfig config)
    {
        try
        {
            config.EnsureExecutablesExist();
            E2EResults.Note("hub.publish.executables", config.PublishDir);
        }
        catch (FileNotFoundException ex)
        {
            E2EResults.Record("hub.publish.executables", false, ex.Message);
            Assert.Fail(ex.Message);
        }
    }

    private static void AssertNoFailures(string label)
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"{label} failed:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, failures)}");
    }

    private const int VkReturn = 0x0D;
    private const uint KeyEventFKeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Waits briefly for a key that an earlier step left held. A held Enter makes the Hub read the next Enter as a key
    /// repeat, so the key is released when it is still down after the wait.
    /// </summary>
    private static void ReleaseHeldKey(int virtualKey)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (IsKeyDown(virtualKey) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (IsKeyDown(virtualKey))
        {
            keybd_event((byte)virtualKey, 0, KeyEventFKeyUp, UIntPtr.Zero);
            E2EResults.Note("hub.regedit.enter", $"Virtual key 0x{virtualKey:X2} was still held and was released.");
        }
    }

    // Names what the Hub showed and held when no Registry Editor appeared. The Hub log is read from the
    // scope this test gave the Hub, so a launch line there shows whether Enter reached the launch path.
    private static string LaunchDiagnostics(AppSession hub)
    {
        AutomationElement? status = hub.Find("HubStatus");
        string text = status is null ? "(missing)" : hub.TextOf(status);
        bool launched = InstallEvidence.HubLogContains(GuiGate.Config.DataRootFor(NoticesScope), "Launched ");
        return $"Hub status={text}, Hub log has a launch line={launched}, {HubUi.DescribeForeground(hub.MainHwnd)}.";
    }

    private static HashSet<int> SnapshotProcessIds(string processName)
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static Process? WaitForNewProcess(LaunchTool tool, HashSet<int> before, DateTime notBeforeUtc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? match = null;
            foreach (var process in Process.GetProcessesByName(tool.ProcessName))
            {
                if (before.Contains(process.Id) || !StartedSince(process, notBeforeUtc) || !InstallEvidence.HasImage(process, tool.ExePath))
                {
                    process.Dispose();
                    continue;
                }

                if (match is null)
                {
                    match = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            if (match is not null)
            {
                return match;
            }

            Thread.Sleep(100);
        }

        return null;
    }

    private static bool StartedSince(Process process, DateTime notBeforeUtc)
    {
        try
        {
            return process.StartTime.ToUniversalTime() >= notBeforeUtc.AddSeconds(-2);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static IntPtr WaitForMainWindow(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    return IntPtr.Zero;
                }

                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return IntPtr.Zero;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static string? ReadWindowTitle(IntPtr handle, string expectedTitle, TimeSpan timeout)
    {
        using var automation = new UIA3Automation();
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = automation.FromHandle(handle).AsWindow().Title;
                if (string.Equals(last, expectedTitle, StringComparison.Ordinal))
                {
                    return last;
                }
            }
            catch (Exception)
            {
                // The window may still be initializing or may have closed.
            }

            Thread.Sleep(100);
        }

        return last;
    }

    private static void CloseChild(Process child, LaunchTool tool)
    {
        int processId = child.Id;
        try
        {
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                if (!child.WaitForExit(TimeSpan.FromSeconds(10)) && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process exited while it was being closed.
        }
        finally
        {
            child.Dispose();
        }

        bool running = IsRunning(processId);
        E2EResults.Record(
            $"{tool.CheckPrefix}.closed",
            !running,
            running
                ? $"{tool.ProcessName} pid {processId} is still running."
                : $"Closed {tool.ProcessName} pid {processId}.");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static AutomationElement? WaitFor(AppSession session, string automationId)
    {
        try
        {
            return session.WaitForElement(automationId, ElementTimeout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string WaitForText(AppSession session, string automationId)
    {
        try
        {
            var element = session.WaitForElement(automationId, ElementTimeout);
            return session.TextOf(element);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
