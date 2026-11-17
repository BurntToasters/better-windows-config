using System.Security.AccessControl;
using System.Security.Principal;

namespace Bwc.Registry;

/// <summary>
/// Computes the descriptor a child key holds after "Replace all child object permission entries". The
/// child's explicit entries are dropped. Its DACL becomes the entries the parent passes down, marked
/// <c>INHERITED_ACE</c>, and the child's DACL is unprotected so it keeps inheriting.
/// </summary>
/// <remarks>
/// The rules follow the ACE inheritance flags for a container child. An entry is passed down only when it
/// carries <c>CONTAINER_INHERIT_ACE</c>. Keys have no object children, so an entry that only has
/// <c>OBJECT_INHERIT_ACE</c> does not apply to a subkey. The copy keeps <c>CONTAINER_INHERIT_ACE</c> so the
/// grandchildren inherit it too. <c>INHERIT_ONLY_ACE</c> is cleared on the copy, because the child applies
/// the entry itself. <c>NO_PROPAGATE_INHERIT_ACE</c> stops the copy at the child. An attempt to compute the
/// inherited entries with <c>ConvertToAutoInheritPrivateObjectSecurity</c> returned an empty, protected DACL
/// for a parent with an inheritable entry, so the rules are applied here instead.
/// </remarks>
internal static class RegistrySecurityInheritance
{
    /// <summary>SE_DACL_AUTO_INHERIT_REQ and SE_DACL_AUTO_INHERITED, carried by every DACL this class computes.</summary>
    private const ControlFlags AutoInheritBits =
        ControlFlags.DiscretionaryAclAutoInheritRequired | ControlFlags.DiscretionaryAclAutoInherited;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericAll = 0x10000000;
    private const uint KeyRead = 0x00020019;
    private const uint KeyWrite = 0x00020006;
    private const uint KeyExecute = 0x00020019;
    private const uint KeyAllAccess = 0x000F003F;

    /// <summary>
    /// Returns the self-relative descriptor for a child whose current descriptor is
    /// <paramref name="current"/>, given the parent's effective descriptor <paramref name="parent"/>.
    /// </summary>
    /// <param name="parent">The parent's effective owner, group and DACL, self-relative.</param>
    /// <param name="current">The child's current owner, group and DACL, self-relative.</param>
    /// <param name="replaceDacl">When true, the DACL is rebuilt from the parent's inheritable entries.</param>
    /// <param name="copyOwner">When true, the child's owner is taken from the parent.</param>
    /// <param name="copyGroup">When true, the child's primary group is taken from the parent.</param>
    public static byte[] ComputeChildDescriptor(
        ReadOnlySpan<byte> parent,
        ReadOnlySpan<byte> current,
        bool replaceDacl,
        bool copyOwner,
        bool copyGroup)
    {
        var parentRaw = new RawSecurityDescriptor(parent.ToArray(), 0);
        var currentRaw = new RawSecurityDescriptor(current.ToArray(), 0);

        // The protected flag is cleared so the child inherits from its parent again.
        ControlFlags flags = currentRaw.ControlFlags & ~ControlFlags.DiscretionaryAclProtected;
        SecurityIdentifier? owner = copyOwner ? parentRaw.Owner : currentRaw.Owner;
        SecurityIdentifier? group = copyGroup ? parentRaw.Group : currentRaw.Group;
        RawAcl dacl = replaceDacl
            ? InheritedAcl(parentRaw.DiscretionaryAcl, owner, group)
            : currentRaw.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 0);
        if (replaceDacl)
        {
            flags |= AutoInheritBits;
        }

