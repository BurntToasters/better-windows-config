namespace Bwc.UI.Icons;

/// <summary>
/// A reference to an icon that a model can carry without holding a bitmap: either one of the classic regedit
/// <see cref="IconKind"/> icons or an icon inside an installed module. <see cref="IconProvider"/> turns it into an
/// image at the window's DPI, so the reference stays valid across a DPI change and on any thread.
/// </summary>
public readonly record struct IconReference
{
    private IconReference(IconKind? kind, ModuleIcon? module, IconKind? fallback)
    {
        Kind = kind;
        Module = module;
        Fallback = fallback;
    }

    /// <summary>The classic icon, or null when this reference names a module icon.</summary>
    public IconKind? Kind { get; }

    /// <summary>The module icon, or null when this reference names a classic icon.</summary>
    public ModuleIcon? Module { get; }

    /// <summary>The glyph shown when the module icon is absent. Null selects the neutral generic glyph.</summary>
    public IconKind? Fallback { get; }

    /// <summary>A classic regedit icon.</summary>
    public static IconReference FromKind(IconKind kind) => new(kind, null, null);

    /// <summary>An icon inside an installed module. <paramref name="fallback"/> is the glyph shown when it is absent.</summary>
    public static IconReference FromModule(ModuleIcon icon, IconKind? fallback = null) => new(null, icon, fallback);

    /// <summary>A classic icon as a reference.</summary>
    public static implicit operator IconReference(IconKind kind) => FromKind(kind);

    /// <summary>A module icon as a reference with the neutral fallback glyph.</summary>
    public static implicit operator IconReference(ModuleIcon icon) => FromModule(icon);
}
