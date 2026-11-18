#if BWC_E2E
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Remote rename by copy: a copy that fails part way leaves no partial key beside the source (writer sections
/// J1, J2), the owner and class name are copied (J3, J4), and keys named "." and ".." are copied (J6). The
/// connection is routed through the local registry, so the managed copy runs without the RemoteRegistry service.
/// Every write lands under
/// HKU\&lt;SID&gt;\Software\BWC-E2E\&lt;run-id&gt;, which is the same key as HKCU\Software\BWC-E2E\&lt;run-id&gt;.
/// </summary>
[TestClass]
public sealed class RegistryCopyStateTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const string Loopback = "127.0.0.1";
    private const string ClassName = "BwcCopyClass";
    private const int ErrorAccessDenied = 5;

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        RegistryPath sandboxBase = RegistryPath.Parse($@"HKCU\{SandboxBase}");
        new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
    }

    [TestMethod]
    public void RenameByCopyRollsBackWhenAValueNameIsRefused()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            BuildSource(runId, nulNamedValueUnderChild: true);
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new RunRootPolicy(runRoot));

            // The copy stops at the value name with NUL, which ValidateValueName refuses with ArgumentException.
            Assert.ThrowsExactly<ArgumentException>(
                () => store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst"));

            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Dst")), "The partial copy must be removed.");
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Source")), "The source must stay.");
            Assert.AreEqual("payload", DecodeText(store, runRoot.Append("Source"), "Text"));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyRollsBackWhenAChildWriteIsRefused()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            BuildSource(runId, nulNamedValueUnderChild: false);
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryPath destinationChild = runRoot.Append("Dst").Append("Sub");
            RegistryStore store = connection.CreateStore(new DenyOperationPolicy(
                new RunRootPolicy(runRoot),
                RegistryWriteOperation.SetValue,
                destinationChild));

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst"));

            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.IsFalse(store.Reader.KeyExists(runRoot.Append("Dst")), "The partial copy must be removed.");
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Source")), "The source must stay.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyReportsAFailedRollback()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);
        RegistryPath destination = runRoot.Append("Dst");

        try
        {
            BuildSource(runId, nulNamedValueUnderChild: true);
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new DenyOperationPolicy(
                new RunRootPolicy(runRoot),
                RegistryWriteOperation.DeleteKey,
                destination));

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst"));

            // The copy failed with ArgumentException, which has no Win32 code, so the rollback's code is reported.
            Assert.AreEqual("RenameKey", error.Operation);
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            StringAssert.Contains(error.Message, "could not be removed");
            StringAssert.Contains(error.Message, destination.ToAbbreviatedString());

            Assert.IsTrue(store.Reader.KeyExists(destination), "A partial copy that cannot be removed is kept, and reported.");
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Source")), "The source must stay.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyCopiesTheOwnerAndTheClassName()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            BuildSource(runId, nulNamedValueUnderChild: false);
            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new RunRootPolicy(runRoot));

            // An elevated token creates keys owned by Administrators, not the user, so the expected owner is read
            // from the source rather than assumed.
            string? sourceOwner = new RawSecurityDescriptor(
                store.Reader.GetKeySecurity(runRoot.Append("Source"), RegistrySecurityInformation.Owner), 0).Owner?.Value;
            Assert.IsNotNull(sourceOwner, "The source key has no owner.");

            store.Writer.RenameKeyByCopy(runRoot.Append("Source"), "Dst");

            RegistryPath destination = runRoot.Append("Dst");
            Assert.AreEqual(ClassName, store.Reader.GetKeyInfo(destination).ClassName, "The class name must be copied.");
            Assert.AreEqual(ClassName, store.Reader.GetKeyInfo(destination.Append("Sub")).ClassName, "The class name of a child must be copied.");

            var owner = new RawSecurityDescriptor(store.Reader.GetKeySecurity(destination, RegistrySecurityInformation.Owner), 0);
            Assert.IsNotNull(owner.Owner, "The copied key has no owner.");
            Assert.AreEqual(sourceOwner, owner.Owner.Value, "The owner must be the source's owner.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameByCopyCopiesDotNamedChildren()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RemoteRunRoot(runId);

        try
        {
            BuildSource(runId, nulNamedValueUnderChild: false);
            string relative = SandboxBase + @"\" + runId + @"\Source";
            RegistryLinkFixture.CreateKey(relative, ".");
            RegistryLinkFixture.CreateKey(relative, "..");
            RegistryLinkFixture.CreateKey(relative + @"\..", "Inner");

            using RemoteRegistryConnection connection = RoutedLoopback();
            RegistryStore store = connection.CreateStore(new RunRootPolicy(runRoot));
            RegistryPath source = runRoot.Append("Source");
            RegistryLinkFixture.WriteValue(
                SandboxBase + @"\" + runId + @"\Source\.",
                "Dot",
                1,
                Encoding.Unicode.GetBytes("dot\0"));
            store.Writer.RenameKeyByCopy(source, "Dst");

            RegistryPath destination = runRoot.Append("Dst");
            IReadOnlyList<string> names = store.Reader.EnumerateSubKeyNames(destination);
            CollectionAssert.Contains(names.ToList(), ".", "The copy must include the key named '.'.");
            CollectionAssert.Contains(names.ToList(), "..", "The copy must include the key named '..'.");
            Assert.AreEqual("dot", DecodeText(store, destination.AppendEnumeratedName("."), "Dot"));
            Assert.IsTrue(store.Reader.KeyExists(destination.AppendEnumeratedName("..").Append("Inner")));
            Assert.IsFalse(store.Reader.KeyExists(source), "The source must be removed by the rename.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    /// <summary>
    /// Builds HKCU\Software\BWC-E2E\&lt;run-id&gt;\Source with a value, a class name, and a child Sub with a value. When
    /// <paramref name="nulNamedValueUnderChild"/> is true, Sub also gets a value whose name contains NUL, which the
    /// managed copy refuses after it has created the destination.
    /// </summary>
    private static void BuildSource(string runId, bool nulNamedValueUnderChild)
    {
        RegistryPath localRoot = LocalRunRoot(runId);
        var writer = new RegistryWriter(new SandboxWritePolicy(localRoot));
        writer.CreateKey(localRoot);

        RegistryLinkFixture.CreateKey(SandboxBase + @"\" + runId, "Source", ClassName);
        RegistryPath source = localRoot.Append("Source");
        RegistryLinkFixture.WriteValue(SandboxBase + @"\" + runId + @"\Source", "Text", 1, Encoding.Unicode.GetBytes("payload\0"));

        // The child carries the class too, so the class test covers a nested key as well as the root.
        RegistryLinkFixture.CreateKey(SandboxBase + @"\" + runId + @"\Source", "Sub", ClassName);
        RegistryPath sub = source.Append("Sub");
        RegistryLinkFixture.WriteValue(SandboxBase + @"\" + runId + @"\Source\Sub", "Text", 1, Encoding.Unicode.GetBytes("nested\0"));
        if (nulNamedValueUnderChild)
        {
            writer.SetValueWithCountedName(sub, "Bad\0Name", 1, Encoding.Unicode.GetBytes("x\0"));
        }
    }

    private static string DecodeText(RegistryStore store, RegistryPath key, string name) =>
        RegistryValueCodec.DecodeString(
            (store.Reader.ReadValue(key, name)
                ?? throw new AssertFailedException($"The value '{name}' is missing.")).Data);

    private static string NewRunId() => "remote-" + Guid.NewGuid().ToString("N");

    private static RegistryPath LocalRunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static RegistryPath RemoteRunRoot(string runId) =>
        RegistryPath.Parse($@"HKU\{CurrentSid()}\{SandboxBase}").Append(runId);

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static RemoteRegistryConnection RoutedLoopback() =>
        RemoteRegistryConnection.CreateForTesting(
            Loopback,
            SafeRegistryKeyHandle.FromPredefined(RegistryRoot.LocalMachine),
            SafeRegistryKeyHandle.FromPredefined(RegistryRoot.Users));

    /// <summary>
    /// Allows writes only under one remote run root. The sandbox policy also refuses "." and ".." components on a
    /// remote write (remote-registry.md section 15.1), which this test needs to allow. The run root is the only place
    /// a test writes, so the test-only policy stays inside the sandbox.
    /// </summary>
    private sealed class RunRootPolicy : IRegistryWritePolicy
    {
        private readonly RegistryPath _remoteRunRoot;

        public RunRootPolicy(RegistryPath remoteRunRoot)
        {
            _remoteRunRoot = remoteRunRoot;
        }

        public bool AllowsLinkValues => false;

        public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
        {
            if (path.Root == RegistryRoot.Users && (path.Equals(_remoteRunRoot) || path.IsDescendantOf(_remoteRunRoot)))
            {
                return;
            }

            throw new RegistryOperationException(
                operation.ToString(),
                path,
                ErrorAccessDenied,
                "The test policy allows writes only under the run root.");
        }
    }

    /// <summary>Refuses one operation on one path, and asks the wrapped policy for everything else.</summary>
    private sealed class DenyOperationPolicy : IRegistryWritePolicy
    {
        private readonly IRegistryWritePolicy _inner;
        private readonly RegistryWriteOperation _denied;
        private readonly RegistryPath _path;

        public DenyOperationPolicy(IRegistryWritePolicy inner, RegistryWriteOperation denied, RegistryPath path)
        {
            _inner = inner;
            _denied = denied;
            _path = path;
        }

        public bool AllowsLinkValues => _inner.AllowsLinkValues;

        public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation)
        {
            if (operation == _denied && path.Equals(_path))
            {
                throw new RegistryOperationException(operation.ToString(), path, ErrorAccessDenied, "Injected by the test.");
            }

            _inner.EnsureWriteAllowed(computerName, path, operation);
        }
    }
}
#endif