        var result = new RawSecurityDescriptor(flags, owner, group, null, dacl);
        byte[] binary = new byte[result.BinaryLength];
        result.GetBinaryForm(binary, 0);
        RegistrySecurityDescriptor.Validate(binary);
        return binary;
    }

    /// <summary>
    /// True when a DACL write has to carry the parent's inheritable entries: the request holds a DACL, it is not
    /// protected, and aclui asked for auto-inheritance (<c>UNPROTECTED_DACL_SECURITY_INFORMATION</c> or the
    /// <c>SE_DACL_AUTO_INHERIT_REQ</c> control bit). A plain DACL write from the engine is stored as given.
    /// </summary>
    public static bool NeedsInheritedEntries(RegistrySecurityInformation information, ReadOnlySpan<byte> request)
    {
        if ((information & RegistrySecurityInformation.Dacl) == 0 ||
            (information & RegistrySecurityInformation.ProtectedDacl) != 0)
        {
            return false;
        }

        ControlFlags flags = new RawSecurityDescriptor(request.ToArray(), 0).ControlFlags;
        if ((flags & ControlFlags.DiscretionaryAclPresent) == 0)
        {
            return false;
        }

        bool unprotected = (information & RegistrySecurityInformation.UnprotectedDacl) != 0;
        if (!unprotected && (flags & ControlFlags.DiscretionaryAclProtected) != 0)
        {
            return false;
        }

        return unprotected || (flags & ControlFlags.DiscretionaryAclAutoInheritRequired) != 0;
    }

    /// <summary>
    /// Returns <paramref name="request"/> with its DACL replaced by the request's explicit entries followed by the
    /// entries inherited from <paramref name="parent"/>. An entry in the request that is already marked
    /// <c>INHERITED_ACE</c> is a stale copy from the time the editor opened, so it is dropped and recomputed. Owner,
    /// group, SACL and the control bits of the request are kept.
    /// </summary>
    /// <param name="request">The self-relative descriptor aclui sent.</param>
    /// <param name="parent">The parent key's descriptor with at least its DACL, self-relative.</param>
    /// <param name="childOwner">The key's owner, for a CREATOR OWNER entry. Null keeps the owner of the request.</param>
    /// <param name="childGroup">The key's primary group, for a CREATOR GROUP entry. Null keeps the request's group.</param>
    public static byte[] MergeInheritedEntries(
        ReadOnlySpan<byte> request,
        ReadOnlySpan<byte> parent,
        SecurityIdentifier? childOwner = null,
        SecurityIdentifier? childGroup = null)
    {
        var requestRaw = new RawSecurityDescriptor(request.ToArray(), 0);
        var parentRaw = new RawSecurityDescriptor(parent.ToArray(), 0);
        if (requestRaw.DiscretionaryAcl is null)
        {
            return request.ToArray();
        }

        RawAcl inherited = InheritedAcl(
            parentRaw.DiscretionaryAcl,
            requestRaw.Owner ?? childOwner,
            requestRaw.Group ?? childGroup);
        byte revision = Math.Max(requestRaw.DiscretionaryAcl.Revision, inherited.Revision);
        var merged = new RawAcl(revision, requestRaw.DiscretionaryAcl.Count + inherited.Count);
        foreach (GenericAce ace in requestRaw.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.Inherited) == 0)
            {
                merged.InsertAce(merged.Count, ace);
            }
        }

        foreach (GenericAce ace in inherited)
        {
            merged.InsertAce(merged.Count, ace);
        }

        var result = new RawSecurityDescriptor(
            requestRaw.ControlFlags | AutoInheritBits,
            requestRaw.Owner,
            requestRaw.Group,
            requestRaw.SystemAcl,
            merged);
        byte[] binary = new byte[result.BinaryLength];
        result.GetBinaryForm(binary, 0);
        RegistrySecurityDescriptor.Validate(binary);
        return binary;
    }

    /// <summary>
    /// Returns <paramref name="descriptor"/> with <c>SE_DACL_PROTECTED</c> set when the information holds
    /// <c>PROTECTED_DACL_SECURITY_INFORMATION</c> and cleared when it holds <c>UNPROTECTED_DACL_SECURITY_INFORMATION</c>.
    /// The registry reads the protection from the descriptor, not from the information bits. Only the control word is
    /// changed. An information value with neither bit returns the descriptor as it was.
    /// </summary>
    public static byte[] WithProtection(RegistrySecurityInformation information, ReadOnlySpan<byte> descriptor)
    {
        byte[] result = descriptor.ToArray();
        if (result.Length < 4 || (information & RegistrySecurityInformation.Dacl) == 0)
        {
            return result;
        }

        ushort control = (ushort)(result[2] | (result[3] << 8));
        if ((information & RegistrySecurityInformation.ProtectedDacl) != 0)
        {
            control |= (ushort)ControlFlags.DiscretionaryAclProtected;
        }
        else if ((information & RegistrySecurityInformation.UnprotectedDacl) != 0)
        {
            control = (ushort)(control & ~(ushort)ControlFlags.DiscretionaryAclProtected);
        }

        result[2] = (byte)(control & 0xFF);
        result[3] = (byte)(control >> 8);
        return result;
    }

    /// <summary>
    /// Returns <paramref name="descriptor"/> in auto-inherit form: each entry that equals an entry the parent passes
    /// down is marked <c>INHERITED_ACE</c>, and <c>SE_DACL_AUTO_INHERITED</c> is set. The registry stores no
    /// inherited marks (a key created under a parent holds plain copies), while the system security API and the
    /// classic editor present them. A protected DACL is returned unchanged, because it inherits nothing.
    /// </summary>
    /// <param name="descriptor">The key's self-relative descriptor with a DACL.</param>
    /// <param name="parent">The parent key's descriptor with at least its DACL, self-relative.</param>
    public static byte[] MarkInheritedEntries(ReadOnlySpan<byte> descriptor, ReadOnlySpan<byte> parent)
    {
        var raw = new RawSecurityDescriptor(descriptor.ToArray(), 0);
        RawAcl? dacl = raw.DiscretionaryAcl;
        if (dacl is null || (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0)
        {
            return descriptor.ToArray();
        }

        RawAcl inherited = InheritedAcl(
            new RawSecurityDescriptor(parent.ToArray(), 0).DiscretionaryAcl,
            raw.Owner,
            raw.Group);
        var used = new bool[dacl.Count];
        var marked = new RawAcl(dacl.Revision, dacl.Count);
        for (int index = 0; index < dacl.Count; index++)
        {
            marked.InsertAce(index, dacl[index]);
        }

        bool changed = false;
        foreach (GenericAce wanted in inherited)
        {
            for (int index = 0; index < dacl.Count; index++)
            {
                GenericAce candidate = dacl[index];
                if (used[index] || (candidate.AceFlags & AceFlags.Inherited) != 0 || !SameEntry(candidate, wanted))
                {
                    continue;
                }

                used[index] = true;
                byte[] binary = new byte[candidate.BinaryLength];
                candidate.GetBinaryForm(binary, 0);
                GenericAce copy = GenericAce.CreateFromBinaryForm(binary, 0);
                copy.AceFlags = candidate.AceFlags | AceFlags.Inherited;
                marked.RemoveAce(index);
                marked.InsertAce(index, copy);
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return descriptor.ToArray();
        }

        var result = new RawSecurityDescriptor(
            raw.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited,
            raw.Owner,
            raw.Group,
            raw.SystemAcl,
            marked);
        byte[] binaryForm = new byte[result.BinaryLength];
        result.GetBinaryForm(binaryForm, 0);
        RegistrySecurityDescriptor.Validate(binaryForm);
        return binaryForm;
    }

    /// <summary>True when both entries have the same type, trustee, mask and flags, ignoring <c>INHERITED_ACE</c>.</summary>
    private static bool SameEntry(GenericAce candidate, GenericAce wanted)
    {
        if (candidate is not QualifiedAce left || wanted is not QualifiedAce right)
        {
            return false;
        }

        const AceFlags Ignored = AceFlags.Inherited;
        return left.AceQualifier == right.AceQualifier &&
            MapGeneric((uint)left.AccessMask) == MapGeneric((uint)right.AccessMask) &&
            (left.AceFlags & ~Ignored) == (right.AceFlags & ~Ignored) &&
            left.SecurityIdentifier.Equals(right.SecurityIdentifier);
    }

    /// <summary>
    /// Returns the DACL a container child inherits from <paramref name="parentDacl"/>. A null parent DACL
    /// gives an empty DACL, which grants nothing except the owner's implicit rights. An entry for CREATOR OWNER
    /// or CREATOR GROUP gives two entries, as the system does: an effective entry for the child's owner (or group)
    /// with the generic rights mapped to key rights, and the inherit-only creator entry for the grandchildren.
    /// </summary>
    /// <param name="childOwner">The child's owner. Null leaves CREATOR OWNER entries as inherit-only copies.</param>
    /// <param name="childGroup">The child's primary group. Null leaves CREATOR GROUP entries as inherit-only copies.</param>
    internal static RawAcl InheritedAcl(
        RawAcl? parentDacl,
        SecurityIdentifier? childOwner = null,
        SecurityIdentifier? childGroup = null)
    {
        byte revision = parentDacl?.Revision ?? GenericAcl.AclRevision;
        var inherited = new RawAcl(revision, 0);
        if (parentDacl is null)
        {
            return inherited;
        }

        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var creatorGroup = new SecurityIdentifier(WellKnownSidType.CreatorGroupSid, null);

        foreach (GenericAce ace in parentDacl)
        {
            AceFlags flags = ace.AceFlags;
            if ((flags & AceFlags.ContainerInherit) == 0)
            {
                continue;
            }

            if (ace is CommonAce common)
            {
                SecurityIdentifier? substitute =
                    common.SecurityIdentifier.Equals(creatorOwner) ? childOwner :
                    common.SecurityIdentifier.Equals(creatorGroup) ? childGroup : null;
                if (substitute is not null)
                {
                    AddCreatorEntries(inherited, common, substitute);
                    continue;
                }
            }

            AceFlags copyFlags = flags | AceFlags.Inherited;
            if ((flags & AceFlags.NoPropagateInherit) != 0)
            {
                // The copy stops at this child: it carries no inheritance flags for the grandchildren.
                copyFlags &= ~(AceFlags.ContainerInherit
                    | AceFlags.ObjectInherit
                    | AceFlags.InheritOnly
                    | AceFlags.NoPropagateInherit);
            }
            else if (!IsCreatorEntry(ace, creatorOwner, creatorGroup))
            {
                // The child applies the entry itself, so the copy is not inherit-only.
                copyFlags &= ~AceFlags.InheritOnly;
            }
            else
            {
                // A creator entry without a known owner or group applies to nobody here.
                copyFlags |= AceFlags.InheritOnly;
            }

            byte[] binary = new byte[ace.BinaryLength];
            ace.GetBinaryForm(binary, 0);
            GenericAce copy = GenericAce.CreateFromBinaryForm(binary, 0);
            copy.AceFlags = copyFlags;
            inherited.InsertAce(inherited.Count, copy);
        }

        return inherited;
    }

    private static bool IsCreatorEntry(GenericAce ace, SecurityIdentifier creatorOwner, SecurityIdentifier creatorGroup) =>
        ace is QualifiedAce qualified &&
        (qualified.SecurityIdentifier.Equals(creatorOwner) || qualified.SecurityIdentifier.Equals(creatorGroup));

    /// <summary>
    /// Adds the effective entry for the child's owner or group and, unless the parent entry stops at the child, the
    /// inherit-only creator entry that the grandchildren inherit.
    /// </summary>
    private static void AddCreatorEntries(RawAcl inherited, CommonAce parentEntry, SecurityIdentifier substitute)
    {
        inherited.InsertAce(
            inherited.Count,
            new CommonAce(
                AceFlags.Inherited,
                parentEntry.AceQualifier,
                (int)MapGeneric((uint)parentEntry.AccessMask),
                substitute,
                false,
                null));

        if ((parentEntry.AceFlags & AceFlags.NoPropagateInherit) != 0)
        {
            return;
        }

        AceFlags copyFlags = (parentEntry.AceFlags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit))
            | AceFlags.InheritOnly
            | AceFlags.Inherited;
        inherited.InsertAce(
            inherited.Count,
            new CommonAce(copyFlags, parentEntry.AceQualifier, parentEntry.AccessMask, parentEntry.SecurityIdentifier, false, null));
    }

    /// <summary>Maps the generic rights of a registry key entry to key rights.</summary>
    internal static uint MapGeneric(uint mask)
    {
        uint result = mask & ~(GenericRead | GenericWrite | GenericExecute | GenericAll);
        if ((mask & GenericRead) != 0)
        {
            result |= KeyRead;
        }

        if ((mask & GenericWrite) != 0)
        {
            result |= KeyWrite;
        }

        if ((mask & GenericExecute) != 0)
        {
            result |= KeyExecute;
        }

        if ((mask & GenericAll) != 0)
        {
            result |= KeyAllAccess;
        }

        return result;
    }
}
