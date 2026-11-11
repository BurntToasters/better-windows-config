using System.ComponentModel;
using System.Diagnostics;
using Bwc.Core;
using Bwc.Hub.Theme;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Hub.Dialogs;

/// <summary>
/// The "Checking for updates" window. It has one Cancel button. The check runs on a worker and
/// calls <see cref="FinishFromWorker"/> when it is done, so the window closes by itself. Cancel
/// closes it with <see cref="Finished"/> false, which the caller reads as "the user stopped it".
/// </summary>
internal sealed class UpdateProgressDialog : ClassicMessageBox
{
    public UpdateProgressDialog(string text)
        : base(
            HubStrings.Get("UpdateCheckTitle"),
            text,
            ClassicMessageIcon.Information,
            ClassicMessageButtons.Ok,
            primaryText: HubStrings.Get("UpdateCancelButton"),
            textAutomationId: "UpdateProgressText")
    {
        HubDialogTheme.Apply(this);
    }

    /// <summary>True once the worker has closed the window itself.</summary>
    public bool Finished { get; private set; }

    /// <summary>Closes the window from the worker's result. Call only on the UI thread, once.</summary>
    public void FinishFromWorker()
    {
        Finished = true;
        Cancel();
    }
}

/// <summary>
/// The update-available window. Download and Install closes the dialog with <see cref="ShowAsync"/>
/// returning true; Later returns false. The release notes button opens the manifest's notes link,
/// which the helper has already checked to be https.
/// </summary>
internal sealed class UpdateAvailableDialog : ClassicDialog
{
    private const int InstallId = 1;
    private const int LaterId = 2;

    private readonly Button _install;

    public UpdateAvailableDialog(string currentVersion, string newVersion, string? notesUrl)
        : base(HubStrings.Get("UpdateAvailableTitle"), ClassicFontSpec.MessageFont)
    {
        var text = new TextBlock
        {
            Text = HubStrings.Format("UpdateAvailableText", newVersion, currentVersion),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(ClassicFontSpec.MessageFont.Face),
            FontSize = ClassicFontSpec.MessageFont.DipSize,
            Foreground = HubBrushes.WindowText,
        };
        AutomationProperties.SetAutomationId(text, "UpdateAvailableText");
        AutomationProperties.SetName(text, text.Text);

        var layout = new StackPanel { Padding = new Thickness(12), Spacing = 8, MinWidth = 380 };
        layout.Children.Add(text);

        if (!string.IsNullOrEmpty(notesUrl))
        {
            var notes = new HyperlinkButton
            {
                Content = HubStrings.Get("UpdateNotesButton"),
                FontFamily = new FontFamily(ClassicFontSpec.MessageFont.Face),
                FontSize = ClassicFontSpec.MessageFont.DipSize,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(notes, "UpdateNotesLink");
            notes.Click += (_, _) => OpenNotes(notesUrl);
            layout.Children.Add(notes);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _install = (Button)CreateControl(new ClassicControlDefinition(
            ClassicControlKind.Button,
            InstallId,
            "UpdateInstall",
            HubStrings.Get("UpdateInstallButton"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            IsDefault = true,
        });
        buttons.Children.Add(_install);
        buttons.Children.Add(CreateControl(new ClassicControlDefinition(
            ClassicControlKind.Button,
            LaterId,
            "UpdateLater",
            HubStrings.Get("UpdateLaterButton"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
        }));

        layout.Children.Add(buttons);
        SetFlowContent(layout);
        HubDialogTheme.Apply(this);
    }

    protected override void OnDialogLoaded()
    {
        _ = _install.Focus(FocusState.Programmatic);
    }

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case InstallId:
                Accept();
                return;
            case LaterId:
                Cancel();
                return;
        }
    }

    private static void OpenNotes(string url)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Exception("Release notes could not be opened.", ex);
        }
    }
}

/// <summary>
/// Help > Update Settings. The channel is one of two radio buttons and the startup check is a check
/// box. OK returns true; Cancel leaves the stored settings unchanged.
/// </summary>
internal sealed class UpdateSettingsDialog : ClassicDialog
{
    private const int OkId = 1;
    private const int CancelId = 2;

    private readonly RadioButton _stable;
    private readonly RadioButton _beta;
    private readonly CheckBox _startup;

    public UpdateSettingsDialog(string channel, bool checkOnStartup)
        : base(HubStrings.Get("UpdateSettingsTitle"), ClassicFontSpec.MessageFont)
    {
        bool beta = string.Equals(channel, "beta", StringComparison.Ordinal);

        var layout = new StackPanel { Padding = new Thickness(12), Spacing = 8, MinWidth = 340 };
        layout.Children.Add(new TextBlock
        {
            Text = HubStrings.Get("UpdateChannelLabel"),
            FontFamily = new FontFamily(ClassicFontSpec.MessageFont.Face),
            FontSize = ClassicFontSpec.MessageFont.DipSize,
            Foreground = HubBrushes.WindowText,
        });

        var channels = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 0) };
        _stable = (RadioButton)CreateControl(new ClassicControlDefinition(
            ClassicControlKind.RadioButton,
            0,
            "UpdateChannelStable",
            HubStrings.Get("UpdateChannelStable"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            Checked = !beta,
        });
        _beta = (RadioButton)CreateControl(new ClassicControlDefinition(
            ClassicControlKind.RadioButton,
            0,
            "UpdateChannelBeta",
            HubStrings.Get("UpdateChannelBeta"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            Checked = beta,
        });
        channels.Children.Add(_stable);
        channels.Children.Add(_beta);
        layout.Children.Add(channels);

        _startup = (CheckBox)CreateControl(new ClassicControlDefinition(
            ClassicControlKind.CheckBox,
            0,
            "UpdateStartupCheck",
            HubStrings.Get("UpdateStartupCheck"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            Checked = checkOnStartup,
        });
        layout.Children.Add(_startup);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(CreateControl(new ClassicControlDefinition(
            ClassicControlKind.Button,
            OkId,
            "UpdateSettingsOk",
            HubStrings.Get("UpdateSettingsSave"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            IsDefault = true,
        }));
        buttons.Children.Add(CreateControl(new ClassicControlDefinition(
            ClassicControlKind.Button,
            CancelId,
            "UpdateSettingsCancel",
            HubStrings.Get("UpdateSettingsCancel"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
        }));
        layout.Children.Add(buttons);
        SetFlowContent(layout);
        HubDialogTheme.Apply(this);
    }

    /// <summary>"beta" when the beta radio button is checked, otherwise "stable".</summary>
    public string Channel => _beta.IsChecked == true ? "beta" : "stable";

    public bool CheckOnStartup => _startup.IsChecked == true;

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case OkId:
                Accept();
                return;
            case CancelId:
                Cancel();
                return;
        }
    }
}
