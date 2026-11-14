using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Offline export coverage: the stable text format, whole-registry root selection, format
/// selection, progress, and cancellation. No test here reads or writes the live registry.
/// </summary>
[TestClass]
public sealed class RegistryExportTests
{
    private static readonly DateTime LastWrite = new(2026, 11, 11, 12, 34, 56, DateTimeKind.Utc);

    [TestMethod]
    public void TextExportWritesStableKeyAndValueBlocks()
    {
        FakeExportSource source = BuildTextFixture();
        var writer = new StringWriter();
        var progress = new RecordingProgress();

        RegistryTextExporter.Export(
            source,
            RegPath(@"HKCU\Software\Bwc\Test"),
            writer,
            CancellationToken.None,
            progress);

        string expected =
            "Key Name:          HKEY_CURRENT_USER\\Software\\Bwc\\Test\r\n"
            + "Class Name:        <NO CLASS>\r\n"
            + "Last Write Time:   2026-11-11 12:34:56 UTC\r\n"
            + "\r\n"
            + "Value 0\r\n"
            + "  Name:            <NO NAME>\r\n"
            + "  Type:            REG_SZ\r\n"
            + "  Data:            hello\r\n"
            + "\r\n"
            + "Value 1\r\n"
            + "  Name:            Text\r\n"
            + "  Type:            REG_SZ\r\n"
            + "  Data:            C:\\\\Path\\\\To\\\\File\r\n"
            + "\r\n"
            + "Value 2\r\n"
            + "  Name:            Units\r\n"
            + "  Type:            REG_DWORD\r\n"
            + "  Data:            0x00001388 (5000)\r\n"
            + "\r\n"
            + "Value 3\r\n"
            + "  Name:            Multi\r\n"
            + "  Type:            REG_MULTI_SZ\r\n"
            + "  Data:            \"one\", \"\", \"two\"\r\n"
            + "\r\n"
            + "Value 4\r\n"
            + "  Name:            Binary\r\n"
            + "  Type:            REG_BINARY\r\n"
            + "  Data:            00 FE\r\n"
            + "\r\n"
            + "Value 5\r\n"
            + "  Name:            EmptyText\r\n"
            + "  Type:            REG_SZ\r\n"
            + "  Data:            (value not set)\r\n"
            + "\r\n"
            + "Value 6\r\n"
            + "  Name:            Lines\r\n"
            + "  Type:            REG_SZ\r\n"
            + "  Data:            a\\nb\r\n"
            + "\r\n"
            + "Key Name:          HKEY_CURRENT_USER\\Software\\Bwc\\Test\\Child\r\n"
            + "Class Name:        MyClass\r\n"
            + "Last Write Time:   2026-11-11 12:34:56 UTC\r\n"
            + "\r\n";

        Assert.AreEqual(expected, writer.ToString());
        Assert.AreEqual(ExportPhase.Completed, progress.Events[^1].Phase);
        Assert.AreEqual(2, progress.Events[^1].KeysExported);
        Assert.AreEqual(7, progress.Events[^1].ValuesExported);
        // Value progress is throttled to one report per interval, so only the first value report is
        // guaranteed. The Completed totals above carry the full count.
        Assert.IsTrue(
            progress.Events.Any(item => item.Phase == ExportPhase.ValueExported),
            "No value progress event was reported.");
    }

    [TestMethod]
    public void TextExportObservesCancellationBetweenKeys()
    {
        FakeExportSource source = BuildTextFixture();
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(item =>
        {
            if (item.Phase == ExportPhase.KeyExported)
            {
                cancellation.Cancel();
            }
        });

        Assert.ThrowsExactly<OperationCanceledException>(
            () => RegistryTextExporter.Export(
                source,
                RegPath(@"HKCU\Software\Bwc\Test"),
                new StringWriter(),
                cancellation.Token,
                progress));

        Assert.IsTrue(progress.Events.Any(item => item.Phase == ExportPhase.KeyExported));
        Assert.IsFalse(progress.Events.Any(item => item.Phase == ExportPhase.Completed));
    }

