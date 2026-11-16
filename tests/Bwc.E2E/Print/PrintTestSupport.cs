using System.Runtime.InteropServices;
using System.Text;
using Bwc.E2E.Harness;
using Microsoft.Win32;

namespace Bwc.E2E.Print;

/// <summary>
/// Shared helpers for the print E2E tests. The tests never open a physical printer's dialog and send a job. They read
/// printer state (EnumPrinters, EnumJobs, the default printer) and drive the app through E2E-only seams that write a
/// PDF to a file under the artifacts folder.
/// </summary>
internal static class PrintTestSupport
{
    internal const string PdfPrinterName = "Microsoft Print to PDF";
    internal const string PdfDriverName = "Microsoft Print To PDF";
    internal const string ArtifactsDirVariable = "BWC_E2E_PRINT_ARTIFACTS_DIR";
    internal const string LineDelayVariable = "BWC_E2E_PRINT_LINE_DELAY_MS";
    internal const string PdfPathVariable = "BWC_E2E_PRINT_PDF_PATH";
    internal const string ResultPathVariable = "BWC_E2E_PRINT_RESULT_PATH";
    internal const string GuardProbeVariable = "BWC_E2E_PRINT_GUARD_PROBE";
    internal const string ReportPathVariable = "BWC_E2E_PRINT_REPORT_PATH";

    /// <summary>The physical printers on this machine. The tests name them only to show they were never used.</summary>
    internal static readonly string[] PhysicalPrinters =
    [
        "HP OfficeJet Pro 9130e Series",
        "Brother MFC-T980DW",
        "Brother MFC-L8900CDW series",
    ];

    /// <summary>What EnumPrinters reports for "Microsoft Print to PDF".</summary>
    internal readonly record struct PdfPrinterState(bool Installed, string Driver)
    {
        /// <summary>True when the name and driver are the ones the PDF test may use.</summary>
        public bool Usable => Installed && string.Equals(Driver, PdfDriverName, StringComparison.OrdinalIgnoreCase);
    }

    internal static PdfPrinterState ProbePdfPrinter()
    {
        foreach (PrinterInfo2 printer in EnumLocalPrinters())
        {
            if (string.Equals(printer.Name, PdfPrinterName, StringComparison.Ordinal))
            {
                return new PdfPrinterState(true, printer.Driver);
            }
        }

        return new PdfPrinterState(false, string.Empty);
    }

    /// <summary>The default printer's name, or null when Windows reports none.</summary>
    internal static string? DefaultPrinterName()
    {
        uint size = 0;
        _ = GetDefaultPrinter(null, ref size);
        if (size == 0)
        {
            return null;
        }

        var name = new StringBuilder((int)size);
        return GetDefaultPrinter(name, ref size) ? name.ToString() : null;
    }

    /// <summary>
    /// Restores the default printer saved before a PDF job, only when the default changed. The only printer-settings
    /// write the tests make. An empty saved name sets nothing, and the saved name is the only name ever set.
    /// </summary>
    internal static string? RestoreDefaultPrinter(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return DefaultPrinterName();
        }

        string? current = DefaultPrinterName();
        if (!string.Equals(current, saved, StringComparison.Ordinal) && !SetDefaultPrinter(saved))
        {
            return current;
        }

        return DefaultPrinterName();
    }

    /// <summary>The number of spooler jobs for a local printer. -1 when the printer cannot be opened.</summary>
    internal static int JobCount(string printerName)
    {
        if (!OpenPrinter(printerName, out nint handle, nint.Zero) || handle == nint.Zero)
        {
            return -1;
        }

        try
        {
            _ = EnumJobs(handle, 0, 999, 1, nint.Zero, 0, out uint needed, out _);
            if (needed == 0)
            {
                return 0;
            }

            nint buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                return EnumJobs(handle, 0, 999, 1, buffer, needed, out _, out uint returned) ? (int)returned : -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = ClosePrinter(handle);
        }
    }

    /// <summary>Seeds a key with values. A large tree has two 4 KB binary values, so a walk takes many lines.</summary>
    internal static void SeedPrintTree(SandboxRegistry sandbox, bool large)
    {
        using (RegistryKey root = sandbox.CreateSubKey(string.Empty))
        {
            root.SetValue("PrintValue", "printed", RegistryValueKind.String);
            root.SetValue("PrintDword", 7, RegistryValueKind.DWord);
            if (large)
            {
                for (int index = 0; index < 2; index += 1)
                {
                    var data = new byte[4096];
                    Random.Shared.NextBytes(data);
                    root.SetValue("PrintBinary" + index, data, RegistryValueKind.Binary);
                }
            }
        }

        using RegistryKey child = sandbox.CreateSubKey("PrintChild");
        child.SetValue("ChildValue", "child", RegistryValueKind.String);
    }

    internal static string CreateFreshDataRoot(E2EConfig config, string scope)
    {
        string path = config.DataRootFor(scope);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteDataRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The application exit path may still hold a settings file for a moment.
        }
    }

    /// <summary>The folder for this run's print artifacts, under the repo's artifacts/e2e/print.</summary>
    internal static string ArtifactDir(E2EConfig config)
    {
        string dir = Path.Combine(config.ArtifactsDir, "print");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The repo's artifacts folder. The app's guard accepts only output files under it.</summary>
    internal static string RepoArtifactsDir(E2EConfig config) => Path.Combine(config.RepoRoot, "artifacts");

    /// <summary>Reads one result line per key from the seam's result file, as name=value pairs keyed by name.</summary>
    internal static Dictionary<string, string> ReadResult(string resultPath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(resultPath))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values[line[..equals]] = line[(equals + 1)..];
            }
        }

        return values;
    }

    internal static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<PrinterInfo2> EnumLocalPrinters()
    {
        const uint PrinterEnumLocal = 0x00000002;
        _ = EnumPrinters(PrinterEnumLocal, null, 2, nint.Zero, 0, out uint needed, out _);
        if (needed == 0)
        {
            yield break;
        }

        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(PrinterEnumLocal, null, 2, buffer, needed, out _, out uint returned))
            {
                yield break;
            }

            int size = Marshal.SizeOf<PrinterInfo2Native>();
            for (int index = 0; index < returned; index += 1)
            {
                var native = Marshal.PtrToStructure<PrinterInfo2Native>(buffer + (index * size));
                yield return new PrinterInfo2(
                    Marshal.PtrToStringUni(native.PrinterName) ?? string.Empty,
                    Marshal.PtrToStringUni(native.DriverName) ?? string.Empty);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct PrinterInfo2(string Name, string Driver);

    // The PRINTER_INFO_2W layout. Only the name and driver pointers are read.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2Native
    {
        public nint ServerName;
        public nint PrinterName;
        public nint ShareName;
        public nint PortName;
        public nint DriverName;
        public nint Comment;
        public nint Location;
        public nint DevMode;
        public nint SepFile;
        public nint PrintProcessor;
        public nint Datatype;
        public nint Parameters;
        public nint SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint CJobs;
        public uint AveragePpm;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        nint buffer,
        uint bufferSize,
        out uint needed,
        out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out nint handle, nint defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(nint handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumJobsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumJobs(
        nint handle,
        uint firstJob,
        uint count,
        uint level,
        nint buffer,
        uint bufferSize,
        out uint needed,
        out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(StringBuilder? name, ref uint size);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetDefaultPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultPrinter(string name);
}
