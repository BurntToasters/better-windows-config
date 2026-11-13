using Bwc.Hub.Theme;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Bwc.Hub.Dialogs;

/// <summary>
/// Help > About. A classic dialog with the product text and an OK button. When the install folder
/// has a third-party notices file, a second button asks the caller to open it. Escape is OK, because
/// the OK button is the default and the dialog has no cancel button.
/// </summary>
internal sealed class AboutDialog : ClassicDialog
{
    private const int OkId = 1;
    private const int NoticesId = 3;

    public AboutDialog(string text, bool noticesPresent)
        : base(HubStrings.Get("AboutTitle"), ClassicFontSpec.MessageFont)
    {
        ArgumentNullException.ThrowIfNull(text);

        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily(ClassicFontSpec.MessageFont.Face),
            FontSize = ClassicFontSpec.MessageFont.DipSize,
            Foreground = HubBrushes.WindowText,
        };
        AutomationProperties.SetAutomationId(body, "AboutText");
        AutomationProperties.SetName(body, text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = (Button)CreateControl(new ClassicControlDefinition(
            ClassicControlKind.Button,
            OkId,
            "AboutOk",
            HubStrings.Get("AboutOkButton"),
            0,
            0,
            0,
            0)
        {
            TabStop = true,
            IsDefault = true,
        });
        buttons.Children.Add(ok);

        if (noticesPresent)
        {
            var notices = (Button)CreateControl(new ClassicControlDefinition(
                ClassicControlKind.Button,
                NoticesId,
                "AboutNotices",
                HubStrings.Get("AboutNoticesButton"),
                0,
                0,
                0,
                0)
            {
                TabStop = true,
            });
            buttons.Children.Add(notices);
        }

        var layout = new StackPanel { Padding = new Thickness(12), Spacing = 0, MinWidth = 380 };
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        SetFlowContent(layout);
    }

    /// <summary>True when the user asked to open the third-party notices file.</summary>
    public bool NoticesRequested { get; private set; }

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case NoticesId:
                NoticesRequested = true;
                Accept();
                return;
            case OkId:
                Accept();
                return;
        }
    }
}
