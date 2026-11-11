using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Multi-select favorite remover (oracle DLG-026 and DLG-027). The list is sorted by the caller and
/// again here so the visible order always matches the menu. The primary action is refused until at
/// least one name is selected.
/// </summary>
public sealed class RemoveFavoritesDialog : ModalDialogWindow
{
    private const string DefaultEmptySelectionMessage = "Select at least one favorite to remove.";

    private readonly ListBox _list;
    private readonly string _emptySelectionMessage;

    public RemoveFavoritesDialog(
        string title,
        string label,
        IReadOnlyList<string> names,
        string primaryText = "OK",
        string secondaryText = "Cancel",
        string? emptySelectionMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(names);

        _emptySelectionMessage = string.IsNullOrWhiteSpace(emptySelectionMessage)
            ? DefaultEmptySelectionMessage
            : emptySelectionMessage;

        Title = title;
        SetHeading(title);
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        SetPreferredSize(470, 360);

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            MinHeight = 160,
            MaxHeight = 240,
        };

        AutomationProperties.SetAutomationId(_list, "FavoritesList");
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

    /// <summary>The names selected when the primary action completed.</summary>
    public IReadOnlyList<string> SelectedNames { get; private set; } = Array.Empty<string>();

    protected override void OnDialogLoaded()
    {
        _ = _list.Focus(FocusState.Programmatic);
    }

    protected override bool TryAccept()
    {
        var selected = new List<string>();
        foreach (object item in _list.SelectedItems)
        {
            if (item is string name)
            {
                selected.Add(name);
            }
        }

        if (selected.Count == 0)
        {
            ShowValidation(_emptySelectionMessage);
            _ = _list.Focus(FocusState.Programmatic);
            return false;
        }

        SelectedNames = selected;
        return true;
    }
}
