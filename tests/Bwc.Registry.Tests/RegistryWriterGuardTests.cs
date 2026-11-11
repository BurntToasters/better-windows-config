#if BWC_E2E
using System.Security.AccessControl;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Symbolic link, rename, copy, restore, and policy guard tests from docs/failure-modes/registry-writer.md.
/// Every write lands under HKCU\Software\BWC-E2E\&lt;run-id&gt;. Each test builds its own run tree,
/// removes link nodes first, then deletes the tree in a finally block.
/// </summary>
[TestClass]
public sealed class RegistryWriterGuardTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorBadFormat = 11;

    private static readonly RegistryReader Reader = new();

    [ClassInitialize]
    public static void EnsureSandboxBase(TestContext testContext)
    {
        _ = testContext;

        var writer = new RegistryWriter(new SandboxWritePolicy(Hkcu(SandboxBase)));
        writer.CreateKey(Hkcu(SandboxBase));
    }

    [TestMethod]
    public void LinkAtLeafIsRefusedForEveryKeyOperation()
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
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            RegistryLinkFixture.CreateLink(sandbox, "Leaf", outside);

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath leaf = Hkcu(sandbox + @"\Leaf");

            AssertLinkRefused(() => writer.SetValue(leaf, "Injected", 1, Utf16("x\0")), "SetValue");
            AssertLinkRefused(() => writer.DeleteValue(leaf, "Marker"), "DeleteValue");
            AssertLinkRefused(() => writer.CreateKey(leaf), "CreateKey");
            AssertLinkRefused(() => writer.CreateKey(leaf.Append("Child")), "CreateKey");
            AssertLinkRefused(() => writer.DeleteKey(leaf, recursive: false), "DeleteKey");
            AssertLinkRefused(() => writer.DeleteKey(leaf, recursive: true), "DeleteKey");
            AssertLinkRefused(() => writer.RenameKey(leaf, "Renamed"), "RenameKey");
            AssertLinkRefused(
                () => writer.SetKeySecurity(leaf, RegistrySecurityInformation.Dacl, EveryoneDescriptor()),
                "SetKeySecurity");

            Assert.IsTrue(RegistryLinkFixture.IsLink(sandbox, "Leaf"), "The link must survive every refused call.");
            Assert.IsNull(Reader.ReadValue(Hkcu(outside), "Injected"));
            Assert.IsNotNull(Reader.ReadValue(Hkcu(outside), "Marker"));
            Assert.IsFalse(Reader.KeyExists(Hkcu(outside + @"\Child")));
            Assert.IsFalse(Reader.KeyExists(Hkcu(outside + @"\Renamed")));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void LinkInMiddleOfPathIsRefused()
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
            setup.CreateKey(Hkcu(outside + @"\Child"));
            RegistryLinkFixture.CreateLink(sandbox, "Mid", outside);

            using var temp = new TempFiles();
            string existing = temp.Write("existing.hiv", Array.Empty<byte>());
            string savePath = temp.Path("save.hiv");
            string backupPath = temp.Path("backup.hiv");

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath child = Hkcu(sandbox + @"\Mid\Child");

            AssertLinkRefused(() => writer.SetValue(child, "Injected", 1, Utf16("x\0")), "SetValue");
            AssertLinkRefused(() => writer.CreateKey(child.Append("Grand")), "CreateKey");
            AssertLinkRefused(() => writer.DeleteValue(child, "Injected"), "DeleteValue");
            AssertLinkRefused(() => writer.DeleteKey(child, recursive: false), "DeleteKey");
            AssertLinkRefused(() => writer.SaveKey(child, savePath), "SaveKey");
            AssertLinkRefused(() => writer.RestoreKey(child, existing), "RestoreKey");
            AssertLinkRefused(() => writer.ReplaceKey(child, existing, backupPath), "ReplaceKey");

            Assert.IsNull(Reader.ReadValue(Hkcu(outside + @"\Child"), "Injected"));
            Assert.IsFalse(Reader.KeyExists(Hkcu(outside + @"\Child\Grand")));
            Assert.IsFalse(File.Exists(savePath), "SaveKey must not create its file when the guard refuses.");
            Assert.IsFalse(File.Exists(backupPath), "ReplaceKey must not create its backup when the guard refuses.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void LinkAtSandboxRootIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string outside = top + @"\Outside";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(outside));
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            RegistryLinkFixture.CreateLink(top, "Sandbox", outside);

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath root = Hkcu(sandbox);

            AssertLinkRefused(() => writer.SetValue(root, "Injected", 1, Utf16("x\0")), "SetValue");
            AssertLinkRefused(() => writer.CreateKey(root), "CreateKey");
            AssertLinkRefused(() => writer.CreateKey(root.Append("Child")), "CreateKey");
            AssertLinkRefused(() => writer.DeleteKey(root, recursive: true), "DeleteKey");

            Assert.IsTrue(RegistryLinkFixture.IsLink(top, "Sandbox"));
            Assert.IsNull(Reader.ReadValue(Hkcu(outside), "Injected"));
            Assert.IsFalse(Reader.KeyExists(Hkcu(outside + @"\Child")));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void LinkAboveSandboxRootIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string outside = top + @"\Outside";
        string via = top + @"\Via";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(outside));
            setup.CreateKey(Hkcu(outside + @"\Sandbox"));
            RegistryLinkFixture.CreateLink(top, "Via", outside);

            RegistryWriter writer = WriterFor(via + @"\Sandbox");
            RegistryPath root = Hkcu(via + @"\Sandbox");

            AssertLinkRefused(() => writer.SetValue(root, "Injected", 1, Utf16("x\0")), "SetValue");
            AssertLinkRefused(() => writer.CreateKey(root.Append("Child")), "CreateKey");
            AssertLinkRefused(() => writer.DeleteKey(root, recursive: false), "DeleteKey");

            Assert.IsTrue(RegistryLinkFixture.IsLink(top, "Via"));
            Assert.IsNull(Reader.ReadValue(Hkcu(outside + @"\Sandbox"), "Injected"));
            Assert.IsFalse(Reader.KeyExists(Hkcu(outside + @"\Sandbox\Child")));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RecursiveDeleteOverLinkIsRefusedAndTargetSurvives()
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
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            setup.CreateKey(Hkcu(sandbox + @"\Parent"));
            RegistryLinkFixture.CreateLink(sandbox + @"\Parent", "Link", outside);

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath parent = Hkcu(sandbox + @"\Parent");

            AssertLinkRefused(() => writer.DeleteKey(parent, recursive: true), "DeleteKey");

            Assert.IsTrue(Reader.KeyExists(parent));
            Assert.IsTrue(RegistryLinkFixture.IsLink(sandbox + @"\Parent", "Link"));
            Assert.IsNotNull(Reader.ReadValue(Hkcu(outside), "Marker"), "The link target must survive the refused delete.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RenameOfSandboxRootIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath root = Hkcu(sandbox);

            var error = Assert.ThrowsExactly<RegistryOperationException>(() => writer.RenameKey(root, "Renamed"));
            Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
            Assert.AreEqual("RenameKey", error.Operation);
            StringAssert.Contains(error.Message, "sandbox root", StringComparison.OrdinalIgnoreCase);

            Assert.IsTrue(Reader.KeyExists(root), "The sandbox root must keep its name.");
            Assert.IsFalse(Reader.KeyExists(Hkcu(top + @"\Renamed")));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RenameOfAncestorIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string movedName = runId + "-moved";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath ancestor = Hkcu(top);

            AssertDenied(() => writer.RenameKey(ancestor, movedName), "RenameKey", "sandbox");

            Assert.IsTrue(Reader.KeyExists(ancestor));
            Assert.IsFalse(Reader.KeyExists(Hkcu($@"{SandboxBase}\{movedName}")));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void CopyFromOutsideTheSandboxIsRefusedBeforeNativeCode()
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
            setup.CreateKey(Hkcu(sandbox + @"\Dest"));
            setup.CreateKey(Hkcu(outside));
            setup.SetValue(Hkcu(outside), "Source", 1, Utf16("from-outside\0"));

            RegistryWriter writer = WriterFor(sandbox);

            AssertDenied(
                () => writer.CopyTree(Hkcu(outside), Hkcu(sandbox + @"\Dest")),
                "CopyTree",
                "sandbox");

            Assert.IsNull(Reader.ReadValue(Hkcu(sandbox + @"\Dest"), "Source"));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void CopyTreeWithLinkInSourceIsRefused()
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
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            setup.CreateKey(Hkcu(sandbox + @"\Src"));
            setup.SetValue(Hkcu(sandbox + @"\Src"), "A", 1, Utf16("a\0"));
            setup.CreateKey(Hkcu(sandbox + @"\Dst"));
            RegistryLinkFixture.CreateLink(sandbox + @"\Src", "Link", outside);

            RegistryWriter writer = WriterFor(sandbox);

            AssertLinkRefused(
                () => writer.CopyTree(Hkcu(sandbox + @"\Src"), Hkcu(sandbox + @"\Dst")),
                "CopyTree");

            Assert.IsNull(Reader.ReadValue(Hkcu(sandbox + @"\Dst"), "A"));
            Assert.AreEqual(0, Reader.EnumerateSubKeyNames(Hkcu(sandbox + @"\Dst")).Count);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void CopyIntoOwnSubtreeIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(sandbox + @"\Src"));
            setup.CreateKey(Hkcu(sandbox + @"\Src\Inner"));

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath source = Hkcu(sandbox + @"\Src");

            Assert.ThrowsExactly<ArgumentException>(() => writer.CopyTree(source, source));
            Assert.ThrowsExactly<ArgumentException>(() => writer.CopyTree(source, source.Append("Inner")));
            Assert.AreEqual(0, Reader.EnumerateSubKeyNames(source.Append("Inner")).Count);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RestoreOfHiveWithLinkIsRefusedAndTargetIsUnchanged()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string restore = sandbox + @"\Restore";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(restore));
            setup.SetValue(Hkcu(restore), "Keep", 1, Utf16("k\0"));

            using var temp = new TempFiles();
            string hive = temp.Write("link.hiv", HiveFileFixture.BuildHive(includeLinkChild: true));

            RegistryWriter writer = WriterFor(sandbox);
            AssertLinkRefused(() => writer.RestoreKey(Hkcu(restore), hive), "RestoreKey");

            Assert.IsNotNull(Reader.ReadValue(Hkcu(restore), "Keep"));
            Assert.AreEqual(0, Reader.EnumerateSubKeyNames(Hkcu(restore)).Count);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void ReplaceOfHiveWithLinkIsRefusedBeforeBackup()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string restore = sandbox + @"\Restore";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(restore));
            setup.SetValue(Hkcu(restore), "Keep", 1, Utf16("k\0"));

            using var temp = new TempFiles();
            string hive = temp.Write("link.hiv", HiveFileFixture.BuildHive(includeLinkChild: true));
            string backup = temp.Path("backup.hiv");

            RegistryWriter writer = WriterFor(sandbox);
            AssertLinkRefused(() => writer.ReplaceKey(Hkcu(restore), hive, backup), "ReplaceKey");

            Assert.IsFalse(File.Exists(backup), "The backup must not be written when the new content is refused.");
            Assert.IsNotNull(Reader.ReadValue(Hkcu(restore), "Keep"));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RestoreOfUnverifiableFileIsRefused()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string restore = sandbox + @"\Restore";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(restore));
            setup.SetValue(Hkcu(restore), "Keep", 1, Utf16("k\0"));

            using var temp = new TempFiles();
            string notAHive = temp.Write("text.hiv", Encoding.ASCII.GetBytes("this is not a registry hive"));
            string truncated = temp.Write("truncated.hiv", HiveFileFixture.BuildTruncatedHive());

            RegistryWriter writer = WriterFor(sandbox);

            foreach (string file in new[] { notAHive, truncated })
            {
                var error = Assert.ThrowsExactly<RegistryOperationException>(
                    () => writer.RestoreKey(Hkcu(restore), file));
                Assert.AreEqual(ErrorBadFormat, error.Win32Error, file);
                Assert.AreEqual("RestoreKey", error.Operation);
                StringAssert.Contains(error.Message, "cannot verify", StringComparison.OrdinalIgnoreCase);
            }

            Assert.IsNotNull(Reader.ReadValue(Hkcu(restore), "Keep"));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void RestoreOfCleanHiveReachesTheNativeCall()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string restore = sandbox + @"\Restore";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));
            setup.CreateKey(Hkcu(restore));

            using var temp = new TempFiles();
            string clean = temp.Write("clean.hiv", HiveFileFixture.BuildHive(includeLinkChild: false));

            RegistryWriter writer = WriterFor(sandbox);

            // The fixture is not a valid hive for RegRestoreKey, so the native call may fail. The
            // guard must not be the reason: its refusals name a symbolic link or an unverifiable file.
            try
            {
                writer.RestoreKey(Hkcu(restore), clean);
            }
            catch (RegistryOperationException error)
            {
                Assert.IsFalse(error.Message.Contains("symbolic link", StringComparison.OrdinalIgnoreCase), error.Message);
                Assert.IsFalse(error.Message.Contains("cannot verify", StringComparison.OrdinalIgnoreCase), error.Message);
            }
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void HiveMountsAreRefusedBeforeAnyPrivilegeCheck()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));

            using var temp = new TempFiles();
            string hive = temp.Write("mount.hiv", HiveFileFixture.BuildHive(includeLinkChild: false));

            RegistryWriter writer = WriterFor(sandbox);

            // Policy denial comes first, so the 5 (not 1314) proves no privilege check ran.
            AssertDenied(
                () => writer.LoadHive(RegistryPath.Parse($@"HKLM\BwcGuardMount-{runId}"), hive),
                "LoadHive",
                "sandbox");
            AssertDenied(
                () => writer.UnloadHive(RegistryPath.Parse($@"HKU\BwcGuardMount-{runId}")),
                "UnloadHive",
                "sandbox");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void PredefinedAliasesOfTheSandboxRoot()
    {
        string runId = NewRunId();
        string child = TopOf(runId) + @"\Sandbox\Child";
        var policy = new SandboxWritePolicy(Hkcu(TopOf(runId) + @"\Sandbox"));

        string[] allowed =
        {
            $@"HKCU\{child}",
            $@"HKEY_CURRENT_USER\{child}",
            $@"Computer\HKEY_CURRENT_USER\{child}",
            $@"hkcu\{child.ToLowerInvariant()}",
        };

        string[] denied =
        {
            $@"HKU\S-1-5-21-1-2-3-1001\{child}",
            $@"HKLM\{child}",
            $@"HKCU\{SandboxBase}2\x",
        };

        foreach (string text in allowed)
        {
            policy.EnsureWriteAllowed(RegistryPath.Parse(text), RegistryWriteOperation.SetValue);
        }

        foreach (string text in denied)
        {
            AssertDenied(
                () => policy.EnsureWriteAllowed(RegistryPath.Parse(text), RegistryWriteOperation.SetValue),
                "SetValue",
                "sandbox",
                text);
        }
    }

    [TestMethod]
    public void DenyAllRefusesEveryWriteWithoutTouchingTheRegistry()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string deny = top + @"\Deny";
        string target = deny + @"\Target";
        string movedName = "Renamed";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(deny));

            using var temp = new TempFiles();
            string hive = temp.Write("deny.hiv", HiveFileFixture.BuildHive(includeLinkChild: false));
            string saveFile = temp.Path("save.hiv");
            string backupFile = temp.Path("backup.hiv");

            var writer = new RegistryWriter(new DenyAllWritePolicy());
            RegistryPath key = Hkcu(deny);
            RegistryPath child = Hkcu(target);
            var data = Utf16("x\0");

            AssertRefused(() => writer.CreateKey(child), "CreateKey", "deny-all");
            AssertRefused(() => writer.SetValue(key, "x", 1, data), "SetValue", "deny-all");
            AssertRefused(() => writer.DeleteValue(key, "x"), "DeleteValue", "deny-all");
            AssertRefused(() => writer.DeleteKey(key, recursive: false), "DeleteKey", "deny-all");
            AssertRefused(() => writer.RenameKey(key, movedName), "RenameKey", "deny-all");
            AssertRefused(() => writer.CopyTree(key, key), "CopyTree", "deny-all");
            AssertRefused(
                () => writer.SetKeySecurity(key, RegistrySecurityInformation.Dacl, EveryoneDescriptor()),
                "SetKeySecurity",
                "deny-all");
            AssertRefused(() => writer.SaveKey(key, saveFile), "SaveKey", "deny-all");
            AssertRefused(() => writer.RestoreKey(key, hive), "RestoreKey", "deny-all");
            AssertRefused(() => writer.ReplaceKey(key, hive, backupFile), "ReplaceKey", "deny-all");
            AssertRefused(
                () => writer.LoadHive(RegistryPath.Parse($@"HKLM\BwcGuardDeny-{runId}"), hive),
                "LoadHive",
                "deny-all");
            AssertRefused(
                () => writer.UnloadHive(RegistryPath.Parse($@"HKLM\BwcGuardDeny-{runId}")),
                "UnloadHive",
                "deny-all");

            Assert.IsFalse(Reader.KeyExists(child));
            Assert.IsFalse(Reader.KeyExists(Hkcu(deny + @"\" + movedName)));
            Assert.IsFalse(File.Exists(saveFile));
            Assert.IsFalse(File.Exists(backupFile));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void DenyAllRefusesTheUnsetPlaceholderPath()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string unset = top + @"\unset";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));

            var writer = new RegistryWriter(new DenyAllWritePolicy());
            RegistryPath placeholder = Hkcu(unset);

            AssertRefused(() => writer.CreateKey(placeholder), "CreateKey", "deny-all");
            Assert.IsFalse(Reader.KeyExists(placeholder), "The placeholder must not be created.");
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void MissingComponentReportsNotFoundNotDenied()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));

            RegistryWriter writer = WriterFor(sandbox);
            RegistryPath missing = Hkcu(sandbox + @"\Missing\Deep");

            var setError = Assert.ThrowsExactly<RegistryOperationException>(
                () => writer.SetValue(missing, "x", 1, Utf16("x\0")));
            Assert.AreEqual(ErrorFileNotFound, setError.Win32Error);
            Assert.AreEqual("SetValue", setError.Operation);

            var createError = Assert.ThrowsExactly<RegistryOperationException>(() => writer.CreateKey(missing));
            Assert.AreEqual(ErrorFileNotFound, createError.Win32Error);
            Assert.AreEqual("CreateKey", createError.Operation);
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void ReadsFollowLinksIntoTheirTarget()
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
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            RegistryLinkFixture.CreateLink(sandbox, "Via", outside);

            Assert.IsTrue(RegistryLinkFixture.IsLink(sandbox, "Via"));
            Assert.IsFalse(RegistryLinkFixture.IsLink(sandbox, "Missing"));

            RegistryValueInfo? marker = Reader.ReadValue(Hkcu(sandbox + @"\Via"), "Marker");
            Assert.IsNotNull(marker, "Native opens follow links, which is why writes need the guard.");
            Assert.AreEqual("keep", RegistryValueCodec.DecodeString(marker!.Data));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void LinkNodeRemovalLeavesTheTargetAlone()
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
            setup.SetValue(Hkcu(outside), "Marker", 1, Utf16("keep\0"));
            RegistryLinkFixture.CreateLink(sandbox, "Gone", outside);

            RegistryLinkFixture.DeleteLinkNodes(sandbox);

            Assert.IsFalse(RegistryLinkFixture.IsLink(sandbox, "Gone"));
            Assert.IsTrue(Reader.KeyExists(Hkcu(outside)), "Removing a link node must not remove its target.");
            Assert.IsNotNull(Reader.ReadValue(Hkcu(outside), "Marker"));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    [TestMethod]
    public void NormalKeyWithValueNamedLikeLinkIsNotALink()
    {
        string runId = NewRunId();
        string top = TopOf(runId);
        string sandbox = top + @"\Sandbox";
        string normal = sandbox + @"\Normal";

        try
        {
            RegistryWriter setup = WriterFor(top);
            setup.CreateKey(Hkcu(top));
            setup.CreateKey(Hkcu(sandbox));

            RegistryWriter writer = WriterFor(sandbox);
            writer.CreateKey(Hkcu(normal));
            writer.SetValue(Hkcu(normal), "SymbolicLinkValue", 1, Utf16("not a link\0"));

            Assert.IsFalse(RegistryLinkFixture.IsLink(sandbox, "Normal"));

            writer.SetValue(Hkcu(normal), "Plain", 1, Utf16("ok\0"));
            RegistryValueInfo? plain = Reader.ReadValue(Hkcu(normal), "Plain");
            Assert.IsNotNull(plain);
            Assert.AreEqual("ok", RegistryValueCodec.DecodeString(plain!.Data));
        }
        finally
        {
            CleanupRun(runId);
        }
    }

    private static string NewRunId() => "guard-" + Guid.NewGuid().ToString("N");

    private static string TopOf(string runId) => $@"{SandboxBase}\{runId}";

    private static RegistryPath Hkcu(string relativePath) => RegistryPath.Parse(@"HKCU\" + relativePath);

    private static RegistryWriter WriterFor(string sandboxRelativePath) =>
        new(new SandboxWritePolicy(Hkcu(sandboxRelativePath)));

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

    private static byte[] EveryoneDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("D:(A;;KA;;;WD)");
        byte[] binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    private static void AssertLinkRefused(Action action, string operation)
    {
        RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(action);
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error);
        Assert.AreEqual(operation, error.Operation);
        StringAssert.Contains(error.Message, "symbolic link", StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertDenied(Action action, string operation, string messageFragment, string? label = null)
    {
        RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(action, label ?? string.Empty);
        Assert.AreEqual(ErrorAccessDenied, error.Win32Error, label);
        Assert.AreEqual(operation, error.Operation, label);
        StringAssert.Contains(error.Message, messageFragment, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRefused(Action action, string operation, string messageFragment)
    {
        AssertDenied(action, operation, messageFragment);
    }

    private static void CleanupRun(string runId)
    {
        foreach (string name in new[] { runId, runId + "-moved" })
        {
            string relative = $@"{SandboxBase}\{name}";

            RegistryLinkFixture.DeleteLinkNodes(relative);

            var store = new RegistryStore(new SandboxWritePolicy(Hkcu(SandboxBase)));
            try
            {
                store.DeleteKey(Hkcu(relative), recursive: true);
            }
            catch (RegistryOperationException error) when (error.Win32Error == ErrorFileNotFound)
            {
                // The tree was never created, or the test already moved it back.
            }
        }
    }

    private sealed class TempFiles : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BwcGuardTests",
            Guid.NewGuid().ToString("N"));

        public TempFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Path(string name) => System.IO.Path.Combine(_directory, name);

        public string Write(string name, byte[] content)
        {
            string file = Path(name);
            File.WriteAllBytes(file, content);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed temp cleanup does not affect the registry state under test.
            }
        }
    }
}
#endif
