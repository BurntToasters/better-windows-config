namespace Bwc.Registry;

public enum RegistryRoot
{
    ClassesRoot = unchecked((int)0x80000000),
    CurrentUser = unchecked((int)0x80000001),
    LocalMachine = unchecked((int)0x80000002),
    Users = unchecked((int)0x80000003),
    CurrentConfig = unchecked((int)0x80000005),
}

public static class RegistryRootExtensions
{
    /// <summary>The predefined HKEY. The handles are negative 32-bit values, sign-extended to pointer size.</summary>
    public static nint ToHkey(this RegistryRoot root) => (nint)(int)root;

    public static string DisplayName(this RegistryRoot root) => root switch
    {
        RegistryRoot.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryRoot.CurrentUser => "HKEY_CURRENT_USER",
        RegistryRoot.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryRoot.Users => "HKEY_USERS",
        RegistryRoot.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };
}
