using Bwc.Registry.Privileges;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Covers hive policy, argument, and file validation without elevation. The argument and privilege
/// tests use <see cref="ShippingWritePolicy"/> on HKLM and HKU targets because every call they make
/// fails in argument validation or the privilege check before any native hive call. Those checks
/// run after the policy check, and the sandbox refuses HKLM and HKU, so the sandbox policy cannot be
/// used there. The one elevated round trip runs inside the sandbox. Every temporary file and key is
/// removed in a finally block.
/// </summary>
[TestClass]
public sealed class RegistryHiveTests
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 183;
    private const int ErrorPrivilegeNotHeld = 1314;

    [TestMethod]
    public void HiveTargetsRejectRootsWrongRootsAndDeepPaths()
    {
        using var temp = new TempHiveDirectory();
        string existing = temp.CreateFile("existing.hiv");
        var writer = new RegistryWriter(new ShippingWritePolicy());

        RegistryPath[] loadTargets =
        {
            RegistryPath.Parse("HKLM"),
            RegistryPath.Parse(@"HKCU\BwcHive"),
            RegistryPath.Parse(@"HKLM\BwcHive\Nested"),
            RegistryPath.Parse("HKU"),
        };

        foreach (RegistryPath target in loadTargets)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => writer.LoadHive(target, existing),
                target.ToDisplayString());
        }

        Assert.ThrowsExactly<ArgumentException>(
            () => writer.UnloadHive(RegistryPath.Parse(@"HKCU\BwcHive")));
        Assert.ThrowsExactly<ArgumentException>(
            () => writer.UnloadHive(RegistryPath.Parse(@"HKLM\BwcHive\Nested")));
        Assert.ThrowsExactly<ArgumentException>(
            () => writer.UnloadHive(RegistryPath.Parse("HKLM")));

        RegistryPath nonRoot = RegistryPath.Parse(@"HKCU\Software\BwcHiveTargets");
        Assert.ThrowsExactly<ArgumentException>(() => writer.SaveKey(RegistryPath.Parse("HKLM"), temp.CreateFile("save.hiv")));
        Assert.ThrowsExactly<ArgumentException>(() => writer.RestoreKey(RegistryPath.Parse("HKLM"), existing));
        Assert.ThrowsExactly<ArgumentException>(
            () => writer.ReplaceKey(RegistryPath.Parse("HKLM"), existing, temp.CreateFile("backup.hiv")));
        _ = nonRoot;
    }

    [TestMethod]
    public void HiveFileArgumentsAreValidatedBeforeAnyNativeCall()
    {
        using var temp = new TempHiveDirectory();
        var writer = new RegistryWriter(new ShippingWritePolicy());
        RegistryPath target = RegistryPath.Parse(@"HKLM\BwcHiveArguments");

        Assert.ThrowsExactly<ArgumentNullException>(() => writer.LoadHive(target, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => writer.UnloadHive(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => writer.SaveKey(target, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => writer.RestoreKey(target, null!));

        Assert.ThrowsExactly<ArgumentException>(() => writer.LoadHive(target, string.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => writer.LoadHive(target, "bad\0path"));
        Assert.ThrowsExactly<ArgumentException>(() => writer.LoadHive(target, temp.Path));
        Assert.ThrowsExactly<ArgumentException>(() => writer.SaveKey(target, " "));
        Assert.ThrowsExactly<ArgumentException>(() => writer.RestoreKey(target, temp.Path));
        Assert.ThrowsExactly<ArgumentException>(
            () => writer.ReplaceKey(target, temp.CreateFile("new.hiv"), temp.CreateFile("new.hiv")));
    }

    [TestMethod]
    public void HivePolicyDenialWinsBeforeFileAndPrivilegeChecks()
    {
        using var temp = new TempHiveDirectory();
        var writer = new RegistryWriter(new DenyHivePolicy());
        RegistryPath loadTarget = RegistryPath.Parse(@"HKLM\BwcHiveDenied");
        RegistryPath otherTarget = RegistryPath.Parse(@"HKCU\Software\BwcHiveDenied");
        string missing = Path.Combine(temp.Path, "missing.hiv");

        AssertPolicyDenied(() => writer.LoadHive(loadTarget, missing), RegistryWriteOperation.LoadHive, loadTarget);
        AssertPolicyDenied(() => writer.UnloadHive(loadTarget), RegistryWriteOperation.UnloadHive, loadTarget);
        AssertPolicyDenied(() => writer.SaveKey(otherTarget, missing), RegistryWriteOperation.SaveKey, otherTarget);
        AssertPolicyDenied(
            () => writer.ReplaceKey(otherTarget, missing, missing + ".bak"),
            RegistryWriteOperation.ReplaceKey,
            otherTarget);
        AssertPolicyDenied(() => writer.RestoreKey(otherTarget, missing), RegistryWriteOperation.RestoreKey, otherTarget);
    }

    [TestMethod]
    public void HiveFileValidationWinsBeforePrivilegeChecks()
    {
        using var temp = new TempHiveDirectory();
        var writer = new RegistryWriter(new ShippingWritePolicy());
        RegistryPath loadTarget = RegistryPath.Parse(@"HKLM\BwcHiveFileCheck");
        RegistryPath treeTarget = RegistryPath.Parse(@"HKCU\Software\BwcHiveFileCheck");
        string missing = Path.Combine(temp.Path, "missing.hiv");
        string existing = temp.CreateFile("existing.hiv");
        string backup = temp.CreateFile("backup.hiv");

        AssertFileError(
            () => writer.LoadHive(loadTarget, missing),
            RegistryWriteOperation.LoadHive,
            loadTarget,
            ErrorFileNotFound);
        AssertFileError(
            () => writer.RestoreKey(treeTarget, missing),
            RegistryWriteOperation.RestoreKey,
            treeTarget,
            ErrorFileNotFound);
        AssertFileError(
            () => writer.ReplaceKey(treeTarget, missing, backup),
            RegistryWriteOperation.ReplaceKey,
            treeTarget,
            ErrorFileNotFound);
        AssertFileError(
            () => writer.SaveKey(treeTarget, existing),
            RegistryWriteOperation.SaveKey,
            treeTarget,
            ErrorFileExists);
        AssertFileError(
            () => writer.ReplaceKey(treeTarget, existing, backup),
            RegistryWriteOperation.ReplaceKey,
            treeTarget,
            ErrorFileExists);
        AssertFileError(
            () => writer.SaveKey(treeTarget, Path.Combine(temp.Path, "missing-dir", "save.hiv")),
            RegistryWriteOperation.SaveKey,
            treeTarget,
            ErrorPathNotFound);
    }

    [TestMethod]
    public void MissingPrivilegesFailBeforeNativeHiveCalls()
    {
        if (PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) &&
            PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege))
        {
            Assert.Inconclusive(
                "This process holds SeBackupPrivilege and SeRestorePrivilege, so the missing privilege path cannot be observed.");
        }

        using var temp = new TempHiveDirectory();
        string existing = temp.CreateFile("existing.hiv");
        RegistryPath target = RegistryPath.Parse(@"HKLM\BwcHivePrivilegeCheck");

        Assert.IsFalse(PrivilegeScope.TryEnable(out PrivilegeScope? scope, out string? error));
        Assert.IsNull(scope);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error), "TryEnable must explain a missing privilege.");
        Assert.IsTrue(
            PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) ||
            PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege) ||
            error!.Contains("SeBackupPrivilege", StringComparison.Ordinal),
            error);

        var writer = new RegistryWriter(new ShippingWritePolicy());
        var failure = Assert.ThrowsExactly<RegistryOperationException>(
            () => writer.LoadHive(target, existing));
        Assert.AreEqual(ErrorPrivilegeNotHeld, failure.Win32Error);
        Assert.AreEqual(nameof(RegistryWriteOperation.LoadHive), failure.Operation);
        Assert.AreEqual(target, failure.Path);

        var reader = new RegistryReader();
        Assert.IsFalse(reader.KeyExists(target), "A failed privilege check must not mount the hive.");
    }

