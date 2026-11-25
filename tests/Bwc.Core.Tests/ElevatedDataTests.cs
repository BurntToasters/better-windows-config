using System.Diagnostics;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

/// <summary>
/// Drives the elevated code path of Log and SettingsStore without an elevated process, through the internal
/// "as if elevated" entry points. Failure modes: docs/failure-modes/core.md, "Elevated data folder and links".
/// </summary>
[TestClass]
public sealed class ElevatedDataTests
{
    private const string Tool = "elev-test";

    [TestCleanup]
    public void Cleanup()
    {
        Log.Shutdown();
        SafeDataFolder.ReleaseAll();
    }

    [TestMethod]
    public void JunctionOnTheDataPathGivesNoFileLogAndLeavesTheTargetUntouched()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        string toolDir = Path.Combine(root, Tool);
        string junction = LinkTools.CreateJunction(toolDir, target);
        try
        {
            Log.InitializeAs(Tool, new LogOptions { Directory = Path.Combine(toolDir, "logs") }, elevated: true, root);
            Log.Info("must not reach the target");

            Assert.IsNull(Log.CurrentFile, "no file log may be selected through a junction.");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length, "the junction target must stay empty.");
        }
        finally
        {
            LinkTools.RemoveJunction(junction);
        }
    }

    [TestMethod]
    public void JunctionOnTheDataRootGivesNoFileLog()
    {
        using var temp = new TempDirectory();
        string target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(target);
        string root = LinkTools.CreateJunction(Path.Combine(temp.Path, "root"), target);
        try
        {
            Log.InitializeAs(Tool, new LogOptions { Directory = Path.Combine(root, Tool, "logs") }, elevated: true, root);
            Log.Info("must not reach the target");

            Assert.IsNull(Log.CurrentFile);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length);
        }
        finally
        {
            LinkTools.RemoveJunction(root);
        }
    }

    [TestMethod]
    public void JunctionOnTheDataPathKeepsSettingsInMemoryOnly()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        string planted = Path.Combine(target, "settings.json");
        File.WriteAllText(planted, "{ not json");
        string toolDir = Path.Combine(root, Tool);
        string junction = LinkTools.CreateJunction(toolDir, target);
        try
        {
            var store = NewStore(Path.Combine(toolDir, "settings.json"), root);

            TestSettings loaded = store.Load();
            Assert.IsNull(loaded.Name, "load must return defaults, not read through the junction.");

            store.Save(new TestSettings { Name = "kept", Count = 7 });
            TestSettings again = store.Load();
            Assert.AreEqual("kept", again.Name, "the saved values must be kept in memory.");
            Assert.AreEqual(7, again.Count);

            Assert.AreEqual("{ not json", File.ReadAllText(planted), "the target file must not change.");
            Assert.AreEqual(1, Directory.GetFileSystemEntries(target).Length, "nothing may be created, moved, or deleted in the target.");
        }
        finally
        {
            LinkTools.RemoveJunction(junction);
        }
    }

    [TestMethod]
    public void ToolFolderHeldByTheLogCannotBeRenamedOrReplaced()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string toolDir = Path.Combine(root, Tool);
        Log.InitializeAs(Tool, new LogOptions { Directory = Path.Combine(toolDir, "logs") }, elevated: true, root);
        Assert.IsNotNull(Log.CurrentFile, "a plain folder must be usable.");

        string moved = Path.Combine(root, Tool + "-moved");
        Assert.ThrowsExactly<IOException>(() => Directory.Move(toolDir, moved));
        Assert.IsFalse(Directory.Exists(moved), "the held tool folder must not be renamed.");
    }

    [TestMethod]
    public void LogFilePreCreatedAsHardLinkIsNotAppendedTo()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string logs = Path.Combine(root, Tool, "logs");
        Directory.CreateDirectory(logs);
        string victim = Path.Combine(temp.Path, "victim.txt");
        File.WriteAllText(victim, "original");
        string daily = Path.Combine(
            logs,
            "log-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt");
        LinkTools.CreateHardLink(daily, victim);

        Log.InitializeAs(Tool, new LogOptions { Directory = logs }, elevated: true, root);
        Log.Info("must not be appended");

        Assert.IsNull(Log.CurrentFile, "a hard linked log file must not be selected.");
        Assert.AreEqual("original", File.ReadAllText(victim), "the hard link target must stay unchanged.");
    }

    [TestMethod]
    public void LogFilePreCreatedAsSymlinkIsNotAppendedTo()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string logs = Path.Combine(root, Tool, "logs");
        Directory.CreateDirectory(logs);
        string victim = Path.Combine(temp.Path, "victim.txt");
        File.WriteAllText(victim, "original");
        string daily = Path.Combine(
            logs,
            "log-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt");
        try
        {
            File.CreateSymbolicLink(daily, victim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symlinks cannot be created here: " + ex.Message);
            return;
        }

        Log.InitializeAs(Tool, new LogOptions { Directory = logs }, elevated: true, root);
        Log.Info("must not be appended");

        Assert.IsNull(Log.CurrentFile);
        Assert.AreEqual("original", File.ReadAllText(victim));
    }

    [TestMethod]
    public void ExpiredHardLinkedLogIsNotDeleted()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string logs = Path.Combine(root, Tool, "logs");
        Directory.CreateDirectory(logs);
        string victim = Path.Combine(temp.Path, "victim.txt");
        File.WriteAllText(victim, "original");
        string old = Path.Combine(logs, "log-20200101.txt");
        LinkTools.CreateHardLink(old, victim);
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddDays(-30));
        string expiredPlain = Path.Combine(logs, "log-20200102.txt");
        File.WriteAllText(expiredPlain, "old");
        File.SetLastWriteTimeUtc(expiredPlain, DateTime.UtcNow.AddDays(-30));

        Log.InitializeAs(Tool, new LogOptions { Directory = logs }, elevated: true, root);

        Assert.IsTrue(File.Exists(old), "a hard linked log must not be deleted by retention.");
        Assert.IsFalse(File.Exists(expiredPlain), "a plain expired log is still deleted through a checked handle.");
        Assert.IsNotNull(Log.CurrentFile, "the plain daily file is still usable.");
    }

    [TestMethod]
    public void PlainFolderLogsAndRollsParts()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string logs = Path.Combine(root, Tool, "logs");

        Log.InitializeAs(Tool, new LogOptions { Directory = logs, MaxFileBytes = 200 }, elevated: true, root);
        for (int i = 0; i < 6; i++)
        {
            Log.Info("entry number " + i.ToString(CultureInfo.InvariantCulture) + " with some padding text");
        }

        Log.Shutdown();
        string[] files = Directory.GetFiles(logs, "log-*.txt");
        Assert.IsTrue(files.Length > 1, "the log must roll to part files.");
        string all = string.Concat(files.Select(File.ReadAllText));
        Assert.IsTrue(all.Contains("entry number 5", StringComparison.Ordinal), "the last entry must be written.");
    }

    [TestMethod]
    public void ProfileRelocatedByJunctionAboveTheRootStillWorks()
    {
        using var temp = new TempDirectory();
        string real = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(real);
        string link = LinkTools.CreateJunction(Path.Combine(temp.Path, "profile"), real);
        try
        {
            string root = Path.Combine(link, "root");
            string logs = Path.Combine(root, Tool, "logs");
            Log.InitializeAs(Tool, new LogOptions { Directory = logs }, elevated: true, root);
            Log.Info("through a relocated profile");

            Assert.IsNotNull(Log.CurrentFile, "a junction above the data root is a legitimate relocation.");
            Log.Shutdown();
            Assert.IsTrue(
                File.ReadAllText(Path.Combine(real, "root", Tool, "logs", Path.GetFileName(Log.CurrentFile!)))
                    .Contains("through a relocated profile", StringComparison.Ordinal));
        }
        finally
        {
            Log.Shutdown();
            SafeDataFolder.ReleaseAll();
            LinkTools.RemoveJunction(link);
        }
    }

    [TestMethod]
    public void LogDirectoryOutsideTheDataRootIsRefused()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string elsewhere = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(root);

        Log.InitializeAs(Tool, new LogOptions { Directory = elsewhere }, elevated: true, root);
        Log.Info("nowhere");

        Assert.IsNull(Log.CurrentFile);
        Assert.IsFalse(Directory.Exists(elsewhere), "nothing may be created outside the data root.");
    }

    [TestMethod]
    public void SettingsFileAsHardLinkIsNeverReadWrittenOrMoved()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string toolDir = Path.Combine(root, Tool);
        Directory.CreateDirectory(toolDir);
        string victim = Path.Combine(temp.Path, "victim.json");
        File.WriteAllText(victim, "{ not json");
        string settings = Path.Combine(toolDir, "settings.json");
        LinkTools.CreateHardLink(settings, victim);

        var store = NewStore(settings, root);
        Assert.IsNull(store.Load().Name, "a hard linked settings file must load as defaults.");
        store.Save(new TestSettings { Name = "memory", Count = 1 });
        Assert.AreEqual("memory", store.Load().Name);

        Assert.AreEqual("{ not json", File.ReadAllText(victim), "the other name of the file must not change.");
        Assert.AreEqual("{ not json", File.ReadAllText(settings), "the linked settings file must not change.");
        Assert.AreEqual(0, Directory.GetFiles(toolDir, "*.bad-*").Length, "a linked file must not be moved aside.");
        Assert.AreEqual(0, Directory.GetFiles(toolDir, "*.tmp-*").Length);
    }

    [TestMethod]
    public void SettingsFileAsSymlinkIsNeverReadWrittenOrMoved()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string toolDir = Path.Combine(root, Tool);
        Directory.CreateDirectory(toolDir);
        string victim = Path.Combine(temp.Path, "victim.json");
        File.WriteAllText(victim, "{ \"Name\": \"victim\" }");
        string settings = Path.Combine(toolDir, "settings.json");
        try
        {
            File.CreateSymbolicLink(settings, victim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symlinks cannot be created here: " + ex.Message);
            return;
        }

        var store = NewStore(settings, root);
        Assert.IsNull(store.Load().Name, "a symlinked settings file must not be read.");
        store.Save(new TestSettings { Name = "memory" });

        Assert.AreEqual("{ \"Name\": \"victim\" }", File.ReadAllText(victim));
        Assert.AreEqual(0, Directory.GetFiles(toolDir, "*.tmp-*").Length);
    }

    [TestMethod]
    public void PlainFolderSettingsRoundTripAndMoveCorruptFileThroughHandles()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string settings = Path.Combine(root, Tool, "settings.json");
        var store = NewStore(settings, root);

        Assert.IsNull(store.Load().Name, "a missing file loads as defaults.");
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(settings)), "load must not create folders.");

        store.Save(new TestSettings { Name = "disk", Count = 4 });
        store.Save(new TestSettings { Name = "disk2", Count = 5 });
        Assert.AreEqual("disk2", NewStore(settings, root).Load().Name);
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(settings)!, "*.tmp-*").Length);

        File.WriteAllText(settings, "{ not json");
        Assert.IsNull(NewStore(settings, root).Load().Name);
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(settings)!, "settings.json.bad-*").Length);
        Assert.IsFalse(File.Exists(settings));
    }

    [TestMethod]
    public void ElevatedKeepsOnlyNewestFiveBadFiles()
    {
        using var temp = new TempDirectory();
        string root = Path.Combine(temp.Path, "root");
        string toolDir = Path.Combine(root, Tool);
        string settings = Path.Combine(toolDir, "settings.json");
        Directory.CreateDirectory(toolDir);
        SeedBadFiles(settings);

        File.WriteAllText(settings, "{ not json");
        Assert.IsNull(NewStore(settings, root).Load().Name);

        AssertNewestFiveKept(toolDir);
    }

    [TestMethod]
    public void UnelevatedKeepsOnlyNewestFiveBadFiles()
    {
        using var temp = new TempDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        SeedBadFiles(settings);

        File.WriteAllText(settings, "{ not json");
        var store = new SettingsStore<TestSettings>(settings, TestSettingsContext.Default.TestSettings);
        Assert.IsNull(store.Load().Name);

        AssertNewestFiveKept(temp.Path);
    }

    [TestMethod]
    public void FiveOrFewerBadFilesAreAllKept()
    {
        using var temp = new TempDirectory();
        string settings = Path.Combine(temp.Path, "settings.json");
        for (int day = 1; day <= 3; day++)
        {
            File.WriteAllText(settings + ".bad-2025010" + day.ToString(CultureInfo.InvariantCulture) + "T000000Z", "x");
        }

        File.WriteAllText(settings, "{ not json");
        var store = new SettingsStore<TestSettings>(settings, TestSettingsContext.Default.TestSettings);
        _ = store.Load();

        Assert.AreEqual(4, Directory.GetFiles(temp.Path, "settings.json.bad-*").Length);
    }

    private static void SeedBadFiles(string settings)
    {
        for (int day = 1; day <= 8; day++)
        {
            File.WriteAllText(settings + ".bad-2025010" + day.ToString(CultureInfo.InvariantCulture) + "T000000Z", "x");
        }

        File.WriteAllText(settings + ".bad-20250109T000000Z-1", "x");
        File.WriteAllText(settings + ".bad-notes", "keep");
        File.WriteAllText(settings + ".bad-20250101T000000Z.keep", "keep");
        File.WriteAllText(settings + ".tmp-abc", "keep");
    }

    private static void AssertNewestFiveKept(string folder)
    {
        string[] exact = Directory.GetFiles(folder, "settings.json.bad-*")
            .Select(Path.GetFileName)
            .Where(name => System.Text.RegularExpressions.Regex.IsMatch(
                name!,
                @"^settings\.json\.bad-\d{8}T\d{6}Z(-\d+)?$"))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        Assert.AreEqual(5, exact.Length, "only the newest 5 .bad-* files may remain: " + string.Join(", ", exact));
        CollectionAssert.AreEqual(
            new[]
            {
                "settings.json.bad-20250106T000000Z",
                "settings.json.bad-20250107T000000Z",
                "settings.json.bad-20250108T000000Z",
                "settings.json.bad-20250109T000000Z-1",
            },
            exact.Take(4).ToArray());
        Assert.IsTrue(
            exact[4].StartsWith(
                "settings.json.bad-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            "the newest copy is the one just created: " + exact[4]);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "settings.json.bad-notes")), "a look-alike name must be left alone.");
        Assert.IsTrue(File.Exists(Path.Combine(folder, "settings.json.bad-20250101T000000Z.keep")));
        Assert.IsTrue(File.Exists(Path.Combine(folder, "settings.json.tmp-abc")));
    }

    private static SettingsStore<TestSettings> NewStore(string path, string root)
    {
        return new SettingsStore<TestSettings>(
            path,
            TestSettingsContext.Default.TestSettings,
            elevated: true,
            root);
    }
}

/// <summary>Creates and removes junctions and hard links for tests. Links are removed themselves, never recursed.</summary>
internal static class LinkTools
{
    public static string CreateJunction(string link, string target)
    {
        RunMklink("/J", link, target);
        return link;
    }

    public static void RemoveJunction(string link)
    {
        // Removing a junction with a non-recursive delete removes only the link, not the target.
        if (Directory.Exists(link))
        {
            Directory.Delete(link, recursive: false);
        }
    }

    public static void CreateHardLink(string link, string existing)
    {
        RunMklink("/H", link, existing);
    }

    private static void RunMklink(string kind, string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Arguments = "/c mklink " + kind + " \"" + link + "\" \"" + target + "\"";
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "mklink failed: " + output);
    }
}
