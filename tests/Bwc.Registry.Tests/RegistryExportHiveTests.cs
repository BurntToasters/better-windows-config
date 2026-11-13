using System.Text;
using Bwc.Registry.Privileges;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Hive export through <see cref="RegistryHiveExporter"/>. The elevated round trip saves a
/// sandbox subtree, checks the regf signature, deletes a value, and restores the saved file. The
/// non-elevated tests prove cancellation happens before native work and a missing privilege leaves
/// no file.
/// </summary>
[TestClass]
public sealed class RegistryExportHiveTests
{
    private const int ErrorPrivilegeNotHeld = 1314;

    [TestMethod]
    public void HiveExportObservesCancellationBeforeCreatingTheFile()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BwcExportHiveTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string hiveFile = System.IO.Path.Combine(directory, "canceled.hiv");

        try
        {
            var service = new RegistryExportService(new RegistryReader(), new RegistryWriter(new ShippingWritePolicy()));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            RegistryPath key = RegistryPath.Parse(@"HKCU\Software\BwcExportHive\Canceled");

            Assert.ThrowsExactly<OperationCanceledException>(
                () => service.ExportHiveFile(key, hiveFile, cancellation.Token));
            Assert.IsFalse(File.Exists(hiveFile), "A canceled hive export created a file.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

#if BWC_E2E
    [TestMethod]
    public void HiveExportReportsStartingBeforeAMissingPrivilege()
    {
        if (PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) &&
            PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege))
        {
            Assert.Inconclusive(
                "This process holds SeBackupPrivilege and SeRestorePrivilege, so the missing privilege path cannot be observed.");
        }

        string runId = "hive-export-" + Guid.NewGuid().ToString("N");
        RegistryPath sandboxBase = RegistryPath.Parse(@"HKCU\Software\BWC-E2E");
        RegistryPath runRoot = sandboxBase.Append(runId);
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BwcExportHiveTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string hiveFile = System.IO.Path.Combine(directory, "missing-privilege.hiv");
        var progress = new List<ExportProgress>();

        try
        {
            new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
            store.CreateKey(runRoot);
            store.SetString(runRoot, "Text", "value");

            var service = new RegistryExportService(store);
            RegistryOperationException error = Assert.ThrowsExactly<RegistryOperationException>(
                () => service.ExportHiveFile(
                    runRoot,
                    hiveFile,
                    CancellationToken.None,
                    new InlineProgress(progress)));

            Assert.AreEqual(ErrorPrivilegeNotHeld, error.Win32Error);
            Assert.AreEqual(nameof(RegistryWriteOperation.SaveKey), error.Operation);
            Assert.AreEqual(ExportPhase.Starting, progress[0].Phase);
            Assert.IsFalse(File.Exists(hiveFile), "A failed hive export created a file.");
        }
        finally
        {
            try
            {
                store.DeleteKey(runRoot, recursive: true);
            }
            catch (RegistryOperationException error) when (error.Win32Error == 2)
            {
                // The run key was never created.
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Elevated")]
    public void ElevatedHiveExportReloadsInsideTheSandbox()
    {
        if (!PrivilegeScope.IsAvailable(PrivilegeScope.BackupPrivilege) ||
            !PrivilegeScope.IsAvailable(PrivilegeScope.RestorePrivilege))
        {
            Assert.Inconclusive(
                "The hive export round trip requires an elevated token with SeBackupPrivilege and SeRestorePrivilege.");
        }

        string runId = "hive-export-" + Guid.NewGuid().ToString("N");
        RegistryPath sandboxBase = RegistryPath.Parse(@"HKCU\Software\BWC-E2E");
        RegistryPath runRoot = sandboxBase.Append(runId);
        RegistryPath source = runRoot.Append("Source");
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BwcExportHiveTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string hiveFile = System.IO.Path.Combine(directory, "export.hiv");
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            new RegistryWriter(new SandboxWritePolicy(sandboxBase)).CreateKey(sandboxBase);
            store.CreateKey(runRoot);
            store.CreateKey(source);
            store.SetString(source, "Text", "from-hive-export");
            store.SetDword(source, "Number", 42);

            var service = new RegistryExportService(store);
            service.ExportHiveFile(source, hiveFile);

            Assert.IsTrue(File.Exists(hiveFile), "The hive export did not create a file.");
            byte[] header = File.ReadAllBytes(hiveFile);
            Assert.IsTrue(header.Length >= 4, "The hive file is shorter than its signature.");
            Assert.AreEqual("regf", Encoding.ASCII.GetString(header, 0, 4));

            store.DeleteValue(source, "Text");
            Assert.IsNull(store.Reader.ReadValue(source, "Text"));

            store.RestoreKey(source, hiveFile);

            RegistryValueInfo? text = store.Reader.ReadValue(source, "Text");
            RegistryValueInfo? number = store.Reader.ReadValue(source, "Number");
            Assert.IsNotNull(text, "The restored key is missing the saved string value.");
            Assert.IsNotNull(number, "The restored key is missing the saved DWORD value.");
            Assert.AreEqual("from-hive-export", RegistryValueCodec.DecodeString(text!.Data));
            Assert.AreEqual(42u, RegistryValueCodec.DecodeDword(number!.Data));
        }
        finally
        {
            try
            {
                store.DeleteKey(runRoot, recursive: true);
            }
            catch (RegistryOperationException error) when (error.Win32Error == 2)
            {
                // The run key was never created.
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
#endif

    private sealed class InlineProgress : IProgress<ExportProgress>
    {
        private readonly List<ExportProgress> _events;

        public InlineProgress(List<ExportProgress> events)
        {
            _events = events;
        }

        public void Report(ExportProgress value) => _events.Add(value);
    }
}
