#if BWC_E2E
using System.IO;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Remote write tests from docs/failure-modes/remote-registry.md (section 15). Policy tests need no connection.
/// Route tests wrap the local HKEY_LOCAL_MACHINE and HKEY_USERS handles in a connection, so the remote code
/// path (root pin, policy with the computer name, guard) runs without the network. Loopback tests connect to
/// 127.0.0.1 or ::1 and report Inconclusive only when the connection itself fails, with its Win32 code.
/// Every write lands under HKEY_USERS\&lt;current SID&gt;\Software\BWC-E2E\&lt;run-id&gt;, which is the same key as the
/// local sandbox run key, and each test removes its run tree.
/// </summary>
[TestClass]
public sealed class RemoteRegistryWriteTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const string Loopback = "127.0.0.1";
    private const int ErrorAccessDenied = 5;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorNotSupported = 50;

    [TestMethod]
    public void ShippingPolicyAllowsRemoteWrites()
    {
        var policy = new ShippingWritePolicy();
        RegistryPath path = RemoteRunRoot(NewRunId());

        foreach (RegistryWriteOperation operation in Enum.GetValues<RegistryWriteOperation>())
        {
            policy.EnsureWriteAllowed(Loopback, path, operation);
            policy.EnsureWriteAllowed("bwc-remote-test", path, operation);
        }
    }

    [TestMethod]
    public void DenyAllPolicyRefusesRemoteAndLocalWrites()
    {
        var policy = new DenyAllWritePolicy();
        RegistryPath path = RemoteRunRoot(NewRunId());

        AssertDenied(() => policy.EnsureWriteAllowed(Loopback, path, RegistryWriteOperation.CreateKey), "CreateKey");
        AssertDenied(() => policy.EnsureWriteAllowed(null, LocalRunRoot(NewRunId()), RegistryWriteOperation.SetValue), "SetValue");
    }

    [TestMethod]
    public void SandboxAllowsRemoteWritesOnLoopbackUnderTheCurrentSid()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        foreach (string computer in new[] { Loopback, "::1" })
        {
            policy.EnsureWriteAllowed(computer, runRoot, RegistryWriteOperation.CreateKey);
            policy.EnsureWriteAllowed(computer, runRoot.Append("Child"), RegistryWriteOperation.SetValue);
            policy.EnsureWriteAllowed(computer, runRoot.Append("Child"), RegistryWriteOperation.DeleteKey);
            policy.EnsureWriteAllowed(computer, runRoot.Append("Child"), RegistryWriteOperation.RenameKey);
            policy.EnsureWriteAllowed(computer, runRoot, RegistryWriteOperation.DeleteKey);
        }
    }

    [TestMethod]
    public void SandboxAllowsRemoteWritesWhateverTheSidCase()
    {
        string runId = NewRunId();
        string sid = CurrentSid();
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        // The registry names are case-insensitive, so a lower-case SID and sandbox names are the same key.
        RegistryPath lower = RegistryPath.Parse($@"HKU\{sid.ToLowerInvariant()}\{SandboxBase.ToLowerInvariant()}\{runId}");
        RegistryPath upper = RegistryPath.Parse($@"HKU\{sid.ToUpperInvariant()}\{SandboxBase.ToUpperInvariant()}\{runId.ToUpperInvariant()}");

        policy.EnsureWriteAllowed(Loopback, lower, RegistryWriteOperation.CreateKey);
        policy.EnsureWriteAllowed(Loopback, upper.Append("Child"), RegistryWriteOperation.SetValue);
    }

    [TestMethod]
    public void SandboxDeniesRemoteWritesToOtherComputers()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        string[] computers =
        {
            "192.168.1.20",
            "bwc-remote-test",
            "127.0.0.2",
            "0.0.0.0",
            "localhost",
            "::ffff:127.0.0.1",
            "127.0.0.1.example",
            string.Empty,
        };

        foreach (string computer in computers)
        {
            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => policy.EnsureWriteAllowed(computer, runRoot, RegistryWriteOperation.SetValue),
                computer);

            Assert.AreEqual(ErrorAccessDenied, error.Win32Error, computer);
            Assert.AreEqual("SetValue", error.Operation, computer);
        }
    }

    [TestMethod]
    public void SandboxDeniesRemoteWritesOutsideTheSandboxSubtree()
    {
        string runId = NewRunId();
        string sid = CurrentSid();
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        RegistryPath[] denied =
        {
            RegistryPath.Parse($@"HKU\S-1-5-18\{SandboxBase}\{runId}"),
            RegistryPath.Parse($@"HKU\{OneDigitOff(sid)}\{SandboxBase}\{runId}"),
            RegistryPath.Parse($@"HKU\{sid}\{SandboxBase}"),
            RegistryPath.Parse($@"HKU\{sid}\{SandboxBase}2\{runId}"),
            RegistryPath.Parse($@"HKU\{sid}\Software"),
            RegistryPath.Parse($@"HKU\{sid}"),
            RegistryPath.Parse("HKU"),
            RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}"),
            RegistryPath.Parse($@"HKLM\{SandboxBase}\{runId}"),
        };

        foreach (RegistryPath path in denied)
        {
            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => policy.EnsureWriteAllowed(Loopback, path, RegistryWriteOperation.CreateKey),
                path.ToAbbreviatedString());

            Assert.AreEqual(ErrorAccessDenied, error.Win32Error, path.ToAbbreviatedString());
        }
    }

    [TestMethod]
    public void SandboxDeniesDotComponentsOnRemoteWrites()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        RegistryPath[] paths =
        {
            runRoot.AppendEnumeratedName(".."),
            runRoot.AppendEnumeratedName("."),
            runRoot.Append("Child").AppendEnumeratedName(".."),
            runRoot.AppendEnumeratedName("..").AppendEnumeratedName("Other"),
        };

        foreach (RegistryPath path in paths)
        {
            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => policy.EnsureWriteAllowed(Loopback, path, RegistryWriteOperation.CreateKey),
                path.ToAbbreviatedString());

            Assert.AreEqual(ErrorAccessDenied, error.Win32Error, path.ToAbbreviatedString());

            // The shipping policy has no path rules, as it does locally.
            new ShippingWritePolicy().EnsureWriteAllowed(Loopback, path, RegistryWriteOperation.CreateKey);
        }
    }

    [TestMethod]
    public void SandboxDeniesRenameOfTheRemoteSandboxRoot()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        var error = Assert.ThrowsExactly<RegistryOperationException>(
            () => policy.EnsureWriteAllowed(Loopback, runRoot, RegistryWriteOperation.RenameKey));
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
        Assert.AreEqual("RenameKey", error.Operation);

        policy.EnsureWriteAllowed(Loopback, runRoot.Append("Child"), RegistryWriteOperation.RenameKey);
        policy.EnsureWriteAllowed(Loopback, runRoot, RegistryWriteOperation.DeleteKey);
    }

    [TestMethod]
    public void SandboxKeepsTheLocalRulesWhenNoComputerIsNamed()
    {
        string runId = NewRunId();
        var policy = new SandboxWritePolicy(LocalRunRoot(runId));

        policy.EnsureWriteAllowed(null, LocalRunRoot(runId).Append("Child"), RegistryWriteOperation.SetValue);
        AssertDenied(
            () => policy.EnsureWriteAllowed(null, RemoteRunRoot(runId), RegistryWriteOperation.SetValue),
            "SetValue");
    }

    [TestMethod]
    public void RemoteStoreRefusesRootsOtherThanLocalMachineAndUsers()
    {
        string runId = NewRunId();
        RegistryPath localRoot = LocalRunRoot(runId);
        RegistryPath classesRoot = RegistryPath.Parse($@"HKCR\{SandboxBase}\{runId}");
        RegistryPath configRoot = RegistryPath.Parse($@"HKCC\{SandboxBase}\{runId}");

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new ShippingWritePolicy());

            Assert.ThrowsExactly<ArgumentException>(() => store.CreateKey(localRoot));
            Assert.ThrowsExactly<ArgumentException>(() => store.SetString(localRoot, "Text", "x"));
            Assert.ThrowsExactly<ArgumentException>(() => store.CreateKey(classesRoot));
            Assert.ThrowsExactly<ArgumentException>(() => store.CreateKey(configRoot));

            Assert.IsFalse(new RegistryReader().KeyExists(localRoot), "A refused write must create nothing.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RoutedStoreWritesEveryOperationUnderTheRemoteSandbox()
    {
        EnsureSandboxBase();
        string runId = NewRunId();

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            ExerciseEveryWriteOperation(store, runId);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void DisposedConnectionRefusesWritesWithoutTouchingTheRegistry()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            connection.Dispose();

            Assert.ThrowsExactly<ObjectDisposedException>(() => store.CreateKey(runRoot));
            Assert.ThrowsExactly<ObjectDisposedException>(() => store.SetDword(runRoot, "Value", 1));
            Assert.ThrowsExactly<ObjectDisposedException>(() => store.Writer.DeleteKey(runRoot, recursive: true));
            Assert.ThrowsExactly<ObjectDisposedException>(() => connection.CreateStore(new ShippingWritePolicy()));

            Assert.IsFalse(new RegistryReader().KeyExists(LocalRunRoot(runId)), "A refused write must create nothing.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteHiveOperationsAreNotSupportedAndTouchNothing()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BwcRemoteWrite", Guid.NewGuid().ToString("N"));
        string saveFile = Path.Combine(tempDirectory, "save.hiv");
        string missingFile = Path.Combine(tempDirectory, "missing.hiv");
        RegistryPath runRoot = RemoteRunRoot(runId);
        RegistryPath hiveMount = RegistryPath.Parse($@"HKU\bwc-hive-{runId}");

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new ShippingWritePolicy());

            AssertNotSupported(() => store.LoadHive(hiveMount, missingFile), "LoadHive");
            AssertNotSupported(() => store.UnloadHive(hiveMount), "UnloadHive");
            AssertNotSupported(() => store.SaveKey(runRoot, saveFile), "SaveKey");
            AssertNotSupported(() => store.ReplaceKey(runRoot, missingFile, saveFile), "ReplaceKey");
            AssertNotSupported(() => store.RestoreKey(runRoot, missingFile), "RestoreKey");

            Assert.IsFalse(File.Exists(saveFile), "SaveKey must not create its file on a remote store.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteStoreRefusesCountedNameValuesBeforeNativeCode()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            store.CreateKey(runRoot);

            var setError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.SetValueWithCountedName(runRoot, "bad\0name", 1, new byte[] { 0, 0 }));
            Assert.AreEqual(ErrorNotSupported, setError.Win32Error);

            var deleteError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.DeleteValueWithCountedName(runRoot, "bad\0name"));
            Assert.AreEqual(ErrorNotSupported, deleteError.Win32Error);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityRejectsMalformedDescriptorsBeforeNativeCode()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore remote = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            var local = new RegistryWriter(new SandboxWritePolicy(LocalRunRoot(runId)));
            remote.CreateKey(runRoot);

            // The same key, named by its local path for the local writer.
            RegistryPath localRunRoot = LocalRunRoot(runId);
            byte[] valid = FullControlForCurrentUser();
            byte[] before = remote.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl);

            byte[] notSelfRelative = (byte[])valid.Clone();
            notSelfRelative[2] &= 0x7F;
            notSelfRelative[3] &= 0x7F;

            byte[] outsideBuffer = (byte[])valid.Clone();
            outsideBuffer[16] = 0xF0;
            outsideBuffer[17] = 0xFF;
            outsideBuffer[18] = 0xFF;
            outsideBuffer[19] = 0x7F;

            byte[][] malformed =
            {
                Array.Empty<byte>(),
                valid[..10],
                notSelfRelative,
                outsideBuffer,
                [.. valid, 0, 0, 0, 0],
                Enumerable.Range(1, 22).Select(value => (byte)value).ToArray(),
            };

            foreach (byte[] descriptor in malformed)
            {
                Assert.ThrowsExactly<ArgumentException>(
                    () => remote.SetKeySecurity(runRoot, RegistrySecurityInformation.Dacl, descriptor));
                Assert.ThrowsExactly<ArgumentException>(
                    () => local.SetKeySecurity(localRunRoot, RegistrySecurityInformation.Dacl, descriptor));
                Assert.ThrowsExactly<ArgumentException>(
                    () => remote.Writer.SetKeySecurity(runRoot, descriptor, RegistrySecurityInformation.Dacl, propagateToSubkeys: true));
            }

            CollectionAssert.AreEqual(before, remote.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RoutedGuardRefusesSymbolicLinks()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        string relativeRun = SandboxBase + @"\" + runId;
        RegistryPath runRoot = RemoteRunRoot(runId);
        RegistryPath target = runRoot.Append("Target");
        RegistryPath link = runRoot.Append("Link");

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            store.CreateKey(runRoot);
            store.CreateKey(target);
            RegistryLinkFixture.CreateLink(relativeRun, "Link", relativeRun + @"\Target");

            var setError = Assert.ThrowsExactly<RegistryOperationException>(() => store.SetDword(link, "Value", 1));
            Assert.AreEqual(ErrorAccessDenied, setError.Win32Error);

            var deleteError = Assert.ThrowsExactly<RegistryOperationException>(() => store.DeleteKey(link, recursive: false));
            Assert.AreEqual(ErrorAccessDenied, deleteError.Win32Error);

            Assert.IsTrue(RegistryLinkFixture.IsLink(relativeRun, "Link"), "A refused delete must leave the link.");
            Assert.IsNull(store.GetValue(target, "Value"), "A refused write must not reach the link target.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RoutedRenameKeyMovesTheKeyAndItsTree()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));

            store.RenameKey(runRoot.Append("Source"), "Renamed");

            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Source")));
            Assert.AreEqual("payload", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Renamed"), "Text").Data));
            Assert.AreEqual("nested", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Renamed").Append("Sub"), "Text").Data));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyMovesTheTree()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));

            store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst");

            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Source")));
            Assert.AreEqual("payload", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Dst"), "Text").Data));
            Assert.AreEqual("nested", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Dst").Append("Sub"), "Text").Data));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyRefusesAnExistingDestination()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));
            store.CreateKey(runRoot.Append("Dst"));
            store.SetString(runRoot.Append("Dst"), "Text", "existing");

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst"));

            Assert.AreEqual(ErrorAlreadyExists, error.Win32Error);
            Assert.AreEqual("existing", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Dst"), "Text").Data));
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Source")), "The source must stay.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyRefusesACaseOnlyRename()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "source"));

            Assert.AreEqual(ErrorNotSupported, error.Win32Error);
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Source")), "A refused rename must change nothing.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyKeepsTheCopyWhenTheSourceDeleteFails()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        RegistryPath source = runRoot.Append("Source");

        try
        {
            using RemoteRegistryConnection connection = RoutedLoopback();
            var policy = new DenyDeleteOfPolicy(new SandboxWritePolicy(LocalRunRoot(runId)), source);
            RegistryStore store = connection.CreateStore(policy);
            BuildSourceTree(store, source);

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.RenameKeyByCopy(source, "Dst"));

            Assert.AreEqual("RenameKey", error.Operation);
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);

            // The message must say that both keys exist and name both of them, so the UI can tell the user.
            StringAssert.Contains(error.Message, "Both keys now exist");
            StringAssert.Contains(error.Message, source.ToAbbreviatedString());
            StringAssert.Contains(error.Message, runRoot.Append("Dst").ToAbbreviatedString());

            // The copy is complete and kept. The source was never removed by this test's policy.
            Assert.AreEqual("payload", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Dst"), "Text").Data));
            Assert.AreEqual("nested", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Dst").Append("Sub"), "Text").Data));
            Assert.IsTrue(store.Reader.KeyExists(source));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteWritesRoundTripOnRealLoopback()
    {
        EnsureSandboxBase();
        string runId = NewRunId();

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip(Loopback);
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            ExerciseEveryWriteOperation(store, runId);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteWritesRoundTripOverTheIPv6Loopback()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip("::1");
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            store.CreateKey(runRoot);
            store.SetDword(runRoot, "Value", 6);

            Assert.AreEqual(6u, RegistryValueCodec.DecodeDword(RequireValue(store, runRoot, "Value").Data));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteRenameKeyLeavesOneKeyAtTheNewName()
    {
        // Which native path runs (RegRenameKey or the copy fallback) is recorded in
        // docs/failure-modes/remote-registry.md section 15.2. This test checks only the result.
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip(Loopback);
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));

            store.RenameKey(runRoot.Append("Source"), "Renamed");

            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Source")));
            Assert.AreEqual("payload", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Renamed"), "Text").Data));
            Assert.AreEqual("nested", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Renamed").Append("Sub"), "Text").Data));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteGuardRefusesSymbolicLinksOnRealLoopback()
    {
        // Checks that the remote RegCreateKeyEx passes REG_OPTION_OPEN_LINK through. See section 15.3.
        EnsureSandboxBase();
        string runId = NewRunId();
        string relativeRun = SandboxBase + @"\" + runId;
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip(Loopback);
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            store.CreateKey(runRoot);
            store.CreateKey(runRoot.Append("Target"));
            RegistryLinkFixture.CreateLink(relativeRun, "Link", relativeRun + @"\Target");

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.SetDword(runRoot.Append("Link"), "Value", 1));
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.IsNull(store.GetValue(runRoot.Append("Target"), "Value"));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteSecurityDescriptorRoundTripsOnRealLoopback()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip(Loopback);
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            store.CreateKey(runRoot);

            byte[] before = store.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl);
            store.SetKeySecurity(runRoot, RegistrySecurityInformation.Dacl, before);

            CollectionAssert.AreEqual(before, store.GetKeySecurity(runRoot, RegistrySecurityInformation.Dacl));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RemoteCopyTreeAndRecursiveDeleteOnRealLoopback()
    {
        EnsureSandboxBase();
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            using RemoteRegistryConnection connection = ConnectOrSkip(Loopback);
            RegistryStore store = connection.CreateStore(new SandboxWritePolicy(LocalRunRoot(runId)));
            BuildSourceTree(store, runRoot.Append("Source"));
            store.CreateKey(runRoot.Append("Copy"));

            store.CopyTree(runRoot.Append("Source"), runRoot.Append("Copy"));
            Assert.AreEqual("nested", RegistryValueCodec.DecodeString(RequireValue(store, runRoot.Append("Copy").Append("Sub"), "Text").Data));

            store.DeleteKey(runRoot.Append("Source"), recursive: true);
            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Source")));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    private static void ExerciseEveryWriteOperation(RegistryStore store, string runId)
    {
        RegistryPath runRoot = RemoteRunRoot(runId);
        RegistryPath localRoot = LocalRunRoot(runId);

        store.CreateKey(runRoot);
        store.SetString(runRoot, "Text", "hello");
        store.SetExpandString(runRoot, "Expand", @"%SystemRoot%\bwc");
        store.SetDword(runRoot, "Dword", 42);
        store.SetDwordBigEndian(runRoot, "BigEndian", 7);
        store.SetQword(runRoot, "Qword", 1UL << 40);
        store.SetBinary(runRoot, "Blob", new byte[] { 0, 1, 2, 255 });
        store.SetMultiString(runRoot, "Multi", new[] { "one", "two" });

        Assert.AreEqual("hello", RegistryValueCodec.DecodeString(RequireValue(store, runRoot, "Text").Data));
        Assert.AreEqual((uint)RegistryValueType.String, RequireValue(store, runRoot, "Text").Type);
        Assert.AreEqual((uint)RegistryValueType.ExpandString, RequireValue(store, runRoot, "Expand").Type);
        Assert.AreEqual(42u, RegistryValueCodec.DecodeDword(RequireValue(store, runRoot, "Dword").Data));
        Assert.AreEqual((uint)RegistryValueType.DwordBigEndian, RequireValue(store, runRoot, "BigEndian").Type);
        Assert.AreEqual(1UL << 40, RegistryValueCodec.DecodeQword(RequireValue(store, runRoot, "Qword").Data));
        CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 255 }, RequireValue(store, runRoot, "Blob").Data);
        CollectionAssert.AreEqual(
            RegistryValueCodec.EncodeMultiString(new[] { "one", "two" }),
            RequireValue(store, runRoot, "Multi").Data);

        // The remote and local views are one key, so the local reader sees the same bytes.
        Assert.AreEqual("hello", RegistryValueCodec.DecodeString(RequireValue(new RegistryReader(), localRoot, "Text").Data));

        RegistryPath child = runRoot.Append("Child");
        store.CreateKey(child);
        store.SetString(child, "Nested", "deep");
        store.DeleteValue(runRoot, "Text");
        Assert.IsNull(store.GetValue(runRoot, "Text"));

        store.DeleteKey(child, recursive: false);
        Assert.IsFalse(store.Reader.KeyExists(child));

        store.CreateKey(child);
        store.SetString(child, "Nested", "deep");
        store.DeleteKey(child, recursive: true);
        Assert.IsFalse(store.Reader.KeyExists(child));

        store.RenameValue(runRoot, "Dword", "Renamed");
        Assert.IsNull(store.GetValue(runRoot, "Dword"));
        Assert.AreEqual(42u, RegistryValueCodec.DecodeDword(RequireValue(store, runRoot, "Renamed").Data));
    }

    private static void BuildSourceTree(RegistryStore store, RegistryPath source)
    {
        store.CreateKey(source.Parent!);
        store.CreateKey(source);
        store.SetString(source, "Text", "payload");
        RegistryPath sub = source.Append("Sub");
        store.CreateKey(sub);
        store.SetString(sub, "Text", "nested");
    }

    private static RegistryValueInfo RequireValue(RegistryStore store, RegistryPath key, string name) =>
        store.GetValue(key, name) ?? throw new AssertFailedException($"The value '{name}' is missing.");

    private static RegistryValueInfo RequireValue(RegistryReader reader, RegistryPath key, string name) =>
        reader.ReadValue(key, name) ?? throw new AssertFailedException($"The value '{name}' is missing.");

    private static void EnsureSandboxBase()
    {
        RegistryPath sandboxBase = LocalSandboxBase();
        new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
    }

    /// <summary>
    /// Connects to the computer and reports Inconclusive only when the connection itself fails. The service is never
    /// queried or started here: a RegConnectRegistry starts the trigger-started RemoteRegistry service.
    /// </summary>
    private static RemoteRegistryConnection ConnectOrSkip(string computerName)
    {
        try
        {
            return RemoteRegistryConnection.Connect(computerName);
        }
        catch (RemoteRegistryException error)
        {
            Assert.Inconclusive($"not run: remote registry unavailable (Win32 {error.Win32Error})");
            throw;
        }
    }

    private static RemoteRegistryConnection RoutedLoopback() =>
        RemoteRegistryConnection.CreateForTesting(
            Loopback,
            SafeRegistryKeyHandle.FromPredefined(RegistryRoot.LocalMachine),
            SafeRegistryKeyHandle.FromPredefined(RegistryRoot.Users));

    private static void AssertDenied(Action action, string operation)
    {
        var error = Assert.ThrowsExactly<RegistryOperationException>(action);
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
        Assert.AreEqual(operation, error.Operation);
    }

    private static void AssertNotSupported(Action action, string operation)
    {
        var error = Assert.ThrowsExactly<RegistryOperationException>(action, operation);
        Assert.AreEqual(ErrorNotSupported, error.Win32Error, operation);
        Assert.AreEqual(operation, error.Operation, operation);
    }

    private static string NewRunId() => "remote-" + Guid.NewGuid().ToString("N");

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static string OneDigitOff(string sid)
    {
        char last = sid[^1];
        char changed = last == '9' ? '8' : (char)(last + 1);
        return sid[..^1] + changed;
    }

    private static RegistryPath LocalSandboxBase() => RegistryPath.Parse($@"HKCU\{SandboxBase}");

    private static RegistryPath LocalRunRoot(string runId) => LocalSandboxBase().Append(runId);

    private static RegistryPath RemoteRunRoot(string runId) =>
        RegistryPath.Parse($@"HKU\{CurrentSid()}\{SandboxBase}").Append(runId);

    private static byte[] FullControlForCurrentUser()
    {
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor($"D:(A;CI;KA;;;{CurrentSid()})");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    /// <summary>Denies DeleteKey for one key, so the copy-then-delete rename fails after the copy succeeds.</summary>
    private sealed class DenyDeleteOfPolicy : IRegistryWritePolicy
    {
        private readonly IRegistryWritePolicy _inner;
        private readonly RegistryPath _denied;

        public DenyDeleteOfPolicy(IRegistryWritePolicy inner, RegistryPath denied)
        {
            _inner = inner;
            _denied = denied;
        }

        public bool AllowsLinkValues => _inner.AllowsLinkValues;

        public bool RejectsSymbolicLinks => _inner.RejectsSymbolicLinks;

        public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
        {
            if (operation == RegistryWriteOperation.DeleteKey && path.Equals(_denied))
            {
                throw new RegistryOperationException("DeleteKey", path, ErrorAccessDenied, "Injected by the test.");
            }

            _inner.EnsureWriteAllowed(computerName, path, operation);
        }
    }
}
#endif
