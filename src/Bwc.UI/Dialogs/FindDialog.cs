using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;

namespace Bwc.UI.Dialogs;

/// <summary>The Find dialog checkbox state and search text. Immutable once a request starts.</summary>
public sealed class FindDialogOptions
{
    public string SearchText { get; init; } = string.Empty;

    public bool LookAtKeys { get; init; } = true;

    public bool LookAtValues { get; init; } = true;

    public bool LookAtData { get; init; } = true;

    public bool MatchWholeStringOnly { get; init; }

    public bool SearchInSubtree { get; init; } = true;
}

/// <summary>
/// Strings supplied by the caller. Bwc.UI does not own the Registry Editor resource map, so every
/// visible string, including the window title, comes through this object.
/// </summary>
public sealed class FindDialogLabels
{
    public required string Title { get; init; }

    public required string FindWhat { get; init; }

    public required string LookAt { get; init; }

    public required string Keys { get; init; }

    public required string Values { get; init; }

    public required string Data { get; init; }

    public required string MatchWholeString { get; init; }

    public required string SearchInSubtree { get; init; }

    public required string FindNext { get; init; }

    public required string Cancel { get; init; }

    public required string Searching { get; init; }

    /// <summary>A composite format string with one argument, the hit display text.</summary>
    public required string FoundFormat { get; init; }

    public required string Finished { get; init; }

    public required string EmptyText { get; init; }

    public required string NoScope { get; init; }
}

/// <summary>One Find Next request. The host must observe the token and complete the dialog.</summary>
public sealed class FindDialogRequestEventArgs : EventArgs
{
    internal FindDialogRequestEventArgs(FindDialogOptions options, CancellationToken cancellationToken)
    {
        Options = options;
        CancellationToken = cancellationToken;
    }

    public FindDialogOptions Options { get; }

    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Classic Find dialog (RT_DIALOG 106, oracle DLG-010 to DLG-012). Find Next stays open and raises
/// <see cref="FindRequested"/>; the host runs the engine on a background thread and calls one of
/// the completion methods. Cancel closes and cancels the request token. The template has no status
/// line, so a finished search or an error is shown in a classic message box. A hit is not announced.
/// </summary>
public sealed class FindDialog : ClassicDialog
{
    private readonly FindDialogLabels _labels;
    private readonly TextBox _findText;
    private readonly CheckBox _keys;
    private readonly CheckBox _values;
    private readonly CheckBox _data;
    private readonly CheckBox _wholeString;
    private readonly CheckBox _subtree;
    private readonly Button _primaryButton;
    private CancellationTokenSource? _searchCts;
    private bool _searching;
    private bool _closed;

