using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// Import Registry File's computer choice (RT_DIALOG 115, DLG-019 family). A single-select list of computers under a
/// "Select Computer:" label, with OK and Cancel. The template is 250 x 111 DLU. The list keeps the order it is given:
/// the local computer first, then each connected computer. The first name is selected when the dialog opens.
/// </summary>
public sealed class ImportComputerDialog : ClassicDialog
{
    private const string ListAutomationId = "ImportComputerList";
    private readonly ListBox _list;

    public ImportComputerDialog(
        string title,
        string label,
        IReadOnlyList<string> names,
        string primaryText,
        string secondaryText)
        : base(CreateTemplate(title, label, primaryText, secondaryText))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(names);

        _list = RequireControl<ListBox>(ListAutomationId);
        _list.SelectionMode = Microsoft.UI.Xaml.Controls.SelectionMode.Single;

        foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            _list.Items.Add(name);
        }

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    /// <summary>The computer selected when OK was pressed.</summary>
    public string SelectedName { get; private set; } = string.Empty;

    protected override void OnDialogLoaded()
    {
        _ = _list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    protected override bool TryAccept()
    {
        if (_list.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        SelectedName = name;
        return true;
    }

    /// <summary>The DLG-019 computer list rectangles, in dialog units relative to the client area.</summary>
    private static ClassicDialogDefinition CreateTemplate(string title, string label, string primaryText, string secondaryText) =>
        new(
            title,
            "RT_DIALOG 115 (en-US regedit.exe.mui)",
            250,
            111,
            [
                new ClassicControlDefinition(ClassicControlKind.Label, 0, "ImportComputerLabel", label, 7, 7, 146, 8),
                new ClassicControlDefinition(ClassicControlKind.ListBox, 102, ListAutomationId, string.Empty, 7, 18, 236, 63)
                {
                    TabStop = true,
                },
                new ClassicControlDefinition(ClassicControlKind.Button, 1, "DialogPrimary", primaryText, 138, 90, 50, 14)
                {
                    TabStop = true,
                    IsDefault = true,
                },
                new ClassicControlDefinition(ClassicControlKind.Button, 2, "DialogSecondary", secondaryText, 193, 90, 50, 14)
                {
                    TabStop = true,
                },
            ])
        {
            FontFace = ClassicDialogMetrics.TemplateFontFace,
            FontPointSize = 8,
        };
}
