using Bwc.Policy.Admx;

namespace Bwc.Policy.Store;

/// <summary>The name rules for a file or language folder in the store. Names are single segments, never paths.</summary>
public static class TemplateFileNames
{
    /// <summary>The ADMX extension, as written to the store.</summary>
    public const string AdmxExtension = ".admx";

    /// <summary>The ADML extension.</summary>
    public const string AdmlExtension = ".adml";

    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Windows also reserves the superscript digit forms (U+00B9, U+00B2, U+00B3).
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    ];

    /// <summary>
    /// True when the text is one safe name segment: no separator, drive, stream or wildcard, no control character, no
    /// trailing dot or space, no leading space, not a device name, and not longer than the file name limit.
    /// </summary>
    public static bool IsSafeSegment(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > AdmxLimits.MaxFileNameLength)
        {
            return false;
        }

        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        if (name[^1] is '.' or ' ' || name[0] == ' ')
        {
            return false;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var head = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return !ReservedDeviceNames.Contains(head, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the text is a safe segment that ends in <c>.admx</c> (any case) and has a stem.</summary>
    public static bool IsSafeAdmxFileName(string? name) =>
        IsSafeSegment(name)
        && name!.Length > AdmxExtension.Length
        && name.EndsWith(AdmxExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stem of a safe ADMX file name.</summary>
    public static string StemOf(string admxFileName) => admxFileName[..^AdmxExtension.Length];

    /// <summary>The ADML file name that belongs to an ADMX file name.</summary>
    public static string AdmlNameOf(string admxFileName) => StemOf(admxFileName) + AdmlExtension;
}
