using Bwc.Registry.Privileges;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

// The hook is process-wide, so these tests run one at a time.
[TestClass]
[DoNotParallelize]
public sealed class PrivilegeScopeRestoreTests
{
    private const int AccessDenied = 5;

    [TestCleanup]
    public void ClearHook()
    {
        PrivilegeDiagnostics.RestoreFailed = null;
    }

    [TestMethod]
    public void FailedBackupRestoreReportsPrivilegeNameAndError()
    {
        var calls = new List<string>();
        var reported = new List<(string Name, int Error)>();
        PrivilegeDiagnostics.RestoreFailed = (name, error) => reported.Add((name, error));

        PrivilegeScope scope = PrivilegeScope.CreateForRestoreTest(
            disableBackup: true,
            disableRestore: false,
            restore: name =>
            {
                calls.Add(name);
                return name == PrivilegeScope.BackupPrivilege ? AccessDenied : 0;
            });

        scope.Dispose();

        CollectionAssert.AreEqual(new[] { PrivilegeScope.BackupPrivilege }, calls);
        Assert.AreEqual(1, reported.Count, "A failed restore must reach the hook exactly once.");
        Assert.AreEqual(PrivilegeScope.BackupPrivilege, reported[0].Name);
        Assert.AreEqual(AccessDenied, reported[0].Error);
    }

    [TestMethod]
    public void FailedRestoreStillAttemptsTheOtherPrivilegeAndDoesNotThrow()
    {
        var calls = new List<string>();
        var reported = new List<string>();
        PrivilegeDiagnostics.RestoreFailed = (name, _) => reported.Add(name);

        PrivilegeScope scope = PrivilegeScope.CreateForRestoreTest(
            disableBackup: true,
            disableRestore: true,
            restore: name =>
            {
                calls.Add(name);
                return name == PrivilegeScope.BackupPrivilege ? AccessDenied : 0;
            });

        scope.Dispose();

        CollectionAssert.AreEqual(
            new[] { PrivilegeScope.BackupPrivilege, PrivilegeScope.RestorePrivilege },
            calls);
        CollectionAssert.AreEqual(new[] { PrivilegeScope.BackupPrivilege }, reported);
    }

    [TestMethod]
    public void ThrowingHookDoesNotEscapeDispose()
    {
        PrivilegeDiagnostics.RestoreFailed = (_, _) => throw new InvalidOperationException("hook failure");

        PrivilegeScope scope = PrivilegeScope.CreateForRestoreTest(
            disableBackup: true,
            disableRestore: false,
            restore: _ => AccessDenied);

        scope.Dispose();
        scope.Dispose();
    }

    [TestMethod]
    public void SuccessfulRestoreDoesNotCallHook()
    {
        int hookCalls = 0;
        PrivilegeDiagnostics.RestoreFailed = (_, _) => hookCalls++;

        PrivilegeScope scope = PrivilegeScope.CreateForRestoreTest(
            disableBackup: true,
            disableRestore: true,
            restore: _ => 0);

        scope.Dispose();

        Assert.AreEqual(0, hookCalls);
    }

    [TestMethod]
    public void OptionalScopeFailedRestoreReportsPrivilegeNameAndError()
    {
        var reported = new List<(string Name, int Error)>();
        var calls = new List<string>();
        PrivilegeDiagnostics.RestoreFailed = (name, error) => reported.Add((name, error));

        OptionalPrivilegeScope scope = OptionalPrivilegeScope.CreateForRestoreTest(
            new[] { OptionalPrivilegeScope.SecurityPrivilege, OptionalPrivilegeScope.TakeOwnershipPrivilege },
            name =>
            {
                calls.Add(name);
                return name == OptionalPrivilegeScope.SecurityPrivilege ? AccessDenied : 0;
            });

        scope.Dispose();

        CollectionAssert.AreEqual(
            new[] { OptionalPrivilegeScope.SecurityPrivilege, OptionalPrivilegeScope.TakeOwnershipPrivilege },
            calls);
        Assert.AreEqual(1, reported.Count, "A failed restore must reach the hook exactly once.");
        Assert.AreEqual(OptionalPrivilegeScope.SecurityPrivilege, reported[0].Name);
        Assert.AreEqual(AccessDenied, reported[0].Error);
    }

    [TestMethod]
    public void OptionalScopeSuccessfulRestoreDoesNotCallHook()
    {
        int hookCalls = 0;
        PrivilegeDiagnostics.RestoreFailed = (_, _) => hookCalls++;

        OptionalPrivilegeScope scope = OptionalPrivilegeScope.CreateForRestoreTest(
            new[] { OptionalPrivilegeScope.SecurityPrivilege },
            _ => 0);

        scope.Dispose();

        Assert.AreEqual(0, hookCalls);
    }
}
