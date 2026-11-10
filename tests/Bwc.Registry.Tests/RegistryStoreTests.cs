#if BWC_E2E
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryStoreTests
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
    public void TypedSettersRoundTripThroughTheRealRegistry()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);

            store.SetString(runRoot, "Text", "hello");
            store.SetExpandString(runRoot, "Expand", "%TEMP%");
            store.SetDword(runRoot, "Dword", 0x01020304u);
            store.SetDwordBigEndian(runRoot, "BigEndian", 0x11223344u);
            store.SetQword(runRoot, "Qword", 0x1122334455667788UL);
            store.SetBinary(runRoot, "Blob", new byte[] { 0x00, 0x01, 0x02, 0xFF });
            store.SetBinary(runRoot, "EmptyBinary", ReadOnlySpan<byte>.Empty);
            store.SetMultiString(runRoot, "Multi", new[] { "one", string.Empty, "two" });
            store.SetMultiString(runRoot, "EmptyMulti", Array.Empty<string>());
            store.SetMultiString(runRoot, "OneEmptyMulti", new[] { string.Empty });
            store.SetString(runRoot, "EmbeddedNul", "ab\0cd");
            store.SetString(runRoot, "Empty", string.Empty);
            store.SetString(runRoot, string.Empty, "default text");
            store.SetValue(runRoot, "OddDword", RegistryValueType.Dword, new byte[] { 0x01, 0x02, 0x03 });
            store.SetValue(runRoot, "Unknown", (RegistryValueType)0x1234, new byte[] { 0xDE, 0xAD });

            RegistryReader reader = store.Reader;

            AssertValue(reader.ReadValue(runRoot, "Text"), "Text", RegistryValueType.String, Encoding.Unicode.GetBytes("hello\0"));
            AssertValue(reader.ReadValue(runRoot, "Expand"), "Expand", RegistryValueType.ExpandString, Encoding.Unicode.GetBytes("%TEMP%\0"));
            AssertValue(reader.ReadValue(runRoot, "Dword"), "Dword", RegistryValueType.Dword, new byte[] { 0x04, 0x03, 0x02, 0x01 });
            AssertValue(reader.ReadValue(runRoot, "BigEndian"), "BigEndian", RegistryValueType.DwordBigEndian, new byte[] { 0x11, 0x22, 0x33, 0x44 });
            AssertValue(
                reader.ReadValue(runRoot, "Qword"),
                "Qword",
                RegistryValueType.Qword,
                new byte[] { 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11 });
            AssertValue(reader.ReadValue(runRoot, "Blob"), "Blob", RegistryValueType.Binary, new byte[] { 0x00, 0x01, 0x02, 0xFF });
            AssertValue(reader.ReadValue(runRoot, "EmptyBinary"), "EmptyBinary", RegistryValueType.Binary, Array.Empty<byte>());
            AssertValue(
                reader.ReadValue(runRoot, "Multi"),
                "Multi",
                RegistryValueType.MultiString,
                Encoding.Unicode.GetBytes("one\0\0two\0\0"));
            AssertValue(
                reader.ReadValue(runRoot, "EmptyMulti"),
                "EmptyMulti",
                RegistryValueType.MultiString,
                new byte[] { 0, 0, 0, 0 });
            AssertValue(
                reader.ReadValue(runRoot, "OneEmptyMulti"),
                "OneEmptyMulti",
                RegistryValueType.MultiString,
                new byte[] { 0, 0, 0, 0 });
            AssertValue(
                reader.ReadValue(runRoot, "EmbeddedNul"),
                "EmbeddedNul",
                RegistryValueType.String,
                Encoding.Unicode.GetBytes("ab\0cd\0"));
            AssertValue(reader.ReadValue(runRoot, "Empty"), "Empty", RegistryValueType.String, new byte[] { 0, 0 });
            AssertValue(reader.ReadValue(runRoot, string.Empty), string.Empty, RegistryValueType.String, Encoding.Unicode.GetBytes("default text\0"));
            AssertValue(reader.ReadValue(runRoot, "OddDword"), "OddDword", RegistryValueType.Dword, new byte[] { 0x01, 0x02, 0x03 });
            AssertValue(reader.ReadValue(runRoot, "Unknown"), "Unknown", (RegistryValueType)0x1234, new byte[] { 0xDE, 0xAD });

            Assert.AreEqual(15, store.GetValues(runRoot).Count);
            Assert.IsNull(store.GetValue(runRoot, "Missing"));

            RegistryValueInfo dword = reader.ReadValue(runRoot, "Dword")!;
            RegistryValueInfo bigEndian = reader.ReadValue(runRoot, "BigEndian")!;
            RegistryValueInfo multi = reader.ReadValue(runRoot, "Multi")!;
            RegistryValueInfo emptyMulti = reader.ReadValue(runRoot, "EmptyMulti")!;
            RegistryValueInfo blob = reader.ReadValue(runRoot, "Blob")!;
            RegistryValueInfo emptyBinary = reader.ReadValue(runRoot, "EmptyBinary")!;
            RegistryValueInfo embeddedNul = reader.ReadValue(runRoot, "EmbeddedNul")!;
            RegistryValueInfo oddDword = reader.ReadValue(runRoot, "OddDword")!;
            RegistryValueInfo defaultValue = reader.ReadDefaultValue(runRoot)!;

            Assert.AreEqual(
                0x01020304u.ToString(CultureInfo.InvariantCulture),
                RegistryValueDisplay.FormatData(dword));
            Assert.AreEqual("11 22 33 44", RegistryValueDisplay.FormatData(bigEndian));
            Assert.AreEqual("one  two", RegistryValueDisplay.FormatData(multi));
            Assert.AreEqual(string.Empty, string.Join(' ', RegistryValueCodec.DecodeMultiString(emptyMulti.Data).Strings));
            Assert.AreEqual("00 01 02 FF", RegistryValueDisplay.FormatData(blob));
            Assert.AreEqual("(zero-length binary value)", RegistryValueDisplay.FormatData(emptyBinary));
            Assert.AreEqual("ab", RegistryValueDisplay.FormatData(embeddedNul));
            Assert.AreEqual("(invalid DWORD (32-bit) value)", RegistryValueDisplay.FormatData(oddDword));
            Assert.AreEqual("(Default)", RegistryValueDisplay.FormatName(defaultValue));
            Assert.AreEqual("default text", RegistryValueDisplay.FormatData(defaultValue));

            using RegistryKey key = RequireRunKey(runId);
            Assert.AreEqual(RegistryValueKind.String, key.GetValueKind("Text"));
            Assert.AreEqual("hello", key.GetValue("Text"));
            Assert.AreEqual(RegistryValueKind.ExpandString, key.GetValueKind("Expand"));
            Assert.AreEqual(
                "%TEMP%",
                key.GetValue("Expand", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.AreEqual(RegistryValueKind.DWord, key.GetValueKind("Dword"));
            Assert.AreEqual(0x01020304, key.GetValue("Dword"));
            Assert.AreEqual(RegistryValueKind.QWord, key.GetValueKind("Qword"));
            Assert.AreEqual((long)0x1122334455667788UL, key.GetValue("Qword"));
            Assert.AreEqual(RegistryValueKind.Binary, key.GetValueKind("Blob"));
            CollectionAssert.AreEqual(new byte[] { 0x00, 0x01, 0x02, 0xFF }, (byte[])key.GetValue("Blob")!);
            Assert.AreEqual(RegistryValueKind.MultiString, key.GetValueKind("Multi"));
            CollectionAssert.AreEqual(new[] { "one", string.Empty, "two" }, (string[])key.GetValue("Multi")!);
            Assert.AreEqual("default text", key.GetValue(string.Empty));

            store.DeleteValue(runRoot, string.Empty);
            Assert.IsNull(store.Reader.ReadDefaultValue(runRoot));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void CreateRenameCopyAndDeleteWorkAgainstTheRealRegistry()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            var store = new RegistryStore(writer);
            Assert.AreSame(writer, store.Writer);

            store.CreateKey(runRoot);

            RegistryPath child = runRoot.Append("Child");
            store.CreateKey(child);
            store.SetString(child, "Nested", "world");

            store.RenameKey(child, "Renamed");

            RegistryPath renamed = runRoot.Append("Renamed");
            Assert.IsFalse(store.Reader.KeyExists(child));
            Assert.IsTrue(store.Reader.KeyExists(renamed));
            AssertValue(store.GetValue(renamed, "Nested"), "Nested", RegistryValueType.String, Encoding.Unicode.GetBytes("world\0"));

            RegistryPath copySource = runRoot.Append("CopySource");
            RegistryPath copyTarget = runRoot.Append("CopyTarget");
            store.CreateKey(copySource);
            store.SetString(copySource, "CopiedText", "copied");
            store.SetDword(copySource, string.Empty, 7u);
            RegistryPath copyChild = copySource.Append("Nested");
            store.CreateKey(copyChild);
            store.SetString(copyChild, "Nested", "nested");

            store.CreateKey(copyTarget);
            store.CopyTree(copySource, copyTarget);

            AssertValue(store.GetValue(copyTarget, "CopiedText"), "CopiedText", RegistryValueType.String, Encoding.Unicode.GetBytes("copied\0"));
            AssertValue(store.GetValue(copyTarget, string.Empty), string.Empty, RegistryValueType.Dword, new byte[] { 7, 0, 0, 0 });
            RegistryPath copiedChild = copyTarget.Append("Nested");
            AssertValue(store.GetValue(copiedChild, "Nested"), "Nested", RegistryValueType.String, Encoding.Unicode.GetBytes("nested\0"));

            store.DeleteValue(copyTarget, "CopiedText");
            Assert.IsNull(store.GetValue(copyTarget, "CopiedText"));

            store.DeleteKey(renamed, recursive: false);
            Assert.IsFalse(store.Reader.KeyExists(renamed));

            RegistryPath container = runRoot.Append("Container");
            store.CreateKey(container);
            store.CreateKey(container.Append("Child"));
            var nonEmptyError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.DeleteKey(container, recursive: false));
            Assert.AreEqual(5, nonEmptyError.Win32Error);
            Assert.IsTrue(store.Reader.KeyExists(container));

            store.DeleteKey(container, recursive: true);
            Assert.IsFalse(store.Reader.KeyExists(container));

            store.DeleteKey(copySource, recursive: true);
            store.DeleteKey(copyTarget, recursive: true);
            Assert.IsFalse(store.Reader.KeyExists(copySource));
            Assert.IsFalse(store.Reader.KeyExists(copyTarget));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void RenameValuePreservesRawDataAndHandlesTheDefaultName()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);

            store.SetString(runRoot, "Old", "value");
            store.SetValue(runRoot, "Odd", RegistryValueType.Dword, new byte[] { 0x01, 0x02, 0x03 });
            store.SetString(runRoot, string.Empty, "default text");
            store.SetString(runRoot, "Source", "after");
            store.SetString(runRoot, "Target", "before");

            store.RenameValue(runRoot, "Old", "New");

            Assert.IsNull(store.GetValue(runRoot, "Old"));
            AssertValue(store.GetValue(runRoot, "New"), "New", RegistryValueType.String, Encoding.Unicode.GetBytes("value\0"));

            store.RenameValue(runRoot, "Odd", "OddRenamed");

            Assert.IsNull(store.GetValue(runRoot, "Odd"));
            AssertValue(
                store.GetValue(runRoot, "OddRenamed"),
                "OddRenamed",
                RegistryValueType.Dword,
                new byte[] { 0x01, 0x02, 0x03 });

            store.RenameValue(runRoot, string.Empty, "FromDefault");

            Assert.IsNull(store.Reader.ReadDefaultValue(runRoot));
            AssertValue(
                store.GetValue(runRoot, "FromDefault"),
                "FromDefault",
                RegistryValueType.String,
                Encoding.Unicode.GetBytes("default text\0"));

            store.RenameValue(runRoot, "FromDefault", string.Empty);

            Assert.IsNull(store.GetValue(runRoot, "FromDefault"));
            AssertValue(
                store.Reader.ReadDefaultValue(runRoot),
                string.Empty,
                RegistryValueType.String,
                Encoding.Unicode.GetBytes("default text\0"));

            store.RenameValue(runRoot, "Source", "Target");

            Assert.IsNull(store.GetValue(runRoot, "Source"));
            AssertValue(store.GetValue(runRoot, "Target"), "Target", RegistryValueType.String, Encoding.Unicode.GetBytes("after\0"));

            store.RenameValue(runRoot, "New", "New");
            store.RenameValue(runRoot, "New", "NEW");

            RegistryValueInfo? caseRenamed = store.GetValue(runRoot, "NEW");
            Assert.IsNotNull(caseRenamed);
            Assert.AreEqual(RegistryValueType.String, caseRenamed!.TypeEnum);
            CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("value\0"), caseRenamed.Data);

            var missingError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.RenameValue(runRoot, "Missing", "Nope"));
            Assert.AreEqual(2, missingError.Win32Error);
            Assert.AreEqual("RenameValue", missingError.Operation);
            Assert.AreEqual(runRoot, missingError.Path);
            Assert.IsNull(store.GetValue(runRoot, "Nope"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void RenameValueRollsBackWhenTheSourceDeleteFails()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var policy = new DeleteFailingPolicy(runRoot, failures: 1);
            var store = new RegistryStore(policy);
            store.CreateKey(runRoot);
            store.SetString(runRoot, "Old", "value");

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.RenameValue(runRoot, "Old", "New"));

            Assert.AreEqual(5, error.Win32Error);
            Assert.AreEqual("DeleteValue", error.Operation);
            Assert.AreEqual(runRoot, error.Path);
            Assert.IsNull(store.GetValue(runRoot, "New"));
            AssertValue(store.GetValue(runRoot, "Old"), "Old", RegistryValueType.String, Encoding.Unicode.GetBytes("value\0"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void RenameValueReportsBothErrorsWhenRollbackAlsoFails()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var policy = new DeleteFailingPolicy(runRoot, failures: 2);
            var store = new RegistryStore(policy);
            store.CreateKey(runRoot);
            store.SetString(runRoot, "Old", "value");

            var error = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.RenameValue(runRoot, "Old", "New"));

            Assert.AreEqual(5, error.Win32Error);
            Assert.AreEqual("DeleteValue", error.Operation);
            StringAssert.Contains(error.Message, "rollback failed", StringComparison.OrdinalIgnoreCase);
            AssertValue(store.GetValue(runRoot, "Old"), "Old", RegistryValueType.String, Encoding.Unicode.GetBytes("value\0"));
            AssertValue(store.GetValue(runRoot, "New"), "New", RegistryValueType.String, Encoding.Unicode.GetBytes("value\0"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void PolicyDenialCreatesNothingAndMapsTheWriterError()
    {
        string runId = NewRunId();
        string outsideId = "outside-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath outside = RegistryPath.Parse($@"HKCU\{SandboxBase}\{outsideId}");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));

            var setError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.SetString(outside, "Value", "x"));
            Assert.AreEqual(5, setError.Win32Error);
            Assert.AreEqual("SetValue", setError.Operation);
            Assert.AreEqual(outside, setError.Path);

            var createError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.CreateKey(outside));
            Assert.AreEqual(5, createError.Win32Error);
            Assert.AreEqual("CreateKey", createError.Operation);

            var deleteError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.DeleteKey(outside, recursive: true));
            Assert.AreEqual(5, deleteError.Win32Error);
            Assert.AreEqual("DeleteKey", deleteError.Operation);

            using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase);
            Assert.IsNotNull(parent);
            Assert.IsNull(parent!.OpenSubKey(outsideId));
        }
        finally
        {
            DeleteRunRoot(runId);
            DeleteRunRoot(outsideId);
        }
    }

    [TestMethod]
    public void MissingKeysAndValuesKeepTheReaderAndWriterErrors()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);

            Assert.IsNull(store.GetValue(runRoot, "Missing"));
            Assert.AreEqual(0, store.GetValues(runRoot).Count);

            var setError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.SetString(runRoot.Append("MissingKey"), "Value", "x"));
            Assert.AreEqual(2, setError.Win32Error);
            Assert.AreEqual("SetValue", setError.Operation);

            var deleteValueError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.DeleteValue(runRoot, "Missing"));
            Assert.AreEqual(2, deleteValueError.Win32Error);
            Assert.AreEqual("DeleteValue", deleteValueError.Operation);

            var deleteKeyError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.DeleteKey(runRoot.Append("MissingKey"), recursive: true));
            Assert.AreEqual(2, deleteKeyError.Win32Error);
            Assert.AreEqual("DeleteKey", deleteKeyError.Operation);

            var createError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.CreateKey(RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}\Missing\Child")));
            Assert.AreEqual(2, createError.Win32Error);
            Assert.AreEqual("CreateKey", createError.Operation);

            var readError = Assert.ThrowsExactly<RegistryOperationException>(
                () => store.GetValues(runRoot.Append("MissingKey")));
            Assert.AreEqual("OpenKey", readError.Operation);
            Assert.AreEqual(2, readError.Win32Error);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    private static string NewRunId() => "store-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static RegistryKey RequireRunKey(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return hkcu.OpenSubKey($@"{SandboxBase}\{runId}")
            ?? throw new AssertFailedException("The sandbox run key is missing.");
    }

    private static void AssertValue(
        RegistryValueInfo? actual,
        string expectedName,
        RegistryValueType expectedType,
        byte[] expectedData)
    {
        Assert.IsNotNull(actual, expectedName);
        Assert.AreEqual(expectedName, actual!.Name, expectedName);
        Assert.AreEqual((uint)expectedType, actual.Type, expectedName);
        Assert.AreEqual(expectedType, actual.TypeEnum, expectedName);
        CollectionAssert.AreEqual(expectedData, actual.Data, expectedName);
    }

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }

    private sealed class DeleteFailingPolicy : IRegistryWritePolicy
    {
        private readonly SandboxWritePolicy _inner;
        private int _remainingFailures;

        public DeleteFailingPolicy(RegistryPath sandboxRoot, int failures)
        {
            _inner = new SandboxWritePolicy(sandboxRoot);
            _remainingFailures = failures;
        }

        public bool AllowsLinkValues => _inner.AllowsLinkValues;

        public void EnsureWriteAllowed(RegistryPath path, RegistryWriteOperation operation)
        {
            _inner.EnsureWriteAllowed(path, operation);

            if (operation == RegistryWriteOperation.DeleteValue && _remainingFailures > 0)
            {
                _remainingFailures -= 1;
                throw new RegistryOperationException(operation.ToString(), path, 5);
            }
        }
    }
}
#endif
