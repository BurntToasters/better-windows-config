using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Bwc.UI.Tree;

namespace Bwc.Regedit.Controllers;

/// <summary>Find, Find Next, and the persisted find state.</summary>
internal sealed class FindController
{
    private readonly RegeditContext _context;
    private FindDialog? _findDialog;
    private CancellationTokenSource? _silentFind;
    private RegistryFindQuery? _lastFindQuery;
    private RegistryFindHit? _lastFindHit;
    private bool _findExhausted;

    public FindController(RegeditContext context)
    {
        _context = context;
    }

    private string Get(string key) => _context.Messages.Get(key);

    public void Find() => _context.StartCommand(ShowFindDialogAsync);

    public void FindNext()
    {
        if (_lastFindQuery is null)
        {
            Find();
            return;
        }

        if (_findExhausted)
        {
            _context.StartCommand(() => _context.ShowMessageAsync(Get("DialogFindFinished")));
            return;
        }

        _silentFind?.Cancel();
        _silentFind?.Dispose();
        var tokenSource = new CancellationTokenSource();
        _silentFind = tokenSource;
        RegistryFindQuery query = _lastFindQuery;
        CancellationToken token = tokenSource.Token;
        _context.StartCommand(async () =>
        {
            try
            {
                await RunFindAsync(null, ToOptions(query), token);
            }
            finally
            {
                if (ReferenceEquals(_silentFind, tokenSource))
                {
                    _silentFind = null;
                }

                tokenSource.Dispose();
            }
        });
    }

    /// <summary>
    /// Resolves the find roots. The E2E build can narrow Find to the sandbox key; ship builds always
    /// search the whole registry.
    /// </summary>
    public static IReadOnlyList<RegistryPath>? ResolveFindRoots()
    {
#if BWC_E2E
        // E2E-only: the sandbox variable narrows Find to the test run key, so ship builds never read it.
        string? configured = Environment.GetEnvironmentVariable("BWC_E2E_SANDBOX_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return RegistryPath.TryParse(configured.Trim(), out RegistryPath? root) && root is not null
            ? new[] { root }
            : null;
#else
        return null;
#endif
    }

    /// <summary>Restores the last query and hit from the saved settings. Called once at startup.</summary>
    public void RestoreFindState()
    {
        _lastFindQuery = TryRestoreFindQuery();
        _lastFindHit = _lastFindQuery is null ? null : TryRestoreFindHit();
        _findExhausted = false;
    }

    private async Task ShowFindDialogAsync()
    {
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Find because another modal is open or the window handle is not attached.");
            return;
        }

        var dialog = new FindDialog(CreateFindLabels(), OptionsFromSettings());
        dialog.FindRequested += OnFindRequested;
        _findDialog = dialog;

        try
        {
            _ = await _context.ShowDialogAsync(dialog);
        }
        finally
        {
            dialog.FindRequested -= OnFindRequested;
            FindDialogOptions options = dialog.Options;
            _context.Settings.FindText = options.SearchText;
            _context.Settings.FindFlags = ToFlags(options);

            if (ReferenceEquals(_findDialog, dialog))
            {
                _findDialog = null;
            }
        }
    }

    private FindDialogLabels CreateFindLabels() => new()
    {
        Title = Get("DialogFindTitle"),
        FindWhat = Get("DialogFindWhat"),
        LookAt = Get("DialogFindLookAt"),
        Keys = Get("DialogFindKeys"),
        Values = Get("DialogFindValues"),
        Data = Get("DialogFindData"),
        MatchWholeString = Get("DialogFindMatchWhole"),
        SearchInSubtree = Get("DialogFindSubtree"),
        FindNext = Get("DialogFindNext"),
        Cancel = Get("DialogFindCancel"),
        Searching = Get("DialogFindSearching"),
        FoundFormat = Get("DialogFindFound"),
        Finished = Get("DialogFindFinished"),
        EmptyText = Get("DialogFindEmptyText"),
        NoScope = Get("DialogFindNoScope"),
    };

    private void OnFindRequested(object? sender, FindDialogRequestEventArgs e)
    {
        if (sender is FindDialog dialog)
        {
            _ = RunFindAsync(dialog, e.Options, e.CancellationToken);
        }
    }

    /// <summary>
    /// Runs one request. The engine call is on the thread pool. A result is applied only while its
    /// token is current, so Cancel or a newer request cannot be overwritten by a late hit.
    /// </summary>
    private async Task RunFindAsync(
        FindDialog? dialog,
        FindDialogOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryBuildQuery(options, out RegistryFindQuery? query) || query is null)
        {
            dialog?.ShowSearchError(Get("DialogFindNoScope"));
            return;
        }

        _context.Settings.FindText = options.SearchText;
        _context.Settings.FindFlags = ToFlags(options);

