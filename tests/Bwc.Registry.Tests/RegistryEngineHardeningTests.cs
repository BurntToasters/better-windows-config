using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Bwc.Registry.Tests;

/// <summary>
/// Hardening tests from docs/failure-modes/registry-writer.md (A11, B6, D3), reg-files.md
/// (sections 4 and 5), export.md (sections 5, 6, and 7), registry-reader.md (17), and
/// remote-registry.md (4). Nothing here writes to the live registry. The scanner tests use
/// temporary hive files, and the exporter tests use an in-memory source.
/// </summary>
[TestClass]
public sealed class RegistryEngineHardeningTests
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorKeyDeleted = 1018;
    private const int ErrorInvalidName = 123;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const string Root = @"HKCU\Software\BWC-Hardening\Fake";

    [TestMethod]
    public void ShippingPolicyRefusesCopyIntoOwnSubtreeBeforeAnyNativeCall()
    {
        var writer = new RegistryWriter(new ShippingWritePolicy());
        RegistryPath source = RegistryPath.Parse(@"HKCU\Software\BWC-E2E\hardening-probe\Src");

        Assert.ThrowsExactly<ArgumentException>(() => writer.CopyTree(source, source));
        Assert.ThrowsExactly<ArgumentException>(() => writer.CopyTree(source, source.Append("Inner")));
    }

    [TestMethod]
    public void WriterEmitsHexOneForRegSzWithLineBreakAndTheReaderRoundTripsIt()
    {
        byte[] data = Encoding.Unicode.GetBytes("first\nsecond\rthird\0");
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Test",
                    new[] { new RegFileValue("Text", 1, data) }),
            });

        using var stream = new MemoryStream();
        RegFileWriter.WriteV5(document, stream);
        string text = Encoding.Unicode.GetString(stream.ToArray(), 2, (int)stream.Length - 2);

        StringAssert.Contains(text, "\"Text\"=hex(1):");
        Assert.IsFalse(text.Contains("\\n", StringComparison.Ordinal), "A line feed escape is not regedit syntax.");
        Assert.IsFalse(text.Contains("\\r", StringComparison.Ordinal), "A carriage return escape is not regedit syntax.");

        stream.Position = 0;
        RegFileDocument parsed = RegFileReader.Parse(stream);
        CollectionAssert.AreEqual(data, parsed.Keys[0].Values[0].Data);
        Assert.AreEqual(1u, parsed.Keys[0].Values[0].Type);
    }

    [TestMethod]
    public void WriterStillEmitsQuotedStringWhenNoLineBreakIsPresent()
    {
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[]
            {
                new RegFileKey(
                    @"HKEY_CURRENT_USER\Software\Bwc\Test",
                    new[] { new RegFileValue("Text", 1, Encoding.Unicode.GetBytes("plain\0")) }),
            });

        using var stream = new MemoryStream();
        RegFileWriter.WriteV5(document, stream);
        string text = Encoding.Unicode.GetString(stream.ToArray(), 2, (int)stream.Length - 2);

        StringAssert.Contains(text, "\"Text\"=\"plain\"");
    }

    [TestMethod]
    public void WriterRefusesValueNameWithLineBreakOrNul()
    {
        foreach (string name in new[] { "A\nB", "A\rB", "A\0B" })
        {
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        @"HKEY_CURRENT_USER\Software\Bwc\Test",
                        new[] { new RegFileValue(name, 1, Encoding.Unicode.GetBytes("x\0")) }),
                });

            using var stream = new MemoryStream();
            Assert.ThrowsExactly<ArgumentException>(() => RegFileWriter.WriteV5(document, stream), name);
        }
    }

    [TestMethod]
    public void ReaderRejectsTheRemovedNulLineFeedAndCarriageReturnEscapes()
    {
        foreach (string escape in new[] { @"\0", @"\n", @"\r" })
        {
            string text = "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[HKEY_CURRENT_USER\\Software\\Bwc\\Test]\r\n"
                + "\"A\"=\"x" + escape + "y\"\r\n";

            using var stream = new MemoryStream(V5Bytes(text));
            Assert.ThrowsExactly<RegFileParseException>(() => RegFileReader.Parse(stream), escape);
        }
    }

    [TestMethod]
    public void ReaderStillAcceptsBackslashAndQuoteEscapes()
    {
        string text = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\Bwc\\Test]\r\n"
            + "\"A\"=\"x\\\\y\\\"z\"\r\n";

        using var stream = new MemoryStream(V5Bytes(text));
        RegFileDocument document = RegFileReader.Parse(stream);

        CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("x\\y\"z\0"), document.Keys[0].Values[0].Data);
    }

    [TestMethod]
    public void KeyNameWithBracketIsWrittenAsClassicDoesAndRefusedOnImport()
    {
        var document = new RegFileDocument(
            RegFileFormat.V5,
            new[] { new RegFileKey(@"HKEY_CURRENT_USER\Software\Bwc\C12]", Array.Empty<RegFileValue>()) });

        using var stream = new MemoryStream();
        RegFileWriter.WriteV5(document, stream);
        string text = Encoding.Unicode.GetString(stream.ToArray(), 2, (int)stream.Length - 2);
        StringAssert.Contains(text, "[HKEY_CURRENT_USER\\Software\\Bwc\\C12]]");

        stream.Position = 0;
        Assert.ThrowsExactly<RegFileParseException>(() => RegFileReader.Parse(stream));
    }

    [TestMethod]
    public void ExporterSkipsSubkeysThatVanishDuringExport()
    {
        var source = new FakeSource();
        source.AddKey(Root, "Keep", "Gone", "Deleted");
        source.AddKey(Root + @"\Keep");
        source.AddKey(Root + @"\Gone");
        source.AddKey(Root + @"\Deleted");
        source.FailValuesWith(Root + @"\Gone", ErrorFileNotFound);
        source.FailValuesWith(Root + @"\Deleted", ErrorKeyDeleted);

        var progress = new RecordingProgress();
        RegFileDocument document = RegFileExporter.Export(
            source,
            RegistryPath.Parse(Root),
            RegFileFormat.V5,
            CancellationToken.None,
            progress);

        CollectionAssert.AreEqual(
            new[]
            {
                @"HKEY_CURRENT_USER\Software\BWC-Hardening\Fake",
                @"HKEY_CURRENT_USER\Software\BWC-Hardening\Fake\Keep",
            },
            document.Keys.Select(key => key.Path).ToArray());
        Assert.AreEqual(2, progress.Events[^1].KeysSkipped);
    }

    [TestMethod]
    public void ExporterStillThrowsWhenTheStartKeyVanishes()
    {
        var source = new FakeSource();
        source.AddKey(Root);
        source.FailValuesWith(Root, ErrorFileNotFound);

        var error = Assert.ThrowsExactly<RegistryOperationException>(
            () => RegFileExporter.Export(source, RegistryPath.Parse(Root), RegFileFormat.V5));
        Assert.AreEqual(ErrorFileNotFound, error.Win32Error);
    }

    [TestMethod]
    public void ExporterRefusesKeyNameWithLineBreak()
    {
        var source = new FakeSource();
        source.AddKey(Root, "Bad\nKey");
        source.AddKey(Root + "\\Bad\nKey");

        var error = Assert.ThrowsExactly<RegistryOperationException>(
            () => RegFileExporter.Export(source, RegistryPath.Parse(Root), RegFileFormat.V5));
        Assert.AreEqual("ExportKeyName", error.Operation);
        Assert.AreEqual(ErrorInvalidName, error.Win32Error);
    }

    [TestMethod]
    public void ExporterRefusesValueNameWithCarriageReturn()
    {
        var source = new FakeSource();
        source.AddKey(Root);
        source.SetValues(Root, new RegistryValueInfo("A\rB", 1, Encoding.Unicode.GetBytes("x\0")));

        var error = Assert.ThrowsExactly<RegistryOperationException>(
            () => RegFileExporter.Export(source, RegistryPath.Parse(Root), RegFileFormat.V5));
        Assert.AreEqual("ExportValueName", error.Operation);
        Assert.AreEqual(ErrorInvalidName, error.Win32Error);
    }

    [TestMethod]
    public void ProgressFromAVeryLargeExportIsThrottledAndEndsWithExactTotals()
    {
        const int ChildCount = 3000;
        var source = new FakeSource();
        var children = new string[ChildCount];
        for (int index = 0; index < ChildCount; index += 1)
        {
            children[index] = "K" + index.ToString(CultureInfo.InvariantCulture);
        }

        source.AddKey(Root, children);
        foreach (string child in children)
        {
            source.AddKey(Root + @"\" + child);
        }

        var progress = new RecordingProgress();
        RegFileDocument document = RegFileExporter.Export(
            source,
            RegistryPath.Parse(Root),
            RegFileFormat.V5,
            CancellationToken.None,
            progress);

        Assert.AreEqual(ChildCount + 1, document.Keys.Count);
        Assert.AreEqual(ExportPhase.Starting, progress.Events[0].Phase);
        Assert.AreEqual(ExportPhase.Completed, progress.Events[^1].Phase);
        Assert.AreEqual(ChildCount + 1, progress.Events[^1].KeysExported);
        Assert.IsTrue(
            progress.Events.Count < ChildCount,
            $"Expected throttled progress, got {progress.Events.Count} reports for {ChildCount + 1} keys.");
    }

    [TestMethod]
    public void StreamedRegFileIsByteIdenticalToTheDocumentWriter()
    {
        var source = new FakeSource();
        source.AddKey(Root, "One", "Two");
        source.AddKey(Root + @"\One");
        source.AddKey(Root + @"\Two");
        source.SetValues(Root, new RegistryValueInfo(string.Empty, 1, Encoding.Unicode.GetBytes("default\0")));
        source.SetValues(
            Root + @"\One",
            new RegistryValueInfo("Num", 4, new byte[] { 0x2a, 0, 0, 0 }),
            new RegistryValueInfo("Text", 1, Encoding.Unicode.GetBytes("He said \"hi\" \\ left\0")));

        using var expected = new MemoryStream();
        RegFileWriter.WriteV5(
            RegFileExporter.Export(source, RegistryPath.Parse(Root), RegFileFormat.V5),
            expected);

        using var actual = new MemoryStream();
        new RegistryExportService(source).WriteRegFile(RegistryPath.Parse(Root), actual, RegFileFormat.V5);

        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

#if BWC_E2E
    // The scanner, the root pin, and the subkey collector are internal. The InternalsVisibleTo
    // grant exists only in the E2E flavor, so these tests compile only there.
    [TestMethod]
    public void ScanAcceptsAHiveWhoseBinSpansSeveralPages()
    {
        using var files = new TempFiles();
        string path = files.Write("multi.hiv", HiveFileFixture.BuildMultiPageHive(includeLinkChild: false));

        Assert.AreEqual(HiveScanOutcome.Clean, HiveFileLinkScanner.Scan(path).Outcome);
    }

    [TestMethod]
    public void ScanFindsALinkInASecondPageOfABin()
    {
        using var files = new TempFiles();
        string path = files.Write("multi-link.hiv", HiveFileFixture.BuildMultiPageHive(includeLinkChild: true));

        HiveScanResult result = HiveFileLinkScanner.Scan(path);

        Assert.AreEqual(HiveScanOutcome.Linked, result.Outcome);
        Assert.AreEqual(@"Root\Child", result.KeyPath);
    }

    [TestMethod]
    public void ScanRefusesABinChainThatDoesNotAddUp()
    {
        using var files = new TempFiles();
        string path = files.Write("broken.hiv", HiveFileFixture.BuildBrokenBinChainHive());

        Assert.AreEqual(HiveScanOutcome.Unverifiable, HiveFileLinkScanner.Scan(path).Outcome);
    }

    [TestMethod]
    public void ScanHoldsTheHiveFileAgainstWritersUntilReleased()
    {
        using var files = new TempFiles();
        string path = files.Write("held.hiv", HiveFileFixture.BuildHive(includeLinkChild: false));

        using (HeldHiveScan held = HiveFileLinkScanner.OpenAndScan(path))
        {
            Assert.AreEqual(HiveScanOutcome.Clean, held.Result.Outcome);

            Assert.ThrowsExactly<IOException>(() =>
            {
                using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
            Assert.ThrowsExactly<IOException>(() => File.Delete(path));
        }

        using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
        }
    }

    [TestMethod]
    public void RootPinKeepsTheHandleUsableAfterTheConnectionIsDisposed()
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? software = hkcu.OpenSubKey("Software");
        Assert.IsNotNull(software);
        using RegistryKey? softwareAgain = hkcu.OpenSubKey("Software");
        Assert.IsNotNull(softwareAgain);

        nint localMachine = DetachHandle(software!);
        nint users = DetachHandle(softwareAgain!);

        RemoteRegistryConnection connection = RemoteRegistryConnection.CreateForTesting(
            "hardening-test",
            SafeRegistryKeyHandle.FromOwned(localMachine),
            SafeRegistryKeyHandle.FromOwned(users));

        SafeRegistryKeyHandle pin = connection.GetRoot(RegistryRoot.LocalMachine);
        connection.Dispose();

        // The pin still holds the key open, so a read through a new wrapper succeeds.
        var probe = new SafeRegistryHandle(pin.DangerousGetHandle(), ownsHandle: false);
        using (RegistryKey key = RegistryKey.FromHandle(probe, RegistryView.Registry64))
        {
            Assert.IsTrue(key.SubKeyCount >= 0);
        }

        pin.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => connection.GetRoot(RegistryRoot.Users));
    }

    [TestMethod]
    public void EnumerateSubKeyNamesGivesUpAfterTheRetryCap()
    {
        int calls = 0;
        var error = Assert.ThrowsExactly<RegistryOperationException>(() =>
            RegistryReader.CollectSubKeyNames(
                RegistryPath.Parse(Root),
                initialCapacity: 4,
                (uint index, char[] buffer, ref uint length) =>
                {
                    calls += 1;
                    length = (uint)buffer.Length * 2;
                    return ErrorMoreData;
                }));

        Assert.AreEqual(ErrorMoreData, error.Win32Error);
        Assert.AreEqual(16, calls, "The name buffer must be retried at most 16 times for one index.");
    }

    [TestMethod]
    public void EnumerateSubKeyNamesGrowsTheBufferAndReturnsTheName()
    {
        string[] names = { "AVeryLongSubkeyName" };
        IReadOnlyList<string> result = RegistryReader.CollectSubKeyNames(
            RegistryPath.Parse(Root),
            initialCapacity: 4,
            (uint index, char[] buffer, ref uint length) =>
            {
                if (index >= names.Length)
                {
                    return ErrorNoMoreItems;
                }

                string name = names[index];
                if (buffer.Length < name.Length + 1)
                {
                    length = (uint)name.Length + 1;
                    return ErrorMoreData;
                }

                name.CopyTo(buffer);
                length = (uint)name.Length;
                return 0;
            });

        CollectionAssert.AreEqual(names, result.ToArray());
    }

#endif

    private static byte[] V5Bytes(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        byte[] result = new byte[body.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        body.CopyTo(result, 2);
        return result;
    }

#if BWC_E2E
    private static nint DetachHandle(RegistryKey key)
    {
        nint raw = key.Handle.DangerousGetHandle();
        key.Handle.SetHandleAsInvalid();
        return raw;
    }

    private sealed class TempFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "BwcHardening",
            Guid.NewGuid().ToString("N"));

        public TempFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Write(string name, byte[] content)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed temp cleanup does not affect the registry state under test.
            }
        }
    }