    [TestMethod]
    public void TextExportSkipsAnInaccessibleChildSubtree()
    {
        FakeExportSource source = BuildTextFixture();
        source.Denied.Add(RegPath(@"HKCU\Software\Bwc\Test\Child"));
        var writer = new StringWriter();
        var progress = new RecordingProgress();

        RegistryTextExporter.Export(
            source,
            RegPath(@"HKCU\Software\Bwc\Test"),
            writer,
            CancellationToken.None,
            progress);

        string text = writer.ToString();
        Assert.IsTrue(text.Contains("Key Name:          HKEY_CURRENT_USER\\Software\\Bwc\\Test\r\n", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(@"Software\Bwc\Test\Child", StringComparison.Ordinal));
        Assert.IsTrue(progress.Events.Any(item => item.Phase == ExportPhase.KeySkipped));
    }

    [TestMethod]
    public void WholeRegistryExportCoversEveryClassicRootInOrder()
    {
        FakeExportSource source = new();
        foreach (RegistryPath root in RegistryExportRoots.Classic)
        {
            source.Add(root);
        }

        RegFileDocument document = RegFileExporter.ExportWholeRegistry(
            source,
            RegFileFormat.V5,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            RegistryExportRoots.Classic.Select(RegPathText).ToArray(),
            document.Keys.Select(key => key.Path).ToArray());

        var writer = new StringWriter();
        RegistryTextExporter.ExportWholeRegistry(source, writer, CancellationToken.None);
        string[] keyLines = writer.ToString()
            .Split("\r\n")
            .Where(line => line.StartsWith("Key Name:", StringComparison.Ordinal))
            .ToArray();

        CollectionAssert.AreEqual(
            RegistryExportRoots.Classic
                .Select(root => "Key Name:          " + RegPathText(root))
                .ToArray(),
            keyLines);
    }

    [TestMethod]
    public void WholeRegistryExportSkipsAnInaccessibleRootAndKeepsTheRest()
    {
        FakeExportSource source = new();
        foreach (RegistryPath root in RegistryExportRoots.Classic)
        {
            source.Add(root);
        }

        source.Denied.Add(RegPath(@"HKU"));
        var progress = new RecordingProgress();

        RegFileDocument document = RegFileExporter.ExportWholeRegistry(
            source,
            RegFileFormat.V5,
            CancellationToken.None,
            progress);

        Assert.AreEqual(4, document.Keys.Count);
        Assert.IsFalse(document.Keys.Any(key => key.Path.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(progress.Events.Any(item => item.Phase == ExportPhase.KeySkipped));
    }

    [TestMethod]
    public void ServiceDefaultsToV5AndSelectsRegedit4OnRequest()
    {
        FakeExportSource source = BuildTextFixture();
        var service = new RegistryExportService(source);
        RegistryPath key = RegPath(@"HKCU\Software\Bwc\Test");

        RegFileDocument defaultDocument = service.ExportRegFile(key);
        Assert.AreEqual(RegFileFormat.V5, defaultDocument.Format);

        using var v5 = new MemoryStream();
        service.WriteRegFile(key, v5, RegFileFormat.V5);
        Assert.AreEqual(0xFF, v5.ToArray()[0]);
        Assert.AreEqual(0xFE, v5.ToArray()[1]);

        RegFileDocument regedit4 = service.ExportRegFile(key, RegFileFormat.Regedit4);
        Assert.AreEqual(RegFileFormat.Regedit4, regedit4.Format);

        using var ansi = new MemoryStream();
        service.WriteRegFile(key, ansi, RegFileFormat.Regedit4);
        Assert.AreEqual((byte)'R', ansi.ToArray()[0]);
    }

    [TestMethod]
    public void FiveAndFourGoldenFilesRemainByteIdentical()
    {
        AssertGolden("bwc-oracle-v5.reg", RegFileFormat.V5);
        AssertGolden("bwc-oracle-many300-v5.reg", RegFileFormat.V5);
        AssertGolden("bwc-oracle-regedit4.reg", RegFileFormat.Regedit4);
    }

    [TestMethod]
    public void RepeatableArtifactRecordsCasesAndOutputs()
    {
        FakeExportSource source = BuildTextFixture();
        var writer = new StringWriter();
        RegistryTextExporter.Export(
            source,
            RegPath(@"HKCU\Software\Bwc\Test"),
            writer,
            CancellationToken.None);

        var goldenCases = new[]
        {
            GoldenCase("bwc-oracle-v5.reg", RegFileFormat.V5),
            GoldenCase("bwc-oracle-many300-v5.reg", RegFileFormat.V5),
            GoldenCase("bwc-oracle-regedit4.reg", RegFileFormat.Regedit4),
        };

        var result = new
        {
            schema = 1,
            generatedBy = "tests/Bwc.Registry.Tests/RegistryExportTests.cs",
            cases = new object[]
            {
                new { name = "text-export-shape", output = writer.ToString() },
                new
                {
                    name = "whole-registry-roots",
                    output = RegistryExportRoots.Classic.Select(RegPathText).ToArray(),
                },
                new
                {
                    name = "regedit4-header-and-encoding",
                    output = Encoding.ASCII.GetString(
                        WriteDocument(
                            new RegFileDocument(
                                RegFileFormat.Regedit4,
                                new[]
                                {
                                    new RegFileKey(
                                        @"HKEY_CURRENT_USER\Software\Bwc\Artifact",
                                        new[] { new RegFileValue("Text", 1, Encoding.Unicode.GetBytes("A\0")) }),
                                }),
                            RegFileFormat.Regedit4)),
                },
                new
                {
                    name = "hive-save",
                    output = "Requires SeBackupPrivilege and SeRestorePrivilege; covered by the Elevated hive round-trip test.",
                },
                new { name = "goldens", output = goldenCases },
            },
        };

        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        string path = Path.Combine(FindRepositoryRoot(), "artifacts", "export-formats", "result.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.IsTrue(File.Exists(path), path);
        Assert.IsTrue(goldenCases.All(item => item.ByteIdentical), "A golden case was not byte identical.");
    }

    private static FakeExportSource BuildTextFixture()
    {
        var source = new FakeExportSource { LastWriteTimeUtc = LastWrite };
        RegistryPath root = RegPath(@"HKCU\Software\Bwc\Test");
        RegistryPath child = root.Append("Child");

        source.Add(
            root,
            values: new[]
            {
                new RegistryValueInfo(string.Empty, 1, Encoding.Unicode.GetBytes("hello\0")),
                new RegistryValueInfo("Text", 1, Encoding.Unicode.GetBytes(@"C:\Path\To\File" + "\0")),
                new RegistryValueInfo("Units", 4, RegistryValueCodec.EncodeDword(5000)),
                new RegistryValueInfo("Multi", 7, RegistryValueCodec.EncodeMultiString(new[] { "one", string.Empty, "two" })),
                new RegistryValueInfo("Binary", 3, new byte[] { 0x00, 0xFE }),
                new RegistryValueInfo("EmptyText", 1, Array.Empty<byte>()),
                new RegistryValueInfo("Lines", 1, Encoding.Unicode.GetBytes("a\nb\0")),
            });
        source.Add(child, className: "MyClass");

        return source;
    }

    private static void AssertGolden(string fileName, RegFileFormat format)
    {
        byte[] source = ReadGolden(fileName);
        RegFileDocument document;

        using (var input = new MemoryStream(source))
        {
            document = RegFileReader.Parse(input);
        }

        Assert.AreEqual(format, document.Format, fileName);
        byte[] actual = WriteDocument(document, format);

        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(source)),
            Convert.ToHexString(SHA256.HashData(actual)),
            fileName);
        CollectionAssert.AreEqual(source, actual, fileName);
    }

    private static GoldenCaseResult GoldenCase(
        string fileName,
        RegFileFormat format)
    {
        byte[] source = ReadGolden(fileName);
        RegFileDocument document;

        using (var input = new MemoryStream(source))
        {
            document = RegFileReader.Parse(input);
        }

        byte[] actual = WriteDocument(document, format);
        bool byteIdentical = source.AsSpan().SequenceEqual(actual);
        return new GoldenCaseResult(
            "golden-" + fileName,
            new
            {
                format = format.ToString(),
                sha256 = Convert.ToHexString(SHA256.HashData(source)),
                actualSha256 = Convert.ToHexString(SHA256.HashData(actual)),
                byteIdentical,
            },
            byteIdentical);
    }

    private static byte[] WriteDocument(RegFileDocument document, RegFileFormat format)
    {
        using var output = new MemoryStream();
        if (format == RegFileFormat.V5)
        {
            RegFileWriter.WriteV5(document, output);
        }
        else
        {
            RegFileWriter.WriteRegedit4(document, output);
        }

        return output.ToArray();
    }

    private static byte[] ReadGolden(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "oracle", fileName));

    private static RegistryPath RegPath(string value) => RegistryPath.Parse(value);

    private static string RegPathText(RegistryPath path) => path.ToDisplayString()["Computer\\".Length..];

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterWindowsConfig.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be found above the test output directory.");
    }

