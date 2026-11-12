using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

public sealed class TestSettings
{
    public string? Name { get; set; }

    public int Count { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(TestSettings))]
internal partial class TestSettingsContext : JsonSerializerContext;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void DocumentedSizeCapIsOneMiB()
    {
        FieldInfo? field = typeof(SettingsStore<TestSettings>).GetField(
            "MaxFileBytes",
            BindingFlags.Public | BindingFlags.Static);
        Assert.IsNotNull(field, "SettingsStore should publish its documented size cap.");
        Assert.AreEqual(
            1L * 1024 * 1024,
            Convert.ToInt64(field!.GetRawConstantValue(), CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void SaveAndLoadRoundTrip()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        store.Save(new TestSettings { Name = "alpha", Count = 3 });
        TestSettings loaded = store.Load();

        Assert.AreEqual("alpha", loaded.Name);
        Assert.AreEqual(3, loaded.Count);
    }

    [TestMethod]
    public void MissingFileReturnsDefaultInstance()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        TestSettings loaded = store.Load();

        Assert.IsNull(loaded.Name);
        Assert.AreEqual(0, loaded.Count);
        Assert.IsFalse(File.Exists(store.Path), "load must not create a file.");
    }

    [TestMethod]
    public void CorruptJsonIsMovedAsideAndDefaultIsReturned()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);
        byte[] original = Encoding.UTF8.GetBytes("{ this is not json");
        File.WriteAllBytes(store.Path, original);

        TestSettings loaded = store.Load();

        Assert.IsNull(loaded.Name);
        Assert.AreEqual(0, loaded.Count);
        Assert.IsFalse(File.Exists(store.Path), "the corrupt file should have been moved.");

        string[] badFiles = Directory.GetFiles(temp.Path, "settings.json.bad-*");
        Assert.AreEqual(1, badFiles.Length, "expected one preserved corrupt file.");
        Assert.IsTrue(original.SequenceEqual(File.ReadAllBytes(badFiles[0])),
            "the preserved corrupt file must keep the original bytes.");
    }

    [TestMethod]
    public void OversizedFileIsMovedAsideWithoutReadingItAll()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        // The documented cap is 1 MiB. Make the file several times larger so an
        // implementation that reads first and checks later shows a large allocation.
        const long cap = 1L * 1024 * 1024;
        long size = cap + (4L * 1024 * 1024);
        using (var stream = new FileStream(store.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(size);
            byte[] prefix = Encoding.UTF8.GetBytes("{\"Name\":\"alpha\",\"Count\":7}");
            stream.Position = 0;
            stream.Write(prefix, 0, prefix.Length);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        TestSettings loaded = store.Load();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsNull(loaded.Name);
        Assert.AreEqual(0, loaded.Count);
        Assert.IsFalse(File.Exists(store.Path), "the oversized file should have been moved aside.");

        string[] badFiles = Directory.GetFiles(temp.Path, "settings.json.bad-*");
        Assert.AreEqual(1, badFiles.Length, "expected one preserved oversized file.");
        Assert.AreEqual(size, new FileInfo(badFiles[0]).Length,
            "the preserved file must keep its original length and bytes.");

        Assert.IsTrue(allocated < 1024 * 1024,
            "Load allocated " + allocated.ToString(CultureInfo.InvariantCulture) +
            " bytes; it must check the documented 1 MiB cap before reading.");
    }

    [TestMethod]
    public void FileAtTheDocumentedCapIsStillRead()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        const int cap = 1024 * 1024;
        string json = "{\"Name\":\"cap\",\"Count\":4}";
        string padded = json + new string(' ', cap - json.Length);
        File.WriteAllText(store.Path, padded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        TestSettings loaded = store.Load();

        Assert.AreEqual("cap", loaded.Name);
        Assert.AreEqual(4, loaded.Count);
        Assert.IsTrue(File.Exists(store.Path), "a file at exactly the cap must not be treated as corrupt.");
    }

    [TestMethod]
    public void SaveLeavesNoTemporaryFiles()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        store.Save(new TestSettings { Name = "alpha", Count = 1 });

        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp-*").Length,
            "no temporary file should remain after a successful save.");
        Assert.IsTrue(File.Exists(store.Path));
    }

    [TestMethod]
    public void SaveOverwritesExistingSettings()
    {
        using var temp = new TempDirectory();
        SettingsStore<TestSettings> store = NewStore(temp.Path);

        store.Save(new TestSettings { Name = "first", Count = 1 });
        store.Save(new TestSettings { Name = "second", Count = 2 });
        TestSettings loaded = store.Load();

        Assert.AreEqual("second", loaded.Name);
        Assert.AreEqual(2, loaded.Count);
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp-*").Length,
            "overwriting must not leave temporary files.");
    }

    private static SettingsStore<TestSettings> NewStore(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore<TestSettings>(path, TestSettingsContext.Default.TestSettings);
        Assert.AreEqual(Path.GetFullPath(path), store.Path);
        return store;
    }
}
