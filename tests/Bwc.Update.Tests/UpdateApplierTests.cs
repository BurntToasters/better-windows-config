using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// Helper-mode cases from docs/failure-modes/update-verifier.md (sections F, G, H). The host is
// a fake: no installer is ever started by these tests.
[TestClass]
public sealed class UpdateApplierTests
{
    /// <summary>One verified-looking update in a staging folder, plus a fake host.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(string version = "0.2.0", string architecture = Architectures.X64)
        {
            Dir = new TempDir();
            Key = SigningKey.Generate(Dir.Sub("key"));
            Root = Dir.Sub("staging");
            Folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            Installer = Path.Combine(Folder, "installer.exe");
            Receipt = Path.Combine(Folder, "receipt.json");
            InstallDir = Dir.Sub("Install Dir With Space");
            Version = version;
            Architecture = architecture;

            Bytes = Payloads.Pattern(4096);
            Sha = Payloads.Sha256Hex(Bytes);
            var staged = Dir.Write("signing-" + Guid.NewGuid().ToString("N") + ".bin", Bytes);
            Signed = Signing.Sign(staged, Key, Comment(version, architecture));
            File.Copy(staged, Installer);
            WriteValidReceipt();
            Host = new FakeHost();
        }

        public TempDir Dir { get; }

        public SigningKey Key { get; }

        public string Root { get; }

        public string Folder { get; }

        public string Installer { get; }

        public string Receipt { get; }

        public string InstallDir { get; }

        public string Version { get; }

        public string Architecture { get; }

        public byte[] Bytes { get; }

        public string Sha { get; }

        public SignedFile Signed { get; }

        public FakeHost Host { get; }

        public UpdateOptions Options => new()
        {
            TrustedPublicKeys = [Key.PublicKeyText],
            InstallerRunTimeout = TimeSpan.FromMinutes(30),
        };

        public UpdateApplier Applier => new(Options, Host);

        public static string Comment(string version, string architecture) =>
            $"timestamp:1794102823\tfile:installer.exe\tversion:{version}\tarch:{architecture}";

        public JsonObject ReceiptJson() => new()
        {
            ["schema"] = 1,
            ["version"] = Version,
            ["channel"] = "beta",
            ["architecture"] = Architecture,
            ["size"] = Bytes.Length,
            ["sha256"] = Sha,
            ["signature"] = Sig.Envelope(Signed.Raw),
            ["trustedComment"] = Comment(Version, Architecture),
        };

        public void WriteValidReceipt() =>
            File.WriteAllText(Receipt, ReceiptJson().ToJsonString());

        public ApplyRequest Request(
            string current = "0.1.0",
            string? architecture = null,
            IReadOnlyList<int>? pids = null,
            TimeSpan? wait = null,
            IReadOnlyList<string>? switches = null) =>
            new(Installer, Receipt, Root, current, architecture ?? Architecture, InstallDir, pids ?? [], wait ?? TimeSpan.FromSeconds(2), switches ?? []);

