using System.Text;

namespace Bwc.UI.Dialogs;

/// <summary>
/// One control of a native dialog template. <paramref name="ClassName"/> is a window class name such as
/// "Static" or "msctls_progress32". The rectangle is in dialog units relative to the client area.
/// </summary>
public sealed record Win32DialogItem(
    int Id,
    string ClassName,
    uint Style,
    string Text,
    int X,
    int Y,
    int Cx,
    int Cy);

/// <summary>
/// A dialog template in the extended (DLGTEMPLATEEX) layout, written to memory. It carries the same fields a
/// DIALOGEX resource carries: style, extended style, rectangle, caption, font, and the controls in template order.
/// The template is the input to <c>CreateDialogIndirectParamW</c>, so the window is created from it and no
/// WinUI window is involved.
/// </summary>
public sealed class Win32DialogTemplate
{
    private const ushort DialogVersion = 1;
    private const ushort ExtendedSignature = 0xFFFF;
    private const uint DsSetFont = 0x00000040;

    private readonly IReadOnlyList<Win32DialogItem> _items;

    /// <summary>
    /// Creates the template. Rectangles are dialog units. A font is required when the style has DS_SETFONT.
    /// </summary>
    public Win32DialogTemplate(
        string caption,
        uint style,
        uint exStyle,
        int x,
        int y,
        int widthDlu,
        int heightDlu,
        string fontFace,
        ushort fontPointSize,
        IReadOnlyList<Win32DialogItem> items)
    {
        ArgumentNullException.ThrowIfNull(caption);
        ArgumentNullException.ThrowIfNull(fontFace);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthDlu);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightDlu);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, ushort.MaxValue);
        if ((style & DsSetFont) != 0 && (fontFace.Length == 0 || fontPointSize == 0))
        {
            throw new ArgumentException("DS_SETFONT needs a font face and a point size.", nameof(style));
        }

        Caption = caption;
        Style = style;
        ExStyle = exStyle;
        X = x;
        Y = y;
        WidthDlu = widthDlu;
        HeightDlu = heightDlu;
        FontFace = fontFace;
        FontPointSize = fontPointSize;
        _items = items;
    }

    public string Caption { get; }

    public uint Style { get; }

    public uint ExStyle { get; }

    public int X { get; }

    public int Y { get; }

    public int WidthDlu { get; }

    public int HeightDlu { get; }

    public string FontFace { get; }

    public ushort FontPointSize { get; }

    /// <summary>The template bytes. The caller pins them for the duration of the create call only.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);

        writer.Write(DialogVersion);
        writer.Write(ExtendedSignature);
        writer.Write(0u);
        writer.Write(ExStyle);
        writer.Write(Style);
        writer.Write((ushort)_items.Count);
        writer.Write((short)X);
        writer.Write((short)Y);
        writer.Write((short)WidthDlu);
        writer.Write((short)HeightDlu);
        writer.Write((ushort)0); // No menu.
        writer.Write((ushort)0); // The default dialog class (#32770).
        WriteString(writer, Caption);

        if ((Style & DsSetFont) != 0)
        {
            writer.Write(FontPointSize);
            writer.Write((ushort)0); // Weight: the font's default.
            writer.Write((byte)0); // Not italic.
            writer.Write((byte)0); // ANSI_CHARSET, as the classic template.
            WriteString(writer, FontFace);
        }

        foreach (Win32DialogItem item in _items)
        {
            writer.Flush();
            AlignToDword(stream);
            writer.Write(0u); // Help id.
            writer.Write(0u); // Extended style.
            writer.Write(item.Style);
            writer.Write((short)item.X);
            writer.Write((short)item.Y);
            writer.Write((short)item.Cx);
            writer.Write((short)item.Cy);
            writer.Write((uint)item.Id);
            WriteString(writer, item.ClassName);
            WriteString(writer, item.Text);
            writer.Write((ushort)0); // No creation data.
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        writer.Write(bytes);
        writer.Write((ushort)0);
    }

    private static void AlignToDword(MemoryStream stream)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }
}
