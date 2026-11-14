using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Bwc.Core;
using Bwc.Hub.Dialogs;
using Bwc.Hub.Tools;
using Bwc.Hub.Updates;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;

namespace Bwc.Hub;

/// <summary>
/// Help > About, Help > Update Settings, and the update check and install. The check (manifest only)
/// runs on a worker through <see cref="UpdateService"/>. Download and Install runs the download step
/// with a progress window, then starts the install. Each interactive step shows a window with Cancel.
/// </summary>
public sealed partial class MainWindow
{
    private const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    private string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    private async Task ShowAboutAsync()
    {
        string noticesPath = Path.Combine(AppContext.BaseDirectory, NoticesFileName);
        bool noticesPresent = File.Exists(noticesPath);
        string text = HubStrings.Format(
            "AboutText",
            HubStrings.Get("ProductName"),
            _version.Display,
            HubStrings.Get("ProductCopyright"),
            HubStrings.Get(noticesPresent ? "AboutNoticesPresent" : "AboutNoticesAbsent"));

        var dialog = new AboutDialog(text, noticesPresent);
        _ = await dialog.ShowAsync(_hwnd);
        if (dialog.NoticesRequested)
        {
            OpenFile(noticesPath);
        }
    }

    private async Task ShowUpdateSettingsAsync()
    {
        var dialog = new UpdateSettingsDialog(UpdateSources.Channel(_settings), _settings.CheckForUpdatesOnStartup);
        bool saved = await dialog.ShowAsync(_hwnd);
        if (!saved)
        {
            return;
        }

        _settings.UpdateChannel = dialog.Channel;
        _settings.CheckForUpdatesOnStartup = dialog.CheckOnStartup;
        try
        {
            SaveSettings();
            StatusText.Text = HubStrings.Get("StatusReady");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessageAsync(
                HubStrings.Get("UpdateSettingsTitle"),
                HubStrings.Format("SettingsSaveFailed", ex.Message),
                ClassicMessageIcon.Error);
        }
    }

    /// <summary>
    /// Runs one update check. The startup check (interactive false) is silent: it shows a window only when it
    /// offers an update. A menu check (interactive true) shows its progress and its result. A menu check that is
    /// clicked while a check runs waits for that check and shows its result, so no second check starts.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_activeCheck is not null)
        {
            if (!interactive)
            {
                Log.Info("Startup update check skipped: a check is already running.");
                return;
            }

            // The running check leaves its result to this wait, so the user sees one result window.
            Task<UpdateCheckResult> running = _activeCheck;
            CancellationTokenSource runningCancel = _activeCancel!;
            _activeCheckHandedOver = true;
            UpdateCheckResult shared = await WaitWithProgressAsync(running, runningCancel, UpdateSources.Channel(_settings));
            await HandleCheckResultAsync(shared, interactive: true);
            return;
        }

        string channel = UpdateSources.Channel(_settings);
        string? manifestUrl = UpdateSources.ManifestUrl(_settings, channel);
        string currentVersion = _version.SemVer;

        // Not disposed: a waiter that arrives late may still cancel it. A cancel source is a small object.
        var cancel = new CancellationTokenSource();
        Task<UpdateCheckResult> work = _updates.CheckAsync(manifestUrl, channel, currentVersion, cancel.Token);
        _activeCheck = work;
        _activeCancel = cancel;
        _activeCheckHandedOver = false;

        UpdateCheckResult result;
        bool handedOver;
        try
        {
            result = interactive && !string.IsNullOrWhiteSpace(manifestUrl)
                ? await WaitWithProgressAsync(work, cancel, channel)
                : await work;
        }
        finally
        {
            handedOver = _activeCheckHandedOver;
            _activeCheck = null;
            _activeCancel = null;
            _activeCheckHandedOver = false;
        }

