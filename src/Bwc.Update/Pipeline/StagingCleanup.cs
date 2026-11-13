using System.Text.RegularExpressions;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// Housekeeping for the staging root (%TEMP%\BWC-Update). Only folders named as a staging folder (32
/// lowercase hex digits) are ever removed, and never through a link or junction. This file is also
/// compiled into the Hub (a source link), so it must not depend on anything else in this project.
/// </summary>
public static partial class StagingCleanup
{
    private const string ReceiptName = "receipt.json";

    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagingNamePattern();

    /// <summary>True for a name that a staging folder is created with.</summary>
    public static bool IsStagingFolderName(string name) => StagingNamePattern().IsMatch(name);

    /// <summary>Removes every staging folder under <paramref name="root"/> except <paramref name="keepFolder"/>.</summary>
    public static void Prune(string root, string keepFolder)
    {
        var keep = Path.GetFullPath(keepFolder);
        foreach (var folder in StagingFolders(root))
        {
            if (!string.Equals(Path.GetFullPath(folder), keep, StringComparison.OrdinalIgnoreCase))
            {
                Remove(folder);
            }
        }
    }

    /// <summary>
    /// Removes staging folders that have no receipt. A helper killed mid-download never writes its
    /// receipt, so its partial installer is discarded here. Returns the number of folders removed.
    /// </summary>
    public static int DiscardIncomplete(string root)
    {
        var removed = 0;
        foreach (var folder in StagingFolders(root))
        {
            if (!File.Exists(Path.Combine(folder, ReceiptName)) && Remove(folder))
            {
                removed++;
            }
        }

        return removed;
    }

    private static IEnumerable<string> StagingFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Where(folder => IsStagingFolderName(Path.GetFileName(folder)) && !IsReparsePoint(folder))
            .ToList();
    }

    private static bool Remove(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file is still held (for example by a running installer). The next run removes it.
            Log.Warn($"could not remove a staging folder: {ex.GetType().Name}");
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown is treated as a link, so the folder is left alone.
            return true;
        }
    }
}
