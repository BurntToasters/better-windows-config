using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Bwc.Gpo.Store;

/// <summary>Why the editor must open read-only.</summary>
public enum GpoReadOnlyReason
{
    /// <summary>The process token is not in the Administrators group (not elevated).</summary>
    NotElevated,

    /// <summary>An access check on the GPO folder says this token cannot add files.</summary>
    NoWriteAccess,

    /// <summary>The Windows edition is Home.</summary>
    HomeEdition,
}

/// <summary>The outcome of the read-only detection: every reason that applies, in a fixed order.</summary>
/// <param name="Reasons">The reasons; empty when writing is allowed.</param>
public sealed class GpoAccessResult(IReadOnlyList<GpoReadOnlyReason> Reasons)
{
    /// <summary>The reasons, in the order not elevated, no write access, Home.</summary>
    public IReadOnlyList<GpoReadOnlyReason> Reasons { get; } = Reasons;

    /// <summary>True when any reason applies.</summary>
    public bool IsReadOnly => Reasons.Count > 0;
}

/// <summary>Replacements for the three machine facts, so tests can drive the aggregation.</summary>
public sealed class GpoAccessProbes
{
    /// <summary>Whether the process is elevated; default <see cref="GpoAccess.IsElevated"/>.</summary>
    public Func<bool>? IsElevated { get; init; }

    /// <summary>The Windows <c>EditionID</c>; default the registry value.</summary>
    public Func<string?>? EditionId { get; init; }

    /// <summary>Whether the folder can receive files; default <see cref="GpoAccess.CanWriteGpoFolder"/>.</summary>
    public Func<string, bool>? CanWriteFolder { get; init; }
}

/// <summary>
/// Read-only detection (docs/contracts/policy-engine.md). The access check reads a security descriptor and asks the
/// operating system; it creates, opens for write and locks nothing. It predicts; a write that then fails is reported
/// by the writer with the path and the Win32 code.
/// </summary>
public static partial class GpoAccess
{
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const int SecurityImpersonation = 2;

    /// <summary>Runs the three checks and reports every reason that applies.</summary>
    /// <param name="policy">The policy that names the GPO folder.</param>
    /// <param name="probes">Replacements for the machine facts; null uses the real ones.</param>
    /// <returns>The reasons.</returns>
    public static GpoAccessResult Check(IGpoStorePolicy policy, GpoAccessProbes? probes = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reasons = new List<GpoReadOnlyReason>();

        Func<bool> elevated = probes?.IsElevated ?? IsElevated;
        if (!Safe(elevated, false))
        {
            reasons.Add(GpoReadOnlyReason.NotElevated);
        }

        Func<string, bool> canWrite = probes?.CanWriteFolder ?? CanWriteGpoFolder;
        if (!Safe(() => canWrite(policy.GpoRoot), false))
        {
            reasons.Add(GpoReadOnlyReason.NoWriteAccess);
        }

        if (IsHomeEdition(Safe<string?>(probes?.EditionId ?? ReadEditionId, null)))
        {
            reasons.Add(GpoReadOnlyReason.HomeEdition);
        }

        return new GpoAccessResult(reasons);
    }

    /// <summary>True when the process token is in the Administrators group (a UAC-filtered token is not).</summary>
    /// <returns>Whether the process is elevated.</returns>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True for the Home family of <c>EditionID</c> (Core, CoreN, CoreSingleLanguage, CoreCountrySpecific).</summary>
    /// <param name="editionId">The edition id, or null when unknown (not Home).</param>
    /// <returns>Whether the edition is Home.</returns>
    public static bool IsHomeEdition(string? editionId) =>
        editionId is not null && editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the GPO folder (or, when missing, its nearest existing ancestor) can receive new files and folders, and
    /// the existing Machine and User folders can receive files.
    /// </summary>
    /// <param name="root">The GPO folder.</param>
    /// <returns>True when the access check grants it.</returns>
    public static bool CanWriteGpoFolder(string root)
    {
        if (!CanWriteFolder(root))
        {
            return false;
        }

        foreach (var child in new[] { GpoFileNames.MachineFolder, GpoFileNames.UserFolder })
        {
            var path = Path.Combine(root, child);
            if (Directory.Exists(path) && !CanWriteFolder(path))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An access check of the process token against the folder's security descriptor for adding files and folders.
    /// A folder that does not exist is judged by its nearest existing ancestor. Reads only.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>True when granted; false when denied, when a file is in the way, or when the check cannot run.</returns>
    public static bool CanWriteFolder(string folder)
    {
        string current;
        try
        {
            current = Path.GetFullPath(folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return false;
            }

            current = parent;
        }

        return Grants(current, FileAddFile | FileAddSubdirectory);
    }

    private static T Safe<T>(Func<T> probe, T fallback)
    {
        try
        {
            return probe();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return fallback;
        }
    }

    private static string? ReadEditionId()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("EditionID") as string;
    }

    private static bool Grants(string directory, uint rights)
    {
        byte[] descriptor;
        try
        {
            var security = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(directory),
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
            descriptor = security.GetSecurityDescriptorBinaryForm();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }

        var duplicate = IntPtr.Zero;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query);
            if (!DuplicateToken(identity.AccessToken.DangerousGetHandle(), SecurityImpersonation, out duplicate))
            {
                return false;
            }

            var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
            var privileges = new PrivilegeSet();
            var length = (uint)Marshal.SizeOf<PrivilegeSet>();
            return AccessCheck(descriptor, duplicate, rights, ref mapping, ref privileges, ref length, out var granted, out var status)
                && status
                && (granted & rights) == rights;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (duplicate != IntPtr.Zero)
            {
                CloseHandle(duplicate);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint Read;
        public uint Write;
        public uint Execute;
        public uint All;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeSet
    {
        public uint Count;
        public uint Control;
        public uint LuidLow;
        public int LuidHigh;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateToken(IntPtr existingToken, int impersonationLevel, out IntPtr duplicateToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AccessCheck(
        byte[] securityDescriptor,
        IntPtr clientToken,
        uint desiredAccess,
        ref GenericMapping genericMapping,
        ref PrivilegeSet privilegeSet,
        ref uint privilegeSetLength,
        out uint grantedAccess,
        [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
