using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Gpedit.Templates;

/// <summary>
/// The details box of the Add/Remove Templates dialog: a read-only text with one line per problem of a refused Add or
/// Remove and a Close button. Close is the default and the cancel button, so Enter and Escape close it too. The window
/// has the automation id <c>GpeditTemplateDetails</c>, the text <c>GpeditTemplateDetails.Text</c> and the button
/// <c>GpeditTemplateDetails.Close</c> (docs/failure-modes/gpedit-templates.md, section 5).
/// </summary>
internal sealed class GpeditTemplateDetailsDialog : ClassicDialog
{
    internal const string RootId = "GpeditTemplateDetails";

    private const string Prefix = RootId + ".";
    private const int StaticId = 65535;
    private const int CloseId = 2;

    internal GpeditTemplateDetailsDialog(string text)
        : base(BuildTemplate())
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, GpeditStrings.Templates("TemplatesDetailsCaption"));
        }

        TextBox box = RequireControl<TextBox>(Prefix + "Text");
        box.Text = text;
        AutomationProperties.SetName(box, GpeditStrings.Templates("TemplatesDetailsName"));
    }

    // The box is 275 x 141 dialog units. Close comes first in the tab order, so Enter closes the box from the start; the text
    // follows it and keeps its own Enter.
    private static ClassicDialogDefinition BuildTemplate()
    {
        ClassicControlDefinition[] controls =
        [
            new(ClassicControlKind.Button, CloseId, Prefix + "Close", GpeditStrings.Templates("TemplatesDetailsClose"), 218, 119, 50, 14)
            {
                TabStop = true,
                IsDefault = true,
            },
            new(ClassicControlKind.Edit, StaticId, Prefix + "Text", GpeditStrings.Templates("TemplatesDetailsName"), 7, 7, 261, 105)
            {
                TabStop = true,
                ReadOnly = true,
                Multiline = true,
            },
        ];
        return new ClassicDialogDefinition(
            GpeditStrings.Templates("TemplatesDetailsCaption"),
            "Details box of the Add/Remove Templates dialog",
            275,
            141,
            controls);
    }
}
