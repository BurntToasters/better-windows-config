using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bwc.E2E.Controls;
using Bwc.E2E.Dialogs;
using Bwc.E2E.Harness;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Hub;

/// <summary>
/// Drives the Hub from its published output. Tests that use no E2E seam run in the ship flavor: the classic
/// window and tool list, Enter and the Alt+H keyboard path, About text, the not-configured update message,
/// and the update settings file. Tests that force the palette (BWC_E2E_THEME) or serve update fixtures
/// (BWC_E2E_UPDATE_*) are tagged E2EOnly: the light and dark captures, the pixel contrast of every dialog
/// (<see cref="DialogContrast"/>), and the update checks in three states (up to date, update available, cancel).
/// Update checks run against a loopback server that serves signed fixtures generated for the run; nothing
/// reaches the network. Captures are written to artifacts/e2e/visual/hub.
/// </summary>
[TestClass]
public sealed class HubTests
{
    private const string HubTitle = "Better Windows Config";
    private const string RegeditTitle = "Registry Editor";
    private const string RegeditProcessName = "BwcRegedit";
    private const string UpdateProcessName = "BwcUpdate";
    private const string AboutTitle = "About Better Windows Config";
    private const string CheckTitle = "Check for Updates";
    private const string AvailableTitle = "Update Available";
    private const string SettingsTitle = "Update Settings";
    private const string LightTheme = "Light";
    private const string DarkTheme = "Dark";
    private const string NoticesScope = "hub-main";
    private const string UnconfiguredScope = "hub-unconfigured";
    private const string SettingsScope = "hub-settings";
    private const string LightScope = "hub-light";
    private const string UpdateScope = "hub-update";
    private const string DarkScope = "hub-dark";
    private const double LightBrightnessFloor = 150;
    private const double DarkBrightnessCeiling = 90;
    private const int HeldManifestMilliseconds = 60_000;
    private const int ProgressManifestMilliseconds = 2_000;
    private const int AboutOpenAttempts = 2;
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
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
            VerifyRegeditLaunchWithEnter(hub, row);
        }

        _ = HubUi.Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
        CheckHelpMenuOpensWithAltH(hub, automation);

        hub.Close();
        E2EResults.Record("hub.closed", !IsRunning(hub.ProcessId), $"Hub pid {hub.ProcessId} was closed by the test.");

        AssertNoFailures("Hub window checks");
    }

    [TestMethod]
    [TestCategory("Hub")]
    public void Hub_CheckForUpdates_ReportsNotConfiguredWithoutServer()
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        EnsureExecutables(config);

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

        using var hub = new AppSession(config, config.HubExePath, environment: Environment(config, SettingsScope, null));
        E2EResults.CurrentSession = hub;
        using var automation = new UIA3Automation();

        CheckUpdateSettings(hub, automation, config, SettingsScope, null);

        hub.Close();
        AssertNoFailures("Hub update settings checks");
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
        server.Offer(sameManifest, sameSignature, fixture.Installer);
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

        // Newer version, with the manifest held long enough to capture the progress window.
        server.Offer(newerManifest, newerSignature, fixture.Installer);
        server.ManifestDelayMilliseconds = ProgressManifestMilliseconds;
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
        string canceledStatus = WaitForText(hub, "HubStatus");
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

        string capture = CaptureMain(hub, "main-dark.png");
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
    /// Closes every Registry Editor process that an earlier test left running, so it cannot hold the foreground from
    /// the Hub. Each close is recorded by the check that CloseChild writes.
    /// </summary>
    private static void CloseRegeditLeftovers()
    {
        foreach (Process process in Process.GetProcessesByName(RegeditProcessName))
        {
            E2EResults.Note("hub.regedit.leftover", $"closing BwcRegedit pid {process.Id} left by an earlier test.");
            CloseChild(process);
        }
    }

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
        AutomationElement? beta = HubUi.WaitWithin(window, "UpdateChannelBeta", DialogTimeout);
        AutomationElement? startup = HubUi.WaitWithin(window, "UpdateStartupCheck", DialogTimeout);
        beta?.Click();
        startup?.Click();

        AutomationElement? save = HubUi.WaitWithin(window, "UpdateSettingsOk", DialogTimeout);
        if (save is not null)
        {
            HubUi.InvokeButton(save);
        }

        E2EResults.Record(
            "hub.settings.closed",
            HubUi.WaitForWindowGone(automation, hub.ProcessId, SettingsTitle, DialogTimeout),
            "Update Settings closed on OK.");

        string path = Path.Combine(config.DataRootFor(scope), "hub", "settings.json");
        bool saved = false;
        string detail = $"settings file '{path}' was not written.";
        if (File.Exists(path))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            string channel = root.TryGetProperty("UpdateChannel", out JsonElement channelValue)
                ? channelValue.GetString() ?? string.Empty
                : string.Empty;
            bool startupOn = root.TryGetProperty("CheckForUpdatesOnStartup", out JsonElement startupValue) &&
                startupValue.ValueKind == JsonValueKind.True;
            saved = channel == "beta" && startupOn;
            detail = $"settings file has UpdateChannel='{channel}' CheckForUpdatesOnStartup={startupOn}.";
        }

        E2EResults.Record("hub.settings.saved", saved, detail);
    }

    private static void RunCheck(AppSession hub, UIA3Automation automation)
    {
        HubUi.OpenHelpMenuByMouse(hub, automation);
        AutomationElement? item = HubUi.FindInProcess(automation, hub.ProcessId, "MenuItem_CheckForUpdates", ElementTimeout);
        HubUi.ClickMenuItem(item);
    }

    private static void VerifyRegeditLaunchWithEnter(AppSession hub, AutomationElement row)
    {
        var before = SnapshotRegeditProcessIds();
        var startedUtc = DateTime.UtcNow;

        try
        {
            _ = HubUi.Foreground(hub.MainHwnd, TimeSpan.FromSeconds(3));
            row.Click();
            Thread.Sleep(200);
            Keyboard.Type(VirtualKeyShort.RETURN);
            E2EResults.Note("hub.regedit.enter", "Enter pressed on the Registry Editor row.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("hub.regedit.enter", false, "Enter on the tool row failed: " + ex.Message);
            return;
        }

        Process? child = null;
        try
        {
            child = WaitForNewRegedit(before, startedUtc, ProcessTimeout);
            E2EResults.Record(
                "hub.regedit.process",
                child is not null,
                child is null
                    ? "No new BwcRegedit process appeared within 15 s."
                    : $"Started BwcRegedit pid {child.Id}.");
            if (child is null)
            {
                return;
            }

            var handle = WaitForMainWindow(child, ProcessTimeout);
            E2EResults.Record(
                "hub.regedit.window",
                handle != IntPtr.Zero,
                handle == IntPtr.Zero
                    ? $"BwcRegedit pid {child.Id} showed no main window within 15 s."
                    : $"BwcRegedit pid {child.Id} main window is 0x{handle.ToInt64():X}.");
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var windowTitle = ReadWindowTitle(handle, TimeSpan.FromSeconds(5));
            E2EResults.Record(
                "hub.regedit.title",
                string.Equals(windowTitle, RegeditTitle, StringComparison.Ordinal),
                $"expected '{RegeditTitle}', got '{windowTitle ?? "(none)"}'.");
        }
        catch (Exception ex)
        {
            E2EResults.Record("hub.regedit.error", false, ex.Message);
        }
        finally
        {
            if (child is not null)
            {
                CloseChild(child);
            }
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
        _ = HubUi.Foreground(hwnd, TimeSpan.FromSeconds(3));
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
    /// </summary>
    private static Dictionary<string, string> Environment(
        E2EConfig config,
        string scope,
        string? theme,
        UpdateFixtureServer? server = null,
        UpdateFixture? fixture = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (theme is not null)
        {
            environment["BWC_E2E_THEME"] = theme;
        }

        foreach (KeyValuePair<string, string> pair in config.DataRootEnvironment(scope))
        {
            environment[pair.Key] = pair.Value;
        }

        if (server is not null && fixture is not null)
        {
            environment["BWC_E2E_UPDATE_MANIFEST_URL"] = server.ManifestUrl;
            environment["BWC_E2E_UPDATE_TRUSTED_KEY"] = fixture.PublicKeyBase64;
            environment["BWC_E2E_UPDATE_ALLOW_HTTP"] = "1";
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

    private static HashSet<int> SnapshotRegeditProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(RegeditProcessName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static Process? WaitForNewRegedit(HashSet<int> before, DateTime notBeforeUtc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? match = null;
            foreach (var process in Process.GetProcessesByName(RegeditProcessName))
            {
                if (before.Contains(process.Id) || !StartedSince(process, notBeforeUtc))
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

    private static string? ReadWindowTitle(IntPtr handle, TimeSpan timeout)
    {
        using var automation = new UIA3Automation();
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = automation.FromHandle(handle).AsWindow().Title;
                if (string.Equals(last, RegeditTitle, StringComparison.Ordinal))
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

    private static void CloseChild(Process child)
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
            "hub.regedit.closed",
            !running,
            running
                ? $"BwcRegedit pid {processId} is still running."
                : $"Closed BwcRegedit pid {processId}.");
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
