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

    /// <summary>The progress dialog line, "Searching the registry...".</summary>
    public required string Searching { get; init; }

    public required string Finished { get; init; }

    public required string EmptyText { get; init; }

    public required string NoScope { get; init; }
}

/// <summary>
/// Classic Find dialog (RT_DIALOG 106, oracle DLG-010 to DLG-012). Find Next validates the text and
/// the look-at flags, accepts, and closes. The host then shows the classic progress dialog and runs
/// the search on a worker thread, as regedit does. Cancel closes without searching. The template
/// has no status line, so a finished search or an error is shown in a classic message box.
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
    private FindDialogOptions? _acceptedOptions;

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

        Activated += OnFindDialogActivated;
        ((FrameworkElement)Content).AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnFindKeyDown),
            handledEventsToo: true);
    }

    /// <summary>The current control values. Read after the dialog closes to persist the draft.</summary>
    public FindDialogOptions Options => new()
    {
        SearchText = _findText.Text ?? string.Empty,
        LookAtKeys = _keys.IsChecked == true,
        LookAtValues = _values.IsChecked == true,
        LookAtData = _data.IsChecked == true,
        MatchWholeStringOnly = _wholeString.IsChecked == true,
        SearchInSubtree = _subtree.IsChecked == true,
    };

    /// <summary>The options accepted by Find Next, or the current controls when it never accepted.</summary>
    public FindDialogOptions AcceptedOptions => _acceptedOptions ?? Options;

    /// <summary>Find Next validates and closes the dialog. Cancel closes it without a search.</summary>
    protected override bool TryAccept()
    {
        string text = _findText.Text ?? string.Empty;
        if (text.Length == 0)
        {
            ShowMessage(_labels.EmptyText, ClassicMessageIcon.Error, refocus: _findText);
            return false;
        }

        FindDialogOptions options = Options;
        if (!options.LookAtKeys && !options.LookAtValues && !options.LookAtData)
        {
            ShowMessage(_labels.NoScope, ClassicMessageIcon.Error, refocus: _findText);
            return false;
        }

        _acceptedOptions = options;
        return true;
    }

    protected override void OnDialogLoaded()
    {
        _ = _findText.Focus(FocusState.Programmatic);
        _findText.SelectAll();
    }

    private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter and Escape are routed by ClassicDialog. F3 inside Find starts Find Next, as classic does.
        if (e.Key == VirtualKey.F3)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void OnFindDialogActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _ = _findText.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Shows a validation message owned by this dialog. The dialog stays disabled until the message closes.</summary>
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
