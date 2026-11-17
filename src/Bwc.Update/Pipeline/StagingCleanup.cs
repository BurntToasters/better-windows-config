using System.Text.RegularExpressions;
using Bwc.Core;

namespace Bwc.Update;

/// <summary>
/// Housekeeping for the staging root (%TEMP%\BWC-Update). Only folders named as a staging folder (32
/// lowercase hex digits) are ever removed, and never through a link or junction. Every level that is walked is checked:
/// a staging root that is a link, or cannot be inspected, is not walked at all, and a link inside a staging folder is
/// removed as a link and never entered (update-elevation.md, HK2 and HK3). This file is also
/// compiled into the Hub (a source link), so it must not depend on anything else in this project.
/// </summary>
public static partial class StagingCleanup
{
    private const string ReceiptName = "receipt.json";

    [GeneratedRegex("^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagingNamePattern();

    /// <summary>True for a name that a staging folder is created with.</summary>
    public static bool IsStagingFolderName(string name) => StagingNamePattern().IsMatch(name);

    /// <summary>A folder younger than this may belong to another install's staged update, so it is kept.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes staging folders under <paramref name="root"/> that are older than <see cref="MinimumAge"/>, except
    /// <paramref name="keepFolder"/>. A younger folder is left alone, so a second install's staged update survives.
    /// </summary>
    public static void Prune(string root, string keepFolder)
    {
        if (!IsPlainRoot(root))
        {
            return;
        }

        var keep = Path.GetFullPath(keepFolder);
        var now = DateTime.UtcNow;
        foreach (var folder in StagingFolders(root))
        {
            if (!string.Equals(Path.GetFullPath(folder), keep, StringComparison.OrdinalIgnoreCase) && IsOlder(folder, now))
            {
                Remove(folder);
            }
        }
    }

    /// <summary>Age is the later of folder creation and receipt write time. Unknown counts as young.</summary>
    private static bool IsOlder(string folder, DateTime nowUtc)
    {
        try
        {
            var last = Directory.GetCreationTimeUtc(folder);
            var receipt = Path.Combine(folder, ReceiptName);
            if (File.Exists(receipt) && File.GetLastWriteTimeUtc(receipt) > last)
            {
                last = File.GetLastWriteTimeUtc(receipt);
            }

            return nowUtc - last >= MinimumAge;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes staging folders that have no receipt. A helper killed mid-download never writes its
    /// receipt, so its partial installer is discarded here. Returns the number of folders removed.
    /// </summary>
    public static int DiscardIncomplete(string root)
    {
        var removed = 0;
        if (!IsPlainRoot(root))
        {
            return removed;
        }

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
            DeleteTree(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file is still held (for example by a running installer). The next run removes it.
            Log.Warn($"could not remove a staging folder: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>True when the root is a real folder: it exists, is not a link or junction, and can be inspected.</summary>
    private static bool IsPlainRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return false;
            }

            var attributes = File.GetAttributes(root);
            return (attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unknown is treated as a link, so nothing is walked.
            return false;
        }
    }

    // A link entry is removed as a link. Its target is never entered, so a planted junction cannot reach other folders.
    private static void DeleteTree(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isFolder)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (isFolder)
            {
                DeleteTree(entry);
            }
            else
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
        }

        Directory.Delete(folder, recursive: false);
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
