using Bwc.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Bwc.UI.Menu;

/// <summary>
/// The classic system edit menu of a text box: Undo, Cut, Copy, Paste, Delete, and Select All, with the separators
/// measured in the oracle. The Right to left, Show Unicode control characters, Insert Unicode control character,
/// Open IME, and Reconversion items are not offered. That is a declared difference (docs/failure-modes/keyboard.md).
/// </summary>
internal static class TextEditMenu
{
    /// <summary>Replaces the box context menu with the edit menu. Item states follow the text on each open.</summary>
    public static void Attach(TextBox box)
    {
        // The automation ids carry the oracle ids of the system edit menu (772, 768 to 771, 177).
        var undo = Item("TextEditUndo", 772);
        undo.Click += (_, _) => box.Undo();
        var cut = Item("TextEditCut", 768);
        cut.Click += (_, _) => box.CutSelectionToClipboard();
        var copy = Item("TextEditCopy", 769);
        copy.Click += (_, _) => box.CopySelectionToClipboard();
        var paste = Item("TextEditPaste", 770);
        paste.Click += (_, _) => box.PasteFromClipboard();
        var delete = Item("TextEditDelete", 771);
        delete.Click += (_, _) => box.SelectedText = string.Empty;
        var selectAll = Item("TextEditSelectAll", 177);
        selectAll.Click += (_, _) => box.SelectAll();

        var flyout = new MenuFlyout();
        flyout.Items.Add(undo);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(delete);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(selectAll);

        flyout.Opening += (_, _) =>
        {
            bool hasSelection = box.SelectionLength > 0;
            bool editable = !box.IsReadOnly;
            bool hasText = box.Text.Length > 0;
            bool allSelected = box.SelectionStart == 0 && box.SelectionLength == box.Text.Length;
            undo.IsEnabled = editable && box.CanUndo;
            cut.IsEnabled = editable && hasSelection;
            copy.IsEnabled = hasSelection;
            paste.IsEnabled = editable && ClipboardHasText();
            delete.IsEnabled = editable && hasSelection;
            selectAll.IsEnabled = hasText && !allSelected;
        };

        box.ContextFlyout = flyout;
    }

    /// <summary>A menu item with its text and its oracle id as the automation id.</summary>
    internal static MenuFlyoutItem Item(string resourceKey, int oracleId)
    {
        var item = new MenuFlyoutItem { Text = BwcStrings.Get(resourceKey) };
        AutomationProperties.SetAutomationId(item, "ContextMenuCommand_" + oracleId);
        return item;
    }

    /// <summary>True when the clipboard holds text. A clipboard that cannot be read counts as empty.</summary>
    internal static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.Text);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
