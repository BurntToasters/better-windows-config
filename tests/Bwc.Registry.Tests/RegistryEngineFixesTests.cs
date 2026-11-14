using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Engine regressions from docs/failure-modes/export.md (dot-named keys, text export skip set and
/// progress throttle) and registry-store.md (odd-length REG_MULTI_SZ). Uses an in-memory source only.
/// </summary>
[TestClass]
public sealed class RegistryEngineFixesTests
{
    private static readonly RegistryPath Root = RegistryPath.Parse(@"HKCU\Software\BWC-Fake\Root");

    [TestMethod]
    public void RegFileExportWritesKeysNamedDotAndDotDot()
    {
        var source = new TreeSource();
        source.Add(Root);
        source.Add(Root.AppendEnumeratedName("."));
        source.Add(Root.AppendEnumeratedName(".."));
        source.Add(Root.Append("Plain"));

        RegFileDocument document = RegFileExporter.Export(source, Root, RegFileFormat.V5, CancellationToken.None);

        Assert.AreEqual(4, document.Keys.Count, "The root, '.', '..', and 'Plain' must all be exported.");
    }

    [TestMethod]
    public void TextExportWritesKeysNamedDotAndDotDot()
    {
        var source = new TreeSource();
        source.Add(Root);
        source.Add(Root.AppendEnumeratedName("."));
        source.Add(Root.AppendEnumeratedName(".."));
        source.Add(Root.Append("Plain"));

        using var writer = new StringWriter();
        RegistryTextExporter.Export(source, Root, writer);

        Assert.AreEqual(4, CountOccurrences(writer.ToString(), "Key Name:"));
    }

    [TestMethod]
    public void TextExportSkipsChildrenThatVanishedOrWereDeletedLikeTheRegExport()
    {
        var source = new TreeSource();
        source.Add(Root);
        source.Add(Root.Append("Gone"));
        source.Add(Root.Append("Deleted"));
        source.Add(Root.Append("Plain"));
        source.Errors[Root.Append("Gone")] = 2;
        source.Errors[Root.Append("Deleted")] = 1018;

        using var writer = new StringWriter();
        RegistryTextExporter.Export(source, Root, writer);
        string text = writer.ToString();

        Assert.IsFalse(text.Contains("\\Gone", StringComparison.Ordinal), "A file-not-found child must be skipped.");
        Assert.IsFalse(text.Contains("\\Deleted", StringComparison.Ordinal), "A deleted child must be skipped.");
        Assert.IsTrue(text.Contains("\\Plain", StringComparison.Ordinal), "The siblings after the skips must export.");
    }

    [TestMethod]
    public void TextExportThrottlesKeyProgressReports()
    {
        var source = new TreeSource();
        source.Add(Root);
        for (int index = 0; index < 3000; index += 1)
        {
            source.Add(Root.Append("K" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var events = new List<ExportProgress>();
        using var writer = new StringWriter();
        RegistryTextExporter.Export(source, Root, writer, CancellationToken.None, new Collector(events));

        int keyEvents = events.Count(item => item.Phase == ExportPhase.KeyExported);
        Assert.IsTrue(keyEvents < 300, $"Expected throttled key progress, got {keyEvents} posts for 3001 keys.");
        Assert.AreEqual(ExportPhase.Completed, events[^1].Phase);
        Assert.AreEqual(3001, events[^1].KeysExported);
    }

    [TestMethod]
    public void OddLengthMultiStringKeepsItsTrailingByteOnSave()
    {
        byte[] original = { 0x61, 0x00, 0x00, 0x00, 0x41 };

        MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(original);
        byte[] saved = RegistryValueCodec.EncodeMultiString(decoded.Strings, original);

        CollectionAssert.AreEqual(original, saved);

        byte[] changed = RegistryValueCodec.EncodeMultiString(new[] { "b" }, original);
        CollectionAssert.AreEqual(new byte[] { 0x62, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41 }, changed);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count += 1;
            index += value.Length;
        }

        return count;
    }

    private sealed class Collector : IProgress<ExportProgress>
    {
        private readonly List<ExportProgress> _events;

        public Collector(List<ExportProgress> events)
        {
            _events = events;
        }

        public void Report(ExportProgress value) => _events.Add(value);
    }

    private sealed class TreeSource : IRegistryExportSource
    {
        private readonly Dictionary<RegistryPath, List<string>> _subKeys = new();
        private readonly HashSet<RegistryPath> _known = new();

        public Dictionary<RegistryPath, int> Errors { get; } = new();

        public void Add(RegistryPath path)
        {
            _known.Add(path);
            _subKeys.TryAdd(path, new List<string>());

            if (path.Parent is not null && _subKeys.TryGetValue(path.Parent, out List<string>? siblings))
            {
                siblings.Add(path.Components[^1]);
            }
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default)
        {
            Check(path, cancellationToken);
            return new RegistryKeyInfo(
                path.Components[^1],
                path,
                _subKeys[path].Count,
                0,
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                0,
                0,
                0,
                string.Empty);
        }

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            Check(path, cancellationToken);
            return Array.Empty<RegistryValueInfo>();
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            Check(path, cancellationToken);
            return _subKeys[path].ToArray();
        }

        private void Check(RegistryPath path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Errors.TryGetValue(path, out int error))
            {
                throw new RegistryOperationException("OpenKey", path, error);
            }

            if (!_known.Contains(path))
            {
                throw new RegistryOperationException("OpenKey", path, 2);
            }
        }
    }
}
