#if BWC_E2E
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Keys named "." and ".." under the sandbox. The registry lists them, and the permission walks, the owner walk, and
/// the search must reach them instead of throwing or skipping them (writer section K). The keys are created with the
/// native API through <see cref="RegistryLinkFixture.CreateKey"/>, because the managed API refuses those names.
/// </summary>
[TestClass]
public sealed class RegistryDotNamedKeyTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        RegistryPath sandboxBase = RegistryPath.Parse($@"HKCU\{SandboxBase}");
        new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
    }

    [TestMethod]
    public void PropagationReachesDotNamedKeys()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath tree = runRoot.Append("Tree");

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.CreateKey(tree);
            RegistryLinkFixture.CreateKey(RelativeOf(tree), ".");
            RegistryLinkFixture.CreateKey(RelativeOf(tree), "..");
            RegistryLinkFixture.CreateKey(RelativeOf(tree) + @"\.", "Child");

            // The current user keeps full control, so the cleanup walk can still reach every key.
            byte[] descriptor = Descriptor($"D:(A;CI;KA;;;{CurrentSid()})(A;CI;KR;;;WD)");
            var progress = new KeyCollector();
            writer.SetKeySecurity(
                tree,
                descriptor,
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                propagateToSubkeys: true,
                progress);

            CollectionAssert.Contains(progress.Paths, tree.AppendEnumeratedName("."));
            CollectionAssert.Contains(progress.Paths, tree.AppendEnumeratedName(".."));
            CollectionAssert.Contains(progress.Paths, tree.AppendEnumeratedName(".").Append("Child"));

            var reader = new RegistryReader();
            AssertGrantsEveryone(reader, tree.AppendEnumeratedName("."));
            AssertGrantsEveryone(reader, tree.AppendEnumeratedName(".."));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void OwnerWalkReachesDotNamedKeys()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath tree = runRoot.Append("Tree");

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.CreateKey(tree);
            RegistryLinkFixture.CreateKey(RelativeOf(tree), ".");
            RegistryLinkFixture.CreateKey(RelativeOf(tree), "..");

            // The current user is already the owner, so the walk changes no owner. The test checks that every key is
            // reached and that the walk completes with no failure list.
            byte[] owner = Descriptor("O:" + CurrentSid());
            var progress = new KeyCollector();
            writer.SetKeyOwnerTree(tree, owner, progress);

            CollectionAssert.Contains(progress.Paths, tree.AppendEnumeratedName("."));
            CollectionAssert.Contains(progress.Paths, tree.AppendEnumeratedName(".."));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void FinderVisitsDotNamedKeys()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath alpha = runRoot.Append("Alpha");

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.CreateKey(alpha);
            RegistryLinkFixture.CreateKey(RelativeOf(alpha), ".");
            RegistryLinkFixture.CreateKey(RelativeOf(alpha) + @"\.", "Target");

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery("Target", lookAtKeys: true, roots: new[] { runRoot });
            List<RegistryFindHit> hits = CollectHits(finder, query);

            Assert.AreEqual(1, hits.Count, "The search must descend into the key named '.'.");
            Assert.AreEqual(alpha.AppendEnumeratedName(".").Append("Target"), hits[0].KeyPath);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    private static void AssertGrantsEveryone(RegistryReader reader, RegistryPath key)
    {
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var descriptor = new RawSecurityDescriptor(reader.GetKeySecurity(key, RegistrySecurityInformation.Dacl), 0);
        RawAcl? acl = descriptor.DiscretionaryAcl;
        Assert.IsNotNull(acl, "The key has no DACL: " + key.ToAbbreviatedString());
        bool granted = acl.OfType<KnownAce>().Any(ace => ace.SecurityIdentifier.Equals(world));

        Assert.IsTrue(granted, "The inherited Everyone entry must reach " + key.ToAbbreviatedString());
    }

    private static List<RegistryFindHit> CollectHits(RegistryFinder finder, RegistryFindQuery query)
    {
        const int Limit = 100;
        var hits = new List<RegistryFindHit>();
        RegistryFindHit? hit = finder.Find(query, null);

        while (hit is not null)
        {
            hits.Add(hit);
            Assert.IsTrue(hits.Count <= Limit, "The finder produced too many hits, likely a repeat loop.");
            hit = finder.FindNext(query, hit);
        }

        return hits;
    }

    private static byte[] Descriptor(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    private static string NewRunId() => "dot-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) => RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    /// <summary>The path of <paramref name="path"/> relative to HKEY_CURRENT_USER, for the native fixture.</summary>
    private static string RelativeOf(RegistryPath path) => string.Join('\\', path.Components);

    private static string CurrentSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    /// <summary>Records the key of every progress report, in order.</summary>
    private sealed class KeyCollector : IProgress<KeySecurityProgress>
    {
        public List<RegistryPath> Paths { get; } = new();

        public void Report(KeySecurityProgress value) => Paths.Add(value.Path);
    }
}
#endif
