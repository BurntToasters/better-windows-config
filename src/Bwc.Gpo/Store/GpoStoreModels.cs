using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;

namespace Bwc.Gpo.Store;

/// <summary>Settings of a <see cref="GpoStore"/>.</summary>
public sealed class GpoStoreOptions
{
    /// <summary>A test hook told after each completed write step; null in the app.</summary>
    public Action<GpoWriteEvent>? WriteObserver { get; set; }

    /// <summary>How long a save waits for the write lock file. Default 15 seconds.</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Retries of a replace after a sharing or lock violation. Default 5.</summary>
    public int WriteRetryCount { get; set; } = 5;

    /// <summary>The wait between those retries. Default 50 ms.</summary>
    public TimeSpan WriteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The read-only detection result the tool obtained from <see cref="GpoAccess.Check"/>. When it says read-only,
    /// every save is refused before the write policy runs. Null means writable.
    /// </summary>
    public GpoAccessResult? Access { get; set; }
}

/// <summary>What was found for one side when the store was opened.</summary>
public sealed class GpoSideSnapshot
{
    /// <summary>The side.</summary>
    public required GpoSide Side { get; init; }

    /// <summary>The registry policy; empty when the file is absent or has zero bytes; null when it could not be read.</summary>
    public PolDocument? Pol { get; init; }

    /// <summary>Why <see cref="Pol"/> is null (a format or a store failure).</summary>
    public Exception? PolFailure { get; init; }

    /// <summary>Whether the Registry.pol file exists.</summary>
    public bool PolExists { get; init; }

    /// <summary>The comments; empty when the file is absent; null when it could not be read.</summary>
    public CmtxDocument? Comments { get; init; }

    /// <summary>Why <see cref="Comments"/> is null.</summary>
    public Exception? CommentsFailure { get; init; }

    /// <summary>Whether the comment.cmtx file exists.</summary>
    public bool CommentsExist { get; init; }
}

/// <summary>A read of the GPO folder. A snapshot only: it may mix files during a save; saving never uses it.</summary>
public sealed class GpoSnapshot
{
    /// <summary>Computer Configuration.</summary>
    public required GpoSideSnapshot Machine { get; init; }

    /// <summary>User Configuration.</summary>
    public required GpoSideSnapshot User { get; init; }

    /// <summary>The version file; a new one when absent; null when it could not be read.</summary>
    public GptIni? GptIni { get; init; }

    /// <summary>Why <see cref="GptIni"/> is null.</summary>
    public Exception? GptIniFailure { get; init; }

    /// <summary>Whether gpt.ini exists.</summary>
    public bool GptIniExists { get; init; }

    /// <summary>One side.</summary>
    /// <param name="side">The side.</param>
    /// <returns>Its snapshot.</returns>
    public GpoSideSnapshot Side(GpoSide side) => side == GpoSide.Machine ? Machine : User;
}

/// <summary>
/// What a save's edit may change: the models of one side, read from disk under the lock. Reading <see cref="Pol"/> or
/// <see cref="Comments"/> of a file that could not be read throws <see cref="GpoStoreException"/>, so an edit that
/// does not touch a file never fails on it.
/// </summary>
public sealed class GpoEditSession
{
    private readonly PolDocument? _pol;
    private readonly CmtxDocument? _comments;
    private readonly Exception? _polFailure;
    private readonly Exception? _commentsFailure;
    private readonly string _polPath;
    private readonly string _cmtxPath;

    internal GpoEditSession(
        GpoSide side,
        string polPath,
        string cmtxPath,
        PolDocument? pol,
        Exception? polFailure,
        CmtxDocument? comments,
        Exception? commentsFailure)
    {
        Side = side;
        _polPath = polPath;
        _cmtxPath = cmtxPath;
        _pol = pol;
        _polFailure = polFailure;
        _comments = comments;
        _commentsFailure = commentsFailure;
    }

    /// <summary>The side being saved. A policy of class Both is saved once per side.</summary>
    public GpoSide Side { get; }

    /// <summary>The registry policy model of this side, as just read.</summary>
    /// <exception cref="GpoStoreException">The file could not be read (malformed or an I/O failure).</exception>
    public PolDocument Pol => _pol ?? throw Unreadable(_polFailure, _polPath);

    /// <summary>The comment model of this side, as just read.</summary>
    /// <exception cref="GpoStoreException">The file could not be read.</exception>
    public CmtxDocument Comments => _comments ?? throw Unreadable(_commentsFailure, _cmtxPath);

    private static GpoStoreException Unreadable(Exception? failure, string path) =>
        failure as GpoStoreException
        ?? new GpoStoreException(
            GpoStoreFailure.Malformed, "Parse", path, 0, $"'{path}' is not a valid file and is left untouched: {failure?.Message}", failure);
}

/// <summary>The outcome of one save.</summary>
public sealed class GpoSaveResult
{
    /// <summary>The result of a save whose edit changed nothing.</summary>
    internal static GpoSaveResult NoChange { get; } = new();

    /// <summary>True when at least one file was written.</summary>
    public bool Wrote => WrittenFiles.Count > 0;

    /// <summary>The files written, in write order.</summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];

    /// <summary>The backup folder made by this save, or null (already made this session, or nothing written).</summary>
    public string? BackupFolder { get; init; }

    /// <summary>The first failure to remove an old backup folder, or null. Never fatal.</summary>
    public GpoStoreException? PruneFailure { get; init; }

    /// <summary>The gpt.ini version after the save, or null when gpt.ini was not written.</summary>
    public uint? GptVersion { get; init; }

    /// <summary>True when the refresh ran and succeeded.</summary>
    public bool Refreshed { get; init; }

    /// <summary>Why the refresh failed, or null. The files stay written.</summary>
    public GpoStoreException? RefreshFailure { get; init; }

    internal GpoSaveResult With(bool refreshed, GpoStoreException? refreshFailure) =>
        new()
        {
            WrittenFiles = WrittenFiles,
            BackupFolder = BackupFolder,
            PruneFailure = PruneFailure,
            GptVersion = GptVersion,
            Refreshed = refreshed,
            RefreshFailure = refreshFailure,
        };
}
