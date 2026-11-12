#if BWC_E2E
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
        Assert.AreEqual(Identity(0x2u), Identity((uint)AceFlags.ContainerInherit), "CONTAINER_INHERIT_ACE is 0x2 in winnt.h.");
        Assert.AreEqual(Identity(0x8u), Identity((uint)AceFlags.InheritOnly), "INHERIT_ONLY_ACE is 0x8 in winnt.h.");
        Assert.AreEqual(Identity(0u), Identity(RegistryInheritanceModes.ThisKeyOnly), "This key only carries no inheritance flags.");
        Assert.AreEqual(
            Identity((uint)AceFlags.ContainerInherit),
            Identity(RegistryInheritanceModes.ThisKeyAndSubkeys),
            "This key and subkeys is CONTAINER_INHERIT_ACE.");
        Assert.AreEqual(
            Identity((uint)(AceFlags.ContainerInherit | AceFlags.InheritOnly)),
            Identity(RegistryInheritanceModes.SubkeysOnly),
            "Subkeys only is CONTAINER_INHERIT_ACE | INHERIT_ONLY_ACE.");
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
