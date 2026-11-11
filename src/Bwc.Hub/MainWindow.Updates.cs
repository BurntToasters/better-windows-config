using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;
using Bwc.Hub.Dialogs;
using Bwc.Hub.Tools;
using Bwc.Hub.Updates;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;

namespace Bwc.Hub;

/// <summary>
/// Help > About, Help > Update Settings, and the update check and install. The check runs on a
/// worker through <see cref="UpdateService"/>; the window stays responsive and a progress window
/// with Cancel is shown for interactive checks.
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

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_checkInProgress)
        {
            return;
        }

        _checkInProgress = true;
        try
        {
            string channel = UpdateSources.Channel(_settings);
            string? manifestUrl = UpdateSources.ManifestUrl(_settings);
            string currentVersion = _version.SemVer;

            UpdateCheckResult result = interactive && !string.IsNullOrWhiteSpace(manifestUrl)
                ? await RunCheckWithProgressAsync(manifestUrl, channel, currentVersion)
                : await _updates.CheckAsync(manifestUrl, channel, currentVersion, CancellationToken.None);

            await HandleCheckResultAsync(result, interactive);
        }
        finally
        {
            _checkInProgress = false;
        }
    }

    /// <summary>
    /// Shows the progress window while the check runs. The window closes when the check finishes.
    /// If the user presses Cancel first, the check is canceled and the helper process is stopped.
    /// </summary>
    private async Task<UpdateCheckResult> RunCheckWithProgressAsync(string manifestUrl, string channel, string currentVersion)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new UpdateProgressDialog(HubStrings.Format("UpdateChecking", channel));
        Task<bool> closed = progress.ShowAsync(_hwnd);
        StatusText.Text = HubStrings.Format("UpdateChecking", channel);

        Task<UpdateCheckResult> work = _updates.CheckAsync(manifestUrl, channel, currentVersion, cancel.Token);
        _ = work.ContinueWith(
            _ => DispatcherQueue.TryEnqueue(progress.FinishFromWorker),
            TaskScheduler.Default);

        _ = await closed;
        if (!progress.Finished)
        {
            cancel.Cancel();
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

    private async Task HandleCheckResultAsync(UpdateCheckResult result, bool interactive)
    {
        switch (result.Kind)
        {
            case UpdateOutcomeKind.Canceled:
                StatusText.Text = HubStrings.Get("UpdateCanceled");
                return;

            case UpdateOutcomeKind.NotConfigured:
                StatusText.Text = HubStrings.Get("UpdateStatusNotConfigured");
                if (interactive)
                {
                    await ShowMessageAsync(HubStrings.Get("UpdateCheckTitle"), result.Message, ClassicMessageIcon.Information);
                }

                return;

            case UpdateOutcomeKind.UpToDate:
                StatusText.Text = HubStrings.Get("UpdateStatusUpToDate");
                if (interactive)
                {
                    await ShowMessageAsync(HubStrings.Get("UpdateCheckTitle"), result.Message, ClassicMessageIcon.Information);
                }

                return;

            case UpdateOutcomeKind.Failed:
                StatusText.Text = HubStrings.Get("UpdateStatusFailed");
                if (interactive)
                {
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
    /// Starts the helper's install and closes the Hub so the installer can replace files. Refuses
    /// while a tool runs from the install folder, because the helper would refuse too and nothing
    /// would visibly happen.
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

        if (!UpdateService.TryFindStaged(offeredVersion, out StagedUpdate? staged) || staged is null)
        {
            await ShowMessageAsync(
                HubStrings.Get("UpdateCheckTitle"),
                HubStrings.Get("UpdateNotStaged"),
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

    private async Task ShowMessageAsync(string caption, string text, ClassicMessageIcon icon)
    {
        var box = new ClassicMessageBox(
            caption,
            text,
            icon,
            ClassicMessageButtons.Ok,
            primaryText: HubStrings.Get("AboutOkButton"));
        HubDialogTheme.Apply(box);
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
