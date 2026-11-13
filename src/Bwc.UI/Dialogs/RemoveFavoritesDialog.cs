using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Remove Favorites (RT_DIALOG 113). A multi-select list, sorted here so the visible order always
/// matches the menu. OK with nothing selected shows the message and keeps the dialog open.
/// </summary>
public sealed class RemoveFavoritesDialog : ClassicDialog
{
    private readonly ListBox _list;
    private readonly string _emptySelectionMessage;

    public RemoveFavoritesDialog(
        string title,
        string label,
        IReadOnlyList<string> names,
        string primaryText = "OK",
        string secondaryText = "Cancel",
        string? emptySelectionMessage = null)
        : base(ClassicDialogTemplates.RemoveFavorites(title, label, primaryText, secondaryText))
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(names);

        _emptySelectionMessage = string.IsNullOrWhiteSpace(emptySelectionMessage)
            ? BwcStrings.Get("RemoveFavoritesEmptySelection")
            : emptySelectionMessage;

        _list = RequireControl<ListBox>("FavoritesList");
        foreach (string name in names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(name);
        }
    }

    /// <summary>The names selected when the primary action completed.</summary>
    public IReadOnlyList<string> SelectedNames { get; private set; } = Array.Empty<string>();

    protected override void OnDialogLoaded()
    {
        _ = _list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
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
            _ = ShowEmptySelectionAsync();
            return false;
        }

        SelectedNames = selected;
        return true;
    }

    private async Task ShowEmptySelectionAsync()
    {
        // The message is owned by this dialog, so this dialog stays disabled until the message closes.
        ModalDialogWindow error = ErrorDialog.ForMessage(Title, _emptySelectionMessage);
        _ = await error.ShowAsync(WindowNative.GetWindowHandle(this));
        _ = _list.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }
}
