using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Dialogs;

/// <summary>
/// Display Binary Data (RT_DIALOG 2021). A read-only view of one value snapshot: each list row holds 16
/// bytes, an offset, and the bytes grouped by the chosen format (Byte, Word, Dword, or Qword, little endian).
/// The dialog owns a copy of the bytes and never writes to the registry. Escape or the OK button dismisses it.
/// </summary>
public sealed class HexViewerDialog : ClassicDialog
{
    private const int BytesPerRow = 16;

    private readonly ListBox _list;
    private readonly TextBlock _valueType;
    private byte[] _data = [];
    private int _elementSize = 1;

    /// <param name="title">The classic caption, "Binary Data".</param>
    /// <param name="closeText">The text of the OK button.</param>
    public HexViewerDialog(string title, string closeText = "OK")
        : base(ClassicDialogTemplates.BinaryData(Required(title, nameof(title)), Required(closeText, nameof(closeText))))
    {
        _list = RequireControl<ListBox>("HexViewerList");
        _valueType = RequireControl<TextBlock>("ValueTypeText");

        RequireControl<RadioButton>("FormatByte").Checked += (_, _) => SetElementSize(1);
        RequireControl<RadioButton>("FormatWord").Checked += (_, _) => SetElementSize(2);
        RequireControl<RadioButton>("FormatDword").Checked += (_, _) => SetElementSize(4);
        RequireControl<RadioButton>("FormatQword").Checked += (_, _) => SetElementSize(8);
    }

    /// <summary>
    /// Shows one value snapshot. The value name is the list's accessible name, and the type name fills the
    /// Value Type field. The key path is accepted for the callers; the classic layout shows neither.
    /// </summary>
    public void SetValue(string displayName, string keyPath, byte[] data, string? typeName = null)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(data);

        _data = (byte[])data.Clone();
        AutomationProperties.SetName(_list, displayName);
        _valueType.Text = string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName;
        Rebuild();
    }

    private static string Required(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, parameter);
        return value;
    }

    private void SetElementSize(int size)
    {
        if (_elementSize == size)
        {
            return;
        }

        _elementSize = size;
        Rebuild();
    }

    /// <summary>Fills the list with one row per 16 bytes in the current format.</summary>
    private void Rebuild()
    {
        _list.Items.Clear();
        for (int rowStart = 0; rowStart < _data.Length; rowStart += BytesPerRow)
        {
            _list.Items.Add(RowText(rowStart));
        }
    }

    private string RowText(int rowStart)
    {
        int end = Math.Min(rowStart + BytesPerRow, _data.Length);
        var text = new StringBuilder();
        text.Append(rowStart.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");

        for (int at = rowStart; at < end; at += _elementSize)
        {
            if (at > rowStart)
            {
                text.Append(' ');
            }

            if (end - at >= _elementSize)
            {
                text.Append(ElementText(at));
            }
            else
            {
                // A value that runs past the end of the data shows its bytes one by one.
                for (int b = at; b < end; b++)
                {
                    text.Append(_data[b].ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }

        return text.ToString();
    }

    /// <summary>The element at <paramref name="at"/> as hex, read little endian and zero padded to its width.</summary>
    private string ElementText(int at)
    {
        ulong value = 0;
        for (int b = _elementSize - 1; b >= 0; b--)
        {
            value = (value << 8) | _data[at + b];
        }

        return value.ToString("X" + (_elementSize * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
