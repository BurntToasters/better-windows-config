using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Bwc.E2E.Harness;

/// <summary>
/// Registry access restricted to HKCU\Software\BWC-E2E\&lt;runId&gt;. All relative paths are
/// validated so a test cannot escape the run subtree. SweepStale only removes old direct children
/// of HKCU\Software\BWC-E2E whose names look like generated run ids.
/// </summary>
public sealed partial class SandboxRegistry
{
    private const string ParentPath = @"Software\BWC-E2E";

#pragma warning disable SYSLIB1054 // The 30 line P/Invoke surface does not justify LibraryImport plumbing.
    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", ExactSpelling = true)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        IntPtr lpClass,
        IntPtr lpcchClass,
        IntPtr lpReserved,
        out uint lpcSubKeys,
        out uint lpcbMaxSubKeyLen,
        out uint lpcbMaxClassLen,
        out uint lpcValues,
        out uint lpcbMaxValueNameLen,
        out uint lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor,
        out long lpftLastWriteTime);

    [DllImport("advapi32.dll", EntryPoint = "RegSetKeySecurity", SetLastError = false)]
    private static extern int RegSetKeySecurity(
        SafeRegistryHandle hKey,
        uint securityInformation,
        byte[] securityDescriptor);
#pragma warning restore SYSLIB1054

    private readonly E2EConfig _config;

    public SandboxRegistry(E2EConfig config)
    {
        _config = config;
    }

    public string RunRootPath => $@"{ParentPath}\{_config.RunId}";

    public string RunRootDisplayPath => $@"HKCU\{RunRootPath}";

    public RegistryKey CreateSubKey(string relativePath)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        return Registry.CurrentUser.CreateSubKey(fullPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key '{fullPath}'.");
    }

    public void SetString(string relativePath, string name, string value) =>
        SetValue(relativePath, name, value, RegistryValueKind.String);

    public void SetDword(string relativePath, string name, int value) =>
        SetValue(relativePath, name, value, RegistryValueKind.DWord);

    public void SetBinary(string relativePath, string name, byte[] value) =>
        SetValue(relativePath, name, value, RegistryValueKind.Binary);

    /// <summary>Writes one value of the given registry kind under the run root, for kinds without a typed helper.</summary>
    public void SetValueOfKind(string relativePath, string name, object value, RegistryValueKind kind) =>
        SetValue(relativePath, name, value, kind);

    /// <summary>
    /// Creates a key named by the registry itself, such as "." or "..", below an ordinary relative parent, and sets
    /// one string value in it. The parent is validated as usual. The name is one component, so the key stays under
    /// the run root.
    /// </summary>
    public void CreateEnumeratedStringKey(string parentRelativePath, string keyName, string valueName, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyName);
        if (keyName.Contains('\\', StringComparison.Ordinal) || keyName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{keyName}' is not a single registry key name.", nameof(keyName));
        }

        var parentPath = Combine(RunRootPath, parentRelativePath);
        using var parent = Registry.CurrentUser.CreateSubKey(parentPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key '{parentPath}'.");
        using var key = parent.CreateSubKey(keyName, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key '{keyName}' under '{parentPath}'.");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    /// <summary>
    /// Adds a deny ACE for the current user to a key under the run root. The ACE is not inherited. Returns the DACL
    /// the key had before, which <see cref="RestoreSecurity"/> puts back.
    /// </summary>
    public RegistrySecurity DenyCurrentUser(string relativePath, RegistryRights rights)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        using var key = Registry.CurrentUser.OpenSubKey(fullPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open registry key '{fullPath}'.");
        RegistrySecurity original = key.GetAccessControl(AccessControlSections.Access);
        RegistrySecurity updated = key.GetAccessControl(AccessControlSections.Access);
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user has no SID.");
        updated.AddAccessRule(new RegistryAccessRule(
            user,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        key.SetAccessControl(updated);
        return original;
    }

    /// <summary>
    /// Puts back the DACL that <see cref="DenyCurrentUser"/> returned. A key that is already gone is skipped.
    /// </summary>
    public void RestoreSecurity(string relativePath, RegistrySecurity original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var fullPath = Combine(RunRootPath, relativePath);

        // A deny on a read right also refuses the writable open that the managed SetAccessControl needs. The key is
        // opened for WRITE_DAC alone, and the DACL is written with RegSetKeySecurity.
        using var key = Registry.CurrentUser.OpenSubKey(fullPath, RegistryRights.ChangePermissions);
        if (key is null)
        {
            return;
        }

        int error = RegSetKeySecurity(key.Handle, DaclSecurityInformation, original.GetSecurityDescriptorBinaryForm());
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private const uint DaclSecurityInformation = 0x00000004;

    /// <summary>Creates a key below the run root, with its parents. An existing key is kept.</summary>
    public void CreateKey(string relativePath)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        using var key = Registry.CurrentUser.CreateSubKey(fullPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key '{fullPath}'.");
    }

    public string? ReadString(string relativePath, string name)
    {
        using var key = OpenExisting(relativePath);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public int? ReadDword(string relativePath, string name)
    {
        using var key = OpenExisting(relativePath);
        var value = key?.GetValue(name);
        return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public byte[]? ReadBinary(string relativePath, string name)
    {
        using var key = OpenExisting(relativePath);
        return key?.GetValue(name) as byte[];
    }

    public void DeleteSubKeyTree(string relativePath)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        Registry.CurrentUser.DeleteSubKeyTree(fullPath, throwOnMissingSubKey: false);
    }

    public bool Exists(string relativePath)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        using var key = Registry.CurrentUser.OpenSubKey(fullPath, writable: false);
        return key is not null;
    }

    /// <summary>
    /// Deletes only the current run subtree. Missing keys are ignored.
    /// </summary>
    public void Cleanup() =>
        Registry.CurrentUser.DeleteSubKeyTree(RunRootPath, throwOnMissingSubKey: false);

    /// <summary>
    /// Deletes matching direct children of HKCU\Software\BWC-E2E that are older than the cutoff.
    /// Non matching names, the current run id, and keys with unreadable timestamps are left alone.
    /// </summary>
    public void SweepStale(TimeSpan olderThan)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(ParentPath, writable: true);
        if (parent is null)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var name in parent.GetSubKeyNames())
        {
            if (!StaleRunIdPattern().IsMatch(name) ||
                string.Equals(name, _config.RunId, StringComparison.Ordinal))
            {
                continue;
            }

            DateTime lastWriteUtc;
            using (var child = parent.OpenSubKey(name, writable: false))
            {
                if (child is null)
                {
                    continue;
                }

                try
                {
                    lastWriteUtc = GetLastWriteTimeUtc(child);
                }
                catch (Win32Exception)
                {
                    continue;
                }
            }

            if (lastWriteUtc >= cutoff)
            {
                continue;
            }

            try
            {
                parent.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Another process may still hold the key. Leave it for the next sweep.
            }
        }
    }

    [GeneratedRegex(@"^\d{8}T\d{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex StaleRunIdPattern();

    private static DateTime GetLastWriteTimeUtc(RegistryKey key)
    {
        var error = RegQueryInfoKey(
            key.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            IntPtr.Zero,
            out var lastWriteFileTime);

        if (error != 0)
        {
            throw new Win32Exception(error);
        }

        return DateTime.FromFileTimeUtc(lastWriteFileTime);
    }

    private static string Combine(string root, string relativePath)
    {
        ValidateRelativePath(relativePath);
        return string.IsNullOrEmpty(relativePath) ? root : $@"{root}\{relativePath}";
    }

    private static void ValidateRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0)
        {
            return;
        }

        if (relativePath.StartsWith('\\') ||
            relativePath.Contains(':') ||
            relativePath.Contains('/'))
        {
            throw new ArgumentException(
                $"Sandbox path '{relativePath}' must be relative to the run root and use backslashes.",
                nameof(relativePath));
        }

        foreach (var segment in relativePath.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException(
                    $"Sandbox path '{relativePath}' contains an invalid segment.",
                    nameof(relativePath));
            }
        }
    }

    private RegistryKey? OpenExisting(string relativePath)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        return Registry.CurrentUser.OpenSubKey(fullPath, writable: false);
    }

    private void SetValue(string relativePath, string name, object value, RegistryValueKind kind)
    {
        var fullPath = Combine(RunRootPath, relativePath);
        using var key = Registry.CurrentUser.CreateSubKey(fullPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key '{fullPath}'.");
        key.SetValue(name, value, kind);
    }
}
