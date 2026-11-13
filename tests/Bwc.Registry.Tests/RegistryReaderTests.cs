#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryReaderTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [TestMethod]
    public void ReadsKeyInfoValuesAndSubkeysFromTheRealRegistry()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        DateTime startedUtc = DateTime.UtcNow.AddMinutes(-2);
        string[] subKeyNames = { "zeta", "Alpha", "Ünïcødé Key ✓", "Key2", "Key10" };
        var expected = new List<(string Name, uint Type, byte[] Data)>();

        void Add(string name, uint type, byte[] data) => expected.Add((name, type, data));

        try
        {
            using (RegistryKey hkcu = OpenHkcu())
            using (RegistryKey key = CreateRunKey(hkcu, runId))
            {
                Add(string.Empty, 4, BitConverter.GetBytes(42u));
                key.SetValue(string.Empty, 42, RegistryValueKind.DWord);

                Add("Text", 1, Encoding.Unicode.GetBytes("hello\0"));
                key.SetValue("Text", "hello", RegistryValueKind.String);

                Add("Expand", 2, Encoding.Unicode.GetBytes("%TEMP%\0"));
                key.SetValue("Expand", "%TEMP%", RegistryValueKind.ExpandString);

                Add("Blob", 3, new byte[] { 0x01, 0x02, 0x03, 0xFF });
                key.SetValue("Blob", new byte[] { 0x01, 0x02, 0x03, 0xFF }, RegistryValueKind.Binary);

                Add("EmptyBinary", 3, Array.Empty<byte>());
                key.SetValue("EmptyBinary", Array.Empty<byte>(), RegistryValueKind.Binary);

                Add("Multi", 7, Encoding.Unicode.GetBytes("one\0\0two\0\0"));
                key.SetValue("Multi", new[] { "one", string.Empty, "two" }, RegistryValueKind.MultiString);

                Add("Qword", 11, BitConverter.GetBytes(0x1122334455667788UL));
                key.SetValue("Qword", 0x1122334455667788UL, RegistryValueKind.QWord);

                Add("Dword", 4, BitConverter.GetBytes(0x01020304u));
                key.SetValue("Dword", 0x01020304, RegistryValueKind.DWord);

                foreach (string subKeyName in subKeyNames)
                {
                    using RegistryKey subKey = key.CreateSubKey(subKeyName)
                        ?? throw new AssertFailedException($"The subkey '{subKeyName}' could not be created.");
                }
            }

            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));

            void AddRaw(string name, uint type, byte[] data)
            {
                writer.SetValue(runRoot, name, type, data);
                Add(name, type, data);
            }

            // RegSetValueExW appends a NUL to REG_SZ data that does not end with one, so the
            // registry stores 16 bytes although the caller supplied the 14 bytes of "without".
            // The codec tests cover a raw no-terminator string that this API cannot produce.
            writer.SetValue(runRoot, "NoTerminator", 1, Encoding.Unicode.GetBytes("without"));
            Add("NoTerminator", 1, Encoding.Unicode.GetBytes("without\0"));
            AddRaw("EmbeddedNul", 1, Encoding.Unicode.GetBytes("ab\0cd\0"));
            AddRaw("OddDword", 4, new byte[] { 0x01, 0x02, 0x03 });
            AddRaw("BigEndian", 5, new byte[] { 0x11, 0x22, 0x33, 0x44 });
            AddRaw("Unknown", 0x1234, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            AddRaw("None", 0, new byte[] { 0xAA });

            var reader = new RegistryReader();
            RegistryKeyInfo info = reader.GetKeyInfo(runRoot);

            Assert.AreEqual(runId, info.Name);
            Assert.AreEqual(runRoot, info.Path);
            Assert.AreEqual(subKeyNames.Length, info.SubKeyCount);
            Assert.AreEqual(expected.Count, info.ValueCount);
            Assert.AreEqual(DateTimeKind.Utc, info.LastWriteTimeUtc.Kind);
            Assert.IsTrue(
                info.LastWriteTimeUtc >= startedUtc,
                $"The last-write time {info.LastWriteTimeUtc:o} predates the test start {startedUtc:o}.");
            Assert.IsTrue(
                info.LastWriteTimeUtc <= DateTime.UtcNow.AddMinutes(1),
                $"The last-write time {info.LastWriteTimeUtc:o} is in the future.");
            Assert.AreEqual(subKeyNames.Max(name => name.Length), info.MaxSubKeyNameLength);
            Assert.AreEqual(expected.Max(value => value.Name.Length), info.MaxValueNameLength);
            Assert.AreEqual(expected.Max(value => value.Data.Length), info.MaxValueDataLength);
            Assert.AreEqual(string.Empty, info.ClassName);

            RegistryKeyInfo rootInfo = reader.GetKeyInfo(RegistryPath.Parse("HKCU"));
            Assert.AreEqual("HKEY_CURRENT_USER", rootInfo.Name);
            Assert.IsTrue(rootInfo.SubKeyCount > 0);

            IReadOnlyList<string> readerSubKeys = reader.EnumerateSubKeyNames(runRoot);
            using (RegistryKey key = OpenRunKey(runId))
            {
                CollectionAssert.AreEqual(key.GetSubKeyNames(), readerSubKeys.ToArray());
            }

            CollectionAssert.AreEquivalent(subKeyNames, readerSubKeys.ToArray());

            IReadOnlyList<RegistryValueInfo> values = reader.EnumerateValues(runRoot);
            using (RegistryKey key = OpenRunKey(runId))
            {
                CollectionAssert.AreEqual(key.GetValueNames(), values.Select(value => value.Name).ToArray());
            }

            Assert.AreEqual(expected.Count, values.Count);
            var byName = values.ToDictionary(value => value.Name, StringComparer.Ordinal);

            foreach (var item in expected)
            {
                Assert.IsTrue(byName.TryGetValue(item.Name, out RegistryValueInfo? actual), item.Name);
                AssertValue(actual!, item.Type, item.Data);
            }

            RegistryValueInfo? text = reader.ReadValue(runRoot, "Text");
            Assert.IsNotNull(text);
            AssertValue(text!, 1, Encoding.Unicode.GetBytes("hello\0"));

            RegistryValueInfo? defaultValue = reader.ReadDefaultValue(runRoot);
            Assert.IsNotNull(defaultValue);
            Assert.AreEqual(string.Empty, defaultValue!.Name);
            AssertValue(defaultValue, 4, BitConverter.GetBytes(42u));

            Assert.IsNull(reader.ReadValue(runRoot, "Missing"));

            RegistryPath missing = runRoot.Append("MissingKey");
            var error = Assert.ThrowsExactly<RegistryOperationException>(() => reader.GetKeyInfo(missing));
            Assert.AreEqual("OpenKey", error.Operation);
            Assert.AreEqual(missing, error.Path);
            Assert.ThrowsExactly<RegistryOperationException>(() => reader.EnumerateSubKeyNames(missing));
            Assert.ThrowsExactly<RegistryOperationException>(() => reader.EnumerateValues(missing));
            Assert.ThrowsExactly<RegistryOperationException>(() => reader.ReadValue(missing, "Text"));

            Assert.IsTrue(reader.KeyExists(runRoot));
            Assert.IsTrue(reader.KeyExists(RegistryPath.Parse("HKCU")));
            Assert.IsFalse(reader.KeyExists(missing));

            Assert.IsTrue(reader.ValueExists(runRoot, "Text"));
            Assert.IsTrue(reader.ValueExists(runRoot, "EmptyBinary"));
            Assert.IsTrue(reader.ValueExists(runRoot, string.Empty));
            Assert.IsFalse(reader.ValueExists(runRoot, "Missing"));
            Assert.IsFalse(reader.ValueExists(missing, "Text"));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void CancellationIsObservedBeforeRegistryAccess()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            using (RegistryKey hkcu = OpenHkcu())
            using (RegistryKey key = CreateRunKey(hkcu, runId))
            {
                key.SetValue("Text", "hello", RegistryValueKind.String);
                using RegistryKey child = key.CreateSubKey("Child")
                    ?? throw new AssertFailedException("The child key could not be created.");
            }

            var reader = new RegistryReader();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(
                () => reader.GetKeyInfo(runRoot, cancellation.Token));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => reader.EnumerateSubKeyNames(runRoot, cancellation.Token));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => reader.EnumerateValues(runRoot, cancellation.Token));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => reader.ReadValue(runRoot, "Text", cancellation.Token));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => reader.ReadDefaultValue(runRoot, cancellation.Token));
            Assert.ThrowsExactly<OperationCanceledException>(
                () => { _ = reader.KeyExists(runRoot, cancellation.Token); });
            Assert.ThrowsExactly<OperationCanceledException>(
                () => { _ = reader.ValueExists(runRoot, "Text", cancellation.Token); });
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void EnumerationReturnsEmbeddedNulNamesWithTheirOwnData()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        const string NulName = "abc\0def";
        const string SoloName = "solo\0x";

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.SetValue(runRoot, "abc", 1, Encoding.Unicode.GetBytes("other\0"));
            writer.SetValue(runRoot, "plain", 4, BitConverter.GetBytes(7u));
            writer.SetValueWithCountedName(runRoot, NulName, 4, BitConverter.GetBytes(0x1388u));
            writer.SetValueWithCountedName(runRoot, SoloName, 3, new byte[] { 0x01, 0x02 });

            var reader = new RegistryReader();
            IReadOnlyList<RegistryValueInfo> values = RunWithin(() => reader.EnumerateValues(runRoot));

            Assert.AreEqual(4, values.Count);
            var byName = values.ToDictionary(value => value.Name, StringComparer.Ordinal);
            AssertValue(byName["abc"], 1, Encoding.Unicode.GetBytes("other\0"));
            AssertValue(byName["plain"], 4, BitConverter.GetBytes(7u));
            AssertValue(byName[NulName], 4, BitConverter.GetBytes(0x1388u));
            AssertValue(byName[SoloName], 3, new byte[] { 0x01, 0x02 });
            Assert.AreEqual(NulName.Length, byName[NulName].Name.Length);
        }
        finally
        {
            DeleteCountedValues(runRoot, NulName, SoloName);
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void EnumerationTerminatesWhileValuesChangeSize()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        byte[] small = { 0x01, 0x02, 0x03, 0x04 };
        byte[] big = Enumerable.Repeat((byte)0x5A, 256 * 1024).ToArray();

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.SetValue(runRoot, "Flip", 3, small);
            writer.SetValue(runRoot, "Stable", 4, BitConverter.GetBytes(9u));

            using var stop = new CancellationTokenSource();
            int writes = 0;
            Task flipper = Task.Run(() =>
            {
                bool useBig = true;
                while (!stop.IsCancellationRequested)
                {
                    writer.SetValue(runRoot, "Flip", 3, useBig ? big : small);
                    useBig = !useBig;
                    writes += 1;
                }
            });

            var reader = new RegistryReader();
            int completed = 0;
            int moreData = 0;
            try
            {
                for (int round = 0; round < 40; round += 1)
                {
                    try
                    {
                        IReadOnlyList<RegistryValueInfo> values = RunWithin(() => reader.EnumerateValues(runRoot));
                        RegistryValueInfo flip = values.Single(value => value.Name == "Flip");
                        RegistryValueInfo stable = values.Single(value => value.Name == "Stable");

                        Assert.IsTrue(
                            flip.DataLength == small.Length || flip.DataLength == big.Length,
                            $"Flip returned {flip.DataLength} bytes, which is neither state.");
                        CollectionAssert.AreEqual(
                            flip.DataLength == small.Length ? small : big,
                            flip.Data,
                            "Flip data does not match its reported size.");
                        AssertValue(stable, 4, BitConverter.GetBytes(9u));
                        completed += 1;
                    }
                    catch (RegistryOperationException ex) when (ex.Win32Error == ErrorMoreData)
                    {
                        // The bounded retry gave up on a value that kept changing. That is the
                        // documented outcome; a hang or a wrong length is not.
                        moreData += 1;
                    }
                }
            }
            finally
            {
                stop.Cancel();
                flipper.GetAwaiter().GetResult();
            }

            Assert.IsTrue(writes > 0, "The writer never ran, so the race was not exercised.");
            Assert.IsTrue(completed > 0, $"No enumeration completed; {moreData} gave up.");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void EnumerationTerminatesWhileValuesAreDeleted()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        const int ValueCount = 300;

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);

            for (int index = 0; index < ValueCount; index += 1)
            {
                writer.SetValue(runRoot, $"v{index:D3}", 4, BitConverter.GetBytes((uint)index));
            }

            Task deleter = Task.Run(() =>
            {
                for (int index = 0; index < ValueCount; index += 2)
                {
                    writer.DeleteValue(runRoot, $"v{index:D3}");
                }
            });

            var reader = new RegistryReader();
            try
            {
                for (int round = 0; round < 20; round += 1)
                {
                    IReadOnlyList<RegistryValueInfo> values = RunWithin(() => reader.EnumerateValues(runRoot));

                    Assert.IsTrue(values.Count <= ValueCount, $"Round {round} returned {values.Count} values.");
                    foreach (RegistryValueInfo value in values)
                    {
                        // Every value that survives keeps the data it was written with, whatever
                        // index it was seen at.
                        Assert.AreEqual(
                            uint.Parse(value.Name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture),
                            BitConverter.ToUInt32(value.Data),
                            value.Name);
                    }
                }
            }
            finally
            {
                deleter.GetAwaiter().GetResult();
            }

            IReadOnlyList<RegistryValueInfo> remaining = reader.EnumerateValues(runRoot);
            Assert.AreEqual(ValueCount / 2, remaining.Count);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void MaximumNameAndMegabyteDataRoundTripThroughEnumeration()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        string longName = new('n', 16383);
        byte[] data = new byte[1024 * 1024];
        for (int index = 0; index < data.Length; index += 1)
        {
            data[index] = (byte)(index * 31);
        }

        try
        {
            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            writer.CreateKey(runRoot);
            writer.SetValue(runRoot, longName, 3, data);

            var reader = new RegistryReader();
            IReadOnlyList<RegistryValueInfo> values = RunWithin(() => reader.EnumerateValues(runRoot));

            Assert.AreEqual(1, values.Count);
            AssertValue(values[0], 3, data);
            Assert.AreEqual(longName, values[0].Name);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    private const int ErrorMoreData = 234;

    private static T RunWithin<T>(Func<T> function)
    {
        Task<T> task = Task.Run(function);
        if (!task.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new AssertFailedException("The registry enumeration did not finish within 30 seconds.");
        }

        return task.GetAwaiter().GetResult();
    }

    private static void DeleteCountedValues(RegistryPath runRoot, params string[] names)
    {
        var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
        foreach (string name in names)
        {
            writer.DeleteValueWithCountedName(runRoot, name);
        }
    }

    private static string NewRunId() => "reader-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static RegistryKey OpenHkcu() =>
        RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

    private static RegistryKey CreateRunKey(RegistryKey hkcu, string runId) =>
        hkcu.CreateSubKey($@"{SandboxBase}\{runId}", writable: true)
        ?? throw new AssertFailedException("The sandbox run key could not be created.");

    private static RegistryKey OpenRunKey(string runId)
    {
        using RegistryKey hkcu = OpenHkcu();
        return hkcu.OpenSubKey($@"{SandboxBase}\{runId}")
            ?? throw new AssertFailedException("The sandbox run key is missing.");
    }

    private static void AssertValue(RegistryValueInfo value, uint expectedType, byte[] expectedData)
    {
        Assert.AreEqual(expectedType, value.Type, value.Name);
        Assert.AreEqual(
            expectedData.Length,
            value.DataLength,
            $"{value.Name}: {Convert.ToHexString(value.Data)}");
        Assert.AreEqual((RegistryValueType)expectedType, value.TypeEnum, value.Name);
        CollectionAssert.AreEqual(expectedData, value.Data, value.Name);
    }

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = OpenHkcu();
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
