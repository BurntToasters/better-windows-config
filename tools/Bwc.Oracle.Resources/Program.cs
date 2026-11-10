using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Native;
using Bwc.Oracle.Resources.Output;

namespace Bwc.Oracle.Resources;

/// Reads classic regedit.exe and its en-US MUI and writes the UI resource oracle:
/// regedit-resources.json (golden, deterministic) and regedit-resources.md (review rendering).
internal static class Program
{
    private const string DefaultOutRelative = "docs/parity/oracle";
    private const string JsonFileName = "regedit-resources.json";
    private const string MarkdownFileName = "regedit-resources.md";

    private static int Main(string[] args)
    {
        try
        {
            string? outArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outArg = args[++i];
                }
                else if (args[i] is "--help" or "-h" or "/?")
                {
                    PrintUsage();
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine($"error: unknown or incomplete argument '{args[i]}'");
                    PrintUsage();
                    return 2;
                }
            }

            string repoRoot = FindRepoRoot();
            string outDir = Path.GetFullPath(Path.Combine(repoRoot, outArg ?? DefaultOutRelative));

            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var sources = new (string Module, string Path)[]
            {
                ("regedit.exe", Path.Combine(windowsDir, "regedit.exe")),
                ("en-US/regedit.exe.mui", Path.Combine(windowsDir, "en-US", "regedit.exe.mui")),
            };

            var golden = new GoldenFile();
            foreach (var (module, path) in sources)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException($"required module not found: {path}", path);
                }

                golden.Header.Sources.Add(DescribeSource(module, path));

                IntPtr handle = ResourceModule.Load(path);
                try
                {
                    var entries = ResourceModule.EnumerateAll(handle);
                    golden.Modules.Add(GoldenBuilder.Build(module, path, entries));
                }
                finally
                {
                    ResourceModule.Unload(handle);
                }
            }

            Directory.CreateDirectory(outDir);
            string jsonPath = Path.Combine(outDir, JsonFileName);
            string markdownPath = Path.Combine(outDir, MarkdownFileName);
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(jsonPath, GoldenJson.Serialize(golden), utf8NoBom);
            File.WriteAllText(markdownPath, GoldenMarkdown.Render(golden), utf8NoBom);

            Console.WriteLine($"wrote {jsonPath}");
            Console.WriteLine($"wrote {markdownPath}");
            foreach (ModuleDump module in golden.Modules)
            {
                Console.WriteLine(
                    $"{module.Module}: menus {module.Menus.Count}, dialogs {module.Dialogs.Count}, " +
                    $"accelerator tables {module.Accelerators.Count}, string blocks {module.StringTables.Count}, " +
                    $"resource types {module.ResourceTypes.Count}, errors {module.ErrorCount}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static SourceInfo DescribeSource(string module, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
        return new SourceInfo
        {
            Module = module,
            Path = path,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            FileVersion = version.FileVersion,
            ProductVersion = version.ProductVersion,
            ProductName = version.ProductName,
            FileDescription = version.FileDescription,
            OriginalFileName = version.OriginalFilename,
            InternalName = version.InternalName,
            CompanyName = version.CompanyName,
        };
    }

    /// The repository root is the nearest folder holding global.json, searched upward from the working directory
    /// first and from the tool's own folder second.
    private static string FindRepoRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "global.json")))
                {
                    return dir.FullName;
                }
            }
        }
        throw new DirectoryNotFoundException("could not find the repository root (a folder containing global.json)");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("usage: dotnet run --project tools/Bwc.Oracle.Resources -- [--out <dir>]");
        Console.Error.WriteLine($"  --out <dir>  output folder, relative to the repository root (default: {DefaultOutRelative})");
    }
}
