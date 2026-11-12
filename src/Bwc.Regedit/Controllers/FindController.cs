using Bwc.Core;
using Bwc.Regedit.Registry;
using Bwc.Registry;
using Bwc.UI.Dialogs;
using Bwc.UI.List;
using Bwc.UI.Tree;
using Microsoft.UI.Xaml;

namespace Bwc.Regedit.Controllers;

/// <summary>Find, Find Next, and the persisted find state.</summary>
internal sealed class FindController
{
#if BWC_E2E
    // E2E-only: a search over a tiny sandbox finishes before UIA can see the progress dialog. Keeping
    // the classic two-window sequence visible for a moment makes the sequence observable.
    private static readonly TimeSpan ProgressMinimum =
        TimeSpan.FromMilliseconds(2000);
#else
    private static readonly TimeSpan ProgressMinimum =
        TimeSpan.FromMilliseconds(250);
#endif

    /// <summary>
    /// The progress line from RT_DIALOG 109. The shared resource map has no key for it, and the
    /// resource map is outside this package.
    /// </summary>
    private const string SearchingRegistryText = "Searching the registry...";

    private readonly RegeditContext _context;
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
        if (_context.DialogOpen || _context.WindowHwnd == 0)
        {
            Log.Warn(
                "Registry Editor ignored Find Next because another modal is open or the window handle is not attached.");
            return;
        }

        if (_lastFindQuery is null)
        {
            Find();
            return;
        }

        if (_findExhausted)
        {
            _context.StartCommand(ShowFinishedMessageAsync);
            return;
        }

        RegistryFindQuery query = _lastFindQuery;
        _context.StartCommand(() => RunFindAsync(ToOptions(query)));
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
        bool accepted;
        try
        {
            accepted = await _context.ShowDialogAsync(dialog);
        }
        finally
        {
            FindDialogOptions options = dialog.Options;
            _context.Settings.FindText = options.SearchText;
            _context.Settings.FindFlags = ToFlags(options);
        }

        if (accepted)
        {
            await RunFindAsync(dialog.AcceptedOptions);
        }
    }

    private FindDialogLabels CreateFindLabels() => new()
    {
        Title = Get("DialogFindTitle"),
        FindWhat = Mnemonic(Get("DialogFindWhat"), "nd"),
        LookAt = Get("DialogFindLookAt"),
        Keys = Mnemonic(Get("DialogFindKeys"), "Keys"),
        Values = Mnemonic(Get("DialogFindValues"), "Values"),
        Data = Mnemonic(Get("DialogFindData"), "Data"),
        MatchWholeString = Mnemonic(Get("DialogFindMatchWhole"), "whole"),
        SearchInSubtree = Mnemonic(Get("DialogFindSubtree"), "subtree"),
        FindNext = Mnemonic(Get("DialogFindNext"), "Find Next"),
        Cancel = Get("DialogFindCancel"),
        Searching = SearchingRegistryText,
        Finished = Get("DialogFindFinished"),
        EmptyText = Get("DialogFindEmptyText"),
        NoScope = Get("DialogFindNoScope"),
    };

    /// <summary>
    /// Marks the template mnemonic letter. The resource strings carry no ampersands, and the shared
    /// resource map is outside this package. A marker is used when the letter is not the first one.
    /// </summary>
    private static string Mnemonic(string text, string marker)
    {
        if (text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? text : text.Insert(index, "&");
    }

    /// <summary>
    /// Runs one find request. The engine call runs on the thread pool while the classic progress
    /// dialog is open. The dialog closes before the hit is applied, so Find Next never reopens Find.
    /// </summary>
    private async Task RunFindAsync(FindDialogOptions options)
    {
        if (!TryBuildQuery(options, out RegistryFindQuery? query) || query is null)
        {
            await ShowFindErrorAsync(Get("DialogFindNoScope"));
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
            await ShowFinishedMessageAsync();
            return;
        }
        else
        {
            cursor = CreateSelectionCursor();
        }

        FindProgressResult result = await FindWithProgressAsync(query, cursor);

        if (result.Error is not null)
        {
            Log.Warn("Registry Editor find failed: " + result.Error.Message);
            await ShowFindErrorAsync(Get("DialogFindSearchFailed"));
            return;
        }

        if (result.Cancelled)
        {
            return;
        }

        RegistryFindHit? hit = result.Hit;
        _lastFindQuery = query;
        _findExhausted = hit is null;
        _lastFindHit = hit;
        PersistFindHit(query, hit);

        if (hit is null)
        {
            await ShowFinishedMessageAsync();
            return;
        }

        bool applied = await ApplyFindHitAsync(hit, CancellationToken.None);
        if (!applied)
        {
            await ShowFinishedMessageAsync();
        }
    }

    /// <summary>
    /// Shows the classic progress dialog and runs the engine behind it. Cancel, Escape, Enter on the
    /// default Cancel button, or the close box cancels the token. The dialog always closes before the
    /// result is returned, so the owner is restored for the message box or selection update.
    /// </summary>
    private async Task<FindProgressResult> FindWithProgressAsync(
        RegistryFindQuery query,
        RegistryFindCursor? cursor)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new FindProgressDialog(
            Get("DialogFindTitle"),
            SearchingRegistryText,
            Get("DialogFindCancel"),
            cancellation.Cancel);

        Task<bool> dialogTask = _context.ShowDialogAsync(progress);
        Task<RegistryFindHit?> searchTask = _context.RunRegistryAsync(
            () => _context.Finder.Find(query, cursor, cancellation.Token),
            cancellation.Token);

        RegistryFindHit? hit = null;
        bool completed = false;
        bool cancelled = false;
        Exception? error = null;

        try
        {
            // The delay is a floor, not a timeout: the engine result and the minimum visibility both
            // have to finish before the dialog closes.
            await Task.WhenAll(searchTask, Task.Delay(ProgressMinimum));
            hit = await searchTask;
            completed = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (completed && !progress.Cancelled)
            {
                progress.CompleteFromHost();
            }
            else
            {
                cancellation.Cancel();
                CloseQuietly(progress);
            }
        }

        _ = await dialogTask;
        cancelled = cancelled || progress.Cancelled || !completed;
        return new FindProgressResult(hit, cancelled, error);
    }

    /// <summary>
    /// Resolves a hit key through the tree source, rebuilds the visible rows with the hit
    /// ancestors expanded, and selects the key. The matching value row is selected when the hit
    /// names a value. Focus is left alone so the window that owns the restored selection keeps it.
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

    private Task ShowFinishedMessageAsync() =>
        _context.ShowDialogAsync(new ClassicMessageBox(
            Get("DialogFindTitle"),
            Get("DialogFindFinished"),
            ClassicMessageIcon.Information,
            ClassicMessageButtons.Ok));

    private Task ShowFindErrorAsync(string message) =>
        _context.ShowDialogAsync(new ClassicMessageBox(
            Get("DialogFindTitle"),
            message,
            ClassicMessageIcon.Error,
            ClassicMessageButtons.Ok));

    private static void CloseQuietly(Window window)
    {
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // The dialog was already closed or never shown. There is nothing else to release.
        }
    }

    private readonly record struct FindProgressResult(
        RegistryFindHit? Hit,
        bool Cancelled,
        Exception? Error);
}
