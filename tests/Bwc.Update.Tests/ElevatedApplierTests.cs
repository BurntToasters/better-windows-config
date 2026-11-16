using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The elevated helper (--apply-elevated). Cases are in docs/failure-modes/update-elevation.md, section EA (and EE for the
// E2E-only root). The host is a fake: no installer starts, no prompt shows, and the runas verb is never used.
[TestClass]
public sealed class ElevatedApplierTests
{
    private const string CompatVariable = "__COMPAT_LAYER";

    // EA1
    [TestMethod]
    public void Not_Elevated_Does_Nothing()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = false;

        var outcome = new ElevatedApplier(h.Options, h.Host).Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.NotElevated, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.AreEqual(0, h.Host.Started.Count + h.Host.ShellStarted.Count, "the Hub is not started either");
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot), "nothing is created");
    }

    // EA4 to EA7
    [TestMethod]
    public void Elevated_Downgrade_Refused()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: "0.3.0"));

        Assert.AreEqual(UpdateFailure.Downgrade, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot), "no copy is made for a refused version");
    }

    [TestMethod]
    public void Elevated_Same_Version_Refused()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: "0.2.0"));

        Assert.AreEqual(UpdateFailure.SameVersion, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot));
    }

    [TestMethod]
    public void Elevated_Prerelease_Of_Same_Core_Refused()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0-beta.1");

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: "0.2.0"));

        Assert.AreEqual(UpdateFailure.Downgrade, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.0")]
    [DataRow("v0.1.0")]
    public void Elevated_Own_Version_Invalid_Refused(string own)
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: own));

        Assert.AreEqual(UpdateFailure.InvalidOptions, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
    }

    // EA8
    [TestMethod]
    [DataRow("other-name")]
    [DataRow("staging-root-itself")]
    [DataRow("traversal")]
    [DataRow("other-root")]
    [DataRow("relative")]
    [DataRow("installer-file")]
    [DataRow("uppercase-hex")]
    [DataRow("nested")]
    public void Elevated_Staging_Shape_Refused(string kind)
    {
        using var h = new UpdateApplierTests.Harness();
        var otherRoot = h.Dir.Sub("other-root");
        string folder;
        switch (kind)
        {
            case "other-name":
                folder = Path.Combine(h.Root, "not-a-guid");
                break;
            case "staging-root-itself":
                folder = h.Root;
                break;
            case "traversal":
                folder = Path.Combine(h.Root, "x", "..", "..", "other-root", Guid.NewGuid().ToString("N"));
                break;
            case "other-root":
                folder = Path.Combine(otherRoot, Guid.NewGuid().ToString("N"));
                break;
            case "relative":
                folder = Path.GetFileName(h.Folder);
                break;
            case "installer-file":
                folder = h.Installer;
                break;
            case "uppercase-hex":
                folder = Path.Combine(h.Root, Guid.NewGuid().ToString("N").ToUpperInvariant());
                break;
            default:
                folder = Path.Combine(h.Folder, Guid.NewGuid().ToString("N"));
                break;
        }

        if (kind is not ("relative" or "installer-file" or "staging-root-itself"))
        {
            Directory.CreateDirectory(folder);
            File.Copy(h.Installer, Path.Combine(folder, "installer.exe"));
            File.Copy(h.Receipt, Path.Combine(folder, "receipt.json"));
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest(stagingFolder: folder));

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot), "nothing is copied from a folder of the wrong shape");
    }

    // EA9
    [TestMethod]
    public void Elevated_Staging_Link_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var target = h.Dir.Sub("junction-target");
        File.Copy(h.Installer, Path.Combine(target, "installer.exe"));
        File.Copy(h.Receipt, Path.Combine(target, "receipt.json"));
        var link = Path.Combine(h.Root, Guid.NewGuid().ToString("N"));
        if (!Links.TryMakeJunction(link, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest(stagingFolder: link));

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot));
    }

    [TestMethod]
    public void Elevated_Staging_Entry_Link_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var target = h.Dir.Sub("entry-target");
        if (!Links.TryMakeJunction(Path.Combine(h.Folder, "extra"), target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot));
    }

    // EA10
    [TestMethod]
    public void Elevated_Oversize_Installer_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        using (var append = new FileStream(h.Installer, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            append.Write(new byte[1024]);
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerTooLarge, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        AssertNoCopyLeft(h);
    }

    // EA11
    [TestMethod]
    public void Elevated_Short_Installer_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        File.WriteAllBytes(h.Installer, h.Bytes[..2048]);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerSizeMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        AssertNoCopyLeft(h);
    }

    // EA12
    [TestMethod]
    public void Elevated_Source_Open_For_Write_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        using var writer = File.Open(h.Installer, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        AssertNoCopyLeft(h);
    }

    // EA13
    [TestMethod]
    public void Elevated_Tampered_Installer_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var bytes = File.ReadAllBytes(h.Installer);
        bytes[1000] ^= 0x01;
        File.WriteAllBytes(h.Installer, bytes);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched, "a tampered installer never runs elevated");
        AssertNoCopyLeft(h);
    }

    // EA14
    [TestMethod]
    public void Elevated_Forged_Signature_Refused()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");
        var attacker = SigningKey.Generate(h.Dir.Sub("attacker"));
        var staged = h.Dir.Write("forged.bin", h.Bytes);
        var forged = Signing.Sign(staged, attacker, UpdateApplierTests.Harness.Comment("0.2.0", Architectures.X64));
        var json = h.ReceiptJson();
        json["signature"] = Sig.Envelope(forged.Raw);
        File.WriteAllText(h.Receipt, json.ToJsonString());

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerSignatureInvalid, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        AssertNoCopyLeft(h);
    }

    // EA15
    [TestMethod]
    public void Elevated_Comment_Mismatch_Refused()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");
        // A genuine signature over the same bytes, but its signed version is 0.3.0 while the receipt says 0.2.0.
        var staged = h.Dir.Write("replayed.bin", h.Bytes);
        var replayed = Signing.Sign(staged, h.Key, UpdateApplierTests.Harness.Comment("0.3.0", Architectures.X64));
        var json = h.ReceiptJson();
        json["signature"] = Sig.Envelope(replayed.Raw);
        File.WriteAllText(h.Receipt, json.ToJsonString());

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerCommentMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        AssertNoCopyLeft(h);
    }

    [TestMethod]
    public void Elevated_Arch_Mismatch_Refused()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest(architecture: Architectures.Arm64));

        Assert.AreEqual(UpdateFailure.ArchitectureMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
    }

    // EA16
    [TestMethod]
    public void Elevated_Starts_Verified_Copy_Not_Source()
    {
        using var h = new UpdateApplierTests.Harness();
        // The source is replaced right before the start. Only the verified copy may be what runs.
        h.Host.BeforeLaunch = _ => File.WriteAllBytes(h.Installer, Payloads.Pattern(4096, seed: 9));

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual(1, h.Host.Runs.Count);
        Assert.AreNotEqual(h.Installer, h.Host.Runs[0].Path, "the source is never started");
        Assert.AreEqual(h.Sha, h.Host.SeenInstallerSha256, "the started bytes are the verified bytes");
    }

    // EA17
    [TestMethod]
    public void Elevated_Starts_Protected_Copy()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.InstallerStarted);
        Assert.IsFalse(outcome.ElevatedHelperStarted);
        var path = h.Host.Runs.Single().Path;
        Assert.AreEqual("installer.exe", Path.GetFileName(path));
        var folder = Path.GetDirectoryName(path)!;
        Assert.AreEqual(32, Path.GetFileName(folder).Length, "a 32 digit folder name");
        Assert.IsTrue(Path.GetFileName(folder).All(c => char.IsAsciiHexDigitLower(c)));
        Assert.AreEqual(h.ProtectedRoot, Path.GetDirectoryName(folder), "the copy is under <install dir>.upd");
        Assert.AreEqual(h.Sha, Payloads.Sha256Hex(File.ReadAllBytes(path)));
    }

    [TestMethod]
    public void Elevated_Fresh_Folder_Per_Run()
    {
        using var h = new UpdateApplierTests.Harness();
        Assert.AreEqual(UpdateFailure.None, h.Elevated.Apply(h.ElevatedRequest()).Failure);
        var first = h.Host.Runs[0].Path;
        // The first copy stays after the start (the installer may still run from it). The next run removes it as stale.
        Assert.AreEqual(UpdateFailure.None, h.Elevated.Apply(h.ElevatedRequest()).Failure);

        Assert.AreNotEqual(first, h.Host.Runs[1].Path);
        Assert.IsFalse(File.Exists(first), "the earlier copy was stale and is gone");
    }

    [TestMethod]
    public void Protected_Root_Is_Sibling_Of_Install_Dir()
    {
        var root = ProtectedStore.RootFor(@"C:\Program Files\Better Windows Config");

        Assert.AreEqual(@"C:\Program Files\Better Windows Config.upd", root);
        Assert.AreEqual(@"C:\Program Files", Path.GetDirectoryName(root));
        Assert.AreEqual(@"C:\Program Files\Better Windows Config.upd", ProtectedStore.RootFor(@"C:\Program Files\Better Windows Config\"));
    }

    // EA18
    [TestMethod]
    public void Elevated_Installer_Command_Line_Exact()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual($"/S /RELAUNCH /PARENTPID={Environment.ProcessId} /D={h.InstallDir}", h.Host.Runs.Single().Arguments);
        Assert.AreEqual(0, h.Host.ElevatedRuns.Count, "the elevated helper does not elevate again");
        Assert.IsTrue(h.InstallDir.Contains(' '), "the directory has a space, and it must stay unquoted after /D=");
    }

    // EA19
    [TestMethod]
    public void Elevated_Copy_Is_Locked_During_Start()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(h.Host.WriteOpenBlocked, "a writer must not open the verified copy while the helper holds it");
        Assert.IsTrue(h.Host.DeleteBlocked, "the verified copy cannot be deleted while the helper holds it");
    }

    // EA20
    [TestMethod]
    public void Protected_Root_Link_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var target = h.Dir.Sub("elsewhere");
        if (!Links.TryMakeJunction(h.ProtectedRoot, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length, "nothing is written through the link");
    }

    [TestMethod]
    public void Protected_Root_Is_File_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        File.WriteAllText(h.ProtectedRoot, "not a folder");

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
    }

    // EA21
    [TestMethod]
    public void Elevated_Install_Dir_Missing_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var request = h.ElevatedRequest() with { OwnDirectory = Path.Combine(h.Dir.Root, "no-such-install") };

        var outcome = h.Elevated.Apply(request);

        Assert.AreEqual(UpdateFailure.InstallDirUnsafe, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(request.OwnDirectory + ProtectedStore.FolderSuffix));
    }

    // EA22
    [TestMethod]
    public void Stale_Removal_Only_Hex_Folders()
    {
        using var h = new UpdateApplierTests.Harness();
        Directory.CreateDirectory(h.ProtectedRoot);
        var stale = Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "installer.exe"), "old");
        var upper = Directory.CreateDirectory(Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N").ToUpperInvariant())).FullName;
        var named = Directory.CreateDirectory(Path.Combine(h.ProtectedRoot, "notes")).FullName;
        var hexFile = Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N"));
        File.WriteAllText(hexFile, "a file with a hex name");
        var loose = Path.Combine(h.ProtectedRoot, "readme.txt");
        File.WriteAllText(loose, "keep");

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsFalse(Directory.Exists(stale), "a stale hex folder is removed");
        Assert.IsTrue(Directory.Exists(upper), "an uppercase name is not ours");
        Assert.IsTrue(Directory.Exists(named));
        Assert.IsTrue(File.Exists(hexFile), "only folders are removed");
        Assert.IsTrue(File.Exists(loose));
    }

    // EA23
    [TestMethod]
    public void Stale_Removal_Never_Follows_Links()
    {
        using var h = new UpdateApplierTests.Harness();
        Directory.CreateDirectory(h.ProtectedRoot);
        var outsideA = h.Dir.Sub("outside-a");
        var outsideB = h.Dir.Sub("outside-b");
        File.WriteAllText(Path.Combine(outsideA, "keep.txt"), "a");
        File.WriteAllText(Path.Combine(outsideB, "keep.txt"), "b");
        var linkedStale = Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N"));
        var realStale = Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realStale);
        if (!Links.TryMakeJunction(linkedStale, outsideA) || !Links.TryMakeJunction(Path.Combine(realStale, "inner"), outsideB))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(File.Exists(Path.Combine(outsideA, "keep.txt")), "a stale folder that is a junction is not followed");
        Assert.IsTrue(Directory.Exists(linkedStale), "a junction is left in place");
        Assert.IsTrue(File.Exists(Path.Combine(outsideB, "keep.txt")), "a junction inside a stale folder is not followed");
        Assert.IsFalse(Directory.Exists(realStale), "the stale folder itself is removed");
    }

    // EA24
    [TestMethod]
    public void Stale_Locked_Folder_Skipped()
    {
        using var h = new UpdateApplierTests.Harness();
        Directory.CreateDirectory(h.ProtectedRoot);
        var busy = Path.Combine(h.ProtectedRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(busy);
        using var held = new FileStream(Path.Combine(busy, "installer.exe"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(Directory.Exists(busy), "a folder with a file in use stays");
        Assert.AreEqual(1, h.Host.Runs.Count, "the update goes on in a new folder");
    }

    // EA25
    [TestMethod]
    public void Elevated_Installer_Start_Failure_Relaunches_Hub()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.InstallerStartThrows = true;

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerFailed, outcome.Failure, outcome.Detail);
        Assert.IsFalse(outcome.InstallerStarted);
        CollectionAssert.AreEqual(new[] { "--update-result", "InstallerFailed" }, h.Host.ShellStarted.Single().Args.ToArray());
        AssertNoCopyLeft(h);
    }

    // EA26
    [TestMethod]
    public void Elevated_Refusal_Removes_Copy()
    {
        using var h = new UpdateApplierTests.Harness();
        File.WriteAllBytes(h.Installer, Payloads.Pattern(4096, seed: 4));

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        AssertNoCopyLeft(h);
    }

    // EA27
    [TestMethod]
    public void Elevated_Refusal_Relaunches_Hub_At_Shell_Integrity()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: "0.3.0"));

        Assert.AreEqual(UpdateFailure.Downgrade, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.HubLaunched);
        var start = h.Host.ShellStarted.Single();
        Assert.AreEqual(Path.Combine(h.InstallDir, "BetterWindowsConfig.exe"), start.Exe);
        Assert.AreEqual(h.InstallDir, start.WorkDir);
        CollectionAssert.AreEqual(new[] { "--update-result", "Downgrade" }, start.Args.ToArray());
        Assert.AreEqual(0, h.Host.Started.Count, "an elevated helper never starts the Hub with the plain start");
    }

    // EA28
    [TestMethod]
    public void Elevated_Refusal_Cannot_DeElevate_Launches_Nothing()
    {
        using var h = new UpdateApplierTests.Harness(version: "0.2.0");
        h.Host.ShellResult = ShellStart.CannotDeElevate;

        var outcome = h.Elevated.Apply(h.ElevatedRequest(ownVersion: "0.3.0"));

        Assert.AreEqual(UpdateFailure.HubMissing, outcome.Failure, outcome.Detail);
        Assert.IsFalse(outcome.HubLaunched);
        StringAssert.Contains(outcome.Detail, "Downgrade", "the original reason stays in the detail");
        Assert.AreEqual(0, h.Host.Started.Count, "there is no fallback to an elevated Hub");
    }

    // EA29
    [TestMethod]
    public void Elevated_Compat_Layer_Removed()
    {
        using var h = new UpdateApplierTests.Harness();
        var previous = Environment.GetEnvironmentVariable(CompatVariable);
        Environment.SetEnvironmentVariable(CompatVariable, "RunAsInvoker");
        try
        {
            var outcome = h.Elevated.Apply(h.ElevatedRequest());

            Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
            Assert.IsNull(Environment.GetEnvironmentVariable(CompatVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompatVariable, previous);
        }
    }

    // EA30
    [TestMethod]
    public void Elevated_Unexpected_Relaunches_Hub()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.BeforeLaunch = _ => throw new NotSupportedException("simulated host failure");

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.Unexpected, outcome.Failure, outcome.Detail);
        Assert.IsFalse(outcome.InstallerStarted);
        CollectionAssert.AreEqual(new[] { "--update-result", "Unexpected" }, h.Host.ShellStarted.Single().Args.ToArray());
        AssertNoCopyLeft(h);
    }

    // EA32
    [TestMethod]
    public void Elevated_Outside_Program_Files_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options with { ProgramFilesRoot = h.Dir.Sub("Program Files") }, h.Host);

        var outcome = applier.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallDirUnsafe, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot));
    }

    [TestMethod]
    public void Elevated_Inside_Program_Files_Accepted()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options with { ProgramFilesRoot = h.Dir.Root }, h.Host);

        var outcome = applier.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
    }

    // EE1
    [TestMethod]
    public void Non_Elevated_With_E2E_Root_Proceeds()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = false;
        var e2eRoot = Path.Combine(h.Dir.Root, "BWC-Protected");
        var request = h.ElevatedRequest() with { E2EProtectedRoot = e2eRoot };

        var outcome = new ElevatedApplier(h.Options, h.Host).Apply(request);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual(e2eRoot, Path.GetDirectoryName(Path.GetDirectoryName(h.Host.Runs.Single().Path)));
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot), "the sibling folder is not used when the E2E root is given");
    }

    // EE2
    [TestMethod]
    [DataRow("temp-itself")]
    [DataRow("staging-root")]
    [DataRow("inside-staging")]
    [DataRow("outside-temp")]
    [DataRow("relative")]
    [DataRow("traversal")]
    public void Non_Elevated_With_Bad_E2E_Root_Refused(string kind)
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = false;
        var temp = h.Dir.Root;
        var root = kind switch
        {
            "temp-itself" => temp,
            "staging-root" => h.Root,
            "inside-staging" => Path.Combine(h.Root, "deeper"),
            "outside-temp" => Path.Combine(Path.GetTempPath(), "bwc-not-this-temp-" + Guid.NewGuid().ToString("N")),
            "relative" => "BWC-Protected",
            _ => Path.Combine(temp, "a", "..", "..", "escaped"),
        };
        var request = h.ElevatedRequest() with { E2EProtectedRoot = root };

        var outcome = new ElevatedApplier(h.Options, h.Host).Apply(request);

        Assert.AreEqual(UpdateFailure.InvalidOptions, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched);
    }

    [TestMethod]
    [DataRow(@"C:\T\run\x", @"C:\T\run", @"C:\T\run\BWC-Update", true)]
    [DataRow(@"C:\T\run\x\y", @"C:\T\run", @"C:\T\run\BWC-Update", true)]
    [DataRow(@"c:\t\RUN\X", @"C:\T\run", @"C:\T\run\BWC-Update", true)]
    [DataRow(@"C:\T\run", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"C:\T\run\BWC-Update", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"C:\T\run\BWC-Update\x", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"C:\T\run2\x", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"C:\T\run\..\x", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"x\y", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"", @"C:\T\run", @"C:\T\run\BWC-Update", false)]
    [DataRow(@"C:\T\run\x", @"", @"C:\T\run\BWC-Update", false)]
    public void E2E_Root_Rule(string candidate, string temp, string staging, bool accepted)
    {
        Assert.AreEqual(accepted, ProtectedRootRule.AcceptE2ERoot(candidate, temp, staging, out var full), candidate);
        Assert.AreEqual(accepted, full is not null);
    }

    private static void AssertNoCopyLeft(UpdateApplierTests.Harness h)
    {
        if (!Directory.Exists(h.ProtectedRoot))
        {
            return;
        }

        Assert.AreEqual(0, Directory.GetDirectories(h.ProtectedRoot).Length, "a refused update leaves no copy in the protected root");
    }
}
