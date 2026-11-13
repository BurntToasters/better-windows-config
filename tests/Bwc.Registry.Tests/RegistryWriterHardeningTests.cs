#if BWC_E2E
using System.Security.AccessControl;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Sandbox tests for the destination-link copy guard, the permission propagation API, and the
/// hardening rules in docs/failure-modes/registry-writer.md (A11a and section I). Every write lands
/// under HKCU\Software\BWC-E2E\&lt;run-id&gt;, and each test removes its run tree in a finally block.
/// </summary>
[TestClass]
public sealed class RegistryWriterHardeningTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;

    private static readonly RegistryReader Reader = new();

    [ClassInitialize]
    public static void EnsureSandboxBase(TestContext testContext)
    {
        _ = testContext;

        var writer = new RegistryWriter(new SandboxWritePolicy(Hkcu(SandboxBase)));
        writer.CreateKey(Hkcu(SandboxBase));
    }

    [TestMethod]
    public void CopyTreeWithLinkInDestinationIsRefusedBeforeAnyCopy()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string outside = top + @"\Outside";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(outside));
            setup.CreateKey(Hkcu(sandbox + @"\Src"));
            setup.CreateKey(Hkcu(sandbox + @"\Src\Link"));
            setup.SetValue(Hkcu(sandbox + @"\Src\Link"), "Leak", 1, Utf16("leak\0"));
            setup.CreateKey(Hkcu(sandbox + @"\Dst"));
            RegistryLinkFixture.CreateLink(sandbox + @"\Dst", "Link", outside);

            RegistryWriter writer = WriterFor(sandbox);

            RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.CopyTree(Hkcu(sandbox + @"\Src"), Hkcu(sandbox + @"\Dst")));
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.AreEqual("CopyTree", error.Operation);
            StringAssert.Contains(error.Message, "symbolic link", StringComparison.OrdinalIgnoreCase);

            // Without the guard, RegCopyTree would follow the destination link and write "Leak" into Outside.
            Assert.IsNull(Reader.ReadValue(Hkcu(outside), "Leak"));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityPropagationRefusesLinkedSubtreeAndChangesNothing()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string tree = sandbox + @"\Tree";
        string outside = top + @"\Outside";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(outside));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\Inner"));
            RegistryLinkFixture.CreateLink(tree + @"\Inner", "Link", outside);

            RegistryWriter writer = WriterFor(sandbox);
            byte[] before = Reader.GetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl);

            RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.SetKeySecurity(
                    Hkcu(tree),
                    EveryoneDescriptor(),
                    RegistrySecurityInformation.Dacl,
                    propagateToSubkeys: true));
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.AreEqual("SetKeySecurity", error.Operation);
            StringAssert.Contains(error.Message, "symbolic link", StringComparison.OrdinalIgnoreCase);

            CollectionAssert.AreEqual(
                before,
                Reader.GetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl),
                "A refused propagation must leave the root key unchanged.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityPropagationStaysInsideTheSandbox()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string outside = top + @"\Outside";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(outside));

            RegistryWriter writer = WriterFor(sandbox);

            RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.SetKeySecurity(
                    Hkcu(outside),
                    EveryoneDescriptor(),
                    RegistrySecurityInformation.Dacl,
                    propagateToSubkeys: true));
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.AreEqual("SetKeySecurity", error.Operation);
            StringAssert.Contains(error.Message, "sandbox write policy", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityPropagationAppliesToEveryKeyAndReportsEachOne()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string tree = sandbox + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\A\B"));
            setup.CreateKey(Hkcu(tree + @"\C"));

            RegistryWriter writer = WriterFor(sandbox);
            var progress = new RecordingSecurityProgress();

            // A container-inheritable entry on the root is what the children inherit.
            var inheritable = new RawSecurityDescriptor($"D:(A;CI;KA;;;{CurrentUserSid()})");
            byte[] inheritableBinary = new byte[inheritable.BinaryLength];
            inheritable.GetBinaryForm(inheritableBinary, 0);

            writer.SetKeySecurity(
                Hkcu(tree),
                RegistrySecurityInformation.Dacl | RegistrySecurityInformation.UnprotectedDacl,
                inheritableBinary);
            writer.SetKeySecurity(
                Hkcu(tree),
                inheritableBinary,
                RegistrySecurityInformation.Dacl,
                propagateToSubkeys: true,
                progress: progress);

            foreach (string key in new[] { tree + @"\A", tree + @"\A\B", tree + @"\C" })
            {
                RawSecurityDescriptor child = new(Reader.GetKeySecurity(Hkcu(key), RegistrySecurityInformation.Dacl), 0);
                RawAcl dacl = child.DiscretionaryAcl ?? throw new InvalidOperationException("The child has no DACL.");
                Assert.AreEqual(1, dacl.Count, key);
                Assert.IsTrue(
                    ((CommonAce)dacl[0]).AceFlags.HasFlag(AceFlags.Inherited),
                    key + " must hold only the inherited entry.");
            }

            CollectionAssert.AreEquivalent(
                new[]
                {
                    Hkcu(tree).ToAbbreviatedString(),
                    Hkcu(tree + @"\A").ToAbbreviatedString(),
                    Hkcu(tree + @"\A\B").ToAbbreviatedString(),
                    Hkcu(tree + @"\C").ToAbbreviatedString(),
                },
                progress.Paths.Select(path => path.ToAbbreviatedString()).ToArray());
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityPropagationCanBeCancelledAfterTheCurrentKey()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string tree = sandbox + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));
            setup.CreateKey(Hkcu(tree + @"\C"));

            RegistryWriter writer = WriterFor(sandbox);
            string treeBefore = Sddl(Reader.GetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl));
            string childBefore = Sddl(Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl));

            using var cancellation = new CancellationTokenSource();
            var progress = new RecordingSecurityProgress(() => cancellation.Cancel());

            // The root grants the current user full control through an inheritable entry, so every child keeps
            // an entry for the current user and the walk can be cancelled between keys.
            Assert.ThrowsExactly<OperationCanceledException>(
                () => writer.SetKeySecurity(
                    Hkcu(tree),
                    InheritableUserDescriptor(),
                    RegistrySecurityInformation.Dacl,
                    propagateToSubkeys: true,
                    progress: progress,
                    cancellationToken: cancellation.Token));

            Assert.AreNotEqual(
                treeBefore,
                Sddl(Reader.GetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl)),
                "The key that was current when the cancel arrived keeps its new descriptor.");
            Assert.AreEqual(
                childBefore,
                Sddl(Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl)),
                "Keys after the cancel point are not changed.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void SetKeySecurityWithoutPropagationStillChangesOnlyTheNamedKey()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string tree = sandbox + @"\Tree";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(tree));
            setup.CreateKey(Hkcu(tree + @"\A"));

            RegistryWriter writer = WriterFor(sandbox);
            string childBefore = Sddl(Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl));

            writer.SetKeySecurity(Hkcu(tree), RegistrySecurityInformation.Dacl, EveryoneDescriptor());

            Assert.AreEqual(
                childBefore,
                Sddl(Reader.GetKeySecurity(Hkcu(tree + @"\A"), RegistrySecurityInformation.Dacl)));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    private static string Sddl(byte[] descriptor) =>
        new RawSecurityDescriptor(descriptor, 0).GetSddlForm(AccessControlSections.Access);

    private static string NewRunId() => "hardening-" + Guid.NewGuid().ToString("N");

    private static string TopOf(string runId) => $@"{SandboxBase}\{runId}";

    private static RegistryPath Hkcu(string relativePath) => RegistryPath.Parse(@"HKCU\" + relativePath);

    private static RegistryWriter WriterFor(string sandboxRelativePath) =>
        new(new SandboxWritePolicy(Hkcu(sandboxRelativePath)));

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

    private static string CurrentUserSid()
    {
        using System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
    }

    private static byte[] InheritableUserDescriptor()
    {
        var descriptor = new RawSecurityDescriptor($"D:(A;CI;KA;;;{CurrentUserSid()})");
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

    /// <summary>
    /// Removes the run tree through the shared walk. The walk restores the owner's full control on each key,
    /// removes link nodes without following them, and deletes through the engine.
    /// See docs/failure-modes/permissions.md section 26.
    /// </summary>
    private static void CleanupRun(string runId) => SandboxRunCleanup.RemoveRunTree(runId);

    private sealed class RecordingSecurityProgress : IProgress<KeySecurityProgress>
    {
        private readonly Action? _onReport;

        public RecordingSecurityProgress(Action? onReport = null)
        {
            _onReport = onReport;
        }

        public List<RegistryPath> Paths { get; } = new();

        public void Report(KeySecurityProgress value)
        {
            Paths.Add(value.Path);
            _onReport?.Invoke();
        }
    }
}
#endif
