#if BWC_E2E
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// KeyExists on the reader and on the writer's rename fallback must agree: ERROR_FILE_NOT_FOUND (2) and
/// ERROR_PATH_NOT_FOUND (3) both mean the key does not exist (reader section 18). The probe is a symbolic link whose
/// target is an NT path outside the registry roots. Opening it follows the link, and the open reports 3 (measured).
/// </summary>
[TestClass]
public sealed class RegistryKeyExistsTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const string DanglingName = "Dangling";
    private const string MissingName = "NoSuchKey";
    private const int ErrorAccessDenied = 5;

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        RegistryPath sandboxBase = RegistryPath.Parse($@"HKCU\{SandboxBase}");
        new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
    }

    [TestMethod]
    public void KeyExistsTreatsPathNotFoundAsMissing()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            CreateDanglingLink(runId, runRoot);

            var reader = new RegistryReader();
            Assert.IsFalse(reader.KeyExists(runRoot.Append(MissingName)), "A missing key is not a key.");
            Assert.IsFalse(reader.KeyExists(runRoot.Append(DanglingName)), "A link to a path outside the registry is not a key.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void KeyExistsAndTheWriterAgreeOnMissingKeys()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            CreateDanglingLink(runId, runRoot);
            RegistryPath source = runRoot.Append("Source");
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(source);

            // The destination is the dangling link. Its probe reports 3 and must read as missing, so the copy goes on to
            // CreateKey, which refuses the link (access denied) and creates nothing. Before the fix the probe threw 3
            // from RenameKey, so the operation tells the two apart.
            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.RenameKeyByCopy(source, DanglingName));

            Assert.AreEqual(nameof(RegistryWriteOperation.CreateKey), error.Operation);
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.IsTrue(new RegistryReader().KeyExists(source), "A refused rename must change nothing.");
            Assert.IsTrue(
                RegistryLinkFixture.IsLink(SandboxBase + @"\" + runId, DanglingName),
                "A refused rename must leave the link in place.");
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    private static void CreateDanglingLink(string runId, RegistryPath runRoot)
    {
        var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
        writer.CreateKey(runRoot);

        string relative = SandboxBase + @"\" + runId;
        RegistryLinkFixture.CreateRawLink(relative, DanglingName, $@"\NoSuchRoot\{relative}\{MissingName}");
    }

    private static string NewRunId() => "exists-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) => RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");
}
#endif
