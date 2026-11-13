#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegFileExporterTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        using Microsoft.Win32.RegistryKey hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Registry64);
        using Microsoft.Win32.RegistryKey? parent = hkcu.CreateSubKey(SandboxBase, writable: true);
        Assert.IsNotNull(parent, "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void ExportWalksKeysInEnumerationOrderAndPreservesRawValues()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath child = runRoot.Append("Child");
        RegistryPath emptyChild = runRoot.Append("EmptyChild");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, string.Empty, "default text");
            store.SetString(runRoot, "Text", "hello");
            store.SetExpandString(runRoot, "Expand", "%TEMP%\\demo");
            store.SetBinary(runRoot, "Binary", new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
            store.SetBinary(runRoot, "EmptyBinary", ReadOnlySpan<byte>.Empty);
            store.SetDword(runRoot, "Dword", 0x01020304u);
            store.SetQword(runRoot, "Qword", 0x1122334455667788UL);
            store.SetMultiString(runRoot, "Multi", new[] { "one", string.Empty, "two" });
            store.CreateKey(child);
            store.SetString(child, "Nested", "world");
            store.CreateKey(emptyChild);

            RegistryReader reader = store.Reader;
            string[] expectedPaths = EnumeratePreOrder(reader, runRoot);

            RegFileDocument document = RegFileExporter.Export(
                reader,
                runRoot,
                RegFileFormat.V5,
                CancellationToken.None);

            Assert.AreEqual(RegFileFormat.V5, document.Format);
            CollectionAssert.AreEqual(
                expectedPaths,
                document.Keys.Select(key => key.Path).ToArray());

            RegFileKey rootKey = FindKey(document, runRoot);
            Assert.AreEqual(8, rootKey.Values.Count);
            AssertValue(FindValue(rootKey, string.Empty), 1, Encoding.Unicode.GetBytes("default text\0"));
            AssertValue(FindValue(rootKey, "Text"), 1, Encoding.Unicode.GetBytes("hello\0"));
            AssertValue(FindValue(rootKey, "Expand"), 2, Encoding.Unicode.GetBytes("%TEMP%\\demo\0"));
            AssertValue(FindValue(rootKey, "Binary"), 3, new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
            AssertValue(FindValue(rootKey, "EmptyBinary"), 3, Array.Empty<byte>());
            AssertValue(FindValue(rootKey, "Dword"), 4, new byte[] { 0x04, 0x03, 0x02, 0x01 });
            AssertValue(
                FindValue(rootKey, "Qword"),
                11,
                new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 });
            AssertValue(
                FindValue(rootKey, "Multi"),
                7,
                Encoding.Unicode.GetBytes("one\0\0two\0\0"));

            RegFileKey childKey = FindKey(document, child);
            AssertValue(FindValue(childKey, "Nested"), 1, Encoding.Unicode.GetBytes("world\0"));
            Assert.AreEqual(0, FindKey(document, emptyChild).Values.Count);

            using var stream = new MemoryStream();
            RegFileWriter.Write(document, stream);
            stream.Position = 0;
            RegFileDocument parsed = RegFileReader.Parse(stream);

            Assert.AreEqual(RegFileFormat.V5, parsed.Format);
            CollectionAssert.AreEqual(expectedPaths, parsed.Keys.Select(key => key.Path).ToArray());
            AssertValue(
                FindValue(FindKey(parsed, runRoot), "Binary"),
                3,
                new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
            AssertValue(
                FindValue(FindKey(parsed, runRoot), "Multi"),
                7,
                Encoding.Unicode.GetBytes("one\0\0two\0\0"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ExportRegedit4WritesAnsiWithoutBomAndParsesBack()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, string.Empty, "default text");
            store.SetString(runRoot, "Text", "hello");
            store.SetExpandString(runRoot, "Expand", "%TEMP%\\demo");
            store.SetBinary(runRoot, "Binary", new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
            store.SetBinary(runRoot, "EmptyBinary", ReadOnlySpan<byte>.Empty);
            store.SetDword(runRoot, "Dword", 0x01020304u);
            store.SetQword(runRoot, "Qword", 0x1122334455667788UL);
            store.SetMultiString(runRoot, "Multi", new[] { "one", string.Empty, "two" });

            RegFileDocument document = RegFileExporter.Export(
                store.Reader,
                runRoot.ToAbbreviatedString(),
                RegFileFormat.Regedit4,
                CancellationToken.None);

            Assert.AreEqual(RegFileFormat.Regedit4, document.Format);

            using var stream = new MemoryStream();
            RegFileWriter.Write(document, stream);
            byte[] bytes = stream.ToArray();

            Assert.AreNotEqual(0xFF, bytes[0]);
            Assert.AreEqual((byte)'R', bytes[0]);

            using var input = new MemoryStream(bytes);
            RegFileDocument parsed = RegFileReader.Parse(input);
            Assert.AreEqual(RegFileFormat.Regedit4, parsed.Format);

            RegFileKey parsedRoot = FindKey(parsed, runRoot);
            AssertValue(FindValue(parsedRoot, string.Empty), 1, Encoding.Unicode.GetBytes("default text\0"));
            AssertValue(FindValue(parsedRoot, "Text"), 1, Encoding.Unicode.GetBytes("hello\0"));
            AssertValue(FindValue(parsedRoot, "Expand"), 2, Encoding.Unicode.GetBytes("%TEMP%\\demo\0"));
            AssertValue(FindValue(parsedRoot, "Binary"), 3, new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
            AssertValue(FindValue(parsedRoot, "EmptyBinary"), 3, Array.Empty<byte>());
            AssertValue(FindValue(parsedRoot, "Dword"), 4, new byte[] { 0x04, 0x03, 0x02, 0x01 });
            AssertValue(
                FindValue(parsedRoot, "Qword"),
                11,
                new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 });
            AssertValue(
                FindValue(parsedRoot, "Multi"),
                7,
                Encoding.Unicode.GetBytes("one\0\0two\0\0"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ExportObservesCancellationBeforeReadingTheStartKey()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, "Text", "hello");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(
                () => RegFileExporter.Export(
                    store.Reader,
                    runRoot,
                    RegFileFormat.V5,
                    cancellation.Token));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ExportRefusesValueNamesWithEmbeddedNulInEveryFormat()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        const string NulName = "abc\0def";

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.SetValue(runRoot, "plain", 1, Encoding.Unicode.GetBytes("x\0"));
            writer.SetValueWithCountedName(runRoot, NulName, 4, BitConverter.GetBytes(0x1388u));

            var reader = new RegistryReader();

            foreach (RegFileFormat format in new[] { RegFileFormat.V5, RegFileFormat.Regedit4 })
            {
                var error = Assert.ThrowsExactly<RegistryOperationException>(
                    () => RegFileExporter.Export(reader, runRoot, format, CancellationToken.None));

                Assert.AreEqual("ExportValueName", error.Operation, format.ToString());
                Assert.AreEqual(123, error.Win32Error, format.ToString());
                Assert.AreEqual(runRoot, error.Path, format.ToString());
            }
        }
        finally
        {
            new RegistryWriter(new SandboxWritePolicy(runRoot)).DeleteValueWithCountedName(runRoot, NulName);
            DeleteRunRoot(runId);
        }
    }

    private static string[] EnumeratePreOrder(RegistryReader reader, RegistryPath start)
    {
        var paths = new List<string>();

        void Walk(RegistryPath path)
        {
            paths.Add(WithoutComputerPrefix(path));
            foreach (string name in reader.EnumerateSubKeyNames(path))
            {
                Walk(path.Append(name));
            }
        }

        Walk(start);
        return paths.ToArray();
    }

    private static string WithoutComputerPrefix(RegistryPath path)
    {
        const string Prefix = "Computer\\";
        string display = path.ToDisplayString();
        return display.StartsWith(Prefix, StringComparison.Ordinal)
            ? display[Prefix.Length..]
            : display;
    }

    private static RegFileKey FindKey(RegFileDocument document, RegistryPath path)
    {
        string expected = WithoutComputerPrefix(path);
        RegFileKey? key = document.Keys.FirstOrDefault(
            candidate => string.Equals(candidate.Path, expected, StringComparison.OrdinalIgnoreCase));
        return key ?? throw new AssertFailedException($"The exported key '{expected}' is missing.");
    }

    private static RegFileValue FindValue(RegFileKey key, string name)
    {
        RegFileValue? value = key.Values.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return value ?? throw new AssertFailedException(
            $"The exported value '{name}' is missing from '{key.Path}'.");
    }

    private static void AssertValue(RegFileValue value, uint type, byte[] data)
    {
        Assert.AreEqual(type, value.Type, value.Name);
        CollectionAssert.AreEqual(data, value.Data, value.Name);
    }

    private static string NewRunId() => "export-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static void DeleteRunRoot(string runId)
    {
        using Microsoft.Win32.RegistryKey hkcu = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Registry64);
        using Microsoft.Win32.RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
