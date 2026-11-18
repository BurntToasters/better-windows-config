using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Bwc.Oracle.Resources.Model;

namespace Bwc.Oracle.Resources.Managed;

/// Finds a managed assembly in the four GAC folders under %SystemRoot%\Microsoft.NET\assembly, and nowhere else.
/// Each folder is probed at <root>\<assembly>\<version folder>\<assembly>.dll. The version folder is
/// "v4.0_<version>_<culture>_<public key token>" with an empty culture for a main assembly.
internal static class GacLocator
{
    private static readonly string[] SatelliteCultures = ["en-US", "en"];

    public sealed record Located(string GacRoot, string VersionFolder, string Path);

    /// The probe order: neutral first, then the folder of the host architecture, then 32 bit, then the rest.
    public static IReadOnlyList<string> RootOrder()
    {
        string host = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "GAC_Arm64" : "GAC_64";
        string other = host == "GAC_Arm64" ? "GAC_64" : "GAC_Arm64";
        return ["GAC_MSIL", host, "GAC_32", other];
    }

    /// Every copy found, in probe order. A main assembly (culture null) matches the folders with no culture. A satellite
    /// matches en-US first, then en.
    public static List<Located> FindAll(string gacBase, string assemblyName, string? culture)
    {
        var found = new List<Located>();
        foreach (string root in RootOrder())
        {
            string assemblyDir = System.IO.Path.Combine(gacBase, root, assemblyName);
            if (!Directory.Exists(assemblyDir))
            {
                continue;
            }

            var candidates = new List<(Version Version, int CultureRank, string Folder)>();
            foreach (string dir in Directory.EnumerateDirectories(assemblyDir))
            {
                string folder = System.IO.Path.GetFileName(dir);
                string[] parts = folder.Split('_');
                if (parts.Length != 4 || !Version.TryParse(parts[1], out Version? version))
                {
                    continue;
                }

                int rank = CultureRank(parts[2], culture);
                if (rank < 0 || !File.Exists(System.IO.Path.Combine(dir, assemblyName + ".dll")))
                {
                    continue;
                }
                candidates.Add((version, rank, folder));
            }

            var best = candidates
                .OrderBy(c => c.CultureRank)
                .ThenByDescending(c => c.Version)
                .ThenBy(c => c.Folder, StringComparer.Ordinal)
                .Cast<(Version Version, int CultureRank, string Folder)?>()
                .FirstOrDefault();
            if (best is { } pick)
            {
                found.Add(new Located(root, pick.Folder, System.IO.Path.Combine(assemblyDir, pick.Folder, assemblyName + ".dll")));
            }
        }
        return found;
    }

    private static int CultureRank(string folderCulture, string? wanted)
    {
        if (wanted is null)
        {
            return folderCulture.Length == 0 ? 0 : -1;
        }
        for (int i = 0; i < SatelliteCultures.Length; i++)
        {
            if (string.Equals(folderCulture, SatelliteCultures[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    public static ManagedAssemblyInfo Describe(string assemblyName, string role, string? culture, IReadOnlyList<Located> copies)
    {
        Located primary = copies[0];
        byte[] bytes = File.ReadAllBytes(primary.Path);
        return new ManagedAssemblyInfo
        {
            Assembly = assemblyName,
            Role = role,
            Culture = culture,
            GacRoot = primary.GacRoot,
            VersionFolder = primary.VersionFolder,
            Path = primary.Path,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            OtherCopies = copies.Skip(1).Select(c => new OtherCopy
            {
                GacRoot = c.GacRoot,
                Path = c.Path,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(c.Path))).ToLowerInvariant(),
            }).ToList(),
        };
    }
}
