using System.Globalization;
using Bwc.Oracle.Resources.Native;

namespace Bwc.Oracle.Resources.Format;

internal static class ResourceTypeNames
{
    public const int Menu = 4;
    public const int Dialog = 5;
    public const int StringTable = 6;
    public const int Accelerator = 9;

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "RT_CURSOR",
        [2] = "RT_BITMAP",
        [3] = "RT_ICON",
        [4] = "RT_MENU",
        [5] = "RT_DIALOG",
        [6] = "RT_STRING",
        [7] = "RT_FONTDIR",
        [8] = "RT_FONT",
        [9] = "RT_ACCELERATOR",
        [10] = "RT_RCDATA",
        [11] = "RT_MESSAGETABLE",
        [12] = "RT_GROUP_CURSOR",
        [14] = "RT_GROUP_ICON",
        [16] = "RT_VERSION",
        [17] = "RT_DLGINCLUDE",
        [19] = "RT_PLUGPLAY",
        [20] = "RT_VXD",
        [21] = "RT_ANICURSOR",
        [22] = "RT_ANIICON",
        [23] = "RT_HTML",
        [24] = "RT_MANIFEST",
    };

    public static string Display(ResourceId type)
    {
        if (type.Number is int number)
        {
            return Names.TryGetValue(number, out var name) ? name : $"RT_UNKNOWN_0x{number:X4}";
        }
        return type.Name ?? string.Empty;
    }
}

/// Bit-to-name decoding for the flag words that appear in resources.
internal static class Decode
{
    // Classic MENU MF_* option word (MENUITEMTEMPLATE.mtOption).
    public static readonly (uint Mask, string Name)[] ClassicMenuOptions =
    [
        (0x0001, "MF_GRAYED"),
        (0x0002, "MF_DISABLED"),
        (0x0004, "MF_BITMAP"),
        (0x0008, "MF_CHECKED"),
        (0x0010, "MF_POPUP"),
        (0x0020, "MF_MENUBARBREAK"),
        (0x0040, "MF_MENUBREAK"),
        (0x0080, "MF_END"),
        (0x0100, "MF_OWNERDRAW"),
        (0x0200, "MF_USECHECKBITMAPS"),
        (0x0800, "MF_SEPARATOR"),
        (0x1000, "MF_DEFAULT"),
        (0x2000, "MF_SYSMENU"),
        (0x4000, "MF_HELP"),
    ];

    // MENUEX dwType (MFT_*). MFT_STRING is 0 and therefore has no name.
    public static readonly (uint Mask, string Name)[] MenuExType =
    [
        (0x00000004, "MFT_BITMAP"),
        (0x00000020, "MFT_MENUBARBREAK"),
        (0x00000040, "MFT_MENUBREAK"),
        (0x00000100, "MFT_OWNERDRAW"),
        (0x00000200, "MFT_RADIOCHECK"),
        (0x00000800, "MFT_SEPARATOR"),
        (0x00002000, "MFT_RIGHTORDER"),
        (0x00004000, "MFT_RIGHTJUSTIFY"),
    ];

    // MENUEX dwState (MFS_*).
    public static readonly (uint Mask, string Name)[] MenuExState =
    [
        (0x00000003, "MFS_GRAYED"),
        (0x00000008, "MFS_CHECKED"),
        (0x00000080, "MFS_HILITE"),
        (0x00001000, "MFS_DEFAULT"),
    ];

    // ACCEL fFlags.
    public static readonly (uint Mask, string Name)[] AcceleratorFlags =
    [
        (0x01, "FVIRTKEY"),
        (0x02, "FNOINVERT"),
        (0x04, "FSHIFT"),
        (0x08, "FCONTROL"),
        (0x10, "FALT"),
    ];

    public static List<string> Names(uint value, (uint Mask, string Name)[] table)
    {
        var names = new List<string>();
        foreach (var (mask, name) in table)
        {
            if ((value & mask) == mask)
            {
                names.Add(name);
            }
        }
        return names;
    }