    private sealed record GoldenCaseResult(string Name, object Output, bool ByteIdentical);

    private sealed class RecordingProgress : IProgress<ExportProgress>
    {
        private readonly Action<ExportProgress>? _onReport;

        public RecordingProgress(Action<ExportProgress>? onReport = null)
        {
            _onReport = onReport;
        }

        public List<ExportProgress> Events { get; } = new();

        public void Report(ExportProgress value)
        {
            Events.Add(value);
            _onReport?.Invoke(value);
        }
    }

    private sealed class FakeExportSource : IRegistryExportSource
    {
        private readonly Dictionary<RegistryPath, FakeKey> _keys = new();

        public DateTime LastWriteTimeUtc { get; set; } = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        public HashSet<RegistryPath> Denied { get; } = new();

        public void Add(
            RegistryPath path,
            string className = "",
            IReadOnlyList<RegistryValueInfo>? values = null)
        {
            _keys[path] = new FakeKey(path, className, values ?? Array.Empty<RegistryValueInfo>());

            if (path.Parent is not null && _keys.TryGetValue(path.Parent, out FakeKey? parent))
            {
                parent.SubKeys.Add(path.Components[^1]);
            }
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FakeKey key = Get(path);
            return new RegistryKeyInfo(
                key.Path.Components.Count == 0 ? key.Path.Root.DisplayName() : key.Path.Components[^1],
                path,
                key.SubKeys.Count,
                key.Values.Count,
                LastWriteTimeUtc,
                0,
                0,
                0,
                key.ClassName);
        }

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Get(path).Values;
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Get(path).SubKeys;
        }

        private FakeKey Get(RegistryPath path)
        {
            if (Denied.Contains(path))
            {
                throw new RegistryOperationException("OpenKey", path, 5);
            }

            if (!_keys.TryGetValue(path, out FakeKey? key))
            {
                throw new RegistryOperationException("OpenKey", path, 2);
            }

            return key;
        }

        private sealed class FakeKey
        {
            public FakeKey(
                RegistryPath path,
                string className,
                IReadOnlyList<RegistryValueInfo> values)
            {
                Path = path;
                ClassName = className;
                Values = values;
            }

            public RegistryPath Path { get; }

            public string ClassName { get; }

            public IReadOnlyList<RegistryValueInfo> Values { get; }

            public List<string> SubKeys { get; } = new();
        }
    }
}
