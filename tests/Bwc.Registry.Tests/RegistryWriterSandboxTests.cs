#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryWriterSandboxTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.CreateSubKey(SandboxBase, writable: true);
        Assert.IsNotNull(parent, "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void CreateSetReadDeleteRenameAndCopyWorkAgainstTheRealRegistry()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));

            writer.CreateKey(runRoot);
            writer.SetValue(runRoot, "Text", 1, Encoding.Unicode.GetBytes("hello\0"));
            writer.SetValue(runRoot, string.Empty, 4, BitConverter.GetBytes(42u));
            byte[] blob = { 0, 1, 2, 3, 255 };
            writer.SetValue(runRoot, "Blob", 3, blob);

            RegistryPath child = runRoot.Append("Child");
            writer.CreateKey(child);
            writer.SetValue(child, "Nested", 1, Encoding.Unicode.GetBytes("world\0"));

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.AreEqual("hello", key.GetValue("Text"));
                Assert.AreEqual(RegistryValueKind.String, key.GetValueKind("Text"));
                Assert.AreEqual(42, key.GetValue(string.Empty));
                Assert.AreEqual(RegistryValueKind.DWord, key.GetValueKind(string.Empty));
                CollectionAssert.AreEqual(blob, (byte[])key.GetValue("Blob")!);
                Assert.AreEqual(RegistryValueKind.Binary, key.GetValueKind("Blob"));

                using RegistryKey childKey = RequireSubKey(key, "Child");
                Assert.AreEqual("world", childKey.GetValue("Nested"));
            }

            writer.RenameKey(child, "Renamed");

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.IsNull(key.OpenSubKey("Child"));
                using RegistryKey renamed = RequireSubKey(key, "Renamed");
                Assert.AreEqual("world", renamed.GetValue("Nested"));
            }

            RegistryPath copySource = runRoot.Append("CopySource");
            RegistryPath copyTarget = runRoot.Append("CopyTarget");
            writer.CreateKey(copySource);
            writer.SetValue(copySource, "CopiedText", 1, Encoding.Unicode.GetBytes("copied\0"));
            writer.SetValue(copySource, string.Empty, 4, BitConverter.GetBytes(7u));
            RegistryPath copyChild = copySource.Append("Nested");
            writer.CreateKey(copyChild);
            writer.SetValue(copyChild, "Nested", 1, Encoding.Unicode.GetBytes("nested\0"));

            writer.CreateKey(copyTarget);
            writer.CopyTree(copySource, copyTarget);

            using (RegistryKey key = RequireRunKey(runId))
            {
                using RegistryKey copy = RequireSubKey(key, "CopyTarget");
                Assert.AreEqual("copied", copy.GetValue("CopiedText"));
                Assert.AreEqual(7, copy.GetValue(string.Empty));
                using RegistryKey nested = RequireSubKey(copy, "Nested");
                Assert.AreEqual("nested", nested.GetValue("Nested"));

                using RegistryKey source = RequireSubKey(key, "CopySource");
                Assert.AreEqual("copied", source.GetValue("CopiedText"));
            }

            writer.DeleteValue(runRoot, "Text");

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.IsNull(key.GetValue("Text"));
            }

            writer.DeleteKey(runRoot.Append("Renamed"), recursive: false);

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.IsNull(key.OpenSubKey("Renamed"));
            }

            writer.DeleteKey(copySource, recursive: true);

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.IsNull(key.OpenSubKey("CopySource"));
            }

            writer.DeleteKey(copyTarget, recursive: true);

            using (RegistryKey key = RequireRunKey(runId))
            {
                Assert.IsNull(key.OpenSubKey("CopyTarget"));
            }
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void WriteOutsideTheSandboxIsDeniedAndCreatesNothing()
    {
        string runId = NewRunId();
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath outside = RegistryPath.Parse($@"HKCU\{SandboxBase}\{outsideId}");

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));

            var createError = Assert.ThrowsExactly<RegistryOperationException>(() => writer.CreateKey(outside));
            Assert.AreEqual(5, createError.Win32Error);
            Assert.AreEqual("CreateKey", createError.Operation);
            Assert.AreEqual(outside, createError.Path);
            Assert.AreEqual(unchecked((int)0x80070005u), createError.HResult);

            Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.SetValue(outside, "Value", 4, BitConverter.GetBytes(1u)));
            Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.DeleteKey(outside, recursive: true));

            using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase);
            Assert.IsNotNull(parent);
            using RegistryKey? created = parent!.OpenSubKey(outsideId);
            Assert.IsNull(created);
        }
        finally
        {
            DeleteRunRoot(runId);
            DeleteRunRoot(outsideId);
        }
    }

    [TestMethod]
    public void LinkValueIsRefusedBeforeNativeCodeAndLeavesNoValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.SetValue(
                    runRoot,
                    "Link",
                    6,
                    Encoding.Unicode.GetBytes(@"\Registry\Machine\Software\Target")));

            Assert.AreEqual(5, error.Win32Error);
            Assert.AreEqual("SetValue", error.Operation);
            Assert.AreEqual(runRoot, error.Path);

            using RegistryKey key = RequireRunKey(runId);
            Assert.IsNull(key.GetValue("Link"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ValueNamesWithNulAreRejectedBeforeNativeCode()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);

            Assert.ThrowsExactly<ArgumentException>(
                () => writer.SetValue(runRoot, "bad\0name", 1, Encoding.Unicode.GetBytes("x\0")));

            using RegistryKey key = RequireRunKey(runId);
            Assert.IsNull(key.GetValue("bad"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void SandboxPolicyAllowsOnlyItsSubtree()
    {
        RegistryPath runRoot = RunRoot(NewRunId());
        var policy = new SandboxWritePolicy(runRoot);

        Assert.IsFalse(policy.AllowsLinkValues);
        Assert.AreEqual(runRoot, policy.SandboxRoot);
        policy.EnsureWriteAllowed(runRoot, RegistryWriteOperation.CreateKey);
        policy.EnsureWriteAllowed(runRoot.Append("Child"), RegistryWriteOperation.SetValue);

        RegistryPath[] denied =
        {
            RegistryPath.Parse($@"HKCU\{SandboxBase}"),
            RegistryPath.Parse(runRoot.ToAbbreviatedString() + "x"),
            RegistryPath.Parse($@"HKCU\{SandboxBase}2"),
            RegistryPath.Parse($@"HKLM\{SandboxBase}\x"),
            RegistryPath.Parse(@"HKCU\Software"),
        };

        foreach (RegistryPath path in denied)
        {
            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => policy.EnsureWriteAllowed(path, RegistryWriteOperation.DeleteKey));

            Assert.AreEqual(5, error.Win32Error);
            Assert.AreEqual("DeleteKey", error.Operation);
            Assert.AreEqual(path, error.Path);
        }
    }

    [TestMethod]
    public void SandboxPolicyRejectsRootsOutsideTheSandboxBase()
    {
        string[] invalid =
        {
            $@"HKLM\{SandboxBase}\x",
            @"HKCU\Software\Other\x",
            $@"HKCU\{SandboxBase}2\x",
            @"HKCU\Software",
            "HKCU",
        };

        foreach (string text in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => new SandboxWritePolicy(RegistryPath.Parse(text)),
                text);
        }

        _ = new SandboxWritePolicy(RegistryPath.Parse($@"HKCU\{SandboxBase}"));
        _ = new SandboxWritePolicy(RegistryPath.Parse($@"HKCU\{SandboxBase}\x"));
    }

    private static string NewRunId() => "bwc-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static RegistryKey RequireRunKey(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return hkcu.OpenSubKey($@"{SandboxBase}\{runId}")
            ?? throw new AssertFailedException("The sandbox run key is missing.");
    }

    private static RegistryKey RequireSubKey(RegistryKey parent, string name) =>
        parent.OpenSubKey(name)
        ?? throw new AssertFailedException($"The registry subkey '{name}' is missing.");

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
