namespace Bwc.Registry;

/// <summary>
/// The security information parts accepted by <c>RegGetKeySecurity</c> and
/// <c>RegSetKeySecurity</c>. The numeric values match the Win32
/// <c>OBJECT_SECURITY_INFORMATION</c> constants so a raw <see cref="uint"/> can be cast directly.
/// </summary>
[Flags]
public enum RegistrySecurityInformation : uint
{
    None = 0,
    Owner = 0x00000001,
    Group = 0x00000002,
    Dacl = 0x00000004,
    Sacl = 0x00000008,
    Label = 0x00000010,
    Attribute = 0x00000020,
    Scope = 0x00000040,
    Backup = 0x00010000,
    ProtectedDacl = 0x80000000,
    ProtectedSacl = 0x40000000,
    UnprotectedDacl = 0x20000000,
    UnprotectedSacl = 0x10000000,
}
