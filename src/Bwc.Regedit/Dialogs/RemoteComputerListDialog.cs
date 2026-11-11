using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.Regedit.Dialogs;

/// <summary>
/// Single-select list of connected remote computers. The primary action is refused until one
/// name is selected, and the dialog stays open so the user can choose again.
/// </summary>
public sealed class RemoteComputerListDialog : ModalDialogWindow
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
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrEmpty(emptySelectionMessage);

        _emptySelectionMessage = emptySelectionMessage;

        Title = title;
        SetHeading(title);
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        SetPreferredSize(470, 360);

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            MinHeight = 160,
            MaxHeight = 240,
        };

        AutomationProperties.SetAutomationId(_list, "RemoteComputerList");
        AutomationProperties.SetName(_list, label);

        foreach (string name in names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(name);
        }

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_list);
        SetBody(body);
    }

    /// <summary>The name selected when the primary action completed.</summary>
    public string? SelectedName { get; private set; }

    protected override void OnDialogLoaded()
    {
        _ = _list.Focus(FocusState.Programmatic);
    }

    protected override bool TryAccept()
    {
        if (_list.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
        {
            ShowValidation(_emptySelectionMessage);
            _ = _list.Focus(FocusState.Programmatic);
            return false;
        }

        SelectedName = name;
        return true;
    }
}