    public FindDialog(FindDialogLabels labels, FindDialogOptions initial)
        : base(ClassicDialogTemplates.Find(
            labels?.Title ?? throw new ArgumentNullException(nameof(labels)),
            labels.FindWhat,
            labels.LookAt,
            labels.Keys,
            labels.Values,
            labels.Data,
            labels.MatchWholeString,
            labels.SearchInSubtree,
            labels.FindNext,
            labels.Cancel,
            initial?.SearchInSubtree ?? throw new ArgumentNullException(nameof(initial))))
    {
        _labels = labels;

        _findText = RequireControl<TextBox>("FindText");
        _findText.Text = initial.SearchText;
        _keys = RequireControl<CheckBox>("FindKeys");
        _values = RequireControl<CheckBox>("FindValues");
        _data = RequireControl<CheckBox>("FindData");
        _wholeString = RequireControl<CheckBox>("FindWholeString");
        _subtree = RequireControl<CheckBox>("FindSubtree");
        _keys.IsChecked = initial.LookAtKeys;
        _values.IsChecked = initial.LookAtValues;
        _data.IsChecked = initial.LookAtData;
        _wholeString.IsChecked = initial.MatchWholeStringOnly;
        _subtree.IsChecked = initial.SearchInSubtree;
        _primaryButton = RequireControl<Button>("DialogPrimary");

        Closed += OnFindDialogClosed;
        Activated += OnFindDialogActivated;
        ((FrameworkElement)Content).AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnFindKeyDown),
            handledEventsToo: true);
    }

    /// <summary>Raised on the UI thread when Find Next starts. One request is in flight at a time.</summary>
    public event EventHandler<FindDialogRequestEventArgs>? FindRequested;

    /// <summary>The current control values, used to persist an unsubmitted edit on close.</summary>
    public FindDialogOptions Options => new()
    {
        SearchText = _findText.Text ?? string.Empty,
        LookAtKeys = _keys.IsChecked == true,
        LookAtValues = _values.IsChecked == true,
        LookAtData = _data.IsChecked == true,
        MatchWholeStringOnly = _wholeString.IsChecked == true,
        SearchInSubtree = _subtree.IsChecked == true,
    };

    /// <summary>True from the moment Find Next is pressed until a completion method runs.</summary>
    public bool IsSearching => _searching;

    /// <summary>Re-enables Find Next after a hit. The hit is shown in the registry tree, as in classic.</summary>
    public void CompleteSearch(string foundDisplayText)
    {
        ArgumentNullException.ThrowIfNull(foundDisplayText);
        if (!_searching)
        {
            return;
        }

        _searching = false;
        SetPrimaryEnabled(true);
    }

    /// <summary>Shows the classic "finished" message and re-enables Find Next.</summary>
    public void ShowFinished()
    {
        if (!_searching)
        {
            return;
        }

        _searching = false;
        SetPrimaryEnabled(true);
        ShowMessage(_labels.Finished, ClassicMessageIcon.Information);
    }

    /// <summary>Shows an engine or navigation failure and re-enables Find Next.</summary>
    public void ShowSearchError(string message)
    {
        if (!_searching)
        {
            return;
        }

        _searching = false;
        SetPrimaryEnabled(true);
        ShowMessage(message ?? string.Empty, ClassicMessageIcon.Error);
    }

    /// <summary>Find Next starts the search and keeps the dialog open. Cancel closes it.</summary>
    protected override bool TryAccept()
    {
        StartFind();
        return false;
    }

    protected override void OnCommand(int id, string automationId)
    {
        if (id == 1)
        {
            StartFind();
        }
        else if (id == 2)
        {
            Cancel();
        }
    }

    protected override void OnDialogLoaded()
    {
        _ = _findText.Focus(FocusState.Programmatic);
        _findText.SelectAll();
    }

    private void StartFind()
    {
        if (_searching)
        {
            return;
        }

        string text = _findText.Text ?? string.Empty;
        if (text.Length == 0)
        {
            ShowMessage(_labels.EmptyText, ClassicMessageIcon.Error, refocus: _findText);
            return;
        }

        FindDialogOptions options = Options;
        if (!options.LookAtKeys && !options.LookAtValues && !options.LookAtData)
        {
            ShowMessage(_labels.NoScope, ClassicMessageIcon.Error, refocus: _findText);
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        _searching = true;
        SetPrimaryEnabled(false);

        EventHandler<FindDialogRequestEventArgs>? handler = FindRequested;
        if (handler is null)
        {
            _searching = false;
            SetPrimaryEnabled(true);
            return;
        }

        handler(
            this,
            new FindDialogRequestEventArgs(options, _searchCts.Token));
    }

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        // Escape and Enter are routed by ClassicDialog. F3 repeats Find Next, as the classic dialog does.
        if (e.Key == VirtualKey.F3)
        {
            e.Handled = true;
            StartFind();
        }
    }

    private void OnFindDialogActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _ = _findText.Focus(FocusState.Programmatic);
        }
    }

    private void OnFindDialogClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Closed -= OnFindDialogClosed;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _searching = false;
    }

    private void SetPrimaryEnabled(bool isEnabled)
    {
        _primaryButton.IsEnabled = isEnabled;
    }

    /// <summary>Shows a message owned by this dialog. The dialog stays disabled until the message closes.</summary>
    private void ShowMessage(string text, ClassicMessageIcon icon, FrameworkElement? refocus = null)
    {
        _ = ShowMessageAsync(text, icon, refocus);
    }

    private async Task ShowMessageAsync(string text, ClassicMessageIcon icon, FrameworkElement? refocus)
    {
        var box = new ClassicMessageBox(_labels.Title, text, icon, ClassicMessageButtons.Ok);
        _ = await box.ShowAsync(WindowNative.GetWindowHandle(this));
        if (refocus is not null)
        {
            _ = refocus.Focus(FocusState.Programmatic);
        }
    }
}
