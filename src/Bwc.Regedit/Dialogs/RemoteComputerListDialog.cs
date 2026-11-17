using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// Disconnect Network Registry (RT_DIALOG 107, DLG-013). A multi-select list of connected remote
/// computers under a "Computer name:" label, with OK and Cancel. The template is 250 x 111 DLU. OK
/// with nothing selected shows the message and keeps the dialog open.
/// </summary>
public sealed class RemoteComputerListDialog : ClassicDialog
{
    private readonly ListBox _list;
    private readonly string _emptySelectionMessage;

    public RemoteComputerListDialog(
        string title,
        string label,
        IReadOnlyList<string> names,
        string emptySelectionMessage,
        string primaryText,
        string secondaryText)
        : base(CreateTemplate(title, label, primaryText, secondaryText))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrEmpty(emptySelectionMessage);

        _emptySelectionMessage = emptySelectionMessage;
        _list = RequireControl<ListBox>(ListAutomationId);
        // Classic list 1175 has no LVS_SINGLESEL, so several computers can be selected and disconnected at once.
        _list.SelectionMode = Microsoft.UI.Xaml.Controls.SelectionMode.Multiple;

        foreach (string name in names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(name);
        }
    }

    private const string ListAutomationId = "RemoteComputerList";

    /// <summary>The names selected when the primary action completed, in list order.</summary>
    public IReadOnlyList<string> SelectedNames { get; private set; } = Array.Empty<string>();

    protected override void OnDialogLoaded()
    {
        _ = _list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    protected override bool TryAccept()
    {
        string[] names = _list.SelectedItems.OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (names.Length == 0)
        {
            _ = ShowEmptySelectionAsync();
            return false;
        }

        SelectedNames = names;
        return true;
    }

    private async Task ShowEmptySelectionAsync()
    {
        // The message is owned by this dialog, so this dialog stays disabled until the message closes.
        ModalDialogWindow error = ErrorDialog.ForMessage(Title, _emptySelectionMessage);
        _ = await error.ShowAsync(WindowNative.GetWindowHandle(this));
        _ = _list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    /// <summary>The DLG-013 rectangles, in dialog units relative to the client area.</summary>
    private static ClassicDialogDefinition CreateTemplate(string title, string label, string primaryText, string secondaryText) =>
        new(
            title,
            "RT_DIALOG 107 (en-US regedit.exe.mui)",
            250,
            111,
            [
                new ClassicControlDefinition(ClassicControlKind.Label, 0, "RemoteComputerLabel", label, 7, 7, 146, 8),
                new ClassicControlDefinition(ClassicControlKind.ListBox, 1175, ListAutomationId, string.Empty, 7, 18, 236, 63)
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