#if BWC_E2E
    /// <summary>
    /// Saves and restores a sandboxed key through a real hive file. The mount half of the round trip
    /// is not run here: LoadHive and UnloadHive target HKLM or HKU, which SandboxWritePolicy refuses
    /// by design, so no unit test mounts a hive.
    /// </summary>
    [TestMethod]
    [TestCategory("Elevated")]
    public void ElevatedSaveAndRestoreRoundTripStaysInsideTheSandbox()
    {
        if (!PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) ||
            !PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege))
        {
            Assert.Inconclusive(
                "The save and restore round-trip requires an elevated token with SeBackupPrivilege and SeRestorePrivilege.");
        }

        using var temp = new TempHiveDirectory();
        string id = Guid.NewGuid().ToString("N");
        string hiveFile = Path.Combine(temp.Path, "roundtrip.hiv");
        RegistryPath runRoot = RegistryPath.Parse(@"HKCU\Software\BWC-E2E\hive-" + id);
        RegistryPath source = runRoot.Append("Source");

        RegistryPath sandboxBase = RegistryPath.Parse(@"HKCU\Software\BWC-E2E");
        new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);

        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            store.CreateKey(runRoot);
            store.CreateKey(source);
            store.SetString(source, "Text", "from-save-key");
            store.SetDword(source, "Number", 42);

            store.SaveKey(source, hiveFile);
            Assert.IsTrue(File.Exists(hiveFile), "RegSaveKey did not create the hive file.");
            Assert.IsTrue(new FileInfo(hiveFile).Length > 0, "The saved hive file is empty.");

            store.DeleteValue(source, "Text");
            Assert.IsNull(store.Reader.ReadValue(source, "Text"), "The value delete did not reach the sandbox key.");

            store.RestoreKey(source, hiveFile);

            RegistryValueInfo? text = store.Reader.ReadValue(source, "Text");
            RegistryValueInfo? number = store.Reader.ReadValue(source, "Number");
            Assert.IsNotNull(text, "The restored key is missing the saved string value.");
            Assert.IsNotNull(number, "The restored key is missing the saved DWORD value.");
            Assert.AreEqual("from-save-key", RegistryValueCodec.DecodeString(text!.Data));
            Assert.AreEqual(42u, RegistryValueCodec.DecodeDword(number!.Data));
        }
        finally
        {
            try
            {
                store.DeleteKey(runRoot, recursive: true);
            }
            catch (RegistryOperationException error) when (error.Win32Error == 2)
            {
                // The run key was never created.
            }
        }
    }
