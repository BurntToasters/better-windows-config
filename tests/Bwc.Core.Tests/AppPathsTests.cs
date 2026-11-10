using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Core.Tests;

[TestClass]
public sealed class AppPathsTests
{
    private string? _originalDataRoot;

    [TestInitialize]
    public void Setup()
    {
        _originalDataRoot = Environment.GetEnvironmentVariable("BWC_DATA_ROOT");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("BWC_DATA_ROOT", _originalDataRoot);
    }

    [TestMethod]
    public void BaseDirectoryUsesEnvironmentOverride()
    {
        using var temp = new TempDirectory();
        Environment.SetEnvironmentVariable("BWC_DATA_ROOT", temp.Path);

        string expected = Path.GetFullPath(temp.Path);
        Assert.IsTrue(
            string.Equals(expected, AppPaths.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            $"expected '{expected}', got '{AppPaths.BaseDirectory}'.");
    }

    [TestMethod]
    public void ForToolReturnsPerToolDirectoryWithoutCreatingIt()
    {
        using var temp = new TempDirectory();
        Environment.SetEnvironmentVariable("BWC_DATA_ROOT", temp.Path);

        string toolDirectory = AppPaths.ForTool("alpha-1");
        string expected = Path.Combine(Path.GetFullPath(temp.Path), "alpha-1");

        Assert.IsTrue(
            string.Equals(expected, toolDirectory, StringComparison.OrdinalIgnoreCase),
            $"expected '{expected}', got '{toolDirectory}'.");
        Assert.IsFalse(Directory.Exists(toolDirectory), "lookup must not create the tool directory.");
        Assert.IsTrue(
            string.Equals(
                Path.Combine(expected, "settings.json"),
                AppPaths.SettingsFile("alpha-1"),
                StringComparison.OrdinalIgnoreCase),
            "settings path should be inside the tool directory.");
        Assert.IsTrue(
            string.Equals(
                Path.Combine(expected, "logs"),
                AppPaths.LogDirectory("alpha-1"),
                StringComparison.OrdinalIgnoreCase),
            "log directory should be inside the tool directory.");
    }

    [TestMethod]
    public void ToolIdTraversalAndInvalidCharactersAreRejected()
    {
        using var temp = new TempDirectory();
        Environment.SetEnvironmentVariable("BWC_DATA_ROOT", temp.Path);

        string[] invalidIds =
        [
            string.Empty,
            "..",
            "../evil",
            "evil/child",
            "evil\\child",
            "Upper",
            "-leading-hyphen",
            "bad_underscore",
            "bad.id",
        ];

        foreach (string id in invalidIds)
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => AppPaths.ForTool(id),
                $"tool id '{id}' should be rejected.");
        }

        Assert.ThrowsExactly<ArgumentException>(
            () => AppPaths.SettingsFile("../evil"),
            "settings lookup should validate the tool id.");
        Assert.ThrowsExactly<ArgumentException>(
            () => AppPaths.LogDirectory("evil/child"),
            "log lookup should validate the tool id.");
    }

    [TestMethod]
    public void CombineIsPureAndValidatesTheToolId()
    {
        using var temp = new TempDirectory();
        string baseDirectory = Path.Combine(temp.Path, "base");

        string combined = AppPaths.Combine(baseDirectory, "tool-9");
        Assert.AreEqual(Path.Combine(baseDirectory, "tool-9"), combined);
        Assert.IsFalse(Directory.Exists(baseDirectory), "combine must not create anything.");
        Assert.IsFalse(Directory.Exists(combined), "combine must not create anything.");

        Assert.ThrowsExactly<ArgumentException>(() => AppPaths.Combine(baseDirectory, ".."));
        Assert.ThrowsExactly<ArgumentException>(() => AppPaths.Combine(baseDirectory, "/absolute"));
    }
}
