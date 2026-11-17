#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// A value name with an embedded NUL can be listed, but no name-based call can address it: the Win32 name APIs stop at
/// the NUL. The reader and the writer must refuse such a name before any native call, so a read or a rename can never
/// reach the plain value (docs/failure-modes/value-display-enumeration.md section 7 and list.md section 11).
/// </summary>
[TestClass]
public sealed class RegistryValueNulNameTests
{
    private const string PlainName = "abc";
    private const string NulName = "abc\0def";

    [TestMethod]
    public void ReadValueRefusesANulNameAndDoesNotReturnThePlainValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            var reader = new RegistryReader();
            _ = Assert.ThrowsExactly<ArgumentException>(() => reader.ReadValue(runRoot, NulName));

            RegistryValueInfo? plain = reader.ReadValue(runRoot, PlainName);
            Assert.IsNotNull(plain);
            CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("plain data\0"), plain.Data);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void ValueExistsRefusesANulNameInsteadOfAnsweringForThePlainValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            var reader = new RegistryReader();
            _ = Assert.ThrowsExactly<ArgumentException>(() => reader.ValueExists(runRoot, NulName));
            Assert.IsTrue(reader.ValueExists(runRoot, PlainName));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void DeleteValueRefusesANulNameAndKeepsThePlainValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
            _ = Assert.ThrowsExactly<ArgumentException>(() => writer.DeleteValue(runRoot, NulName));

            Assert.IsTrue(new RegistryReader().ValueExists(runRoot, PlainName));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameValueRefusesANulOldNameBeforeAnyCopyOrDelete()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.RenameValue(runRoot, NulName, "renamed"));

            var reader = new RegistryReader();
            RegistryValueInfo? plain = reader.ReadValue(runRoot, PlainName);
            Assert.IsNotNull(plain);
            CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("plain data\0"), plain.Data);
            Assert.IsFalse(reader.ValueExists(runRoot, "renamed"));
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void RenameValueRefusesANulNewNameAndKeepsTheOldValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            _ = Assert.ThrowsExactly<ArgumentException>(() => store.RenameValue(runRoot, PlainName, NulName));

            var reader = new RegistryReader();
            Assert.IsTrue(reader.ValueExists(runRoot, PlainName));
            RegistryValueInfo? nulValue = Enumerate(runRoot).SingleOrDefault(value => value.Name == NulName);
            Assert.IsNotNull(nulValue);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    [TestMethod]
    public void EnumerationStillReturnsTheNulNamedValueWithItsOwnData()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        try
        {
            SeedPlainAndNulValues(runRoot);

            RegistryValueInfo? nulValue = Enumerate(runRoot).SingleOrDefault(value => value.Name == NulName);
            Assert.IsNotNull(nulValue);
            CollectionAssert.AreEqual(BitConverter.GetBytes(0x1388u), nulValue.Data);
            Assert.AreEqual(4u, nulValue.Type);
        }
        finally
        {
            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    private static void SeedPlainAndNulValues(RegistryPath runRoot)
    {
        var writer = new RegistryWriter(new SandboxWritePolicy(runRoot));
        writer.CreateKey(runRoot);
        writer.SetValue(runRoot, PlainName, 1, Encoding.Unicode.GetBytes("plain data\0"));
        writer.SetValueWithCountedName(runRoot, NulName, 4, BitConverter.GetBytes(0x1388u));
    }

    private static IReadOnlyList<RegistryValueInfo> Enumerate(RegistryPath runRoot) =>
        new RegistryReader().EnumerateValues(runRoot);

    private static string NewRunId() => "nulname-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxRunCleanup.SandboxBase}\{runId}");
}
#endif
