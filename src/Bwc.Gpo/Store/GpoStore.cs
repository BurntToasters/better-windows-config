using Bwc.Gpo.Comments;
using Bwc.Gpo.Pol;

namespace Bwc.Gpo.Store;

/// <summary>
/// The local GPO as three kinds of file: open reads them, save runs the contract's save sequence (re-read under the
/// write lock file, apply the caller's edit to the fresh models, skip everything when nothing changed, back up once per
/// session, write comment.cmtx then Registry.pol then gpt.ini, refresh). Every byte goes through
/// <see cref="GpoFileWriter"/> and the write policy. One instance is one session: the backup is made once per instance.
/// </summary>
public sealed class GpoStore
{
    private readonly IGpoStorePolicy _policy;
    private readonly GpoStoreOptions _options;
    private readonly GpoFileWriter _writer;
    private bool _backedUp;

    /// <summary>Creates a store.</summary>
    /// <param name="policy">The write policy: roots, lock file, refresher and path check.</param>
    /// <param name="options">Settings and the test hook; null for the defaults.</param>
    public GpoStore(IGpoStorePolicy policy, GpoStoreOptions? options = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new GpoStoreOptions();
        _writer = new GpoFileWriter(policy, _options.WriteObserver)
        {
            RetryCount = _options.WriteRetryCount,
            RetryDelay = _options.WriteRetryDelay,
        };
    }

    /// <summary>The policy this store writes through.</summary>
    public IGpoStorePolicy Policy => _policy;