    public static string Hex32(uint value) => $"0x{value:X8}";

    public static string Hex16(ushort value) => $"0x{value:X4}";
}

/// Standard window-class atoms used by DLGITEMTEMPLATE class ordinals.
internal static class ControlClass
{
    private static readonly string[] Atoms = ["Button", "Edit", "Static", "ListBox", "ScrollBar", "ComboBox"];

    public static string? NameOf(int ordinal) => ordinal is >= 0x80 and <= 0x85 ? Atoms[ordinal - 0x80] : null;
}

/// Readable names for accelerator keys.
internal static class KeyNames
{
    private static readonly Dictionary<int, string> Named = new()
    {
        [0x08] = "VK_BACK",
        [0x09] = "VK_TAB",
        [0x0D] = "VK_RETURN",
        [0x13] = "VK_PAUSE",
        [0x14] = "VK_CAPITAL",
        [0x1B] = "VK_ESCAPE",
        [0x20] = "VK_SPACE",
        [0x21] = "VK_PRIOR",
        [0x22] = "VK_NEXT",
        [0x23] = "VK_END",
        [0x24] = "VK_HOME",
        [0x25] = "VK_LEFT",
        [0x26] = "VK_UP",
        [0x27] = "VK_RIGHT",
        [0x28] = "VK_DOWN",
        [0x2C] = "VK_SNAPSHOT",
        [0x2D] = "VK_INSERT",
        [0x2E] = "VK_DELETE",
        [0x2F] = "VK_HELP",
        [0x5B] = "VK_LWIN",
        [0x5C] = "VK_RWIN",
        [0x5D] = "VK_APPS",
        [0x6A] = "VK_MULTIPLY",
        [0x6B] = "VK_ADD",
        [0x6C] = "VK_SEPARATOR",
        [0x6D] = "VK_SUBTRACT",
        [0x6E] = "VK_DECIMAL",
        [0x6F] = "VK_DIVIDE",
        [0x90] = "VK_NUMLOCK",
        [0x91] = "VK_SCROLL",
        [0xBA] = "VK_OEM_1",
        [0xBB] = "VK_OEM_PLUS",
        [0xBC] = "VK_OEM_COMMA",
        [0xBD] = "VK_OEM_MINUS",
        [0xBE] = "VK_OEM_PERIOD",
        [0xBF] = "VK_OEM_2",
        [0xC0] = "VK_OEM_3",
        [0xDB] = "VK_OEM_4",
        [0xDC] = "VK_OEM_5",
        [0xDD] = "VK_OEM_6",
        [0xDE] = "VK_OEM_7",
        [0xE2] = "VK_OEM_102",
    };

    public static string Describe(ushort key, bool virtualKey)
    {
        if (!virtualKey)
        {
            if (key is >= 0x20 and <= 0x7E)
            {
                return $"'{(char)key}'";
            }
            // ASCII control codes are how Ctrl+letter accelerators are stored (0x06 is Ctrl+F).
            return key is >= 1 and <= 26 ? $"ASCII 0x{key:X2} (Ctrl+{(char)(key + 0x40)})" : $"ASCII 0x{key:X2}";
        }
        if (key is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A))
        {
            return $"'{(char)key}'";
        }
        if (key is >= 0x70 and <= 0x87)
        {
            return $"VK_F{key - 0x6F}";
        }
        if (key is >= 0x60 and <= 0x69)
        {
            return $"VK_NUMPAD{key - 0x60}";
        }
        return Named.TryGetValue(key, out var name) ? name : $"VK_0x{key:X2}";
    }
}

internal static class LanguageTags
{
    /// BCP-47 tag for a LANGID, "neutral" for 0, or a hex fallback for ids .NET does not know.
    public static string Of(int languageId)
    {
        if (languageId == 0)
        {
            return "neutral";
        }
        try
        {
            return CultureInfo.GetCultureInfo(languageId).Name;
        }
        catch (ArgumentException)
        {
            return $"lang-0x{languageId:X4}";
        }
    }
}