        bool repeatsLastQuery = _lastFindQuery is not null && SameQuery(_lastFindQuery, query);
        RegistryFindCursor? cursor;

        if (repeatsLastQuery && _lastFindHit is not null)
        {
            cursor = RegistryFindCursor.FromHit(_lastFindHit);
        }
        else if (repeatsLastQuery && _findExhausted)
        {
            if (dialog is not null)
            {
                dialog.ShowFinished();
            }
            else
            {
                await _context.ShowMessageAsync(Get("DialogFindFinished"));
            }

            return;
        }
        else
        {
            cursor = CreateSelectionCursor();
        }

        RegistryFindHit? hit;
        try
        {
            hit = await _context.RunRegistryAsync(
                () => _context.Finder.Find(query, cursor, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("Registry Editor find failed: " + ex.Message);
            if (dialog is not null)
            {
                dialog.ShowSearchError(Get("DialogFindSearchFailed"));
            }
            else
            {
                await _context.ShowMessageAsync(Get("DialogFindSearchFailed"));
            }

            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _lastFindQuery = query;
        _findExhausted = hit is null;

        if (hit is not null)
        {
            _lastFindHit = hit;
        }

        PersistFindHit(query, hit);

        if (hit is null)
        {
            if (dialog is not null)
            {
                dialog.ShowFinished();
            }
            else
            {
                await _context.ShowMessageAsync(Get("DialogFindFinished"));
            }

            return;
        }

        bool applied = await ApplyFindHitAsync(hit, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (dialog is not null)
        {
            if (applied)
            {
                dialog.CompleteSearch(hit.DisplayText);
            }
            else
            {
                dialog.ShowFinished();
            }
        }
    }

    /// <summary>
    /// Resolves a hit key through the tree source, rebuilds the visible rows with the hit
    /// ancestors expanded, selects the key, and selects the matching value row when there is one.
    /// Focus is deliberately left alone so a modal Find dialog keeps keyboard focus.
    /// </summary>
    private async Task<bool> ApplyFindHitAsync(RegistryFindHit hit, CancellationToken cancellationToken)
    {
        string displayPath = hit.KeyPath.ToDisplayString();
        TreeNode? node;

        try
        {
            node = await _context.TreeSource.FindPathAsync(displayPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Registry Editor could not resolve the find hit '{displayPath}': {ex.Message}");
            node = null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (node is null)
        {
            Log.Warn(
                $"Registry Editor dropped the find hit '{displayPath}' because the key no longer resolves.");
            return false;
        }

        _context.ApplyingNavigation = true;
        try
        {
            _context.Shell.TreeContent.SetRoots(_context.TreeSource.Roots);
            _context.Shell.TreeContent.SelectNode(node);
            _context.Shell.CurrentPath = node.RegistryPath ?? displayPath;
        }
        finally
        {
            _context.ApplyingNavigation = false;
        }

        if (hit.ValueName is not null)
        {
            await _context.LoadValuesAsync(node, RegistryValueDisplay.FormatName(hit.ValueName));
        }

        return true;
    }

    private RegistryFindCursor? CreateSelectionCursor()
    {
        if (!_context.TryGetSelectedKeyPath(out RegistryPath key))
        {
            return null;
        }

        IReadOnlyList<DetailsRow> rows = _context.Shell.ListContent.SelectedRows;
        if (rows.Count > 0 && _context.TryGetRawValueName(rows[0], out string rawName))
        {
            return RegistryFindCursor.FromValueSelection(key, rawName);
        }

        return RegistryFindCursor.FromKeySelection(key);
    }

    private bool TryBuildQuery(FindDialogOptions options, out RegistryFindQuery? query)
    {
        query = null;
        if (string.IsNullOrEmpty(options.SearchText) ||
            (!options.LookAtKeys && !options.LookAtValues && !options.LookAtData))
        {
            return false;
        }

        try
        {
            query = new RegistryFindQuery(
                options.SearchText,
                options.LookAtKeys,
                options.LookAtValues,
                options.LookAtData,
                options.MatchWholeStringOnly,
                options.SearchInSubtree,
                _context.FindRoots);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private FindDialogOptions OptionsFromSettings()
    {
        int flags = _context.Settings.FindFlags;
        return new FindDialogOptions
        {
            SearchText = _context.Settings.FindText ?? string.Empty,
            LookAtKeys = HasFindFlag(flags, RegeditFindFlags.Keys),
            LookAtValues = HasFindFlag(flags, RegeditFindFlags.Values),
            LookAtData = HasFindFlag(flags, RegeditFindFlags.Data),
            MatchWholeStringOnly = HasFindFlag(flags, RegeditFindFlags.MatchWholeString),
            SearchInSubtree = HasFindFlag(flags, RegeditFindFlags.SearchInSubtree),
        };
    }

    private static FindDialogOptions ToOptions(RegistryFindQuery query) => new()
    {
        SearchText = query.SearchText,
        LookAtKeys = query.LookAtKeys,
        LookAtValues = query.LookAtValues,
        LookAtData = query.LookAtData,
        MatchWholeStringOnly = query.MatchWholeStringOnly,
        SearchInSubtree = query.SearchInSubtree,
    };

    private static int ToFlags(FindDialogOptions options)
    {
        RegeditFindFlags flags = RegeditFindFlags.None;

        if (options.LookAtKeys)
        {
            flags |= RegeditFindFlags.Keys;
        }

        if (options.LookAtValues)
        {
            flags |= RegeditFindFlags.Values;
        }

        if (options.LookAtData)
        {
            flags |= RegeditFindFlags.Data;
        }

        if (options.MatchWholeStringOnly)
        {
            flags |= RegeditFindFlags.MatchWholeString;
        }

        if (options.SearchInSubtree)
        {
            flags |= RegeditFindFlags.SearchInSubtree;
        }

        return (int)flags;
    }

    private static int QueryFlags(RegistryFindQuery query)
    {
        RegeditFindFlags flags = RegeditFindFlags.None;

        if (query.LookAtKeys)
        {
            flags |= RegeditFindFlags.Keys;
        }

        if (query.LookAtValues)
        {
            flags |= RegeditFindFlags.Values;
        }

        if (query.LookAtData)
        {
            flags |= RegeditFindFlags.Data;
        }

        if (query.MatchWholeStringOnly)
        {
            flags |= RegeditFindFlags.MatchWholeString;
        }

        if (query.SearchInSubtree)
        {
            flags |= RegeditFindFlags.SearchInSubtree;
        }

        return (int)flags;
    }

    private static bool HasFindFlag(int flags, RegeditFindFlags flag) =>
        ((RegeditFindFlags)flags & flag) == flag;

    private static bool SameQuery(RegistryFindQuery left, RegistryFindQuery right) =>
        string.Equals(left.SearchText, right.SearchText, StringComparison.Ordinal) &&
        left.LookAtKeys == right.LookAtKeys &&
        left.LookAtValues == right.LookAtValues &&
        left.LookAtData == right.LookAtData &&
        left.MatchWholeStringOnly == right.MatchWholeStringOnly &&
        left.SearchInSubtree == right.SearchInSubtree;

    private RegistryFindQuery? TryRestoreFindQuery()
    {
        RegeditSettings settings = _context.Settings;
        if (string.IsNullOrEmpty(settings.FindQueryText))
        {
            return null;
        }

        var options = new FindDialogOptions
        {
            SearchText = settings.FindQueryText,
            LookAtKeys = HasFindFlag(settings.FindQueryFlags, RegeditFindFlags.Keys),
            LookAtValues = HasFindFlag(settings.FindQueryFlags, RegeditFindFlags.Values),
            LookAtData = HasFindFlag(settings.FindQueryFlags, RegeditFindFlags.Data),
            MatchWholeStringOnly = HasFindFlag(
                settings.FindQueryFlags,
                RegeditFindFlags.MatchWholeString),
            SearchInSubtree = HasFindFlag(
                settings.FindQueryFlags,
                RegeditFindFlags.SearchInSubtree),
        };

        return TryBuildQuery(options, out RegistryFindQuery? query) ? query : null;
    }

    private RegistryFindHit? TryRestoreFindHit()
    {
        RegeditSettings settings = _context.Settings;
        if (settings.FindLastHitKind is < 0 or > 2 ||
            string.IsNullOrWhiteSpace(settings.FindLastHitKey) ||
            !RegistryPath.TryParse(settings.FindLastHitKey, out RegistryPath? path) ||
            path is null)
        {
            return null;
        }

        var kind = (RegistryFindHitKind)settings.FindLastHitKind;
        string? valueName = kind == RegistryFindHitKind.KeyName
            ? null
            : settings.FindLastHitValue ?? string.Empty;
        string matchedText = settings.FindLastHitText ?? valueName ?? path.ToDisplayString();

        try
        {
            return new RegistryFindHit(path, valueName, kind, matchedText);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void PersistFindHit(RegistryFindQuery query, RegistryFindHit? hit)
    {
        RegeditSettings settings = _context.Settings;
        settings.FindQueryText = query.SearchText;
        settings.FindQueryFlags = QueryFlags(query);

        if (hit is null)
        {
            settings.FindLastHitKey = null;
            settings.FindLastHitValue = null;
            settings.FindLastHitKind = -1;
            settings.FindLastHitText = null;
            return;
        }

        settings.FindLastHitKey = hit.KeyPath.ToDisplayString();
        settings.FindLastHitValue = hit.ValueName;
        settings.FindLastHitKind = (int)hit.Kind;
        settings.FindLastHitText = hit.MatchedText;
    }
}