#endif

    private static void AssertPolicyDenied(
        Action action,
        RegistryWriteOperation operation,
        RegistryPath expectedPath)
    {
        var error = Assert.ThrowsExactly<RegistryOperationException>(action);
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
        Assert.AreEqual(operation.ToString(), error.Operation);
        Assert.AreEqual(expectedPath, error.Path);
    }

    private static void AssertFileError(
        Action action,
        RegistryWriteOperation operation,
        RegistryPath expectedPath,
        int expectedError)
    {
        var error = Assert.ThrowsExactly<RegistryOperationException>(action);
        Assert.AreEqual(expectedError, error.Win32Error);
        Assert.AreEqual(operation.ToString(), error.Operation);
        Assert.AreEqual(expectedPath, error.Path);
    }

    private sealed class DenyHivePolicy : IRegistryWritePolicy
    {
        public bool AllowsLinkValues => false;

        public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
        {
            ArgumentNullException.ThrowIfNull(path);
            throw new RegistryOperationException(operation.ToString(), path, ErrorAccessDenied);
        }
    }

    private sealed class TempHiveDirectory : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BwcHiveTests",
            Guid.NewGuid().ToString("N"));

        public TempHiveDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string Path => _path;

        public string CreateFile(string name)
        {
            string file = System.IO.Path.Combine(_path, name);
            File.WriteAllBytes(file, Array.Empty<byte>());
            return file;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_path))
                {
                    Directory.Delete(_path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup is reported by the next test run's unique folder.
            }
        }
    }
}