#endif

    private sealed class RecordingProgress : IProgress<ExportProgress>
    {
        public List<ExportProgress> Events { get; } = new();

        public void Report(ExportProgress value) => Events.Add(value);
    }

    /// <summary>In-memory export source. Keys are stored by abbreviated path, case-insensitively.</summary>
    private sealed class FakeSource : IRegistryExportSource
    {
        private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<RegistryValueInfo>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _valueFailures = new(StringComparer.OrdinalIgnoreCase);

        public void AddKey(string path, params string[] childNames)
        {
            if (!_children.TryGetValue(path, out List<string>? children))
            {
                children = new List<string>();
                _children[path] = children;
            }

            children.AddRange(childNames);
        }

        public void SetValues(string path, params RegistryValueInfo[] values)
        {
            _values[path] = new List<RegistryValueInfo>(values);
        }

        public void FailValuesWith(string path, int win32Error)
        {
            _valueFailures[path] = win32Error;
        }

        public RegistryKeyInfo GetKeyInfo(RegistryPath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<RegistryValueInfo> EnumerateValues(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            string key = path.ToAbbreviatedString();
            if (_valueFailures.TryGetValue(key, out int error))
            {
                throw new RegistryOperationException("EnumerateValues", path, error);
            }

            return _values.TryGetValue(key, out List<RegistryValueInfo>? values)
                ? values
                : Array.Empty<RegistryValueInfo>();
        }

        public IReadOnlyList<string> EnumerateSubKeyNames(
            RegistryPath path,
            CancellationToken cancellationToken = default)
        {
            return _children.TryGetValue(path.ToAbbreviatedString(), out List<string>? names)
                ? names
                : Array.Empty<string>();
        }
    }
}