        public void Dispose() => Dir.Dispose();
    }

    [TestMethod]
    public async Task Apply_Path_Outside_Root_Refused()
    {
        using var h = new Harness();
        var outside = h.Dir.Sub("outside");
        var installer = Path.Combine(outside, "installer.exe");
        var receipt = Path.Combine(outside, "receipt.json");
        File.Copy(h.Installer, installer);
        File.Copy(h.Receipt, receipt);

        var request = h.Request() with { InstallerPath = installer, ReceiptPath = receipt };
        var outcome = await h.Applier.ApplyAsync(request, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Path_Traversal_Refused()
    {
        using var h = new Harness();
        var escaped = Path.Combine(h.Root, "..", "escape", Guid.NewGuid().ToString("N"), "installer.exe");

        var outcome = await h.Applier.ApplyAsync(h.Request() with { InstallerPath = escaped }, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Wrong_File_Name_Refused()
    {
        using var h = new Harness();
        var renamed = Path.Combine(h.Folder, "setup.exe");
        File.Copy(h.Installer, renamed);

        var outcome = await h.Applier.ApplyAsync(h.Request() with { InstallerPath = renamed }, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Folder_Name_Refused()
    {
        using var h = new Harness();
        var folder = Path.Combine(h.Root, "not-a-guid");
        Directory.CreateDirectory(folder);
        File.Copy(h.Installer, Path.Combine(folder, "installer.exe"));
        File.Copy(h.Receipt, Path.Combine(folder, "receipt.json"));

        var request = h.Request() with
        {
            InstallerPath = Path.Combine(folder, "installer.exe"),
            ReceiptPath = Path.Combine(folder, "receipt.json"),
        };
        var outcome = await h.Applier.ApplyAsync(request, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Reparse_Point_Refused()
    {
        using var h = new Harness();
        var target = h.Dir.Sub("junction-target");
        File.Copy(h.Installer, Path.Combine(target, "installer.exe"));
        File.Copy(h.Receipt, Path.Combine(target, "receipt.json"));
        var link = Path.Combine(h.Root, Guid.NewGuid().ToString("N"));
        MakeJunction(link, target);

        var request = h.Request() with
        {
            InstallerPath = Path.Combine(link, "installer.exe"),
            ReceiptPath = Path.Combine(link, "receipt.json"),
        };
        var outcome = await h.Applier.ApplyAsync(request, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Receipt_Elsewhere_Refused()
    {
        using var h = new Harness();
        var elsewhere = h.Dir.Write("elsewhere.json", File.ReadAllBytes(h.Receipt));

        var outcome = await h.Applier.ApplyAsync(h.Request() with { ReceiptPath = elsewhere }, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("empty-object")]
    [DataRow("schema-2")]
    [DataRow("unknown-channel")]
    [DataRow("oversize")]
    public async Task Apply_Receipt_Invalid_Refused(string kind)
    {
        using var h = new Harness();
        string text;
        if (kind == "not-json")
        {
            text = "hello";
        }
        else if (kind == "empty-object")
        {
            text = "{}";
        }
        else if (kind == "oversize")
        {
            text = new string(' ', 20 * 1024) + h.ReceiptJson().ToJsonString();
        }
        else
        {
            var json = h.ReceiptJson();
            if (kind == "schema-2")
            {
                json["schema"] = 2;
            }
            else
            {
                json["channel"] = "nightly";
            }

            text = json.ToJsonString();
        }

        File.WriteAllText(h.Receipt, text);
        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.ReceiptInvalid, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Arch_Mismatch_Refused()
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(architecture: Architectures.Arm64), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.ArchitectureMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Downgrade_Refused()
    {
        using var h = new Harness(version: "0.2.0");

        var outcome = await h.Applier.ApplyAsync(h.Request(current: "0.3.0"), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.Downgrade, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Same_Version_Refused()
    {
        using var h = new Harness(version: "0.2.0");

        var outcome = await h.Applier.ApplyAsync(h.Request(current: "0.2.0"), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.SameVersion, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.0")]
    public async Task Apply_Missing_Current_Version_Usage(string current)
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(current: current), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InvalidOptions, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Modified_Installer_Refused()
    {
        using var h = new Harness();
        var bytes = File.ReadAllBytes(h.Installer);
        bytes[1000] ^= 0x01;
        File.WriteAllBytes(h.Installer, bytes);

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Replayed_Installer_Refused()
    {
        using var h = new Harness(version: "0.2.0");
        // A genuinely signed 0.3.0 installer is put where the 0.2.0 installer was; the receipt still says 0.2.0.
        var staged = h.Dir.Write("newer.bin", Payloads.Pattern(4096, seed: 3));
        Signing.Sign(staged, h.Key, SigningKitComment("0.3.0"));
        File.Copy(staged, h.Installer, overwrite: true);

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Apply_Swapped_During_Wait_Refused()
    {
        using var h = new Harness();
        var swapped = Payloads.Pattern(4096, seed: 5);
        var calls = 0;
        h.Host.Live = _ =>
        {
            calls++;
            if (calls == 1)
            {
                File.WriteAllBytes(h.Installer, swapped);
                return [4321];
            }

            return [];
        };

        var outcome = await h.Applier.ApplyAsync(h.Request(pids: [4321]), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Verified_Installer_Is_Locked()
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(h.Host.WriteOpenBlocked, "a writer must not open the verified file while the helper holds it");
        Assert.IsTrue(h.Host.DeleteBlocked, "a delete must not succeed while the helper holds the verified file");
    }

    [TestMethod]
    public async Task Launch_Sees_Verified_Bytes()
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual(h.Sha, h.Host.SeenInstallerSha256);
    }

    [TestMethod]
    public async Task Apply_Forged_Receipt_Refused()
    {
        using var h = new Harness(version: "0.2.0");
        var attacker = SigningKey.Generate(h.Dir.Sub("attacker"));
        var forged = Payloads.Pattern(4096, seed: 7);
        var staged = h.Dir.Write("forged.bin", forged);
        var forgedSigned = Signing.Sign(staged, attacker, SigningKitComment("0.3.0"));
        File.Copy(staged, h.Installer, overwrite: true);

        var json = new JsonObject
        {
            ["schema"] = 1,
            ["version"] = "0.3.0",
            ["channel"] = "beta",
            ["architecture"] = Architectures.X64,
            ["size"] = forged.Length,
            ["sha256"] = Payloads.Sha256Hex(forged),
            ["signature"] = Sig.Envelope(forgedSigned.Raw),
            ["trustedComment"] = SigningKitComment("0.3.0"),
        };
        File.WriteAllText(h.Receipt, json.ToJsonString());

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerSignatureInvalid, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public void Helper_Relocates_Out_Of_Install_Dir()
    {
        using var h = new Harness();
        var installDir = h.InstallDir;
        var helperFolder = Path.Combine(installDir, "helper");
        Directory.CreateDirectory(helperFolder);
        File.WriteAllText(Path.Combine(helperFolder, "BwcUpdate.dll"), "binary");
        var sub = Path.Combine(helperFolder, "runtimes");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "native.dll"), "native");

        Assert.IsTrue(HelperRelocation.ShouldRelocate(helperFolder, installDir, relocated: false));
        Assert.IsFalse(HelperRelocation.ShouldRelocate(h.Dir.Sub("elsewhere"), installDir, relocated: false));

        var copy = h.Dir.Sub("copy");
        HelperRelocation.CopyTree(helperFolder, copy);
        Assert.IsTrue(File.Exists(Path.Combine(copy, "BwcUpdate.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(copy, "runtimes", "native.dll")));
    }

    [TestMethod]
    public void Relocated_Copy_Does_Not_Relocate_Again()
    {
        using var h = new Harness();
        Assert.IsFalse(HelperRelocation.ShouldRelocate(h.InstallDir, h.InstallDir, relocated: true));
    }

    [TestMethod]
    public async Task Apply_Still_Running_Refused()
    {
        using var h = new Harness();
        h.Host.Live = _ => [1234];

        var outcome = await h.Applier.ApplyAsync(h.Request(pids: [1234], wait: TimeSpan.FromMilliseconds(300)), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.ProcessesStillRunning, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
        Assert.AreEqual(0, h.Host.Started.Count, "the Hub may still be running, so it is not relaunched");
    }

    [TestMethod]
    public async Task Apply_Waits_Then_Installs()
    {
        using var h = new Harness();
        var calls = 0;
        h.Host.Live = _ => ++calls <= 2 ? [1234] : [];

        var outcome = await h.Applier.ApplyAsync(h.Request(pids: [1234]), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual(1, h.Host.Runs.Count);
        Assert.IsTrue(calls >= 3, "the helper must poll until the waited processes are gone");
    }

    [TestMethod]
    [DataRow("relative")]
    [DataRow("quote")]
    [DataRow("newline")]
    [DataRow("trailing-backslash")]
    public async Task Install_Dir_Unsafe_Refused(string kind)
    {
        using var h = new Harness();
        var installDir = kind switch
        {
            "relative" => "relative\\dir",
            "quote" => h.Dir.Root + "\\bad\"dir",
            "newline" => h.Dir.Root + "\\bad\ndir",
            _ => h.Dir.Root + "\\",
        };

        var outcome = await h.Applier.ApplyAsync(h.Request() with { InstallDir = installDir }, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallDirUnsafe, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public async Task Install_Dir_Missing_Refused()
    {
        using var h = new Harness();
        var missing = Path.Combine(h.Dir.Root, "does-not-exist");

        var outcome = await h.Applier.ApplyAsync(h.Request() with { InstallDir = missing }, CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallDirUnsafe, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    [DataRow("/ALLUSERS", true)]
    [DataRow("/CURRENTUSER", true)]
    [DataRow("/S", false)]
    [DataRow("/D", false)]
    [DataRow("/allusers", false)]
    [DataRow("/ALLUSERS=1", false)]
    [DataRow("/ALL USERS", false)]
    [DataRow("/X\"", false)]
    [DataRow("ALLUSERS", false)]
    public async Task Installer_Arg_Validation(string switchText, bool accepted)
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(switches: [switchText]), CancellationToken.None);

        if (accepted)
        {
            Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        }
        else
        {
            Assert.AreEqual(UpdateFailure.InstallerArgumentRejected, outcome.Failure, outcome.Detail);
            Assert.AreEqual(0, h.Host.Runs.Count);
        }
    }

    [TestMethod]
    public async Task Installer_Command_Line_Exact()
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(switches: ["/ALLUSERS"]), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.AreEqual($"/S /ALLUSERS /D={h.InstallDir}", h.Host.Runs[0].Arguments);
        Assert.IsTrue(h.InstallDir.Contains(' '), "the directory has a space, and it must stay unquoted after /D=");
    }

    [TestMethod]
    public async Task Installer_Failure_Still_Relaunches_Hub()
    {
        using var h = new Harness();
        h.Host.InstallerExitCode = 3;

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerFailed, outcome.Failure, outcome.Detail);
        Assert.AreEqual(3, outcome.InstallerExitCode);
        Assert.AreEqual(1, h.Host.Started.Count, "the Hub comes back even when the installer fails");
    }

    [TestMethod]
    public async Task Installer_Timeout_Reported()
    {
        using var h = new Harness();
        h.Host.InstallerDelay = TimeSpan.FromSeconds(5);
        var options = h.Options with { InstallerRunTimeout = TimeSpan.FromMilliseconds(200) };

        var outcome = await new UpdateApplier(options, h.Host).ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.InstallerTimedOut, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Started.Count, "the installer may still be writing, so the Hub is not started");
    }

    [TestMethod]
    public async Task Hub_Missing_Reported()
    {
        using var h = new Harness();
        h.Host.StartSucceeds = false;

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.HubMissing, outcome.Failure, outcome.Detail);
        Assert.IsFalse(outcome.HubLaunched);
    }

    [TestMethod]
    public async Task Hub_Relaunch_Uses_Install_Dir()
    {
        using var h = new Harness();

        var outcome = await h.Applier.ApplyAsync(h.Request(), CancellationToken.None);

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        Assert.IsTrue(outcome.HubLaunched);
        var started = h.Host.Started.Single();
        Assert.AreEqual(Path.Combine(h.InstallDir, "BetterWindowsConfig.exe"), started.Exe);
        Assert.AreEqual(h.InstallDir, started.WorkDir);
    }

    private static string SigningKitComment(string version) => Harness.Comment(version, Architectures.X64);

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start cmd");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Inconclusive("mklink /J is not available here");
        }
    }
}
