namespace Bwc.Policy.Admx;

/// <summary>
/// Hard limits applied while loading templates. Template files are untrusted input and the app that reads them
/// runs elevated, so every limit is a named constant rather than a literal at the point of use.
/// </summary>
public static class AdmxLimits
{
    /// <summary>The largest ADMX or ADML file, in bytes. A larger file is refused before it is parsed.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>The deepest element nesting accepted in a template file.</summary>
    public const int MaxXmlDepth = 100;

    /// <summary>The longest template file name, in characters.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>The longest namespace, prefix, category name, policy name, element id and string id.</summary>
    public const int MaxNameLength = 255;

    /// <summary>The longest registry key path.</summary>
    public const int MaxKeyLength = 32767;

    /// <summary>The longest single component of a registry key path.</summary>
    public const int MaxKeyComponentLength = 255;

    /// <summary>The longest registry value name.</summary>
    public const int MaxValueNameLength = 16383;

    /// <summary>The longest ADML string, in characters. A longer string is not stored.</summary>
    public const int MaxStringLength = 1024 * 1024;
}
