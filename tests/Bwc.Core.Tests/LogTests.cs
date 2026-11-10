using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

[TestClass]
public sealed class LogTests
{
    [TestMethod]
    public void InfoWarnErrorWriteOneDailyFileWithLevelText()
    {
        using var temp = new TempDirectory();
        try
        {
            Log.Initialize("log-test", new LogOptions { Directory = temp.Path });
            Log.Info("info message");
            Log.Warn("warn message");
            Log.Error("error message");

            string? file = Log.CurrentFile;
            Assert.IsNotNull(file);
            Assert.IsTrue(
                string.Equals(
                    Path.GetFullPath(temp.Path),
                    Log.Directory is null ? null : Path.GetFullPath(Log.Directory),
                    StringComparison.OrdinalIgnoreCase),
                "Directory should report the initialized log directory.");
            Assert.AreEqual(
                "log-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt",
                Path.GetFileName(file));

            Log.Shutdown();
            string text = File.ReadAllText(file);
            Assert.IsTrue(text.Contains(" INFO ", StringComparison.Ordinal), "log should contain INFO.");
            Assert.IsTrue(text.Contains(" WARN ", StringComparison.Ordinal), "log should contain WARN.");
            Assert.IsTrue(text.Contains(" ERROR ", StringComparison.Ordinal), "log should contain ERROR.");
            Assert.IsTrue(text.Contains("info message", StringComparison.Ordinal), "log should contain the message.");
            Assert.IsTrue(text.Contains("log-test", StringComparison.Ordinal), "log should contain the tool id.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void SmallLimitRollsToPartFilesWithoutOverwritingExistingParts()
    {
        using var temp = new TempDirectory();
        try
        {
            Log.Initialize("log-roll", new LogOptions { Directory = temp.Path, MaxFileBytes = 256 });
            string? daily = Log.CurrentFile;
            Assert.IsNotNull(daily);

            string existingPart = Path.Combine(temp.Path, Path.GetFileNameWithoutExtension(daily) + ".1.txt");
            File.WriteAllText(existingPart, "sentinel");

            for (int i = 0; i < 20; i++)
            {
                Log.Info("message " + i.ToString(CultureInfo.InvariantCulture) + " " + new string('x', 80));
            }

            Log.Shutdown();

            string[] files = Directory.GetFiles(temp.Path, "log-*.txt");
            Assert.IsTrue(files.Length >= 3, "expected the daily file plus at least two parts.");
            Assert.IsTrue(File.Exists(Path.Combine(temp.Path, Path.GetFileNameWithoutExtension(daily) + ".2.txt")),
                "expected the roll to skip the existing .1 part and create .2.");
            Assert.AreEqual("sentinel", File.ReadAllText(existingPart),
                "an existing part file must never be overwritten.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void RetentionDeletesOnlyOldMatchingLogFiles()
    {
        using var temp = new TempDirectory();
        try
        {
            string oldDaily = Path.Combine(temp.Path, "log-20200101.txt");
            string oldPart = Path.Combine(temp.Path, "log-20200101.1.txt");
            string recentLog = Path.Combine(temp.Path, "log-recent.txt");
            string otherFile = Path.Combine(temp.Path, "notes.txt");

            File.WriteAllText(oldDaily, "old daily");
            File.WriteAllText(oldPart, "old part");
            File.WriteAllText(recentLog, "recent");
            File.WriteAllText(otherFile, "unrelated");

            DateTime old = DateTime.UtcNow.AddDays(-30);
            File.SetLastWriteTimeUtc(oldDaily, old);
            File.SetLastWriteTimeUtc(oldPart, old);
            File.SetLastWriteTimeUtc(otherFile, old);

            Log.Initialize("log-retain", new LogOptions { Directory = temp.Path, RetainDays = 7 });
            Log.Shutdown();

            Assert.IsFalse(File.Exists(oldDaily), "old daily log should be deleted.");
            Assert.IsFalse(File.Exists(oldPart), "old part log should be deleted.");
            Assert.IsTrue(File.Exists(recentLog), "a fresh matching log file should be kept.");
            Assert.IsTrue(File.Exists(otherFile), "a non-matching file must never be deleted.");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void InfoAfterFailedInitializeDoesNotThrow()
    {
        using var temp = new TempDirectory();
        try
        {
            string blockingFile = Path.Combine(temp.Path, "not-a-directory");
            File.WriteAllText(blockingFile, "x");

            Log.Initialize("log-fail", new LogOptions { Directory = blockingFile });
            Log.Info("this must fall back to trace");

            Assert.IsNull(Log.CurrentFile, "a failed initialize should not report a current file.");
        }
        finally
        {
            Log.Shutdown();
        }
    }
}