        if (!handedOver)
        {
            // A check that finished just as Cancel was pressed still counts as canceled.
            if (cancel.IsCancellationRequested)
            {
                result = new UpdateCheckResult(UpdateOutcomeKind.Canceled, string.Empty);
            }

            await HandleCheckResultAsync(result, interactive);
        }
    }

    /// <summary>
    /// Shows the progress window while the check runs. The window closes when the check finishes.
    /// If the user presses Cancel first, the check is canceled and the helper process is stopped.
    /// </summary>
    private async Task<UpdateCheckResult> WaitWithProgressAsync(Task<UpdateCheckResult> work, CancellationTokenSource cancel, string channel)
    {
        var progress = new UpdateProgressDialog(HubStrings.Format("UpdateChecking", channel));
        Task<bool> closed = progress.ShowAsync(_hwnd);
        StatusText.Text = HubStrings.Format("UpdateChecking", channel);

        _ = work.ContinueWith(
            _ => DispatcherQueue.TryEnqueue(progress.FinishFromWorker),
            TaskScheduler.Default);

        _ = await closed;
        if (!progress.Finished)
        {
            // The status shows "canceled" now. The helper is stopped below, which can take seconds, and its late
            // result is then ignored (CheckForUpdatesAsync maps a canceled check to Canceled).
            cancel.Cancel();
            StatusText.Text = HubStrings.Get("UpdateCanceled");
        }

        try
        {
            return await work;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateOutcomeKind.Canceled, string.Empty);
        }
    }

    /// <summary>
    /// Maps one check result to the window or the status line. A silent check (startup) changes neither for
    /// up to date, not configured, canceled, or a failure: those go to the log only.
    /// </summary>
    private async Task HandleCheckResultAsync(UpdateCheckResult result, bool interactive)
    {
        Log.Info("Update check finished: " + result.Kind + ".");
        if (result.Code is not null)
        {
            Log.Info("Update check code: " + result.Code + ".");
        }

        switch (result.Kind)
        {
            case UpdateOutcomeKind.Canceled:
                if (interactive)
                {
                    StatusText.Text = HubStrings.Get("UpdateCanceled");
                }

                return;

            case UpdateOutcomeKind.NotConfigured:
                if (interactive)
                {
                    StatusText.Text = HubStrings.Get("UpdateStatusNotConfigured");
                    await ShowMessageAsync(HubStrings.Get("UpdateCheckTitle"), result.Message, ClassicMessageIcon.Information);
                }

                return;

            case UpdateOutcomeKind.UpToDate:
                if (interactive)
                {
                    StatusText.Text = HubStrings.Get("UpdateStatusUpToDate");
                    await ShowMessageAsync(HubStrings.Get("UpdateCheckTitle"), result.Message, ClassicMessageIcon.Information);
                }

                return;

            case UpdateOutcomeKind.Failed:
                if (interactive)
                {
                    StatusText.Text = HubStrings.Get("UpdateStatusFailed");
                    await ShowMessageAsync(
                        HubStrings.Get("UpdateFailedTitle"),
                        HubStrings.Format("UpdateFailedText", result.Message),
                        ClassicMessageIcon.Error);
                }

                return;

            case UpdateOutcomeKind.Available:
                string version = result.Version ?? string.Empty;
                StatusText.Text = HubStrings.Format("UpdateStatusAvailable", version);
                var dialog = new UpdateAvailableDialog(_version.SemVer, version, result.NotesUrl);
                bool install = await dialog.ShowAsync(_hwnd);
                if (install)
                {
                    await InstallAsync(version);
                }

                return;
        }
    }

    /// <summary>
    /// Refuses while a tool runs from the install folder (the installer would refuse too, after the Hub
    /// had already closed). Otherwise downloads the installer with progress, then starts the install and
    /// closes the Hub so the installer can replace files.
    /// </summary>
    private async Task InstallAsync(string offeredVersion)
    {
        string installDir = InstallDirectory;
        IReadOnlyList<string> running = UpdateService.ToolsRunningFrom(installDir);
        if (running.Count > 0)
        {
            await ShowMessageAsync(
                HubStrings.Get("UpdateCheckTitle"),
                HubStrings.Format("UpdateInstallBlocked", string.Join(", ", running)),
                ClassicMessageIcon.Warning);
            return;
        }

        string channel = UpdateSources.Channel(_settings);
        string? manifestUrl = UpdateSources.ManifestUrl(_settings, channel);
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            await ShowMessageAsync(HubStrings.Get("UpdateCheckTitle"), HubStrings.Get("UpdateNotConfigured"), ClassicMessageIcon.Information);
            return;
        }

        StagedUpdate? staged = await DownloadWithProgressAsync(manifestUrl, channel, _version.SemVer);
        if (staged is null)
        {
            return;
        }

        if (!string.Equals(staged.Version, offeredVersion, StringComparison.Ordinal))
        {
            await ShowMessageAsync(
                HubStrings.Get("UpdateCheckTitle"),
                HubStrings.Get("UpdateVersionChanged"),
                ClassicMessageIcon.Warning);
            return;
        }

        try
        {
            UpdateService.StartInstall(staged, _version.SemVer, installDir, Environment.ProcessId);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Exception("Update install could not start.", ex);
            await ShowMessageAsync(
                HubStrings.Get("UpdateFailedTitle"),
                HubStrings.Format("UpdateInstallStartFailed", ex.Message),
                ClassicMessageIcon.Error);
            return;
        }

        Log.Info("Hub closing so the update can install.");
        Close();
    }

    /// <summary>
    /// Runs the download step behind a progress window with Cancel. The status line shows the percentage.
    /// Returns the staged installer, or null when the user canceled or the download failed (the reason is shown).
    /// </summary>
    private async Task<StagedUpdate?> DownloadWithProgressAsync(string manifestUrl, string channel, string currentVersion)
    {
        using var cancel = new CancellationTokenSource();
        var dialog = new UpdateProgressDialog(HubStrings.Get("UpdateDownloading"));
        Task<bool> closed = dialog.ShowAsync(_hwnd);
        StatusText.Text = HubStrings.Get("UpdateDownloading");

        // Progress<T> reports on the UI thread, so the window text and the status line change together.
        var progress = new Progress<DownloadProgressReport>(report =>
        {
            if (report.Total > 0)
            {
                int percent = (int)Math.Min(100, report.Received * 100 / report.Total);
                string text = HubStrings.Format("UpdateDownloadProgress", percent.ToString(CultureInfo.InvariantCulture));
                dialog.SetProgress(text);
                StatusText.Text = text;
            }
        });

        Task<(UpdateCheckResult Result, StagedUpdate? Staged)> work = _updates.DownloadAsync(manifestUrl, channel, currentVersion, progress, cancel.Token);
        _ = work.ContinueWith(
            _ => DispatcherQueue.TryEnqueue(dialog.FinishFromWorker),
            TaskScheduler.Default);

        _ = await closed;
        if (!dialog.Finished)
        {
            cancel.Cancel();
            StatusText.Text = HubStrings.Get("UpdateDownloadCanceled");
        }

        (UpdateCheckResult Result, StagedUpdate? Staged) outcome;
        try
        {
            outcome = await work;
        }
        catch (OperationCanceledException)
        {
            outcome = (new UpdateCheckResult(UpdateOutcomeKind.Canceled, string.Empty), null);
        }

        switch (outcome.Result.Kind)
        {
            case UpdateOutcomeKind.Available when outcome.Staged is not null:
                return outcome.Staged;

            case UpdateOutcomeKind.Canceled:
                StatusText.Text = HubStrings.Get("UpdateDownloadCanceled");
                return null;

            default:
                Log.Info("Update download finished without an installer: " + outcome.Result.Kind + ".");
                if (outcome.Result.Code is not null)
                {
                    Log.Info("Update download code: " + outcome.Result.Code + ".");
                }

                StatusText.Text = HubStrings.Get("UpdateStatusDownloadFailed");
                await ShowMessageAsync(
                    HubStrings.Get("UpdateDownloadFailedTitle"),
                    HubStrings.Format("UpdateDownloadFailedText", outcome.Result.Message),
                    ClassicMessageIcon.Error);
                return null;
        }
    }

    /// <summary>
    /// Shows why the helper did not install, when it relaunched the Hub with <c>--update-result</c>. The code is
    /// the helper's failure name. It goes to the log only. Every text says the current version is still in place.
    /// </summary>
    internal async Task ShowUpdateResultAsync(string code)
    {
        Log.Info("Hub started after an update did not install. Code " + code + ".");
        string version = _version.SemVer;
        string text = code switch
        {
            "ProcessesStillRunning" => HubStrings.Format("UpdateResultRunning", version),
            "RunningElevated" => HubStrings.Format("UpdateResultElevated", version),
            "InstallerFailed" => HubStrings.Format("UpdateResultInstallerFailed", version),
            "Unexpected" => HubStrings.Format("UpdateResultUnexpected", version),
            _ => HubStrings.Format("UpdateResultNotInstalled", version),
        };

        StatusText.Text = HubStrings.Get("UpdateStatusNotInstalled");
        await ShowMessageAsync(HubStrings.Get("UpdateNotInstalledTitle"), text, ClassicMessageIcon.Warning);
    }

    private async Task ShowMessageAsync(string caption, string text, ClassicMessageIcon icon)
    {
        var box = new ClassicMessageBox(
            caption,
            text,
            icon,
            ClassicMessageButtons.Ok,
            primaryText: HubStrings.Get("AboutOkButton"));
        _ = await box.ShowAsync(_hwnd);
    }

    private static void OpenFile(string path)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Exception("The file could not be opened: " + path, ex);
        }
    }
}
