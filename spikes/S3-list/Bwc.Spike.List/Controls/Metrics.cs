using Microsoft.UI.Xaml.Media;
using Bwc.Spike.ListApp.Model;

namespace Bwc.Spike.ListApp.Controls;

/// <summary>Geometry and shared brushes used by RowPanel, HeaderPanel and DetailsList. All values are DIPs (= px at 100%).</summary>
internal static class Metrics
{
    internal const double Pad = 4;
    internal const double IconGap = 4;
    internal const double ArrowWidth = 8;
    internal const double ArrowHeight = 4;
    internal const double ArrowSpace = 14;
    internal const double GripWidth = 8;

    internal static readonly FontFamily Font = new FontFamily("Segoe UI");

    internal static readonly SolidColorBrush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    internal static readonly SolidColorBrush Selection = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD7));
    internal static readonly SolidColorBrush Text = new SolidColorBrush(Microsoft.UI.Colors.Black);
    internal static readonly SolidColorBrush SelectedText = new SolidColorBrush(Microsoft.UI.Colors.White);
    internal static readonly SolidColorBrush HeaderBackground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
    internal static readonly SolidColorBrush Divider = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xC6, 0xC6, 0xC6));

    // Placeholder type icons (16x16 colored squares).
    private static readonly SolidColorBrush IconSz = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2B, 0x7B, 0xD6));
    private static readonly SolidColorBrush IconDword = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3C, 0x9A, 0x3C));
    private static readonly SolidColorBrush IconBinary = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x44, 0xAD));
    private static readonly SolidColorBrush IconMulti = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xD9, 0x82, 0x2B));

    internal static SolidColorBrush IconFor(string type)
    {
        switch (type)
        {
            case "REG_DWORD": return IconDword;
            case "REG_BINARY": return IconBinary;
            case "REG_MULTI_SZ": return IconMulti;
            default: return IconSz;
        }
    }
}
