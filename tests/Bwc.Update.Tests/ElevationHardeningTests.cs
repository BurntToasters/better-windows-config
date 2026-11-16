using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Update.Tests;

// The I2b hardening cases of docs/failure-modes/update-elevation.md (section HD): protected folders and the copy handle
// (HP), the installer's temp folder (HT), environment (HE), --close-running privilege (HC), link-safe staging cleanup
// (HK),
// and the installer comment (HI). The host is a fake: no installer starts, no prompt shows, and nothing is elevated.
[TestClass]
public sealed class ElevationHardeningTests
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;

    // ---- HP: principals and the rule matrix -------------------------------------------------------------------------

    [TestMethod]
    public void Ship_Principals_Are_System_And_Administrators()
    {
        var acl = ProtectedAcl.Ship();

        Assert.AreEqual(Administrators, acl.Owner);
        CollectionAssert.AreEquivalent(new[] { System_, Administrators }, acl.FullControl.ToArray());

        var security = acl.NewSecurity();
        Assert.IsTrue(security.AreAccessRulesProtected, "no inheritance from the parent");
        Assert.AreEqual(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        CollectionAssert.AreEquivalent(
            new[] { System_, Administrators },
            rules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToArray());
        Assert.IsTrue(rules.All(rule => rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == FileSystemRights.FullControl));
        Assert.IsNull(acl.Inspect(security), "a folder made by the rule passes the rule");
    }

    private static DirectorySecurity Security(SecurityIdentifier owner, params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var rule in rules)
        {
            security.AddAccessRule(rule);
        }

        return security;
    }

    private static FileSystemAccessRule Allow(SecurityIdentifier sid, FileSystemRights rights, PropagationFlags propagation = PropagationFlags.None) =>
        new(sid, rights, propagation == PropagationFlags.InheritOnly ? InheritanceFlags.ObjectInherit : InheritanceFlags.None, propagation, AccessControlType.Allow);

    private static FileSystemAccessRule Deny(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

    [TestMethod]
    public void Acl_Rule_Matrix()
    {
        var ship = ProtectedAcl.Ship();
        var full = FileSystemRights.FullControl;
        var cases = new (string Name, DirectorySecurity Security, bool Accepted)[]
        {
            ("principals only", Security(Administrators, Allow(System_, full), Allow(Administrators, full)), true),
            ("owner SYSTEM", Security(System_, Allow(System_, full)), true),
            ("owner Users", Security(Users, Allow(System_, full), Allow(Administrators, full)), false),
            ("owner Everyone", Security(Everyone, Allow(System_, full)), false),
            ("TrustedInstaller writes", Security(Administrators, Allow(TrustedInstaller, full), Allow(Administrators, full)), true),
            ("Users read and execute", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.ReadAndExecute)), true),
            ("Users modify", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.Modify)), false),
            ("Users write data", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.WriteData)), false),
            ("Users append data", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.AppendData)), false),
            ("Users write attributes", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.WriteAttributes)), false),
            ("Users delete", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.Delete)), false),
            ("Users delete children", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.DeleteSubdirectoriesAndFiles)), false),
            ("Users change permissions", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.ChangePermissions)), false),
            ("Users take ownership", Security(Administrators, Allow(Administrators, full), Allow(Users, FileSystemRights.TakeOwnership)), false),
            ("Everyone full control", Security(Administrators, Allow(Administrators, full), Allow(Everyone, full)), false),
            ("Users full control, inherit only", Security(Administrators, Allow(Administrators, full), Allow(Users, full, PropagationFlags.InheritOnly)), true),
            ("Users write, denied", Security(Administrators, Allow(Administrators, full), Deny(Users, FileSystemRights.Write)), true),
            ("current user writes", Security(Administrators, Allow(Administrators, full), Allow(CurrentUser, FileSystemRights.Modify)), false),
        };

        foreach (var (name, security, accepted) in cases)
        {
            var reason = ship.Inspect(security);
            Assert.AreEqual(accepted, reason is null, $"{name}: {reason ?? "accepted"}");
        }
    }

    [TestMethod]
    public void Acl_Rule_Matrix_For_Current_User()
    {
        var acl = ProtectedAcl.ForCurrentUser();
        var full = FileSystemRights.FullControl;

        Assert.IsNull(acl.Inspect(Security(CurrentUser, Allow(CurrentUser, full), Allow(System_, full))));
        Assert.IsNull(acl.Inspect(Security(System_, Allow(CurrentUser, full))));
        Assert.IsNotNull(acl.Inspect(Security(Users, Allow(CurrentUser, full))), "a foreign owner");
        Assert.IsNotNull(acl.Inspect(Security(CurrentUser, Allow(CurrentUser, full), Allow(Users, FileSystemRights.Modify))), "a foreign writer");
        Assert.IsNull(acl.Inspect(Security(CurrentUser, Allow(CurrentUser, full), Allow(Users, FileSystemRights.ReadAndExecute))));
    }

    // ---- HP: real folders ---------------------------------------------------------------------------------------------

    private static void AssertProtected(string folder, ProtectedAcl acl)
    {
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, $"{folder}: inheritance is off");
        Assert.AreEqual(acl.Owner, security.GetOwner(typeof(SecurityIdentifier)), $"{folder}: owner");
        var explicitRules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var inherited = security.GetAccessRules(false, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.AreEqual(0, inherited.Count, $"{folder}: nothing is inherited");
        CollectionAssert.AreEquivalent(
            acl.FullControl.ToArray(),
            explicitRules.Select(rule => (SecurityIdentifier)rule.IdentityReference).ToArray(),
            $"{folder}: only the principals have an entry");
        Assert.IsNull(acl.Inspect(security), $"{folder}: passes the rule");
    }

    [TestMethod]
    public void Root_Is_Created_Protected()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        AssertProtected(h.ProtectedRoot, ProtectedAcl.ForCurrentUser());
    }

    [TestMethod]
    public void Copy_Folder_Is_Created_Protected()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        var copyFolder = Path.GetDirectoryName(h.Host.Runs.Single().Path)!;
        AssertProtected(copyFolder, ProtectedAcl.ForCurrentUser());
    }

    [TestMethod]
    public void Existing_Protected_Root_Is_Reused()
    {
        using var h = new UpdateApplierTests.Harness();
        Assert.AreEqual(UpdateFailure.None, h.Elevated.Apply(h.ElevatedRequest()).Failure);
        var created = new DirectoryInfo(h.ProtectedRoot).CreationTimeUtc;

        var second = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, second.Failure, second.Detail);
        Assert.AreEqual(created, new DirectoryInfo(h.ProtectedRoot).CreationTimeUtc, "the same root, not a new one");
    }

    [TestMethod]
    public void Existing_Root_With_Foreign_Write_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        var root = Directory.CreateDirectory(h.ProtectedRoot);
        var security = root.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        root.SetAccessControl(security);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(h.ProtectedRoot).Length, "nothing is copied into a root that others can write");
    }

    [TestMethod]
    public void Existing_Root_Read_Only_Entry_Accepted()
    {
        using var h = new UpdateApplierTests.Harness();
        var root = Directory.CreateDirectory(h.ProtectedRoot);
        var security = root.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        root.SetAccessControl(security);

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
    }

    [TestMethod]
    public void Existing_Root_With_Foreign_Owner_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        Directory.CreateDirectory(h.ProtectedRoot);
        var owner = new DirectoryInfo(h.ProtectedRoot).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (Administrators.Equals(owner) || System_.Equals(owner))
        {
            Assert.Inconclusive("this process creates folders owned by an administrator, so none is foreign to the ship rule");
        }

        // The ship rule, applied to a folder that the current user made: the owner is neither SYSTEM nor Administrators.
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options with { ProtectedAcl = ProtectedAcl.Ship() }, h.Host);

        var outcome = applier.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
    }

    [TestMethod]
    public void Root_Acl_Failure_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options with { ProtectedAcl = ProtectedAcl.Ship() }, h.Host);

        var outcome = applier.Apply(h.ElevatedRequest());

        if (outcome.Failure == UpdateFailure.None)
        {
            Assert.Inconclusive("this process may set an administrator as owner, so the ship rule could be applied");
        }

        // An unelevated process cannot make an administrator-owned folder. There is no fallback to an inherited folder.
        Assert.IsTrue(outcome.Failure is UpdateFailure.PathRejected or UpdateFailure.Io, outcome.Failure + ": " + outcome.Detail);
        Assert.AreEqual(0, h.Host.Runs.Count);
        Assert.IsFalse(Directory.Exists(h.ProtectedRoot), "no unprotected folder is left behind");
    }

    // ---- HP7: the copy handle ---------------------------------------------------------------------------------------

    [TestMethod]
    public void Copy_Handles_Are_Held_Through_Start()
    {
        using var h = new UpdateApplierTests.Harness();
        var probes = new List<string>();
        h.Host.BeforeLaunch = path =>
        {
            // Readers may coexist (the installer starts from this file); a writer or a delete may not.
            try
            {
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                probes.Add("read-ok");
            }
            catch (IOException)
            {
                probes.Add("read-blocked");
            }
        };

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        CollectionAssert.AreEqual(new[] { "read-ok" }, probes, "a reader can open the held file");
        Assert.IsTrue(h.Host.WriteOpenBlocked, "a writer cannot open it while the helper holds it");
        Assert.IsTrue(h.Host.DeleteBlocked, "it cannot be deleted while the helper holds it");
    }

    [TestMethod]
    public void Copy_Changed_Between_Handles_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options, h.Host)
        {
            // The first reader lets a writer in (it shares read and write, as it must while the writer is open). The second
            // reader is opened after this and must see the change.
            BetweenHandles = path =>
            {
                using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                writer.Position = 10;
                writer.WriteByte(0xEE);
            },
        };

        var outcome = applier.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerHashMismatch, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched, "a copy that changed after it was verified is never started");
        AssertNoCopyLeft(h);
    }

    [TestMethod]
    public void Copy_Held_By_A_Writer_At_The_Second_Handle_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        FileStream? intruder = null;
        var applier = new ElevatedApplier(h.Options, h.Host)
        {
            BetweenHandles = path => intruder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite),
        };

        try
        {
            var outcome = applier.Apply(h.ElevatedRequest());

            Assert.AreEqual(UpdateFailure.PathRejected, outcome.Failure, outcome.Detail);
            Assert.AreEqual(0, h.Host.Launched, "a writer that still holds the file blocks the start");
        }
        finally
        {
            intruder?.Dispose();
        }
    }

    // ---- HT: the temp folder ------------------------------------------------------------------------------------------

    [TestMethod]
    public void Elevated_Installer_Gets_Protected_Temp()
    {
        using var h = new UpdateApplierTests.Harness();

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
        var (temp, existed) = h.Host.TempFolders.Single();
        var copyFolder = Path.GetDirectoryName(h.Host.Runs.Single().Path)!;
        Assert.IsTrue(existed, "the folder exists when the installer starts");
        Assert.AreEqual(Path.Combine(copyFolder, "tmp"), temp);
        AssertProtected(temp, ProtectedAcl.ForCurrentUser());
    }

    [TestMethod]
    public void Elevated_Temp_Folder_Failure_Refused()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        var applier = new ElevatedApplier(h.Options, h.Host)
        {
            // The folder is taken before the helper makes it, so it cannot be created as new.
            AfterCopyFolder = copyFolder => Directory.CreateDirectory(Path.Combine(copyFolder, "tmp")),
        };

        var outcome = applier.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerFailed, outcome.Failure, outcome.Detail);
        Assert.AreEqual(0, h.Host.Launched, "nothing starts with the user's temp folder as a fallback");
        Assert.AreEqual(0, h.Host.TempFolders.Count);
        AssertNoCopyLeft(h);
    }

    [TestMethod]
    public void Elevated_Refusal_Removes_Copy()
    {
        using var h = new UpdateApplierTests.Harness();
        h.Host.Elevated = true;
        h.Host.InstallerStartThrows = true;

        var outcome = h.Elevated.Apply(h.ElevatedRequest());

        Assert.AreEqual(UpdateFailure.InstallerFailed, outcome.Failure, outcome.Detail);
        AssertNoCopyLeft(h);
    }

    private static void AssertNoCopyLeft(UpdateApplierTests.Harness h) =>
        Assert.AreEqual(0, Directory.GetDirectories(h.ProtectedRoot).Length, "a refused update leaves no copy or temp folder");

    // ---- HE: environment ---------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("DOTNET_STARTUP_HOOKS", true)]
    [DataRow("dotnet_startup_hooks", true)]
    [DataRow("DOTNET_gcServer", true)]
    [DataRow("COREHOST_TRACE", true)]
    [DataRow("CORECLR_ENABLE_PROFILING", true)]
    [DataRow("CORECLR_PROFILER_PATH", true)]
    [DataRow("COMPlus_EnableDiagnostics", true)]
    [DataRow("complus_x", true)]
    [DataRow("DOTNETX", false)]
    [DataRow("MYDOTNET_X", false)]
    [DataRow("DOTNET_", false)]
    [DataRow("PATH", false)]
    [DataRow("TEMP", false)]
    public void Runtime_Variable_Rule(string name, bool runtime)
    {
        Assert.AreEqual(runtime, ChildEnvironment.IsRuntimeVariable(name), name);
    }

    [TestMethod]
    public void Elevated_Installer_Environment_Scrubbed()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = @"C:\evil.dll",
            ["coreclr_profiler"] = "{x}",
            ["COMPlus_Y"] = "1",
            ["COREHOST_TRACE"] = "1",
            ["__COMPAT_LAYER"] = "RunAsInvoker",
            ["TEMP"] = @"C:\Users\x\AppData\Local\Temp",
            ["TMP"] = @"C:\Users\x\AppData\Local\Temp",
            ["PATH"] = @"C:\Windows",
        };

        ChildEnvironment.ForInstaller(environment, @"C:\Program Files\App.upd\abc\tmp");

        CollectionAssert.AreEquivalent(new[] { "TEMP", "TMP", "PATH" }, environment.Keys.ToArray());
        Assert.AreEqual(@"C:\Program Files\App.upd\abc\tmp", environment["TEMP"]);
        Assert.AreEqual(@"C:\Program Files\App.upd\abc\tmp", environment["TMP"]);
        Assert.AreEqual(@"C:\Windows", environment["PATH"]);
    }

    [TestMethod]
    public void Elevated_Scrubs_Runtime_Variables_From_Process()
    {
        const string Name = "DOTNET_BWC_TEST_SCRUB";
        Environment.SetEnvironmentVariable(Name, "1");
        IReadOnlyList<KeyValuePair<string, string>> removed = [];
        try
        {
            removed = ChildEnvironment.RemoveRuntimeVariables();

            Assert.IsNull(Environment.GetEnvironmentVariable(Name));
            Assert.IsTrue(removed.Any(pair => pair.Key == Name && pair.Value == "1"), "what was removed is returned");
        }
        finally
        {
            ChildEnvironment.Restore(removed);
        }

        Assert.AreEqual("1", Environment.GetEnvironmentVariable(Name), "Restore puts it back");
        Environment.SetEnvironmentVariable(Name, null);
    }

    [TestMethod]
    public void Medium_Apply_Scrubs_Runtime_Variables_Before_Elevated_Start()
    {
        const string Name = "DOTNET_BWC_TEST_APPLY";
        using var h = new UpdateApplierTests.Harness();
        Environment.SetEnvironmentVariable(Name, "1");
        try
        {
            var outcome = h.Applier.ApplyAsync(h.Request(), CancellationToken.None).GetAwaiter().GetResult();

            Assert.AreEqual(UpdateFailure.None, outcome.Failure, outcome.Detail);
            var seen = h.Host.RuntimeVariablesAtElevatedStart.Single();
            CollectionAssert.DoesNotContain(seen, Name, "the elevated helper does not inherit it");
            Assert.AreEqual("1", Environment.GetEnvironmentVariable(Name), "the medium helper has it back after the start");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    // ---- HC: --close-running and SeDebugPrivilege --------------------------------------------------------------------

    private sealed class Recorder : IDisposable
    {
        public bool Enabled { get; private set; } = true;

        public void Dispose() => Enabled = false;
    }

    private sealed class WatchingSource(Func<bool> enabled, List<bool> seen) : IProcessSource
    {
        public IReadOnlyList<ProcessEntry> ByName(string processName)
        {
            seen.Add(enabled());
            return [];
        }
    }

    private sealed class ThrowingSource : IProcessSource
    {
        public IReadOnlyList<ProcessEntry> ByName(string processName) => throw new InvalidOperationException("scan failed");
    }

    [TestMethod]
    public void CloseRunning_Enables_Debug_Privilege_Around_Scan()
    {
        using var dir = new TempDir();
        var recorder = new Recorder();
        var seen = new List<bool>();

        var code = ProcessCommands.CloseRunningScoped(
            dir.Sub("install"), [], 1, TextWriter.Null, new WatchingSource(() => recorder.Enabled, seen), ProcessScan.CurrentSessionId(), _ => { },
            () => true, () => recorder);

        Assert.AreEqual(0, code);
        Assert.IsTrue(seen.Count > 0 && seen.All(flag => flag), "the privilege is on for every lookup");
        Assert.IsFalse(recorder.Enabled, "and put back afterwards");
    }

    [TestMethod]
    public void CloseRunning_Restores_Privilege_When_Scan_Throws()
    {
        using var dir = new TempDir();
        var recorder = new Recorder();

        Assert.ThrowsExactly<InvalidOperationException>(() => ProcessCommands.CloseRunningScoped(
            dir.Sub("install"), [], 1, TextWriter.Null, new ThrowingSource(), ProcessScan.CurrentSessionId(), _ => { },
            () => true, () => recorder));

        Assert.IsFalse(recorder.Enabled, "the privilege goes back to its state even when the scan fails");
    }

    [TestMethod]
    public void CloseRunning_Without_Privilege_Still_Scans()
    {
        using var dir = new TempDir();
        var seen = new List<bool>();

        var code = ProcessCommands.CloseRunningScoped(
            dir.Sub("install"), [], 1, TextWriter.Null, new WatchingSource(() => false, seen), ProcessScan.CurrentSessionId(), _ => { },
            () => true, () => null);

        Assert.AreEqual(0, code, "a token without the privilege is not an error");
        Assert.IsTrue(seen.Count > 0, "the scan ran");
    }

    [TestMethod]
    public void CloseRunning_Not_Elevated_Does_Not_Enable_Privilege()
    {
        using var dir = new TempDir();
        var asked = 0;

        var code = ProcessCommands.CloseRunningScoped(
            dir.Sub("install"), [], 1, TextWriter.Null, new WatchingSource(() => false, []), ProcessScan.CurrentSessionId(), _ => { },
            () => false, () =>
            {
                asked++;
                return new Recorder();
            });

        Assert.AreEqual(0, code);
        Assert.AreEqual(0, asked, "a medium process never asks for the privilege");
    }

    [TestMethod]
    public void Privilege_Not_Held_Is_Not_Enabled()
    {
        if (PrivilegeScope.IsHeld(PrivilegeScope.DebugPrivilege))
        {
            Assert.Inconclusive("this token holds SeDebugPrivilege");
        }

        using var scope = PrivilegeScope.TryEnable(PrivilegeScope.DebugPrivilege);

        Assert.IsNull(scope, "nothing to enable");
        Assert.IsFalse(PrivilegeScope.IsEnabled(PrivilegeScope.DebugPrivilege));
    }

    [TestMethod]
    public void Privilege_Scope_Restores_State()
    {
        // A standard token holds this one, disabled. It stands in for SeDebugPrivilege, which a medium token does not hold.
        const string Name = "SeIncreaseWorkingSetPrivilege";
        if (!PrivilegeScope.IsHeld(Name) || PrivilegeScope.IsEnabled(Name))
        {
            Assert.Inconclusive("this token does not hold " + Name + " in the disabled state");
        }

        using (var scope = PrivilegeScope.TryEnable(Name))
        {
            Assert.IsNotNull(scope);
            Assert.IsTrue(PrivilegeScope.IsEnabled(Name), "enabled inside the scope");
        }

        Assert.IsFalse(PrivilegeScope.IsEnabled(Name), "back to disabled");
        Assert.IsTrue(PrivilegeScope.IsHeld(Name), "still held");
    }

    [TestMethod]
    public void Unreadable_Image_With_Matching_Name_Counts()
    {
        Assert.IsTrue(RunningImageRule.Decide(ImageStatus.Unreadable, null, @"C:\Program Files\Better Windows Config"));
    }

    [TestMethod]
    public void Readable_Image_Elsewhere_Does_Not_Count()
    {
        Assert.IsFalse(RunningImageRule.Decide(ImageStatus.Readable, @"D:\Tools\BwcRegedit.exe", @"C:\Program Files\Better Windows Config"));
    }

    // ---- HR: Program Files rule ------------------------------------------------------------------------------------

    [TestMethod]
    public void Program_Files_Rule_Fails_Closed_On_Bad_Path()
    {
        var tooLong = @"C:\Program Files\" + new string('a', 40000);

        Assert.IsFalse(ProgramFilesRule.IsUnder("C:\\Program Files\\App\0evil", @"C:\Program Files"), "an embedded null");
        Assert.IsFalse(ProgramFilesRule.IsUnder(@"C:\Program Files\App", "C:\\Program Files\0"), "a null in the root");
        Assert.IsFalse(ProgramFilesRule.IsUnder(tooLong, @"C:\Program Files"), "a path that cannot be normalized");
        Assert.IsFalse(ProgramFilesRule.IsUnder(@"\\?\C:\Program Files\App\..\..\Users", @"C:\Program Files"));
        Assert.IsTrue(ProgramFilesRule.IsUnder(@"C:\Program Files\App", @"C:\Program Files"), "a normal path still passes");
    }

    // ---- HK: the gate for --check and --download ----------------------------------------------------------------------

    [TestMethod]
    [DataRow(UpdateMode.Check, true)]
    [DataRow(UpdateMode.Download, true)]
    [DataRow(UpdateMode.Apply, false)]
    [DataRow(UpdateMode.ApplyElevated, false)]
    [DataRow(UpdateMode.CloseRunning, false)]
    [DataRow(UpdateMode.WaitPid, false)]
    [DataRow(UpdateMode.LaunchHub, false)]
    [DataRow(UpdateMode.Help, false)]
    [DataRow(UpdateMode.Invalid, false)]
    public void Elevation_Gate_Refuses_Check_And_Download(UpdateMode mode, bool refuses)
    {
        Assert.AreEqual(refuses, ElevationPolicy.RefusesElevated(mode));
        Assert.IsFalse(string.IsNullOrWhiteSpace(ElevationPolicy.RefusalDetail));
    }

    // ---- HK: staging cleanup through links ------------------------------------------------------------------------------

    private const string StageName = "0123456789abcdef0123456789abcdef";
    private const string KeepStage = "fedcba9876543210fedcba9876543210";

    [TestMethod]
    public void Prune_Root_Link_Refused()
    {
        using var dir = new TempDir();
        var target = dir.Sub("target");
        var victim = Path.Combine(target, StageName);
        Directory.CreateDirectory(victim);
        Directory.SetCreationTimeUtc(victim, DateTime.UtcNow.AddDays(-5));
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "x");
        var root = Path.Combine(dir.Root, "staging-link");
        if (!Links.TryMakeJunction(root, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        StagingCleanup.Prune(root, Path.Combine(root, KeepStage));

        Assert.IsTrue(File.Exists(Path.Combine(victim, "keep.txt")), "nothing is removed through a linked root");
    }

    [TestMethod]
    public void Discard_Root_Link_Refused()
    {
        using var dir = new TempDir();
        var target = dir.Sub("target");
        var victim = Path.Combine(target, StageName);
        Directory.CreateDirectory(victim);
        var root = Path.Combine(dir.Root, "staging-link");
        if (!Links.TryMakeJunction(root, target))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        var removed = StagingCleanup.DiscardIncomplete(root);

        Assert.AreEqual(0, removed);
        Assert.IsTrue(Directory.Exists(victim), "nothing is removed through a linked root");
    }

    [TestMethod]
    public void Prune_Inner_Link_Not_Followed()
    {
        using var dir = new TempDir();
        var root = dir.Sub("staging");
        var outside = dir.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        var old = Path.Combine(root, StageName);
        Directory.CreateDirectory(old);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-5));
        if (!Links.TryMakeJunction(Path.Combine(old, "inner"), outside))
        {
            Assert.Inconclusive("mklink /J is not available here");
        }

        StagingCleanup.Prune(root, Path.Combine(root, KeepStage));

        Assert.IsTrue(File.Exists(Path.Combine(outside, "keep.txt")), "a link inside a staging folder is not entered");
        Assert.IsFalse(Directory.Exists(old), "the staging folder itself is removed");
    }

    [TestMethod]
    public void Prune_Unreadable_Root_Refused()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Root, "no-such-root");

        StagingCleanup.Prune(missing, Path.Combine(missing, KeepStage));
        StagingCleanup.Prune("\0bad", "\0bad");

        Assert.AreEqual(0, StagingCleanup.DiscardIncomplete(missing));
        Assert.AreEqual(0, StagingCleanup.DiscardIncomplete("\0bad"));
    }
}
