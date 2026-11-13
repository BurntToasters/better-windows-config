namespace Bwc.UI.Menu;

/// <summary>
/// Window-wide menu display state. Keyboard menu use (Alt, F10, or a letter on the bar) shows the
/// mnemonic underlines; pointer use hides them, as classic Windows does. Only the UI thread reads or
/// writes this state.
/// </summary>
public static class ClassicMenuState
{
    private static bool _showMnemonics;

    /// <summary>True while the mnemonic underlines are drawn on the menu bar and open popups.</summary>
    public static bool ShowMnemonics
    {
        get => _showMnemonics;
        set
        {
            if (_showMnemonics == value)
            {
                return;
            }

            _showMnemonics = value;
            MnemonicsChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="ShowMnemonics"/> changes. Every <see cref="MenuLabel"/> redraws.</summary>
    public static event EventHandler? MnemonicsChanged;
}
