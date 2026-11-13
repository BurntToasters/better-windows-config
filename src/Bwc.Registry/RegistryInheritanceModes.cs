namespace Bwc.Registry;

/// <summary>
/// The ACE inheritance flags for the three key inherit types offered by the Permissions dialog. The
/// values are the winnt.h ACE flags, so a value written with one of them is inherited exactly as the
/// dialog's label says.
/// </summary>
public static class RegistryInheritanceModes
{
    /// <summary>"This key only": the entry applies to the key and is not inherited.</summary>
    public const uint ThisKeyOnly = 0x00;

    /// <summary>"This key and subkeys": CONTAINER_INHERIT_ACE (0x2).</summary>
    public const uint ThisKeyAndSubkeys = 0x02;

    /// <summary>"Subkeys only": CONTAINER_INHERIT_ACE | INHERIT_ONLY_ACE (0x2 | 0x8).</summary>
    public const uint SubkeysOnly = 0x02 | 0x08;
}
