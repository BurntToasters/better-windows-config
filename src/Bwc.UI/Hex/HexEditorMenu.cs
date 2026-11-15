using Bwc.UI.Menu;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Bwc.UI.Hex;

/// <summary>
/// The context menu of the hex area in Edit Binary Value: Cut, Copy, Paste, Delete, a separator, and Select All
/// (RT_MENU 108 in the oracle). It opens on a right click and on Shift+F10 or the Apps key.
/// </summary>
public sealed partial class HexEditor
{
    private void OnHexRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ShowEditMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void ShowEditMenu(Point point)
    {
        bool hasSelection = _selectionLength > 0;
        bool editable = !_isReadOnly;

        // The automation ids are the RT_MENU 108 ids: Cut 24, Copy 3, Paste 22, Delete 46, Select All 1024.
        var cut = TextEditMenu.Item("TextEditCut", 24);
        cut.IsEnabled = editable && hasSelection;
        cut.Click += (_, _) => CutSelection();
        var copy = TextEditMenu.Item("TextEditCopy", 3);
        copy.IsEnabled = hasSelection;
        copy.Click += (_, _) => CopySelection();
        var paste = TextEditMenu.Item("TextEditPaste", 22);
        paste.IsEnabled = editable && TextEditMenu.ClipboardHasText();
        paste.Click += (_, _) => PasteFromClipboard();
        var delete = TextEditMenu.Item("TextEditDelete", 46);
        delete.IsEnabled = editable && hasSelection;
        delete.Click += (_, _) => DeleteSelectionOrCaret(backspace: false);
        var selectAll = TextEditMenu.Item("TextEditSelectAll", 1024);
        selectAll.IsEnabled = _length > 0;
        selectAll.Click += (_, _) => SelectAll();

        var flyout = new MenuFlyout();
        flyout.Items.Add(cut);
        flyout.Items.Add(copy);
        flyout.Items.Add(paste);
        flyout.Items.Add(delete);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(selectAll);
        flyout.ShowAt(this, point);
    }

    /// <summary>Copies the selection and then removes it, as Cut does.</summary>
    private void CutSelection()
    {
        if (_isReadOnly || _selectionLength <= 0)
        {
            return;
        }

        CopySelection();
        DeleteSelectionOrCaret(backspace: false);
    }

    /// <summary>Shift+F10 and the Apps key open the menu at the row of the caret.</summary>
    private void ShowEditMenuAtCaret()
    {
        double y = _caret < 0 ? 0 : (_caret / BytesPerRow) * RowHeight;
        ShowEditMenu(new Point(16, Math.Max(0, y - _scroll.VerticalOffset) + RowHeight));
    }
}
