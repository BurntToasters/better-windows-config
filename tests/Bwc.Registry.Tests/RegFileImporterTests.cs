#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegFileImporterTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.CreateSubKey(SandboxBase, writable: true);
        Assert.IsNotNull(parent, "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void ImportAppliesValuesDeletesAndIgnoresMissingTargets()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath child = runRoot.Append("Child");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(runRoot),
                        new[]
                        {
                            new RegFileValue(string.Empty, 1, Encoding.Unicode.GetBytes("default text\0")),
                            new RegFileValue("Text", 1, Encoding.Unicode.GetBytes("hello\0")),
                            new RegFileValue("Dword", 4, new byte[] { 0x04, 0x03, 0x02, 0x01 }),
                            new RegFileValue("Blob", 3, new byte[] { 0x00, 0x01, 0x02, 0xFF }),
                        }),
                    new RegFileKey(
                        WithoutComputerPrefix(child),
                        new[] { new RegFileValue("Nested", 1, Encoding.Unicode.GetBytes("world\0")) }),
                });

            RegFileImporter.Apply(store, document, CancellationToken.None);

            Assert.AreEqual("default text", ReadString(store, runRoot, string.Empty));
            Assert.AreEqual("hello", ReadString(store, runRoot, "Text"));
            Assert.AreEqual(0x01020304u, ReadDword(store, runRoot, "Dword"));
            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x01, 0x02, 0xFF },
                ReadBytes(store, runRoot, "Blob"));
            Assert.AreEqual("world", ReadString(store, child, "Nested"));

            var deleteDocument = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(runRoot),
                        new[]
                        {
                            new RegFileValue("Text", 0, Array.Empty<byte>(), delete: true),
                            new RegFileValue("Missing", 0, Array.Empty<byte>(), delete: true),
                        }),
                    new RegFileKey(
                        WithoutComputerPrefix(child),
                        Array.Empty<RegFileValue>(),
                        delete: true),
                });

            RegFileImporter.Apply(store, deleteDocument, CancellationToken.None);

            Assert.IsNull(ReadString(store, runRoot, "Text"));
            Assert.AreEqual("default text", ReadString(store, runRoot, string.Empty));
            Assert.IsFalse(store.Reader.KeyExists(child));

            // A second delete of the same missing key and value is a no-op.
            RegFileImporter.Apply(store, deleteDocument, CancellationToken.None);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ApplyFileSkipsAMalformedLineAndReportsItsLine()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath target = runRoot.Append("Malformed");
        string file = Path.Combine(Path.GetTempPath(), "bwc-reg-" + Guid.NewGuid().ToString("N") + ".reg");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            string text =
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + "[" + WithoutComputerPrefix(target) + "]\r\n"
                + "\"Name=missing-quote\r\n"
                + "\"After\"=\"kept\"\r\n";
            File.WriteAllBytes(file, EncodeV5(text));

            RegFileImportResult result = RegFileImporter.ApplyFile(store, file, CancellationToken.None);

            Assert.IsFalse(result.IsRefused);
            RegFileImportFailure failure = Assert.ContainsSingle(result.Failures);
            Assert.AreEqual(RegFileImportFailureKind.MalformedLine, failure.Kind);
            Assert.AreEqual(4, failure.Line);
            Assert.IsFalse(result.HasWriteFailures);
            Assert.AreEqual("kept", ReadString(store, target, "After"));
            Assert.IsNull(ReadString(store, target, "Name"));
        }
        finally
        {
            File.Delete(file);
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void PolicyDenialLeavesNoKeyOutsideSandbox()
    {
        string runId = NewRunId();
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath outside = RegistryPath.Parse($@"HKCU\Software\{outsideId}");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(outside),
                        new[] { new RegFileValue("Value", 4, new byte[] { 1, 0, 0, 0 }) }),
                });

            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            RegFileImportFailure failure = Assert.ContainsSingle(result.Failures);
            Assert.AreEqual(RegFileImportFailureKind.WriteFailed, failure.Kind);
            Assert.AreEqual(5, failure.Win32Error);
            Assert.AreEqual(WithoutComputerPrefix(outside), failure.KeyPath);
            Assert.AreEqual(0, result.KeysWritten);
            Assert.AreEqual(0, result.ValuesWritten);
            Assert.IsFalse(store.Reader.KeyExists(outside));

            var invalid = new RegFileDocument(
                RegFileFormat.V5,
                new[] { new RegFileKey(@"NotARoot\Key", Array.Empty<RegFileValue>()) });
            Assert.ThrowsExactly<ArgumentException>(
                () => RegFileImporter.Apply(store, invalid, CancellationToken.None));
        }
        finally
        {
            DeleteRunRoot(runId);
            DeleteRunRoot(outsideId);
        }
    }

    [TestMethod]
    public void ApplyObservesPreCancelledTokenBeforeWriting()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(runRoot),
                        new[] { new RegFileValue("Text", 1, Encoding.Unicode.GetBytes("hello\0")) }),
                });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(
                () => RegFileImporter.Apply(store, document, cancellation.Token));
            Assert.IsFalse(store.Reader.KeyExists(runRoot));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ReExportAfterImportMatchesGeneratedDocument()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath source = runRoot.Append("Source");
        RegistryPath target = runRoot.Append("Target");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            SeedCommonValues(store, source);

            RegFileDocument sourceDocument = RegFileExporter.Export(
                store.Reader,
                source,
                RegFileFormat.V5,
                CancellationToken.None);
            RegFileDocument targetDocument = Retarget(sourceDocument, source, target);

            RegFileImporter.Apply(store, targetDocument, CancellationToken.None);

            RegFileDocument reExported = RegFileExporter.Export(
                store.Reader,
                target,
                RegFileFormat.V5,
                CancellationToken.None);

            AssertDocumentsEqual(targetDocument, reExported);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ApplyCreatesMissingAncestorsTopDown()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath deep = runRoot.Append("Missing").Append("Ancestor").Append("Deep").Append("Key");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(deep),
                        new[] { new RegFileValue("Leaf", 1, Encoding.Unicode.GetBytes("deep\0")) }),
                });

            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            Assert.IsFalse(result.HasWriteFailures);
            Assert.AreEqual(1, result.KeysWritten);
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Missing")));
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Missing").Append("Ancestor")));
            Assert.IsTrue(store.Reader.KeyExists(runRoot.Append("Missing").Append("Ancestor").Append("Deep")));
            Assert.AreEqual("deep", ReadString(store, deep, "Leaf"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void PredefinedRootSectionIsNotCreatedAndItsWritesAreChecked()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        "HKEY_CURRENT_USER",
                        new[] { new RegFileValue("Probe", 1, Encoding.Unicode.GetBytes("x\0"), line: 3) },
                        line: 2),
                    new RegFileKey("HKEY_CURRENT_USER", Array.Empty<RegFileValue>(), delete: true, line: 4),
                });

            // The root always exists, so no CreateKey runs. The sandbox policy refuses the value
            // write and the root delete, and both are reported as failures instead of exceptions.
            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            Assert.AreEqual(2, result.Failures.Count);
            Assert.IsTrue(result.Failures.All(f => f.Kind == RegFileImportFailureKind.WriteFailed));
            Assert.AreEqual(3, result.Failures[0].Line);
            Assert.AreEqual(4, result.Failures[1].Line);
            Assert.AreEqual(0, result.ValuesWritten);
            Assert.AreEqual(0, result.KeysDeleted);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void WriteFailureDoesNotStopTheImport()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath first = runRoot.Append("First");
        RegistryPath last = runRoot.Append("Last");
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath outside = RegistryPath.Parse($@"HKCU\Software\{outsideId}");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(first),
                        new[]
                        {
                            new RegFileValue("Bad\0Name", 1, Encoding.Unicode.GetBytes("x\0"), line: 3),
                            new RegFileValue("Good", 1, Encoding.Unicode.GetBytes("kept\0"), line: 4),
                        },
                        line: 2),
                    new RegFileKey(
                        WithoutComputerPrefix(outside),
                        new[] { new RegFileValue("Value", 4, new byte[] { 1, 0, 0, 0 }, line: 6) },
                        line: 5),
                    new RegFileKey(
                        WithoutComputerPrefix(last),
                        new[] { new RegFileValue("Tail", 1, Encoding.Unicode.GetBytes("end\0"), line: 8) },
                        line: 7),
                });

            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            Assert.AreEqual(2, result.KeysWritten);
            Assert.AreEqual(2, result.ValuesWritten);
            Assert.AreEqual(2, result.Failures.Count);
            Assert.AreEqual(3, result.Failures[0].Line);
            Assert.AreEqual(RegFileImportFailureKind.WriteFailed, result.Failures[0].Kind);
            Assert.AreEqual(5, result.Failures[1].Line);
            Assert.AreEqual(5, result.Failures[1].Win32Error);
            Assert.AreEqual("kept", ReadString(store, first, "Good"));
            Assert.AreEqual("end", ReadString(store, last, "Tail"));
            Assert.IsFalse(store.Reader.KeyExists(outside));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void DeniedDeleteIsReportedAndTheNextSectionStillRuns()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath next = runRoot.Append("Next");
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath outside = RegistryPath.Parse($@"HKCU\Software\{outsideId}");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(WithoutComputerPrefix(outside), Array.Empty<RegFileValue>(), delete: true, line: 2),
                    new RegFileKey(
                        WithoutComputerPrefix(next),
                        new[] { new RegFileValue("V", 1, Encoding.Unicode.GetBytes("v\0"), line: 4) },
                        line: 3),
                });

            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            RegFileImportFailure failure = Assert.ContainsSingle(result.Failures);
            Assert.AreEqual(RegFileImportFailureKind.WriteFailed, failure.Kind);
            Assert.AreEqual(2, failure.Line);
            Assert.AreEqual(5, failure.Win32Error);
            Assert.AreEqual(0, result.KeysDeleted);
            Assert.AreEqual("v", ReadString(store, next, "V"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ApplyCountsWritesAndDeletesSeparately()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath child = runRoot.Append("Child");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, "Old", "gone");
            store.CreateKey(child);

            var document = new RegFileDocument(
                RegFileFormat.V5,
                new[]
                {
                    new RegFileKey(
                        WithoutComputerPrefix(runRoot),
                        new[]
                        {
                            new RegFileValue("New", 1, Encoding.Unicode.GetBytes("a\0")),
                            new RegFileValue("Old", 0, Array.Empty<byte>(), delete: true),
                            new RegFileValue("Absent", 0, Array.Empty<byte>(), delete: true),
                            new RegFileValue("Another", 4, new byte[] { 2, 0, 0, 0 }),
                        }),
                    new RegFileKey(WithoutComputerPrefix(child), Array.Empty<RegFileValue>(), delete: true),
                    new RegFileKey(WithoutComputerPrefix(child), Array.Empty<RegFileValue>(), delete: true),
                });

            RegFileImportResult result = RegFileImporter.Apply(store, document, CancellationToken.None);

            Assert.IsFalse(result.IsRefused);
            Assert.IsFalse(result.HasWriteFailures);
            Assert.AreEqual(1, result.KeysWritten);
            Assert.AreEqual(1, result.KeysDeleted);
            Assert.AreEqual(2, result.ValuesWritten);
            Assert.AreEqual(1, result.ValuesDeleted);
            Assert.AreEqual(0, result.Failures.Count);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ProgressIsReportedAfterEachFinishedSection()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath[] sections =
        {
            runRoot.Append("P1"),
            runRoot.Append("P2"),
            runRoot.Append("P3"),
        };

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            var progress = new RecordingProgress();
            RegFileDocument document = SectionsDocument(sections);

            RegFileImporter.Apply(store, document, CancellationToken.None, progress);

            CollectionAssert.AreEqual(
                new[]
                {
                    new ImportProgress(1, 3, WithoutComputerPrefix(sections[0])),
                    new ImportProgress(2, 3, WithoutComputerPrefix(sections[1])),
                    new ImportProgress(3, 3, WithoutComputerPrefix(sections[2])),
                },
                progress.Reports.ToArray());
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void CancellationFromProgressStopsBetweenSections()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath[] sections = { runRoot.Append("C1"), runRoot.Append("C2") };

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            using var cancellation = new CancellationTokenSource();
            var progress = new RecordingProgress { OnReport = _ => cancellation.Cancel() };
            RegFileDocument document = SectionsDocument(sections);

            Assert.ThrowsExactly<OperationCanceledException>(
                () => RegFileImporter.Apply(store, document, cancellation.Token, progress));

            Assert.IsTrue(store.Reader.KeyExists(sections[0]), "The finished section stays applied.");
            Assert.IsFalse(store.Reader.KeyExists(sections[1]), "The next section never starts.");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ApplyFileRefusesAFileWithoutHeaderAndWritesNothing()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath target = runRoot.Append("NoHeader");
        string file = Path.Combine(Path.GetTempPath(), "bwc-reg-" + Guid.NewGuid().ToString("N") + ".reg");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            string text = "[" + WithoutComputerPrefix(target) + "]\r\n\"Text\"=\"x\"\r\n";
            File.WriteAllBytes(file, Encoding.Unicode.GetBytes(text));

            RegFileImportResult result = RegFileImporter.ApplyFile(store, file, CancellationToken.None);

            Assert.IsTrue(result.IsRefused);
            Assert.AreEqual(RegFileRefusal.NotRegistryFile, result.Refusal);
            Assert.AreEqual(0, result.KeysWritten);
            Assert.IsFalse(store.Reader.KeyExists(target));
        }
        finally
        {
            File.Delete(file);
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ApplyFileReportsAMissingFileAsReadFailed()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        string file = Path.Combine(Path.GetTempPath(), "bwc-reg-missing-" + Guid.NewGuid().ToString("N") + ".reg");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));

            RegFileImportResult result = RegFileImporter.ApplyFile(store, file, CancellationToken.None);

            Assert.IsTrue(result.IsRefused);
            Assert.AreEqual(RegFileRefusal.ReadFailed, result.Refusal);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    private static RegFileDocument SectionsDocument(IReadOnlyList<RegistryPath> sections)
    {
        var keys = new List<RegFileKey>(sections.Count);
        foreach (RegistryPath section in sections)
        {
            keys.Add(new RegFileKey(
                WithoutComputerPrefix(section),
                new[] { new RegFileValue("V", 1, Encoding.Unicode.GetBytes("v\0")) }));
        }

        return new RegFileDocument(RegFileFormat.V5, keys);
    }

    private sealed class RecordingProgress : IProgress<ImportProgress>
    {
        public List<ImportProgress> Reports { get; } = new();

        public Action<ImportProgress>? OnReport { get; init; }

        public void Report(ImportProgress value)
        {
            Reports.Add(value);
            OnReport?.Invoke(value);
        }
    }

    private static void SeedCommonValues(RegistryStore store, RegistryPath root)
    {
        store.CreateKey(root);
        store.SetString(root, string.Empty, "default text");
        store.SetString(root, "Text", "hello");
        store.SetExpandString(root, "Expand", "%TEMP%\\demo");
        store.SetBinary(root, "Binary", new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF });
        store.SetBinary(root, "EmptyBinary", ReadOnlySpan<byte>.Empty);
        store.SetDword(root, "Dword", 0x01020304u);
        store.SetQword(root, "Qword", 0x1122334455667788UL);
        store.SetMultiString(root, "Multi", new[] { "one", string.Empty, "two" });

        RegistryPath child = root.Append("Child");
        store.CreateKey(child);
        store.SetString(child, "Nested", "world");
        store.CreateKey(root.Append("EmptyChild"));
    }

    private static RegFileDocument Retarget(
        RegFileDocument document,
        RegistryPath source,
        RegistryPath target)
    {
        string sourcePath = WithoutComputerPrefix(source);
        string targetPath = WithoutComputerPrefix(target);
        var keys = new List<RegFileKey>(document.Keys.Count);

        foreach (RegFileKey key in document.Keys)
        {
            string path = key.Path.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase)
                ? targetPath + key.Path[sourcePath.Length..]
                : key.Path;
            var values = key.Values
                .Select(value => new RegFileValue(value.Name, value.Type, value.Data, value.Delete))
                .ToArray();
            keys.Add(new RegFileKey(path, values, key.Delete));
        }

        return new RegFileDocument(document.Format, keys);
    }

    private static void AssertDocumentsEqual(RegFileDocument expected, RegFileDocument actual)
    {
        Assert.AreEqual(expected.Format, actual.Format);
        Assert.AreEqual(expected.Keys.Count, actual.Keys.Count);

        for (var keyIndex = 0; keyIndex < expected.Keys.Count; keyIndex += 1)
        {
            RegFileKey expectedKey = expected.Keys[keyIndex];
            RegFileKey actualKey = actual.Keys[keyIndex];
            Assert.AreEqual(expectedKey.Path, actualKey.Path, $"key {keyIndex}");
            Assert.AreEqual(expectedKey.Delete, actualKey.Delete, expectedKey.Path);
            Assert.AreEqual(
                expectedKey.Values.Count,
                actualKey.Values.Count,
                expectedKey.Path);

            for (var valueIndex = 0; valueIndex < expectedKey.Values.Count; valueIndex += 1)
            {
                RegFileValue expectedValue = expectedKey.Values[valueIndex];
                RegFileValue actualValue = actualKey.Values[valueIndex];
                string context = expectedKey.Path + " / " + expectedValue.Name;
                Assert.AreEqual(expectedValue.Name, actualValue.Name, context);
                Assert.AreEqual(expectedValue.Type, actualValue.Type, context);
                Assert.AreEqual(expectedValue.Delete, actualValue.Delete, context);
                CollectionAssert.AreEqual(expectedValue.Data, actualValue.Data, context);
            }
        }
    }

    private static byte[] EncodeV5(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[body.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = 0xFE;
        body.CopyTo(bytes, 2);
        return bytes;
    }

    private static string? ReadString(RegistryStore store, RegistryPath key, string name) =>
        store.GetValue(key, name) is { } value
            ? Encoding.Unicode.GetString(value.Data).TrimEnd('\0')
            : null;

    private static uint ReadDword(RegistryStore store, RegistryPath key, string name)
    {
        RegistryValueInfo value = store.GetValue(key, name)
            ?? throw new AssertFailedException($"The value '{name}' is missing.");
        return BitConverter.ToUInt32(value.Data);
    }

    private static byte[] ReadBytes(RegistryStore store, RegistryPath key, string name)
    {
        RegistryValueInfo value = store.GetValue(key, name)
            ?? throw new AssertFailedException($"The value '{name}' is missing.");
        return value.Data;
    }

    private static string WithoutComputerPrefix(RegistryPath path)
    {
        const string Prefix = "Computer\\";
        string display = path.ToDisplayString();
        return display.StartsWith(Prefix, StringComparison.Ordinal)
            ? display[Prefix.Length..]
            : display;
    }

    private static string NewRunId() => "import-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
