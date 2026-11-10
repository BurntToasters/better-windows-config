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
