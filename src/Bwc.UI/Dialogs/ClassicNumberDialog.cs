using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>
/// The shared body of the Edit DWORD and Edit QWORD dialogs: value data, the read-only value name,
/// and the Base group. Hexadecimal is selected by default and a hexadecimal value has no prefix in
/// the box, as classic regedit shows it. Each base change converts the shown value, so 0x1388 in
/// hexadecimal shows 5000 in decimal. A value that does not parse in the current base keeps the
/// dialog open and shows an error.
/// </summary>
public abstract class ClassicNumberDialog : ClassicDialog
{
    private readonly int _bits;
    private readonly TextBox _input;
    private readonly RadioButton _hex;
    private readonly RadioButton _dec;
    private NumberBase _base = NumberBase.Hexadecimal;

    /// <summary>The number base the value is shown and typed in.</summary>
    private enum NumberBase
    {
        Hexadecimal,
        Decimal,
    }

    /// <param name="template">The Edit DWORD or Edit QWORD template.</param>
    /// <param name="bits">The value width, 32 or 64.</param>
    /// <param name="initialText">The starting value. A leading 0x means hexadecimal; anything else is decimal.</param>
    /// <param name="valueName">The value name shown read-only. Empty when the caller does not supply it.</param>
    protected ClassicNumberDialog(ClassicDialogDefinition template, int bits, string initialText, string valueName)
        : base(template)
    {
        if (bits is not (32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "A number dialog holds 32 or 64 bits.");
        }

        _bits = bits;
        _input = RequireControl<TextBox>("ValueEditorInput");
        _hex = RequireControl<RadioButton>("BaseHex");
        _dec = RequireControl<RadioButton>("BaseDec");
        RequireControl<TextBox>("ValueNameInput").Text = valueName ?? string.Empty;

        string start = initialText ?? string.Empty;
        _input.Text = TryParseInitial(start, out ulong initial) ? Render(initial, NumberBase.Hexadecimal) : start;

        _hex.Checked += OnBaseChecked;
        _dec.Checked += OnBaseChecked;
    }

    /// <summary>The parsed value after a successful primary action, in the dialog's width.</summary>
    protected ulong AcceptedValue { get; private set; }

    /// <summary>Parses the box in the current base, and keeps the dialog open with an error when it does not parse.</summary>
    protected override bool TryAccept()
    {
        if (!TryParse(_input.Text, _base, _bits, out ulong value))
        {
            _ = ShowInputErrorAsync();
            return false;
        }

        AcceptedValue = value;
        return true;
    }

    /// <summary>Selects the value and focuses it, as the classic dialog does when it opens.</summary>
    protected override void OnDialogLoaded()
    {
        _ = _input.Focus(FocusState.Programmatic);
        _input.SelectAll();
    }

    private void OnBaseChecked(object sender, RoutedEventArgs e)
    {
        NumberBase next = ReferenceEquals(sender, _hex) ? NumberBase.Hexadecimal : NumberBase.Decimal;
        if (next == _base)
        {
            return;
        }

        // Convert the shown value. Text that does not parse in the old base stays as typed.
        if (TryParse(_input.Text, _base, _bits, out ulong value))
        {
            _input.Text = Render(value, next);
        }

        _base = next;
    }

    private async Task ShowInputErrorAsync()
    {
        string message = _base == NumberBase.Hexadecimal
            ? $"Enter a hexadecimal number from 0 to {MaxText(NumberBase.Hexadecimal)}."
            : $"Enter a decimal number from 0 to {MaxText(NumberBase.Decimal)}.";

        // The error is owned by this dialog, so this dialog stays disabled until the error closes.
        ModalDialogWindow error = ErrorDialog.ForMessage(Title, message);
        _ = await error.ShowAsync(WindowNative.GetWindowHandle(this));

        _ = _input.Focus(FocusState.Programmatic);
        _input.SelectAll();
    }

    private string MaxText(NumberBase numberBase) => numberBase switch
    {
        NumberBase.Hexadecimal => _bits == 32 ? "FFFFFFFF" : "FFFFFFFFFFFFFFFF",
        _ => _bits == 32 ? uint.MaxValue.ToString(CultureInfo.InvariantCulture) : ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
    };

    private static string Render(ulong value, NumberBase numberBase) => numberBase == NumberBase.Hexadecimal
        ? value.ToString("X", CultureInfo.InvariantCulture)
        : value.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseInitial(string text, out ulong value)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return TryParse(trimmed, NumberBase.Hexadecimal, 64, out value);
        }

        return TryParse(trimmed, NumberBase.Decimal, 64, out value);
    }

    /// <summary>
    /// Parses text in one base. Hexadecimal accepts an optional 0x prefix and no sign. Decimal
    /// accepts digits only. Both reject values above the width.
    /// </summary>
    private static bool TryParse(string? text, NumberBase numberBase, int bits, out ulong value)
    {
        value = 0;
        string digits = (text ?? string.Empty).Trim();
        ulong max = bits == 32 ? uint.MaxValue : ulong.MaxValue;

        if (numberBase == NumberBase.Hexadecimal)
        {
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                digits = digits[2..];
            }

            if (digits.Length == 0 || digits.Length > 16 || !digits.All(char.IsAsciiHexDigit))
            {
                return false;
            }

            value = ulong.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        else
        {
            if (digits.Length == 0 || digits.Length > 20 || !digits.All(char.IsAsciiDigit))
            {
                return false;
            }

            if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
        }

        return value <= max;
    }
}
