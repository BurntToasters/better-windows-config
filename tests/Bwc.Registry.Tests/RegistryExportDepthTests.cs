using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// docs/failure-modes/export.md, "An export walk stops at 512 levels". The source is a single chain of keys, so a walk
/// that has no limit goes all the way down. The start key is level 0; keys down to level 512 are exported and the key at
/// level 513 is skipped with its subtree. Nothing here touches the registry.
/// </summary>
[TestClass]
public sealed class RegistryExportDepthTests
{
    private const int Limit = 512;
    private const string StartText = @"HKCU\Software\BWC-Depth\Start";

    [TestMethod]
    public void RegFileExportStopsAtFiveHundredTwelveLevels()
    {
        var source = new ChainSource(levels: 600);
        var progress = new LastProgress();

        RegFileDocument document = RegFileExporter.Export(
            source,
            RegistryPath.Parse(StartText),
            RegFileFormat.V5,
            CancellationToken.None,
            progress);

        Assert.AreEqual(Limit + 1, document.Keys.Count, "The start key and 512 levels below it.");
        Assert.IsNotNull(progress.Completed);
        Assert.AreEqual(1, progress.Completed.KeysSkipped, "The key at level 513 is skipped; nothing below it is read.");
        Assert.AreEqual(Limit + 1, progress.Completed.KeysExported);
        Assert.AreEqual(Limit + 1, source.DeepestEnumerated);
    }

    [TestMethod]
    public void RegFileStreamedExportStopsAtFiveHundredTwelveLevels()
    {
        var service = new RegistryExportService(new ChainSource(levels: 600));
        using var stream = new MemoryStream();

        service.WriteRegFile(RegistryPath.Parse(StartText), stream, RegFileFormat.V5);

        string text = Encoding.Unicode.GetString(stream.ToArray(), 2, (int)stream.Length - 2);
        Assert.AreEqual(Limit + 1, CountOccurrences(text, "[HKEY_CURRENT_USER\\"), "The start key and 512 levels below it.");
    }

    [TestMethod]
    public void TextExportStopsAtFiveHundredTwelveLevels()
    {
        var source = new ChainSource(levels: 600);
        var progress = new LastProgress();
        using var writer = new StringWriter();

        RegistryTextExporter.Export(
            source,
            RegistryPath.Parse(StartText),
            writer,
            CancellationToken.None,
            progress);

        Assert.AreEqual(Limit + 1, CountOccurrences(writer.ToString(), "Key Name:"), "The start key and 512 levels below it.");
        Assert.IsNotNull(progress.Completed);
        Assert.AreEqual(1, progress.Completed.KeysSkipped);
        Assert.AreEqual(Limit + 1, source.DeepestEnumerated);
    }

    [TestMethod]
    public void AChainExactlyAtTheLimitIsExportedWholeWithNothingSkipped()
    {
        var source = new ChainSource(levels: Limit);
        var progress = new LastProgress();

        RegFileDocument document = RegFileExporter.Export(
            source,
            RegistryPath.Parse(StartText),
            RegFileFormat.V5,
            CancellationToken.None,
            progress);

        Assert.AreEqual(Limit + 1, document.Keys.Count);
        Assert.IsNotNull(progress.Completed);
        Assert.AreEqual(0, progress.Completed.KeysSkipped);
    }

    [TestMethod]
    public void WholeRegistryExportStopsAtFiveHundredTwelveLevelsBelowEachRoot()
    {
        var source = new ChainSource(levels: 600, rootOnly: true);

        RegFileDocument document = RegFileExporter.ExportWholeRegistry(source, RegFileFormat.V5);

        // Five roots, each its own level 0; only HKEY_CURRENT_USER has the chain below it.
        Assert.AreEqual(Limit + 1 + 4, document.Keys.Count);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            index += needle.Length;
        }

        return count;
    }

    private sealed class LastProgress : IProgress<ExportProgress>
    {
        public ExportProgress? Completed { get; private set; }

        public void Report(ExportProgress value)
        {
            if (value.Phase == ExportPhase.Completed)
            {
                Completed = value;
            }
        }
    }

    /// <summary>
    /// A chain of keys named "k" below the start key, <paramref name="levels"/> deep. With rootOnly, the chain hangs
    /// off HKEY_CURRENT_USER itself and the other roots are empty.
    /// </summary>
    private sealed class ChainSource : IRegistryExportSource
    {
        private readonly int _levels;
        private readonly int _startComponents;

        public ChainSource(int levels, bool rootOnly = false)
        {
            _levels = levels;
            _startComponents = rootOnly ? 0 : RegistryPath.Parse(StartText).Components.Count;
        }

        public int DeepestEnumerated { get; private set; }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default) =>
            new(
                "k",
                path,
                subKeyCount: 0,
                valueCount: 0,
                DateTime.UnixEpoch,
                maxSubKeyNameLength: 0,
                maxValueNameLength: 0,
                maxValueDataLength: 0,
                className: string.Empty);

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            DeepestEnumerated = Math.Max(DeepestEnumerated, LevelOf(path) + 1);
            return Array.Empty<RegistryValueInfo>();
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            if (!IsOnChain(path))
            {
                return Array.Empty<string>();
            }

            return LevelOf(path) < _levels ? new[] { "k" } : Array.Empty<string>();
        }

        private bool IsOnChain(RegistryPath path) =>
            path.Components.Count >= _startComponents
            && (_startComponents > 0 || path.Root == RegistryRoot.CurrentUser);

        private int LevelOf(RegistryPath path) => path.Components.Count - _startComponents;
    }
}
