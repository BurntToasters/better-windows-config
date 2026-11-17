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
/// dialog open and shows an error. A decimal value above the width asks the classic truncation
/// question (strings 110, 119 and 120): Yes stores the largest value of the width, No keeps the editor.
/// </summary>
public abstract class ClassicNumberDialog : ClassicDialog
{
    private readonly int _bits;
    private readonly TextBox _input;
    private readonly RadioButton _hex;
    private readonly RadioButton _dec;
    private NumberBase _base = NumberBase.Hexadecimal;
    private bool _truncationConfirmed;

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
        if (_truncationConfirmed)
        {
            AcceptedValue = MaxValue;
            return true;
        }

        if (!TryParse(_input.Text, _base, _bits, out ulong value))
        {
            if (_base == NumberBase.Decimal && IsDecimalOverflow(_input.Text, _bits))
            {
                _ = ConfirmTruncationAsync();
            }
            else
            {
                _ = ShowInputErrorAsync();
            }

            return false;
        }

        AcceptedValue = value;
        return true;
    }

    /// <summary>
    /// Asks the classic truncation question. The caption is string 110 and the text is string 119 (DWORD) or 120
    /// (QWORD). No returns to the editor with the typed text. Yes accepts the largest value of the width.
    /// </summary>
    private async Task ConfirmTruncationAsync()
    {
        string text = BwcStrings.Get(_bits == 32 ? "NumberOverflowDword" : "NumberOverflowQword");
        var prompt = new ConfirmDialog(BwcStrings.Get("NumberOverflowCaption"), text);

        // The prompt is owned by this dialog, which stays disabled until the prompt closes.
        bool yes = await prompt.ShowAsync(WindowNative.GetWindowHandle(this));
        if (yes && prompt.Result == ClassicMessageResult.Yes)
        {
            _truncationConfirmed = true;
            Accept();
            return;
        }

        _ = _input.Focus(FocusState.Programmatic);
        _input.SelectAll();
    }

    private ulong MaxValue => _bits == 32 ? uint.MaxValue : ulong.MaxValue;

    /// <summary>True for decimal digits whose value is above the width's maximum. Leading zeros do not count.</summary>
    private static bool IsDecimalOverflow(string? text, int bits)
    {
        string digits = (text ?? string.Empty).Trim().TrimStart('0');
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (digits.Length > 20 || !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
        {
            return true;
        }

        return value > (bits == 32 ? uint.MaxValue : ulong.MaxValue);
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
            ? string.Format(BwcStrings.Get("NumberErrorHexadecimal"), MaxText(NumberBase.Hexadecimal))
            : string.Format(BwcStrings.Get("NumberErrorDecimal"), MaxText(NumberBase.Decimal));

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
            // Leading zeros are not part of the value, so they do not count toward the digit limit.
            digits = digits.TrimStart('0');
            if (digits.Length == 0 && (text ?? string.Empty).Trim().Length > 0)
            {
                digits = "0";
            }

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
