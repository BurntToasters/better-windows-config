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
            ? InheritedAcl(parentRaw.DiscretionaryAcl)
            : currentRaw.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 0);

        var result = new RawSecurityDescriptor(flags, owner, group, null, dacl);
        byte[] binary = new byte[result.BinaryLength];
        result.GetBinaryForm(binary, 0);
        RegistrySecurityDescriptor.Validate(binary);
        return binary;
    }

    /// <summary>
    /// Returns the DACL a container child inherits from <paramref name="parentDacl"/>. A null parent DACL
    /// gives an empty DACL, which grants nothing except the owner's implicit rights.
    /// </summary>
    internal static RawAcl InheritedAcl(RawAcl? parentDacl)
    {
        byte revision = parentDacl?.Revision ?? GenericAcl.AclRevision;
        var inherited = new RawAcl(revision, 0);
        if (parentDacl is null)
        {
            return inherited;
        }

        foreach (GenericAce ace in parentDacl)
        {
            AceFlags flags = ace.AceFlags;
            if ((flags & AceFlags.ContainerInherit) == 0)
            {
                continue;
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
            else
            {
                // The child applies the entry itself, so the copy is not inherit-only.
                copyFlags &= ~AceFlags.InheritOnly;
            }

            byte[] binary = new byte[ace.BinaryLength];
            ace.GetBinaryForm(binary, 0);
            GenericAce copy = GenericAce.CreateFromBinaryForm(binary, 0);
            copy.AceFlags = copyFlags;
            inherited.InsertAce(inherited.Count, copy);
        }

        return inherited;
    }
}
