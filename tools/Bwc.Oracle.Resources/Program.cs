using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bwc.Oracle.Resources.Managed;
using Bwc.Oracle.Resources.Model;
using Bwc.Oracle.Resources.Msc;
using Bwc.Oracle.Resources.Native;
using Bwc.Oracle.Resources.Output;

namespace Bwc.Oracle.Resources;

/// Reads classic Windows tools and writes the UI resource oracle.
///   --tool regedit (default): regedit.exe and its en-US MUI into regedit-resources.json (golden, deterministic) and
///   regedit-resources.md (review rendering).
///   --tool gpedit: gpedit.dll, admtmpl.dll, mmc.exe, mmcndmgr.dll, mmcbase.dll and their en-US MUI, the managed WinForms
///   resources of the policy-setting dialog, and gpedit.msc, into docs/parity/oracle/gpedit (see
///   docs/failure-modes/oracle-gpedit-static.md).
internal static class Program
{
    private const string DefaultOutRelative = "docs/parity/oracle";
    private const string GpeditOutRelative = "docs/parity/oracle/gpedit";
    private const string JsonFileName = "regedit-resources.json";
    private const string MarkdownFileName = "regedit-resources.md";

    private static readonly string[] GpeditModules = ["gpedit.dll", "admtmpl.dll", "mmc.exe", "mmcndmgr.dll", "mmcbase.dll"];
    private const string EditorAssembly = "Microsoft.GroupPolicy.AdmTmplEditor";
    private const string EditorSatellite = "Microsoft.GroupPolicy.AdmTmplEditor.Resources";

