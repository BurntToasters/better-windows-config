using Bwc.Gpo.Refresh;

namespace Bwc.Gpo.Store;

/// <summary>
/// A policy for tests and the E2E flavor: a GPO root that must lie strictly inside a run data root, with no reparse
/// point at the root, on the chain between it and the run data root, or below it. Compiled in every flavor; the tool
/// decides when to use it. Reads no environment variable.
/// </summary>
public sealed class SandboxGpoStorePolicy : IGpoStorePolicy
{
    /// <summary>The name of the refresh record file the default recorder appends to, inside the sandbox root.</summary>
    public const string RefreshRecordFileName = "bwc-refresh.log";

    private readonly string[] _roots;

    /// <summary>Creates the policy, validating the roots.</summary>
    /// <param name="root">The sandbox GPO root. It need not exist yet.</param>
    /// <param name="runDataRoot">The run data root that must contain <paramref name="root"/> strictly.</param>
    /// <param name="backupRoot">The backup root; defaults to <paramref name="root"/> plus <c>.backup</c> beside it.</param>
    /// <param name="refresher">A refresher; defaults to a recorder that writes <see cref="RefreshRecordFileName"/>.</param>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    /// <exception cref="GpoStoreException">A root is outside the run root, overlaps, or has a reparse point.</exception>
    public SandboxGpoStorePolicy(string root, string runDataRoot, string? backupRoot = null, IPolicyRefresher? refresher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDataRoot);

        var run = Normalize(runDataRoot, "run data root");
        GpoRoot = Normalize(root, "GPO root");
        BackupRoot = backupRoot is null ? GpoRoot + ".backup" : Normalize(backupRoot, "backup root");

        if (!GpoPathChecks.IsUnder(run, GpoRoot))
        {
            throw GpoPathChecks.Denied("Sandbox", GpoRoot, "the GPO root is not strictly inside the run data root");
        }

        if (!GpoPathChecks.IsUnder(run, BackupRoot))
        {
            throw GpoPathChecks.Denied("Sandbox", BackupRoot, "the backup root is not strictly inside the run data root");
        }

        if (GpoPathChecks.IsSameOrUnder(GpoRoot, BackupRoot) || GpoPathChecks.IsSameOrUnder(BackupRoot, GpoRoot))
        {
            throw GpoPathChecks.Denied("Sandbox", BackupRoot, "the backup root and the GPO root must not overlap");
        }

        foreach (var folder in new[] { GpoRoot, BackupRoot })
        {
            if (GpoPathChecks.HasReparsePoint(run, folder, includePath: true, includeBoundary: false))
            {
                throw GpoPathChecks.Denied("Sandbox", folder, "a reparse point is at or above the root inside the run data root");
            }

            var below = FindReparsePointBelow(folder);
            if (below is not null)
            {
                throw GpoPathChecks.Denied("Sandbox", below, "a reparse point is below the root");
            }
        }

        _roots = [GpoRoot, BackupRoot];
        LockPath = Path.Combine(BackupRoot, "write.lock");
        Refresher = refresher ?? new RecordingPolicyRefresher(Path.Combine(GpoRoot, RefreshRecordFileName), new GpoFileWriter(this));
    }

    /// <inheritdoc />
    public string GpoRoot { get; }

    /// <inheritdoc />
    public string BackupRoot { get; }

    /// <inheritdoc />
    public string LockPath { get; }

    /// <inheritdoc />
    public IPolicyRefresher Refresher { get; }

    /// <inheritdoc />
    public void EnsureWriteAllowed(string path) => GpoPathChecks.Check(path, _roots, removing: false);

    /// <inheritdoc />
    public void EnsureRemoveAllowed(string path) => GpoPathChecks.Check(path, _roots, removing: true);

    private static string Normalize(string path, string what)
    {
        if (!GpoPathChecks.TryNormalize(path, out var full))
        {
            throw GpoPathChecks.Denied("Sandbox", path, $"the {what} is not a valid path");
        }

        return full;
    }

    private static string? FindReparsePointBelow(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false, IgnoreInaccessible = false };
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", options))
            {
                if (GpoPathChecks.IsReparsePoint(entry))
                {
                    return entry;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }

        return null;
    }
}