    /// <summary>
    /// Reads the five files. Takes no lock and writes nothing. A missing file is an empty model; an unreadable or
    /// invalid file is reported per file and the rest still load.
    /// </summary>
    /// <param name="cancellationToken">Checked between files.</param>
    /// <returns>A snapshot.</returns>
    public GpoSnapshot Open(CancellationToken cancellationToken = default)
    {
        var root = _policy.GpoRoot;
        var machine = ReadSide(root, GpoSide.Machine, cancellationToken);
        var user = ReadSide(root, GpoSide.User, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var gpt = LoadGpt(GpoFileNames.GptIniPath(root));
        return new GpoSnapshot
        {
            Machine = machine,
            User = user,
            GptIni = gpt.Document,
            GptIniFailure = gpt.Failure,
            GptIniExists = gpt.Exists,
        };
    }

    /// <summary>
    /// One save of one side. Re-reads the files under the lock, runs <paramref name="edit"/> on the fresh models, and
    /// writes only what changed: <c>comment.cmtx</c> (when its bytes changed), <c>Registry.pol</c> (when its bytes
    /// changed), then <c>gpt.ini</c> with the side's version bump and extension pair (when Registry.pol was written),
    /// then a refresh of that side. A save that changes nothing writes nothing, makes no backup and does not refresh.
    /// </summary>
    /// <param name="side">The side being saved. A policy of class Both is saved once per side.</param>
    /// <param name="edit">The change, run under the lock on models read just now. It must not call the store.</param>
    /// <param name="cancellationToken">Checked before the first write only; a started save runs to the end.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="GpoStoreException">Read-only mode, lock, backup, policy, write or malformed-file failures.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the first write.</exception>
    public GpoSaveResult Save(GpoSide side, Action<GpoEditSession> edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var root = _policy.GpoRoot;
        if (_options.Access is { IsReadOnly: true } access)
        {
            throw new GpoStoreException(
                GpoStoreFailure.ReadOnlyMode, "Save", root, 0, $"The editor is read-only ({string.Join(", ", access.Reasons)}); nothing is saved.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        GpoSaveResult result;
        bool polWritten;
        using (WriteLock.Acquire(_policy, _options.LockTimeout))
        {
            (result, polWritten) = SaveLocked(side, edit, cancellationToken);
        }

        if (!polWritten)
        {
            return result;
        }

        try
        {
            _policy.Refresher.Refresh(side);
            return result.With(refreshed: true, refreshFailure: null);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            var failure = e as GpoStoreException
                ?? new GpoStoreException(GpoStoreFailure.RefreshFailed, "Refresh", side.ToString(), 0, $"Refreshing {side} policy failed: {e.Message}", e);
            return result.With(refreshed: false, refreshFailure: failure);
        }
    }

    private (GpoSaveResult Result, bool PolWritten) SaveLocked(GpoSide side, Action<GpoEditSession> edit, CancellationToken cancellationToken)
    {
        var root = _policy.GpoRoot;
        var polPath = GpoFileNames.PolPath(root, side);
        var cmtxPath = GpoFileNames.CmtxPath(root, side);
        var gptPath = GpoFileNames.GptIniPath(root);

        var pol = LoadPol(polPath);
        var cmtx = LoadCmtx(cmtxPath);
        var gpt = LoadGpt(gptPath);

        edit(new GpoEditSession(side, polPath, cmtxPath, pol.Document, pol.Failure, cmtx.Document, cmtx.Failure));

        var cmtxChanged = cmtx.Document is { HasChanges: true };

        // The first save of a side creates its Registry.pol even when the edit adds no record: capture A/Machine_CommentOnly
        // and K/KeyLeadingBackslash_Enable (D10), where the file is the 8-byte empty document.
        var polChanged = pol.Document is { HasChanges: true } || (pol.Document is not null && !pol.Exists && cmtxChanged);
        if (!polChanged && !cmtxChanged)
        {
            return (GpoSaveResult.NoChange, false);
        }

        var gptChanged = polChanged || (cmtxChanged && GptIniRules.CommentOnlyChangeBumpsVersion);
        if (gptChanged)
        {
            PrepareGpt(gpt, gptPath, side, pol.Document is null ? null : pol.Document.RecordCount > 0);
        }

        // Nothing is written until every target is known to be writable, so a read-only file cannot split a save.
        if (cmtxChanged)
        {
            _writer.EnsureWritable(cmtxPath);
        }

        if (polChanged)
        {
            _writer.EnsureWritable(polPath);
        }

        if (gptChanged)
        {
            _writer.EnsureWritable(gptPath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        string? backupFolder = null;
        GpoStoreException? pruneFailure = null;
        if (!_backedUp)
        {
            backupFolder = _writer.BackupFiles(GpoFileNames.AllFiles(root));
            _backedUp = true;
            pruneFailure = _writer.PruneBackups(GpoFileWriter.BackupsToKeep);
        }

        var written = new List<string>();
        uint? version = null;
        try
        {
            if (cmtxChanged)
            {
                _writer.WriteAtomic(cmtxPath, cmtx.Document!.ToBytes());
                written.Add(cmtxPath);
            }

            if (polChanged)
            {
                _writer.WriteAtomic(polPath, pol.Document!.ToBytes());
                written.Add(polPath);
            }

            if (gptChanged)
            {
                _writer.WriteAtomic(gptPath, gpt.Document!.ToBytes());
                written.Add(gptPath);
                version = gpt.Document.Version;
            }
        }
        catch (GpoStoreException e) when (written.Count > 0)
        {
            // There is no rollback (docs/failure-modes/gpo-store.md, E10 and E15): say which files already reached disk.
            e.SetWrittenFiles(written.ToArray());
            throw;
        }

        var result = new GpoSaveResult
        {
            WrittenFiles = written,
            BackupFolder = backupFolder,
            PruneFailure = pruneFailure,
            GptVersion = version,
        };
        return (result, polChanged);
    }

    private static void PrepareGpt(Loaded<GptIni> gpt, string gptPath, GpoSide side, bool? sideHasSettings)
    {
        if (gpt.Document is null)
        {
            throw gpt.Failure as GpoStoreException
                ?? new GpoStoreException(
                    GpoStoreFailure.Malformed, "Parse", gptPath, 0, $"'{gptPath}' is not valid and is left untouched: {gpt.Failure?.Message}", gpt.Failure);
        }

        try
        {
            if (sideHasSettings == true)
            {
                gpt.Document.EnsureExtensionPair(side);
            }
            else if (sideHasSettings == false && GptIniRules.EmptiedSideBlanksPairLine)
            {
                gpt.Document.BlankExtensionPair(side);
            }

            gpt.Document.BumpVersion(side);
        }
        catch (GptIniFormatException e)
        {
            throw new GpoStoreException(GpoStoreFailure.Malformed, "Parse", gptPath, 0, e.Message, e);
        }
    }

    private GpoSideSnapshot ReadSide(string root, GpoSide side, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pol = LoadPol(GpoFileNames.PolPath(root, side));
        cancellationToken.ThrowIfCancellationRequested();
        var cmtx = LoadCmtx(GpoFileNames.CmtxPath(root, side));
        return new GpoSideSnapshot
        {
            Side = side,
            Pol = pol.Document,
            PolFailure = pol.Failure,
            PolExists = pol.Exists,
            Comments = cmtx.Document,
            CommentsFailure = cmtx.Failure,
            CommentsExist = cmtx.Exists,
        };
    }

    private static Loaded<PolDocument> LoadPol(string path)
    {
        if (!TryOpen(path, out var stream, out var exists, out var failure))
        {
            return new Loaded<PolDocument>(null, failure, exists);
        }

        if (stream is null)
        {
            return new Loaded<PolDocument>(PolDocument.Create(), null, false);
        }

        using (stream)
        {
            try
            {
                // A zero-byte file is not a policy file: classic refuses to open it (D/File_Empty), so the reader reports Empty.
                var document = PolDocument.Parse(stream);
                return new Loaded<PolDocument>(document, null, true);
            }
            catch (PolFormatException e)
            {
                return new Loaded<PolDocument>(null, e, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Loaded<PolDocument>(null, GpoStoreException.FromIo("Read", path, e), true);
            }
        }
    }

    private static Loaded<CmtxDocument> LoadCmtx(string path) =>
        LoadBytes(path, CmtxDocument.MaxBytes, CmtxDocument.CreateEmpty, bytes => CmtxDocument.Parse(bytes), e => e is CmtxFormatException);

    private static Loaded<GptIni> LoadGpt(string path) =>
        LoadBytes(path, GptIni.MaxBytes, GptIni.CreateNew, bytes => GptIni.Parse(bytes), e => e is GptIniFormatException);

    private static Loaded<T> LoadBytes<T>(string path, int maxBytes, Func<T> whenAbsent, Func<byte[], T> parse, Func<Exception, bool> isFormatFailure)
        where T : class
    {
        if (!TryOpen(path, out var stream, out var exists, out var failure))
        {
            return new Loaded<T>(null, failure, exists);
        }

        if (stream is null)
        {
            return new Loaded<T>(whenAbsent(), null, false);
        }

        using (stream)
        {
            try
            {
                var buffer = new byte[(int)Math.Min(stream.Length, maxBytes + 1L)];
                stream.ReadExactly(buffer);
                return new Loaded<T>(parse(buffer), null, true);
            }
            catch (Exception e) when (isFormatFailure(e))
            {
                return new Loaded<T>(null, e, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new Loaded<T>(null, GpoStoreException.FromIo("Read", path, e), true);
            }
        }
    }

    /// <summary>Opens a file for a tolerant read. A missing file gives a null stream; an error gives a failure.</summary>
    private static bool TryOpen(string path, out FileStream? stream, out bool exists, out Exception? failure)
    {
        stream = null;
        failure = null;
        exists = false;
        if (Directory.Exists(path))
        {
            exists = true;
            failure = new GpoStoreException(GpoStoreFailure.Io, "Read", path, 5, $"'{path}' is a folder where a file is expected.");
            return false;
        }

        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            exists = true;
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            exists = true;
            failure = GpoStoreException.FromIo("Read", path, e);
            return false;
        }
    }

    private sealed record Loaded<T>(T? Document, Exception? Failure, bool Exists)
        where T : class;
}
