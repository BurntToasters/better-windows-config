#if BWC_E2E
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

/// <summary>
/// Exercises the security descriptor engine against a disposable key under
/// HKCU\Software\BWC-E2E. Writes are limited to the run root by SandboxWritePolicy, and the
/// original descriptor is restored before the run root is deleted.
/// </summary>
[TestClass]
public sealed class RegistrySecurityTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const int KeyAllAccess = 0x000F003F;

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.CreateSubKey(SandboxBase, writable: true);
        Assert.IsNotNull(parent, "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void SecurityInformationFlagsKeepTheWin32Values()
    {
        Assert.AreEqual(0x00000001u, (uint)RegistrySecurityInformation.Owner);
        Assert.AreEqual(0x00000002u, (uint)RegistrySecurityInformation.Group);
        Assert.AreEqual(0x00000004u, (uint)RegistrySecurityInformation.Dacl);
        Assert.AreEqual(0x00000008u, (uint)RegistrySecurityInformation.Sacl);
        Assert.AreEqual(0x00000010u, (uint)RegistrySecurityInformation.Label);
        Assert.AreEqual(0x00000040u, (uint)RegistrySecurityInformation.Scope);
        Assert.AreEqual(0x00010000u, (uint)RegistrySecurityInformation.Backup);
        Assert.AreEqual(0x80000000u, (uint)RegistrySecurityInformation.ProtectedDacl);
        Assert.AreEqual(0x40000000u, (uint)RegistrySecurityInformation.ProtectedSacl);
        Assert.AreEqual(0x20000000u, (uint)RegistrySecurityInformation.UnprotectedDacl);
        Assert.AreEqual(0x10000000u, (uint)RegistrySecurityInformation.UnprotectedSacl);
    }

    [TestMethod]
    public void GetSetAndRestoreDaclOnDisposableKey()
    {
        string runId = "security-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath child = runRoot.Append("Child");
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));
        byte[]? original = null;

        try
        {
            store.CreateKey(runRoot);
            store.CreateKey(child);

            RegistryReader reader = store.Reader;
            original = reader.GetKeySecurity(
                child,
                RegistrySecurityInformation.Owner
                    | RegistrySecurityInformation.Group
                    | RegistrySecurityInformation.Dacl);

            Assert.IsTrue(original.Length > 0, "The original descriptor must not be empty.");
            RegistrySecurityDescriptor.Validate(original);
            Assert.IsTrue(RegistrySecurityDescriptor.TryGetLength(original, out int originalLength));
            Assert.AreEqual(original.Length, originalLength);

            byte[] replacement = FullControlDaclDescriptor();
            store.SetKeySecurity(
                child,
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.ProtectedDacl,
                replacement);

            byte[] actual = reader.GetKeySecurity(child, RegistrySecurityInformation.Dacl);
            RegistrySecurityDescriptor.Validate(actual);

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User
                ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
            bool userHasFullControl = false;
            var raw = new RawSecurityDescriptor(actual, 0);
            Assert.IsNotNull(raw.DiscretionaryAcl, "The DACL must be present after the write.");
            foreach (GenericAce ace in raw.DiscretionaryAcl)
            {
                if (ace is CommonAce common &&
                    common.SecurityIdentifier.Equals(user) &&
                    (common.AccessMask & KeyAllAccess) == KeyAllAccess)
                {
                    userHasFullControl = true;
                    break;
                }
            }

            Assert.IsTrue(userHasFullControl, "The replacement DACL must grant the current user full control.");
            Assert.IsTrue(
                (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                "The replacement DACL must stay protected.");

            byte[] ownerAndGroup = reader.GetKeySecurity(
                child,
                RegistrySecurityInformation.Owner | RegistrySecurityInformation.Group);
            RegistrySecurityDescriptor.Validate(ownerAndGroup);
            var ownerAndGroupRaw = new RawSecurityDescriptor(ownerAndGroup, 0);
            Assert.IsNotNull(ownerAndGroupRaw.Owner, "The owner security information must return an owner.");
            Assert.IsNotNull(ownerAndGroupRaw.Group, "The group security information must return a group.");
        }
        finally
        {
            try
            {
                if (original is not null)
                {
                    store.SetKeySecurity(
                        child,
                        RegistrySecurityInformation.Owner
                            | RegistrySecurityInformation.Group
                            | RegistrySecurityInformation.Dacl,
                        original);

                    byte[] restored = store.Reader.GetKeySecurity(
                        child,
                        RegistrySecurityInformation.Owner
                            | RegistrySecurityInformation.Group
                            | RegistrySecurityInformation.Dacl);
                    RegistrySecurityDescriptor.Validate(restored);
                    CollectionAssert.AreEqual(original, restored, "The original descriptor must be restored exactly.");
                }
            }
            finally
            {
                DeleteRunRoot(runId);
            }
        }
    }

    [TestMethod]
    public void PolicyDenialBlocksSecurityWritesOutsideTheSandbox()
    {
        string runId = "security-" + Guid.NewGuid().ToString("N");
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath outside = RegistryPath.Parse($@"HKCU\{SandboxBase}\{outsideId}");
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            store.CreateKey(runRoot);

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.SetKeySecurity(
                    outside,
                    RegistrySecurityInformation.Dacl,
                    FullControlDaclDescriptor()));

            Assert.AreEqual(5, error.Win32Error);
            Assert.AreEqual("SetKeySecurity", error.Operation);
            Assert.AreEqual(outside, error.Path);
            Assert.IsFalse(store.Reader.KeyExists(outside), "A denied write must not create the outside key.");
        }
        finally
        {
            DeleteRunRoot(runId);
            DeleteRunRoot(outsideId);
        }
    }

    [TestMethod]
    public void InvalidDescriptorsAreRejectedBeforeAnyWrite()
    {
        string runId = "security-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            store.CreateKey(runRoot);
            byte[] original = store.Reader.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl);

            Assert.ThrowsExactly<ArgumentException>(
                () => store.SetKeySecurity(runRoot, RegistrySecurityInformation.Dacl, ReadOnlySpan<byte>.Empty));
            Assert.ThrowsExactly<ArgumentException>(
                () => store.SetKeySecurity(
                    runRoot,
                    RegistrySecurityInformation.Dacl,
                    new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05 }));

            byte[] valid = FullControlDaclDescriptor();
            byte[] padded = new byte[valid.Length + 1];
            valid.CopyTo(padded, 0);
            Assert.ThrowsExactly<ArgumentException>(
                () => store.SetKeySecurity(runRoot, RegistrySecurityInformation.Dacl, padded));

            Assert.IsFalse(RegistrySecurityDescriptor.TryGetLength(ReadOnlySpan<byte>.Empty, out _));
            Assert.IsFalse(RegistrySecurityDescriptor.TryGetLength(new byte[24], out _));
            Assert.ThrowsExactly<ArgumentException>(
                () => RegistrySecurityDescriptor.Validate(new byte[24]));
            Assert.ThrowsExactly<ArgumentException>(
                () => store.SetKeySecurity(runRoot, RegistrySecurityInformation.None, valid));
            Assert.ThrowsExactly<ArgumentException>(
                () => store.Reader.GetKeySecurity(runRoot, RegistrySecurityInformation.None));

            CollectionAssert.AreEqual(
                original,
                store.Reader.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl),
                "Rejected descriptors must leave the key security unchanged.");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void MissingKeysMapToTheSecurityOperation()
    {
        string runId = "security-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath missing = runRoot.Append("Missing");
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            store.CreateKey(runRoot);

            var readError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.GetKeySecurity(missing, RegistrySecurityInformation.Dacl));
            Assert.AreEqual(2, readError.Win32Error);
            Assert.AreEqual("GetKeySecurity", readError.Operation);
            Assert.AreEqual(missing, readError.Path);

            var writeError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.SetKeySecurity(
                    missing,
                    RegistrySecurityInformation.Dacl,
                    FullControlDaclDescriptor()));
            Assert.AreEqual(2, writeError.Win32Error);
            Assert.AreEqual("SetKeySecurity", writeError.Operation);
            Assert.AreEqual(missing, writeError.Path);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    private static byte[] FullControlDaclDescriptor()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var descriptor = new RawSecurityDescriptor($"D:P(A;;KA;;;{user.Value})");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