    private static int Main(string[] args)
    {
        try
        {
            string? outArg = null;
            string tool = "regedit";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outArg = args[++i];
                }
                else if (args[i] == "--tool" && i + 1 < args.Length)
                {
                    tool = args[++i];
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

            if (tool is not ("regedit" or "gpedit"))
            {
                Console.Error.WriteLine($"error: unknown tool '{tool}' (valid: regedit, gpedit)");
                PrintUsage();
                return 2;
            }

            string repoRoot = FindRepoRoot();
            string defaultOut = tool == "gpedit" ? GpeditOutRelative : DefaultOutRelative;
            string outDir = Path.GetFullPath(Path.Combine(repoRoot, outArg ?? defaultOut));
            return tool == "gpedit" ? RunGpedit(outDir) : RunRegedit(outDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunRegedit(string outDir)
    {
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
            AddModule(golden, module, path);
        }

        Directory.CreateDirectory(outDir);
        string jsonPath = Path.Combine(outDir, JsonFileName);
        string markdownPath = Path.Combine(outDir, MarkdownFileName);
        // Write to temporary files and rename, so a reader never sees a partial dump.
        WriteAtomic(jsonPath, GoldenJson.Serialize(golden));
        WriteAtomic(markdownPath, GoldenMarkdown.Render(golden));

        Console.WriteLine($"wrote {jsonPath}");
        Console.WriteLine($"wrote {markdownPath}");
        PrintModuleSummary(golden);
        return 0;
    }

    private static int RunGpedit(string outDir)
    {
        string systemDir = Environment.SystemDirectory;
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string gacBase = Path.Combine(windowsDir, "Microsoft.NET", "assembly");
        string mscPath = Path.Combine(systemDir, "gpedit.msc");

        // Resolve every input first and fail with one message, so nothing is written from a partial set.
        var sources = new List<(string Module, string Path)>();
        foreach (string name in GpeditModules)
        {
            sources.Add((name, Path.Combine(systemDir, name)));
            sources.Add((name + ".mui", Path.Combine(systemDir, "en-US", name + ".mui")));
        }

        var problems = new List<string>();
        problems.AddRange(sources.Where(s => !File.Exists(s.Path)).Select(s => $"required module not found: {s.Path}"));
        if (!File.Exists(mscPath))
        {
            problems.Add($"required console file not found: {mscPath}");
        }

        List<GacLocator.Located> editor = GacLocator.FindAll(gacBase, EditorAssembly, culture: null);
        List<GacLocator.Located> satellite = GacLocator.FindAll(gacBase, EditorSatellite, culture: "en-US");
        string probed = string.Join(", ", GacLocator.RootOrder()) + " under " + gacBase;
        if (editor.Count == 0)
        {
            problems.Add($"{EditorAssembly}.dll not found in the GAC folders ({probed})");
        }
        if (satellite.Count == 0)
        {
            problems.Add($"{EditorSatellite}.dll (en-US or en) not found in the GAC folders ({probed})");
        }
        if (problems.Count > 0)
        {
            throw new FileNotFoundException("gpedit static oracle inputs are missing:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));
        }

        var golden = new GoldenFile();
        foreach (var (module, path) in sources)
        {
            AddModule(golden, module, path);
        }

        var forms = new FormsFile();
        forms.Assemblies.Add(GacLocator.Describe(EditorAssembly, "main", null, editor));
        forms.Assemblies.Add(GacLocator.Describe(EditorSatellite, "satellite", "en-US", satellite));
        ManagedResources.Read(EditorAssembly, editor[0].Path, forms);
        ManagedResources.Read(EditorSatellite, satellite[0].Path, forms);
        forms.ResourceFiles.Sort((a, b) =>
        {
            int byAssembly = string.CompareOrdinal(a.Assembly, b.Assembly);
            return byAssembly != 0 ? byAssembly : string.CompareOrdinal(a.Name, b.Name);
        });
        forms.Skipped.Sort((a, b) =>
        {
            int byAssembly = string.CompareOrdinal(a.Assembly, b.Assembly);
            return byAssembly != 0 ? byAssembly : string.CompareOrdinal(a.Name, b.Name);
        });
        CheckForms(forms);

        var msc = ConsoleFileParser.Parse(mscPath);
        IndexFile index = ResourceIndex.Build(golden, forms);
        // Checked before anything is written: a failed check leaves every output file as it was.
        string indexText = ResourceIndex.Serialize(index);

        Directory.CreateDirectory(outDir);
        string[] written =
        [
            Path.Combine(outDir, "gpedit-resources.json"),
            Path.Combine(outDir, "gpedit-resources.md"),
            Path.Combine(outDir, "gpedit-forms.json"),
            Path.Combine(outDir, "gpedit-msc.json"),
            Path.Combine(outDir, "gpedit-resource-index.json"),
        ];
        WriteAtomic(written[0], GoldenJson.Serialize(golden));
        WriteAtomic(
            written[1],
            GoldenMarkdown.Render(
                golden,
                title: "Classic gpedit resource oracle",
                source: "classic `gpedit.dll`, `admtmpl.dll`, `mmc.exe`, `mmcndmgr.dll`, `mmcbase.dll` and their `en-US` MUI resource files",
                regenerate: "npm run oracle -- --tool gpedit --only resources"));
        WriteAtomic(written[2], GoldenJson.Serialize(forms));
        WriteAtomic(written[3], GoldenJson.Serialize(msc));
        WriteAtomic(written[4], indexText);

        foreach (string path in written)
        {
            Console.WriteLine($"wrote {path}");
        }
        PrintModuleSummary(golden);
        foreach (ManagedResourceFile file in forms.ResourceFiles)
        {
            Console.WriteLine($"{file.Assembly}: {file.Name}: {file.EntryCount} entries{(file.Error is null ? string.Empty : ", error " + file.Error)}");
        }
        return 0;
    }

    /// The Editor form is the reason the extractor exists, so its absence or failure fails the run.
    private static void CheckForms(FormsFile forms)
    {
        if (forms.ResourceFiles.Count == 0)
        {
            throw new InvalidDataException("no .resources stream was found in the managed assemblies");
        }

        ManagedResourceFile? editorForm = forms.ResourceFiles.FirstOrDefault(f => f.Name.EndsWith(".Editor.en.resources", StringComparison.Ordinal))
            ?? forms.ResourceFiles.FirstOrDefault(f => f.Name.EndsWith(".Editor.resources", StringComparison.Ordinal));
        if (editorForm is null)
        {
            throw new InvalidDataException("the Editor form resources (Editor.en.resources) were not found; found: " + string.Join(", ", forms.ResourceFiles.Select(f => f.Name)));
        }
        if (editorForm.Error is not null)
        {
            throw new InvalidDataException($"the Editor form resources could not be read: {editorForm.Error}");
        }
    }

    private static void AddModule(GoldenFile golden, string module, string path)
    {
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

    private static void PrintModuleSummary(GoldenFile golden)
    {
        foreach (ModuleDump module in golden.Modules)
        {
            Console.WriteLine(
                $"{module.Module}: menus {module.Menus.Count}, dialogs {module.Dialogs.Count}, " +
                $"accelerator tables {module.Accelerators.Count}, string blocks {module.StringTables.Count}, " +
                $"resource types {module.ResourceTypes.Count}, errors {module.ErrorCount}");
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, utf8NoBom);
        File.Move(temp, path, overwrite: true);
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
        Console.Error.WriteLine("usage: dotnet run --project tools/Bwc.Oracle.Resources -- [--tool regedit|gpedit] [--out <dir>]");
        Console.Error.WriteLine("  --tool <name>  regedit (default) or gpedit");
        Console.Error.WriteLine($"  --out <dir>    output folder, relative to the repository root (default: {DefaultOutRelative}, or {GpeditOutRelative} for gpedit)");
    }
}
