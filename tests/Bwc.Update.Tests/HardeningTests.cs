using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Hardening from the 0.1.1 audit (docs/failure-modes/update-verifier.md, section S).
[TestClass]
public sealed class HardeningTests
{
    [TestMethod]
    public void Receipt_Locked_Refused_Not_Thrown()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, UpdateReceipt.FileName);
        File.WriteAllText(path, "{}");
        using var held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var outcome = UpdateReceipt.Read(path);

        Expect.Fails(outcome, UpdateFailure.ReceiptInvalid);
    }

    [TestMethod]
    public void Receipt_Unreadable_Refused()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Root, UpdateReceipt.FileName);
        File.WriteAllText(path, "{}");
        DenyOwnAccess(path, FileSystemRights.ReadData);
        try
        {
            var outcome = UpdateReceipt.Read(path);

            Expect.Fails(outcome, UpdateFailure.ReceiptInvalid);
        }
        finally
        {
            AllowOwnAccess(path, FileSystemRights.ReadData);
        }
    }

    [TestMethod]
    public void Verify_Read_Failure_Refused()
    {
        var expected = new InstallerExpectation("0.2.0", Architectures.X64, 4096, new string('0', 64), "sig");

        var outcome = InstallerVerifier.Verify(new FailingStream(4096), expected, ["RWQ"]);

        Expect.Fails(outcome, UpdateFailure.Io);
    }

    [TestMethod]
    public void Final_Path_Mismatch_Refused()
    {
        using var dir = new TempDir();
        var target = dir.Sub("target");
        var realInstaller = Path.Combine(target, InstallerPaths.InstallerName);
        File.WriteAllText(realInstaller, "x");
        var junction = Path.Combine(dir.Root, "junction");
        if (!Links.TryMakeJunction(junction, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var viaJunction = Path.Combine(junction, InstallerPaths.InstallerName);
        using var file = File.Open(viaJunction, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = InstallerPaths.FinalPathOf(file.SafeFileHandle);

        Assert.IsNotNull(actual, "the final path of an open file can be read");
        Assert.IsFalse(InstallerPaths.SamePath(viaJunction, actual), "the junction path is not the file that was opened");
        Assert.IsTrue(InstallerPaths.SamePath(realInstaller, actual), "the final path is the real file");
    }

    [TestMethod]
    public void Final_Path_Compare_Normalizes()
    {
        Assert.IsTrue(InstallerPaths.SamePath(@"C:\Stage\abc\installer.exe", @"\\?\c:\stage\ABC\installer.exe"));
        Assert.IsTrue(InstallerPaths.SamePath(@"C:\Stage\abc\installer.exe", @"C:\STAGE\abc\Installer.exe"));
        Assert.IsFalse(InstallerPaths.SamePath(@"C:\Stage\abc\installer.exe", @"C:\Stage\abc\other.exe"));
        Assert.IsFalse(InstallerPaths.SamePath(@"C:\Stage\abc\installer.exe", @"D:\Stage\abc\installer.exe"));
    }

    [TestMethod]
    public void Receipt_Write_Is_Atomic()
    {
        using var dir = new TempDir();
        var folder = dir.Sub("staged");
        var path = Path.Combine(folder, UpdateReceipt.FileName);
        File.WriteAllText(path, "an older receipt");
        var receipt = new UpdateReceipt("0.2.0", UpdateChannel.Beta, Architectures.X64, 4096, new string('a', 64), "sig", "trusted");

        UpdateReceipt.Write(path, receipt);

        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(folder), "no temp file is left beside the receipt");
        StringAssert.Contains(File.ReadAllText(path), "\"version\":\"0.2.0\"");
    }

    private static void DenyOwnAccess(string path, FileSystemRights rights)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), rights, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void AllowOwnAccess(string path, FileSystemRights rights)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(new FileSystemAccessRule(CurrentUser(), rights, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static SecurityIdentifier CurrentUser() => WindowsIdentity.GetCurrent().User!;

    /// <summary>A stream whose reads fail, as a damaged or locked file would.</summary>
    private sealed class FailingStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; } = length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("simulated read failure");

        public override long Seek(long offset, SeekOrigin origin) => Position = offset;

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
