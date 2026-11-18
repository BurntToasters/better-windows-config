using System.Security.AccessControl;
using System.Security.Principal;

namespace Bwc.Update;

/// <summary>
/// Who may own and write the elevated helper's protected folders. The ship set is SYSTEM and the Administrators group,
/// owner Administrators. The E2E sandbox (and the unit tests) use the same rules with the current user in place of
/// Administrators,
/// so the logic can run without an elevated token. Folders are created with an explicit protected DACL (no inheritance from
/// a parent that another user could influence), and an existing root is accepted only when its owner is one of the owners
/// and its DACL grants write to nobody else (update-elevation.md, HP1 to HP6).
/// </summary>
public sealed class ProtectedAcl
{
    /// <summary>Rights that let a trustee change what is in the folder, or the folder itself.</summary>
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    private const int GenericWrite = 0x40000000;
    private const int GenericAll = 0x10000000;

    /// <summary>NT SERVICE\TrustedInstaller: the service identity that owns and writes Windows-installed folders.</summary>
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private ProtectedAcl(
        SecurityIdentifier owner,
        IReadOnlyList<SecurityIdentifier> trustedOwners,
        IReadOnlyList<SecurityIdentifier> fullControl,
        IReadOnlyList<SecurityIdentifier> trustedWriters)
    {
        Owner = owner;
        TrustedOwners = trustedOwners;
        FullControl = fullControl;
        TrustedWriters = trustedWriters;
    }

    /// <summary>The owner a new folder gets.</summary>
    public SecurityIdentifier Owner { get; }

    /// <summary>The owners an existing folder may have, besides SYSTEM. Always includes <see cref="Owner"/>.</summary>
    public IReadOnlyList<SecurityIdentifier> TrustedOwners { get; }

    /// <summary>The only entries of a new DACL.</summary>
    public IReadOnlyList<SecurityIdentifier> FullControl { get; }

    /// <summary>The trustees that may hold write access on an existing folder.</summary>
    public IReadOnlyList<SecurityIdentifier> TrustedWriters { get; }

    /// <summary>The ship rules: SYSTEM and Administrators full control, owner Administrators.</summary>
    public static ProtectedAcl Ship()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return new ProtectedAcl(administrators, [administrators], [system, administrators], [system, administrators, TrustedInstaller]);
    }

    /// <summary>
    /// The same rules with the current user in place of Administrators. E2E sandbox and tests only. A sandbox folder that
    /// was created under the user's temp folder inherits an Administrators entry, so an existing one may carry it. An
    /// existing folder may also be owned by Administrators, because an elevated process (the release gate, the elevated
    /// runner) creates folders with that owner. Administrators may already write here, so this trusts no one new.
    /// </summary>
    public static ProtectedAcl ForCurrentUser()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("the current user has no SID");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        return new ProtectedAcl(user, [user, administrators], [system, user], [system, user, administrators]);
    }

    /// <summary>The owners an existing folder may have: <see cref="TrustedOwners"/> and SYSTEM.</summary>
    public bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null
        && (TrustedOwners.Contains(owner) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

    /// <summary>A security descriptor for a new folder: owner, one full-control entry per principal, inheritance off.</summary>
    public DirectorySecurity NewSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in FullControl)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Null when an existing folder with this security is acceptable. Otherwise the reason: a foreign owner, a DACL that is
    /// not read as SIDs, or an allow entry that applies to the folder itself and grants write to a trustee outside
    /// <see cref="TrustedWriters"/>. A deny entry and an inherit-only entry do not count.
    /// </summary>
    public string? Inspect(FileSystemSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        SecurityIdentifier? owner;
        AuthorizationRuleCollection rules;
        try
        {
            owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or InvalidOperationException or SystemException)
        {
            return "the owner or the access list cannot be read";
        }

        if (!IsTrustedOwner(owner))
        {
            return "the owner is " + (owner?.Value ?? "unknown") + ", not a trusted owner";
        }

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
            {
                continue;
            }

            if (((int)rule.FileSystemRights & ((int)WriteRights | GenericWrite | GenericAll)) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier trustee || !TrustedWriters.Contains(trustee))
            {
                return "write access is granted to " + rule.IdentityReference.Value;
            }
        }

        return null;
    }
}
