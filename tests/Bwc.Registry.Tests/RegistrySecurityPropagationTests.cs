#if BWC_E2E
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Bwc.Registry.Privileges;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

/// <summary>
/// Sandbox tests for the classic "Replace all child object permission entries" semantics and the
/// inherit-type flags shown by the Permissions dialog. Every write lands under
/// HKCU\Software\BWC-E2E\&lt;run-id&gt;, and each test removes its run tree in a finally block.
/// See docs/failure-modes/permissions.md sections 15 to 23.
/// </summary>
[TestClass]
public sealed class RegistrySecurityPropagationTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const int ErrorAccessDenied = 5;

    private static readonly RegistryReader Reader = new();

    [ClassInitialize]
    public static void EnsureSandboxBase(TestContext testContext)
    {
        _ = testContext;

        var writer = new RegistryWriter(new SandboxWritePolicy(Hkcu(SandboxBase)));
        writer.CreateKey(Hkcu(SandboxBase));
    }

    [TestMethod]
    public void PropagationGivesEveryDescendantOnlyInheritedEntries()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\A\B"));
            setup.CreateKey(Hkcu(tree + @"\C"));

            // Explicit, non-inherited entries on the children that propagation must remove.
            setup.SetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl, EveryoneDescriptor());
            setup.SetKeySecurity(Hkcu(tree + @"\C"), RegistrySecurityInformation.Dacl, EveryoneDescriptor());

            RegistryWriter writer = WriterFor(top);
            byte[] parent = UserDescriptor("D:(A;CI;KA;;;{0})");
            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl, parent);
            writer.SetKeySecurity(Hkcu(tree), parent, RegistrySecurityInformation.Dacl, propagateToSubkeys: true);

            SecurityIdentifier user = CurrentUser();
            foreach (string child in new[] { tree + @"\A", tree + @"\A\B", tree + @"\C" })
            {
                RawSecurityDescriptor descriptor = Read(child);
                Assert.IsFalse(
                    (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                    child + " must not be protected after propagation.");
                Assert.IsNotNull(Dacl(descriptor), child);
                Assert.AreEqual(1, Dacl(descriptor).Count, child + " must have exactly one entry.");

                CommonAce ace = (CommonAce)Dacl(descriptor)[0];
                Assert.IsTrue(ace.AceFlags.HasFlag(AceFlags.Inherited), child + " entry must be marked inherited.");
                Assert.AreEqual(user, ace.SecurityIdentifier, child);
                Assert.AreEqual(0x000F003Fu, (uint)ace.AccessMask & 0x000F003Fu, child + " must keep full control.");
            }

            RawSecurityDescriptor root = Read(tree);
            Assert.AreEqual(1, Dacl(root).Count, "The root keeps the explicit entry it was given.");
            Assert.IsFalse(
                ((CommonAce)Dacl(root)[0]).AceFlags.HasFlag(AceFlags.Inherited),
                "The root entry is explicit, not inherited.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void PropagationClearsTheProtectedStateOfChildren()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));

            // A child whose DACL is protected stops inheriting, so it has to be unprotected by propagation.
            setup.SetKeySecurity(
                Hkcu(tree + @"\A"),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                ProtectedEveryoneDescriptor());
            Assert.IsTrue(
                (Read(tree + @"\A").ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                "Test setup: the child must start protected.");

            RegistryWriter writer = WriterFor(top);
            byte[] parent = UserDescriptor("D:(A;CI;KA;;;{0})");
            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl, parent);
            writer.SetKeySecurity(Hkcu(tree), parent, RegistrySecurityInformation.Dacl, propagateToSubkeys: true);

            RawSecurityDescriptor child = Read(tree + @"\A");
            Assert.IsFalse(
                (child.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                "Propagation must clear the protected state.");
            Assert.AreEqual(1, Dacl(child).Count);
            Assert.AreEqual(CurrentUser(), ((CommonAce)Dacl(child)[0]).SecurityIdentifier);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void PropagationRemovesTheUsersOwnAccessAndTheOwnerRecoversIt()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\C"));

            // Everyone can list the children before the propagation, which is the case the walk must handle.
            setup.SetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl, EveryoneDescriptor());
            setup.SetKeySecurity(Hkcu(tree + @"\C"), RegistrySecurityInformation.Dacl, EveryoneDescriptor());

            // Without CONTAINER_INHERIT_ACE the parent entry is not passed to children, so no child inherits an
            // entry for the current user. The propagation is not refused. Each child is left with no entries.
            // The children are leaves: under the sandbox policy the link guard opens each key from its root, so
            // a key below a child that the walk has just emptied cannot be reached (permissions.md section 26).
            RegistryWriter writer = WriterFor(top);
            byte[] parentOnly = UserDescriptor("D:(A;;KA;;;{0})");
            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl, parentOnly);
            writer.SetKeySecurity(Hkcu(tree), parentOnly, RegistrySecurityInformation.Dacl, propagateToSubkeys: true);

            foreach (string child in new[] { tree + @"\A", tree + @"\C" })
            {
                Assert.AreEqual(0, Dacl(Read(child)).Count, child + " must hold no entries after propagation.");
            }

            // The current user has lost enumeration on A. The owner can still read the descriptor.
            RegistryOperationException denied = Assert.ThrowsExactly<RegistryOperationException>(
                () => Reader.EnumerateSubKeyNames(Hkcu(tree + @"\A")));
            Assert.AreEqual(ErrorAccessDenied, denied.Win32Error);
            Assert.AreEqual(0, Dacl(Read(tree + @"\A")).Count);

            // Recovery: the owner opens the key with WRITE_DAC alone and writes a DACL that grants the current user
            // full control again. The engine is not used for this write under the sandbox, because its link guard
            // also asks the key for KEY_QUERY_VALUE and KEY_ENUMERATE_SUB_KEYS, which the emptied key denies.
            Assert.IsFalse(
                RegistryLinkFixture.RestoreKeyOrRemoveLink(tree, "A", UserDescriptor("D:(A;CI;KA;;;{0})")),
                "A is a plain key, so the WRITE_DAC open does not remove it as a link.");

            IReadOnlyList<string> recovered = Reader.EnumerateSubKeyNames(Hkcu(tree + @"\A"));
            Assert.AreEqual(0, recovered.Count, "The recovered key lists its subkeys again, and it has none.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void CleanupRemovesNestedKeysThatDenyTheCurrentUser()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\A\B"));

            // B is set first, while A still lets the current user reach it through the link guard. Then A is set
            // to a DACL that grants the user access to A only. B keeps an empty DACL, which the walk must restore.
            setup.SetKeySecurity(Hkcu(tree + @"\A\B"), RegistrySecurityInformation.Dacl, EmptyDescriptor());
            setup.SetKeySecurity(
                Hkcu(tree + @"\A"),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                UserDescriptor("D:(A;;KA;;;{0})"));

            RegistryOperationException denied = Assert.ThrowsExactly<RegistryOperationException>(
                () => Reader.EnumerateSubKeyNames(Hkcu(tree + @"\A\B")));
            Assert.AreEqual(ErrorAccessDenied, denied.Win32Error, "Test setup: B must deny the current user.");

            CleanupRun(runId);

            Assert.IsFalse(Reader.KeyExists(Hkcu(top)), "The run tree must be gone, including the keys that denied the user.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void PropagationRefusesSaclBeforeAnyChange()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.SetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl, EveryoneDescriptor());
            byte[] before = Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl);

            RegistryWriter writer = WriterFor(top);
            Assert.ThrowsExactly<ArgumentException>(
                () => writer.SetKeySecurity(
                    Hkcu(tree),
                    UserDescriptor("D:(A;CI;KA;;;{0})"),
                    RegistrySecurityInformation.Dacl | RegistrySecurityInformation.Sacl,
                    propagateToSubkeys: true));

            CollectionAssert.AreEqual(
                before,
                Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl),
                "A refused SACL propagation must not change any child.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void InheritModesUseTheWin32AceFlags()
    {
        // The managed AceFlags values are the winnt.h ACE flag values. Identity keeps the analyzers from
        // folding these comparisons into constant assertions.
        // Measured on classic regedit (docs/parity/oracle/permissions-inherit.json), from the Applies-to combo's
        // inherit-type table: "This key only" 0x0, "This key and subkeys" 0x2, "Subkeys only" 0xA.
        // The table has no 0x1 or 0x3 entry.
        Assert.AreEqual(Identity(0x2u), Identity((uint)AceFlags.ContainerInherit), "CONTAINER_INHERIT_ACE is 0x2 in winnt.h.");
        Assert.AreEqual(Identity(0x8u), Identity((uint)AceFlags.InheritOnly), "INHERIT_ONLY_ACE is 0x8 in winnt.h.");
        Assert.AreEqual(
            Identity(0u),
            Identity(RegistryInheritanceModes.ThisKeyOnly),
            "This key only is 0x0 (permissions-inherit.json).");
        Assert.AreEqual(
            Identity(0x2u),
            Identity(RegistryInheritanceModes.ThisKeyAndSubkeys),
            "This key and subkeys is 0x2 (permissions-inherit.json).");
        Assert.AreEqual(
            Identity(0xAu),
            Identity(RegistryInheritanceModes.SubkeysOnly),
            "Subkeys only is 0xA, CONTAINER_INHERIT_ACE | INHERIT_ONLY_ACE (permissions-inherit.json).");
    }

    [TestMethod]
    public void CopyFromNativeConvertsAnAbsoluteDescriptor()
    {
        byte[] acl = AclBytes("D:(A;;KA;;;WD)");
        IntPtr absolute = Marshal.AllocHGlobal(256);
        GCHandle pinned = GCHandle.Alloc(acl, GCHandleType.Pinned);

        try
        {
            Assert.IsTrue(InitializeSecurityDescriptor(absolute, 1), "InitializeSecurityDescriptor failed.");
            Assert.IsTrue(
                SetSecurityDescriptorDacl(absolute, true, pinned.AddrOfPinnedObject(), false),
                "SetSecurityDescriptorDacl failed.");

            byte[] copied = RegistrySecurityDescriptor.CopyFromNative(absolute);

            RegistrySecurityDescriptor.Validate(copied);
            var raw = new RawSecurityDescriptor(copied, 0);
            Assert.IsTrue(
                (raw.ControlFlags & ControlFlags.SelfRelative) != 0,
                "The copied descriptor must be self-relative.");
            Assert.AreEqual(
                new RawSecurityDescriptor("D:(A;;KA;;;WD)").GetSddlForm(AccessControlSections.Access),
                raw.GetSddlForm(AccessControlSections.Access));
        }
        finally
        {
            pinned.Free();
            Marshal.FreeHGlobal(absolute);
        }
    }

    [TestMethod]
    public void CopyFromNativeRejectsANullPointer()
    {
        Assert.ThrowsExactly<ArgumentException>(() => RegistrySecurityDescriptor.CopyFromNative(IntPtr.Zero));
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(IntPtr securityDescriptor, uint revision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] bool daclPresent,
        IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] bool daclDefaulted);

    private static byte[] AclBytes(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var binary = new byte[Dacl(descriptor).BinaryLength];
        Dacl(descriptor).GetBinaryForm(binary, 0);
        return binary;
    }

    private static string NewRunId() => "propagation-" + Guid.NewGuid().ToString("N");

    private static string TopOf(string runId) => $@"{SandboxBase}\{runId}";

    private static RegistryPath Hkcu(string relativePath) => RegistryPath.Parse(@"HKCU\" + relativePath);

    private static RegistryWriter WriterFor(string sandboxRelativePath) =>
        new(new SandboxWritePolicy(Hkcu(sandboxRelativePath)));

    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static uint Identity(uint value) => value;

    private static RawAcl Dacl(RawSecurityDescriptor descriptor) =>
        descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("The descriptor has no DACL.");

    private static RawSecurityDescriptor Read(string relativePath) =>
        new(Reader.GetKeySecurity(
            Hkcu(relativePath),
            RegistrySecurityInformation.Owner | RegistrySecurityInformation.Dacl), 0);

    /// <summary>
    /// The owner walk changes only the owner. A child that denies WRITE_OWNER to the current user fails, and the walk
    /// still reaches the children below it and the sibling. The owner keeps WRITE_DAC, so the cleanup can restore the
    /// child's DACL. See docs/failure-modes/permissions.md section 35.
    /// </summary>
    [TestMethod]
    public void OwnerRecursionContinuesPastADeniedChild()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\A\B"));
            setup.CreateKey(Hkcu(tree + @"\C"));
            setup.SetKeySecurity(
                Hkcu(tree + @"\A"),
                RegistrySecurityInformation.Dacl,
                UserDescriptor("D:(D;;WO;;;{0})(A;CI;KA;;;{0})"));

            var reported = new List<string>();
            var progress = new SynchronousProgress(reported);
            RegistryWriter writer = WriterFor(top);
            byte[] owner = UserDescriptor("O:{0}");

            RegistrySecurityWalkException walk = Assert.ThrowsExactly<RegistrySecurityWalkException>(
                () => writer.SetKeyOwnerTree(Hkcu(tree), owner, progress));

            Assert.AreEqual(1, walk.Failures.Count, "Only the denied child fails.");
            Assert.AreEqual(ErrorAccessDenied, walk.Failures[0].Win32Error);
            Assert.AreEqual(
                Hkcu(tree + @"\A").ToAbbreviatedString(),
                walk.Failures[0].Path.ToAbbreviatedString());

            // The walk continues past the failed child: its child and its sibling are still changed.
            CollectionAssert.Contains(reported, Hkcu(tree + @"\A\B").ToAbbreviatedString());
            CollectionAssert.Contains(reported, Hkcu(tree + @"\C").ToAbbreviatedString());
            CollectionAssert.Contains(reported, Hkcu(tree).ToAbbreviatedString());
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>
    /// The defect of docs/failure-modes/permissions.md section 40: aclui sends only the explicit entries with
    /// SE_DACL_AUTO_INHERIT_REQ, and a raw write dropped every inherited entry. The stored DACL must be the explicit
    /// entries followed by the entries the parent passes down, marked inherited, with the DACL unprotected.
    /// </summary>
    [TestMethod]
    public void UnprotectedDaclWriteKeepsTheEntriesInheritedFromTheParent()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})(A;CI;KR;;;BU)"));

            // What aclui sends on Apply: explicit entries only, with the auto-inherit request bit.
            RegistryWriter writer = WriterFor(top);
            writer.SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                SddlBinary("D:AR(A;;KR;;;WD)"));

            RawSecurityDescriptor stored = Read(child);
            Assert.IsFalse((stored.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "The DACL stays unprotected.");
            Assert.AreEqual(3, Dacl(stored).Count, "One explicit entry and the two entries inherited from the parent.");

            var explicitAce = (CommonAce)Dacl(stored)[0];
            Assert.IsFalse(explicitAce.AceFlags.HasFlag(AceFlags.Inherited), "The explicit entry comes first.");
            Assert.AreEqual(new SecurityIdentifier(WellKnownSidType.WorldSid, null), explicitAce.SecurityIdentifier);

            var inheritedUser = (CommonAce)Dacl(stored)[1];
            Assert.IsTrue(inheritedUser.AceFlags.HasFlag(AceFlags.Inherited), "The user's entry is inherited.");
            Assert.AreEqual(CurrentUser(), inheritedUser.SecurityIdentifier);
            Assert.AreEqual(0x000F003Fu, (uint)inheritedUser.AccessMask & 0x000F003Fu);

            var inheritedUsers = (CommonAce)Dacl(stored)[2];
            Assert.IsTrue(inheritedUsers.AceFlags.HasFlag(AceFlags.Inherited));
            Assert.AreEqual(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), inheritedUsers.SecurityIdentifier);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>A protected DACL keeps only what the caller sent, and stays protected.</summary>
    [TestMethod]
    public void ProtectedDaclWriteStoresExactlyWhatWasSent()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})(A;CI;KR;;;BU)"));

            RegistryWriter writer = WriterFor(top);
            writer.SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:PAR(A;;KA;;;{0})(A;;KR;;;WD)"));

            RawSecurityDescriptor stored = Read(child);
            Assert.IsTrue((stored.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, "The DACL stays protected.");
            Assert.AreEqual(2, Dacl(stored).Count, "A protected DACL gets no inherited entry.");
            foreach (GenericAce ace in Dacl(stored))
            {
                Assert.IsFalse(ace.AceFlags.HasFlag(AceFlags.Inherited), "No entry is marked inherited.");
            }
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>
    /// Entries marked inherited in the request are stale copies from the time the editor opened. They are dropped
    /// and recomputed from the parent, so an entry the parent no longer passes down disappears and one it still
    /// passes down is stored once.
    /// </summary>
    [TestMethod]
    public void StaleInheritedEntriesInTheRequestAreRecomputedFromTheParent()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})"));

            // The request carries a stale entry for SYSTEM and a copy of the user's inherited entry.
            RegistryWriter writer = WriterFor(top);
            writer.SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                UserDescriptor("D:AR(A;;KR;;;WD)(A;CIID;KA;;;SY)(A;CIID;KA;;;{0})"));

            RawSecurityDescriptor stored = Read(child);
            Assert.AreEqual(2, Dacl(stored).Count, "The explicit entry and one inherited entry for the user.");
            Assert.IsFalse(((CommonAce)Dacl(stored)[0]).AceFlags.HasFlag(AceFlags.Inherited));
            var inherited = (CommonAce)Dacl(stored)[1];
            Assert.IsTrue(inherited.AceFlags.HasFlag(AceFlags.Inherited));
            Assert.AreEqual(CurrentUser(), inherited.SecurityIdentifier);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>
    /// A DACL write that asks for neither auto-inheritance nor an unprotected DACL is stored as given. The engine's own
    /// setup and recovery writes rely on this, and a descriptor read from a key can be copied to another key.
    /// </summary>
    [TestMethod]
    public void PlainDaclWriteIsStoredAsGiven()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})"));

            setup.SetKeySecurity(Hkcu(child), RegistrySecurityInformation.Dacl, UserDescriptor("D:(A;;KA;;;{0})"));

            Assert.AreEqual(1, Dacl(Read(child)).Count, "A plain DACL write adds no inherited entry.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>
    /// Elevated: a child owned by Administrators, with no entry for the current user, cannot take WRITE_DAC. The
    /// propagation fails for that child with access denied, and the walk still changes its sibling. The owner is
    /// restored under SeRestorePrivilege in the finally block, before the run tree is removed.
    /// See docs/failure-modes/permissions.md sections 33 and 39.
    /// </summary>
    [TestMethod]
    [TestCategory("Elevated")]
    public void PropagationContinuesPastAChildOwnedByAdministrators()
    {
        if (!IsElevated())
        {
            Assert.Inconclusive("Not elevated: this test runs only through the elevated runner.");
        }

        if (!PrivilegeScope.TryEnable(out PrivilegeScope? privileges, out string? error) || privileges is null)
        {
            Assert.Inconclusive("The restore privilege is not held: " + error);
        }

        using PrivilegeScope restoreScope = privileges;
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\C"));

            setup.SetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Owner, SddlBinary("O:BA"));
            setup.SetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl, SddlBinary("D:"));

            RegistryWriter writer = WriterFor(top);
            byte[] parent = UserDescriptor("D:(A;CI;KA;;;{0})");
            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl, parent);

            RegistrySecurityWalkException walk = Assert.ThrowsExactly<RegistrySecurityWalkException>(
                () => writer.SetKeySecurity(Hkcu(tree), parent, RegistrySecurityInformation.Dacl, propagateToSubkeys: true));

            Assert.AreEqual(1, walk.Failures.Count, "Only the Administrators-owned child fails.");
            Assert.AreEqual(ErrorAccessDenied, walk.Failures[0].Win32Error);
            Assert.AreEqual(1, Dacl(Read(tree + @"\C")).Count, "The sibling still receives its inherited entry.");
        }
        finally
        {
            try
            {
                // The restore privilege is still enabled here, so the owner can be set back to the current user.
                WriterFor(top).SetKeySecurity(
                    Hkcu(tree + @"\A"),
                    RegistrySecurityInformation.Owner,
                    UserDescriptor("O:{0}"));
            }
            finally
            {
                CleanupRun(runId);
            }
        }
    }

    /// <summary>
    /// Section 42 item A. The registry stores the entries of a new key without inherited marks. The editor gets them
    /// marked, sends back the explicit entries only, and the stored DACL then holds each inherited entry once.
    /// </summary>
    [TestMethod]
    public void ApplyOfThePresentedDescriptorStoresNoDuplicates()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})(A;CI;KR;;;BU)"));
            setup.CreateKey(Hkcu(child));

            byte[] stored = Reader.GetKeySecurity(Hkcu(child), RegistrySecurityInformation.Dacl);
            byte[] parent = Reader.GetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl);
            Assert.IsFalse(
                Dacl(new RawSecurityDescriptor(stored, 0)).Cast<GenericAce>().Any(a => a.AceFlags.HasFlag(AceFlags.Inherited)),
                "The registry stores a new key's entries without inherited marks.");

            var presented = new RawSecurityDescriptor(RegistrySecurityPresentation.MarkInheritedEntries(stored, parent), 0);
            Assert.AreEqual(2, Dacl(presented).Count);
            Assert.IsTrue(Dacl(presented).Cast<GenericAce>().All(a => a.AceFlags.HasFlag(AceFlags.Inherited)));
            Assert.IsTrue(presented.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited));

            // What aclui sends back: the explicit entries only, plus the auto-inherit request.
            RegistryWriter writer = WriterFor(top);
            writer.SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl,
                SddlBinary("D:AR(A;;KR;;;WD)"));

            RawAcl after = Dacl(Read(child));
            Assert.AreEqual(3, after.Count, "One explicit entry and the parent's two entries, each once.");
            Assert.AreEqual(1, after.Cast<GenericAce>().Count(a => !a.AceFlags.HasFlag(AceFlags.Inherited)));
            Assert.AreEqual(
                1,
                after.Cast<CommonAce>().Count(a => a.SecurityIdentifier.Equals(CurrentUser())),
                "The user's entry exists once, as an inherited entry.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>
    /// Section 42 item B. A CREATOR OWNER entry that the parent passes down becomes an effective entry for the child's
    /// owner and an inherit-only copy for the grandchildren.
    /// </summary>
    [TestMethod]
    public void InheritedEntriesReplaceCreatorOwner()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})(A;CIIO;GR;;;CO)"));
            setup.CreateKey(Hkcu(child));

            WriterFor(top).SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                SddlBinary("D:AR(A;;KR;;;WD)"));

            RawSecurityDescriptor stored = Read(child);
            AssertCreatorOwnerEntries(stored);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>Section 42 item B for "Replace all child object permission entries".</summary>
    [TestMethod]
    public void PropagationReplacesCreatorOwner()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));

            byte[] parent = UserDescriptor("D:P(A;CI;KA;;;{0})(A;CIIO;GR;;;CO)");
            RegistryWriter writer = WriterFor(top);
            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl, parent);
            writer.SetKeySecurity(Hkcu(tree), parent, RegistrySecurityInformation.Dacl, propagateToSubkeys: true);

            AssertCreatorOwnerEntries(Read(child));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    private static void AssertCreatorOwnerEntries(RawSecurityDescriptor stored)
    {
        SecurityIdentifier owner = stored.Owner ?? throw new InvalidOperationException("The key has no owner.");
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        CommonAce[] aces = Dacl(stored).Cast<CommonAce>().ToArray();

        CommonAce effective = aces.Single(a => a.SecurityIdentifier.Equals(owner) && a.AceFlags == AceFlags.Inherited && (uint)a.AccessMask == 0x00020019u);
        Assert.AreEqual(0x00020019u, (uint)effective.AccessMask, "The effective entry maps GENERIC_READ to the key rights.");

        CommonAce creator = aces.Single(a => a.SecurityIdentifier.Equals(creatorOwner));
        Assert.AreEqual(
            AceFlags.ContainerInherit | AceFlags.InheritOnly | AceFlags.Inherited,
            creator.AceFlags,
            "The creator entry stays inheritable and inherit-only for the grandchildren.");
    }

    /// <summary>
    /// Section 42 item D. A key that grants the owner only WRITE_OWNER (an OWNER RIGHTS entry removes the implicit
    /// WRITE_DAC) still takes an owner write, because the open asks for WRITE_OWNER and nothing else.
    /// </summary>
    [TestMethod]
    public void OwnerChangeNeedsOnlyWriteOwner()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string locked = top + @"\Locked";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(locked));
            setup.SetKeySecurity(
                Hkcu(locked),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;;0xB0019;;;{0})(A;;RC;;;OW)"));

            WriterFor(top).SetKeySecurity(
                Hkcu(locked),
                RegistrySecurityInformation.Owner,
                SddlBinary("O:" + CurrentUser().Value));

            Assert.AreEqual(CurrentUser(), Read(locked).Owner);
        }
        finally
        {
            // The key grants DELETE and read access but not WRITE_DAC, so the shared cleanup cannot restore it. Delete it first.
            try
            {
                WriterFor(top).DeleteKey(Hkcu(locked), recursive: false);
            }
            catch (RegistryOperationException)
            {
                // Removed already or never created.
            }

            CleanupRun(runId);
        }
    }

    /// <summary>Section 42 item H. A merged descriptor carries the auto-inherited bit.</summary>
    [TestMethod]
    public void MergeResultCarriesTheAutoInheritBits()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string tree = top + @"\Tree";
        string child = tree + @"\A";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(child));
            setup.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:P(A;CI;KA;;;{0})"));

            WriterFor(top).SetKeySecurity(
                Hkcu(child),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                SddlBinary("D:(A;;KR;;;WD)"));

            Assert.IsTrue(Read(child).ControlFlags.HasFlag(ControlFlags.DiscretionaryAclAutoInherited));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>Section 42 item H. The information bit decides the protected control bit of the stored DACL.</summary>
    [TestMethod]
    public void UnprotectedInformationClearsTheProtectedBit()
    {
        string runId = NewRunId();
        string top = TopOf(runId);

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(top + @"\A"));

            // The descriptor says protected, the information says unprotected: the information wins.
            setup.SetKeySecurity(
                Hkcu(top + @"\A"),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                UserDescriptor("D:P(A;;KA;;;{0})"));

            Assert.IsFalse(Read(top + @"\A").ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    /// <summary>Section 42 item H. ProtectedDacl in the information protects the stored DACL as given.</summary>
    [TestMethod]
    public void ProtectedInformationSetsTheProtectedBit()
    {
        string runId = NewRunId();
        string top = TopOf(runId);

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(top + @"\A"));

            setup.SetKeySecurity(
                Hkcu(top + @"\A"),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                UserDescriptor("D:(A;;KA;;;{0})"));

            RawSecurityDescriptor stored = Read(top + @"\A");
            Assert.IsTrue(stored.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.AreEqual(1, Dacl(stored).Count);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    private static bool IsElevated() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static byte[] SddlBinary(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    /// <summary>Records each reported key on the calling thread, so the test sees the walk order at once.</summary>
    private sealed class SynchronousProgress(List<string> reported) : IProgress<KeySecurityProgress>
    {
        public void Report(KeySecurityProgress value) => reported.Add(value.Path.ToAbbreviatedString());
    }

    private static byte[] UserDescriptor(string sddlTemplate)
    {
        string sddl = string.Format(sddlTemplate, CurrentUser().Value);
        var descriptor = new RawSecurityDescriptor(sddl);
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    private static byte[] EveryoneDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("D:(A;;KA;;;WD)");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    /// <summary>A descriptor with an empty DACL: no entry grants anything, not even to the current user.</summary>
    private static byte[] EmptyDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("D:");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    private static byte[] ProtectedEveryoneDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("D:P(A;;KA;;;WD)");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    /// <summary>
    /// Removes the run tree through the shared walk, which gives each key the owner's full control before
    /// its subkeys are listed. See docs/failure-modes/permissions.md section 26.
    /// </summary>
    private static void CleanupRun(string runId) => SandboxRunCleanup.RemoveRunTree(runId);
}
#endif
