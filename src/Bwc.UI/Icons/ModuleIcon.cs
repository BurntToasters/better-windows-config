namespace Bwc.UI.Icons;

/// <summary>The folder a <see cref="ModuleIcon"/> is read from. Only these two exist.</summary>
public enum IconModuleDirectory
{
    /// <summary>The system folder (C:\Windows\System32), where gpedit.dll and admtmpl.dll live.</summary>
    System32,

    /// <summary>The Windows folder (C:\Windows), where regedit.exe lives.</summary>
    Windows,
}

/// <summary>
/// An icon inside an installed module (an exe or dll), named by file name and by either the resource id of a
/// group icon or its index among the module's group icons. Nothing is stored in BWC: the icon is read from the
/// installed file at run time by <see cref="IconProvider"/>, and a module or icon that is absent gives the BWC
/// fallback glyph. The module must be a plain file name; anything with a folder part is treated as absent.
/// </summary>
public readonly record struct ModuleIcon
{
    private ModuleIcon(string module, int value, bool isIndex, IconModuleDirectory directory)
    {
        Module = module;
        Value = value;
        IsIndex = isIndex;
        Directory = directory;
    }

    /// <summary>The module file name, lower case (for example <c>gpedit.dll</c>). Null only for <c>default</c>.</summary>
    public string? Module { get; }

    /// <summary>The group icon resource id, or the zero-based index when <see cref="IsIndex"/> is true.</summary>
    public int Value { get; }

    /// <summary>True when <see cref="Value"/> is an index into the module's group icons in resource directory order.</summary>
    public bool IsIndex { get; }

    /// <summary>The folder the module is read from.</summary>
    public IconModuleDirectory Directory { get; }

    /// <summary>Names the group icon with this resource id.</summary>
    public static ModuleIcon ById(string module, int resourceId, IconModuleDirectory directory = IconModuleDirectory.System32) =>
        new(Normalize(module), resourceId, isIndex: false, directory);

    /// <summary>Names the nth group icon of the module, counted from zero in resource directory order.</summary>
    public static ModuleIcon ByIndex(string module, int index, IconModuleDirectory directory = IconModuleDirectory.System32) =>
        new(Normalize(module), index, isIndex: true, directory);

    private static string Normalize(string? module) => (module ?? string.Empty).Trim().ToLowerInvariant();

    /// <inheritdoc />
    public override string ToString() =>
        $"{Directory}\\{Module}{(IsIndex ? "#" : "@")}{Value}";
}
