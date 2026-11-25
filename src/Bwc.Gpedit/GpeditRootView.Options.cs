using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;
using Bwc.Gpedit.Tree;
using Bwc.UI.Console.Options;
using Bwc.UI.Console.Shell;
using Bwc.UI.Menu;

namespace Bwc.Gpedit;

/// <summary>
/// File > Options and the Help entries the frame adds to the editor (piece U3b, docs/failure-modes/console-options.md): the
/// Options dialog and its one real choice (View > Customize View is allowed or not), the web site entry, About and the
/// snap-in About entry. The dialog is the frame's (<c>Bwc.UI.Console.Options</c>); this file opens it, applies what it
/// returns and keeps the choice for the settings file. The frame host of the E2E flavor shares the code and keeps it
/// for the run only.
/// </summary>
public sealed partial class GpeditRootView
{
    /// <summary>
    /// The project page, opened by Help > web site entry. A fixed constant: no address comes from a file or from the user.
    /// It is the address the update feed already names.
    /// </summary>
    internal const string ProjectWebSite = "https://github.com/BurntToasters/better-windows-config";

    private ConsoleOptions _options = ConsoleOptions.Default;
    private int _webSiteRequests;
    private string _webSiteUrl = string.Empty;
    private bool? _snapInAboutShown;

    /// <summary>Applies the saved options when the editor starts.</summary>
    private void ApplySavedOptions()
    {
        _options = new ConsoleOptions(_settings.AllowCustomizeViews);
        ApplyOptionEffects();
    }

    /// <summary>View > Customize View is enabled exactly when the options allow it. File > Options is never disabled.</summary>
    private void ApplyOptionEffects() =>
        ClassicMenuBuilder.SetCommandEnabled(Shell.MenuBar, ConsoleCommandIds.CustomizeView, _options.AllowCustomizeViews);

    // ---- File > Options ------------------------------------------------------------------------------

    private void RunOptions()
    {
        if (!FrameDialogAllowed || !TryGetOwnerHandle(out nint owner))
        {
            return;
        }

        _ = RunOptionsAsync(owner);
    }

    private async Task RunOptionsAsync(nint owner)
    {
        _frameDialogOpen = true;
        try
        {
            var dialog = new ConsoleOptionsDialog(_options, GpeditStrings.Get("GpeditTitle"));
            if (await dialog.ShowAsync(owner) && dialog.Result is { } result && !_closed)
            {
                _options = result;
                ApplyOptionEffects();
                if (IsEditorHost)
                {
                    ScheduleSettingsSave();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The Options dialog failed: " + ex.Message);
        }
        finally
        {
            _frameDialogOpen = false;
        }
    }

    // ---- Help > web site -----------------------------------------------------------------------------

    /// <summary>
    /// Help > web site entry. The E2E flavor counts the request and publishes the address and never starts a browser; the
    /// ship flavor opens the project page with the shell after an https check.
    /// </summary>
    private void RunWebSite()
    {
        _webSiteRequests++;
        _webSiteUrl = ProjectWebSite;
        if (IsEditorHost)
        {
            UpdateReadout();
        }

#if !BWC_E2E
        OpenWebSite();
#endif
    }

#if !BWC_E2E
    private static void OpenWebSite()
    {
        if (!Uri.TryCreate(ProjectWebSite, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warn("The web site entry was refused: its address is not an https address.");
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Log.Warn("The web site could not be opened: " + ex.Message);
        }
    }
#endif

    // ---- Help > About and the snap-in About ----------------------------------------------------------

    /// <summary>Help > About: the native box of the tool, in both flavors. It is not the Help command.</summary>
    private void RunAbout() => ShowAboutBox(GpeditStrings.Get("GpeditTitle"));

    /// <summary>Help > snap-in About: the same box, titled with the name of the snap-in.</summary>
    private void RunSnapInAbout() => ShowAboutBox(GpeditStrings.Commands("GpeditSnapInName"));

    /// <summary>
    /// The command returns first and the modal box opens from the dispatcher, so a UI Automation Invoke of the menu entry is
    /// not held until the box closes (the way Export List opens its dialog).
    /// </summary>
    private void ShowAboutBox(string applicationName)
    {
        if (!FrameDialogAllowed || !TryGetOwnerHandle(out nint owner))
        {
            return;
        }

        _frameDialogOpen = true;
        if (!DispatcherQueue.TryEnqueue(() => RunAboutBox(owner, applicationName)))
        {
            _frameDialogOpen = false;
        }
    }

    private void RunAboutBox(nint owner, string applicationName)
    {
        try
        {
            _ = GpeditAbout.Show(owner, applicationName, Environment.ProcessPath ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("The About box failed: " + ex.Message);
        }
        finally
        {
            _frameDialogOpen = false;
        }
    }

    /// <summary>
    /// The snap-in About entry shows at the two configuration nodes and the placeholder nodes, and nowhere else: not at the
    /// root node, Administrative Templates, a category or All Settings. It runs after every change of the node.
    /// </summary>
    private void UpdateHelpMenu()
    {
        if (!IsEditorHost)
        {
            return;
        }

        bool show = GpeditTreeSource.InfoOf(Shell.TreeContent.SelectedNode) is { } info
            && info.Kind is GpeditNodeKind.Configuration or GpeditNodeKind.SoftwareSettings or GpeditNodeKind.WindowsSettings or GpeditNodeKind.WindowsSettingsChild;
        if (_snapInAboutShown == show)
        {
            return;
        }

        _snapInAboutShown = show;
        ClassicMenuBuilder.SetCommandVisible(Shell.MenuBar, ConsoleCommandIds.SnapInAbout, show);
    }
}
