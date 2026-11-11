using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
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
