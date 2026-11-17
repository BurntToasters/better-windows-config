#if BWC_E2E
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// The one hive exception of <see cref="SandboxWritePolicy"/> (docs/failure-modes/hives.md section 2): LoadHive and
/// UnloadHive at exactly HKEY_USERS\BWC-E2E-&lt;runId&gt;. Every case calls the policy only, so no key, file, or
/// privilege is touched.
/// </summary>
[TestClass]
public sealed class SandboxHiveMountPolicyTests
{
    private const int ErrorAccessDenied = 5;
    private const string RunId = "20261117T000000Z-0123abcd";
    private const string MountName = "BWC-E2E-" + RunId;

    private static readonly RegistryWriteOperation[] HiveOperations =
    [
        RegistryWriteOperation.LoadHive,
        RegistryWriteOperation.UnloadHive,
    ];

    [TestMethod]
    public void TheRunMountAllowsLoadAndUnload()
    {
        var policy = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId));
        foreach (RegistryWriteOperation operation in HiveOperations)
        {
            policy.EnsureWriteAllowed(null, RegistryPath.Parse(@"HKU\" + MountName), operation);
            policy.EnsureWriteAllowed(null, RegistryPath.Parse(@"HKU\" + MountName.ToLowerInvariant()), operation);
        }
    }

    [TestMethod]
    public void EveryOtherOperationAtTheMountIsRefused()
    {
        var policy = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId));
        RegistryPath mount = RegistryPath.Parse(@"HKU\" + MountName);
        foreach (RegistryWriteOperation operation in Enum.GetValues<RegistryWriteOperation>().Except(HiveOperations))
        {
            AssertRefused(policy, null, mount, operation);
        }
    }

    [TestMethod]
    public void PathsBelowTheMountAreRefusedForEveryOperation()
    {
        var policy = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId));
        RegistryPath below = RegistryPath.Parse(@"HKU\" + MountName + @"\Sub");
        foreach (RegistryWriteOperation operation in Enum.GetValues<RegistryWriteOperation>())
        {
            AssertRefused(policy, null, below, operation);
        }
    }

    [TestMethod]
    public void OtherRootsAndOtherNamesAreRefused()
    {
        var policy = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId));
        string[] targets =
        [
            @"HKLM\" + MountName,
            @"HKCU\" + MountName,
            @"HKU\BWC-E2E-20261117T000000Z-ffffffff",
            @"HKU\" + MountName + "x",
            @"HKU\x" + MountName,
            @"HKU\BWC-E2E-",
            @"HKU\" + RunId,
            "HKU",
        ];

        foreach (string target in targets)
        {
            foreach (RegistryWriteOperation operation in HiveOperations)
            {
                AssertRefused(policy, null, RegistryPath.Parse(target), operation);
            }
        }
    }

    [TestMethod]
    public void ASandboxRootThatIsNotARunRootAllowsNoHiveOperation()
    {
        var bare = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E"));
        var deeper = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId + @"\Sub"));
        foreach (RegistryWriteOperation operation in HiveOperations)
        {
            AssertRefused(bare, null, RegistryPath.Parse(@"HKU\BWC-E2E-BWC-E2E"), operation);
            AssertRefused(bare, null, RegistryPath.Parse(@"HKU\BWC-E2E-"), operation);
            AssertRefused(deeper, null, RegistryPath.Parse(@"HKU\" + MountName), operation);
            AssertRefused(deeper, null, RegistryPath.Parse(@"HKU\BWC-E2E-Sub"), operation);
        }
    }

    [TestMethod]
    public void ARemoteComputerGetsNoHiveException()
    {
        var policy = new SandboxWritePolicy(RegistryPath.Parse(@"HKCU\Software\BWC-E2E\" + RunId));
        foreach (string computer in new[] { "127.0.0.1", "::1", "OTHER" })
        {
            foreach (RegistryWriteOperation operation in HiveOperations)
            {
                AssertRefused(policy, computer, RegistryPath.Parse(@"HKU\" + MountName), operation);
            }
        }
    }

    private static void AssertRefused(
        SandboxWritePolicy policy,
        string? computer,
        RegistryPath path,
        RegistryWriteOperation operation)
    {
        RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(
            () => policy.EnsureWriteAllowed(computer, path, operation),
            $"{operation} at '{path.ToAbbreviatedString()}' on '{computer ?? "local"}' was allowed.");
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
        Assert.AreEqual(operation.ToString(), error.Operation);
    }
}
#endif
