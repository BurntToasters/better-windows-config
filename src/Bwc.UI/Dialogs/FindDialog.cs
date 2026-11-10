using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

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
/// Classic Find dialog (oracle DLG-010 to DLG-012). Find Next stays open and raises
/// <see cref="FindRequested"/>; the host runs the engine on a background thread and calls one of
/// the completion methods. Cancel closes and cancels the request token.
/// </summary>
public sealed class FindDialog : ModalDialogWindow
{
    private readonly FindDialogLabels _labels;
    private readonly TextBox _findText;
    private readonly CheckBox _keys;
    private readonly CheckBox _values;
    private readonly CheckBox _data;
    private readonly CheckBox _wholeString;
    private readonly CheckBox _subtree;
    private readonly TextBlock _status;
    private CancellationTokenSource? _searchCts;
    private Button? _primaryButton;
    private bool _searching;
    private bool _closed;

    public FindDialog(FindDialogLabels labels, FindDialogOptions initial)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(initial);

        _labels = labels;
        Title = labels.Title;
        SetHeading(labels.Title);
        PrimaryText = labels.FindNext;
        SecondaryText = labels.Cancel;
        SetPreferredSize(440, 330);

        _findText = new TextBox
        {
            Text = initial.SearchText,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            MinHeight = 0,
            Padding = new Thickness(6, 3, 6, 3),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        AutomationProperties.SetAutomationId(_findText, "FindText");
        AutomationProperties.SetName(_findText, labels.FindWhat);

        _keys = CreateCheckBox("FindKeys", labels.Keys, initial.LookAtKeys);
        _values = CreateCheckBox("FindValues", labels.Values, initial.LookAtValues);
        _data = CreateCheckBox("FindData", labels.Data, initial.LookAtData);
        _wholeString = CreateCheckBox(
            "FindWholeString",
            labels.MatchWholeString,
            initial.MatchWholeStringOnly);
        _subtree = CreateCheckBox("FindSubtree", labels.SearchInSubtree, initial.SearchInSubtree);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Text = string.Empty,
        };

        AutomationProperties.SetAutomationId(_status, "FindStatus");

        var lookAt = new StackPanel { Spacing = 2 };
        lookAt.Children.Add(new TextBlock
        {
            Text = labels.LookAt,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        });
        lookAt.Children.Add(_keys);
        lookAt.Children.Add(_values);
        lookAt.Children.Add(_data);

        var body = new Grid { RowSpacing = 6, ColumnSpacing = 8 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var findWhat = new TextBlock
        {
            Text = labels.FindWhat,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        Grid.SetRow(findWhat, 0);
        Grid.SetColumn(findWhat, 0);
        Grid.SetRow(_findText, 0);
        Grid.SetColumn(_findText, 1);
        Grid.SetRow(lookAt, 1);
        Grid.SetColumn(lookAt, 1);
        Grid.SetRow(_wholeString, 2);
        Grid.SetColumnSpan(_wholeString, 2);
        Grid.SetRow(_subtree, 3);
        Grid.SetColumnSpan(_subtree, 2);
        Grid.SetRow(_status, 4);
        Grid.SetColumnSpan(_status, 2);

        body.Children.Add(findWhat);
        body.Children.Add(_findText);
        body.Children.Add(lookAt);
        body.Children.Add(_wholeString);
        body.Children.Add(_subtree);
        body.Children.Add(_status);

        SetBody(body);

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

    /// <summary>Shows Found: text and re-enables Find Next.</summary>
    public void CompleteSearch(string foundDisplayText)
    {
        ArgumentNullException.ThrowIfNull(foundDisplayText);
        if (!_searching)
        {
            return;
        }

        _searching = false;
        SetPrimaryEnabled(true);
        SetStatus(string.Format(
            CultureInfo.CurrentCulture,
            _labels.FoundFormat,
            foundDisplayText));
    }

    /// <summary>Shows the oracle finished message and re-enables Find Next.</summary>
    public void ShowFinished()
    {
        if (!_searching)
        {
            return;
        }

        _searching = false;
        SetPrimaryEnabled(true);
        SetStatus(_labels.Finished);
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
        SetStatus(message ?? string.Empty);
    }

    protected override bool TryAccept()
    {
        StartFind();
        return false;
    }

    protected override void OnDialogLoaded()
    {
        _primaryButton = FindButton("DialogPrimary");
        _findText.Focus(FocusState.Programmatic);
        _findText.SelectAll();
    }

    private static CheckBox CreateCheckBox(string automationId, string text, bool isChecked)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            MinHeight = 0,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        AutomationProperties.SetAutomationId(checkBox, automationId);
        AutomationProperties.SetName(checkBox, text);
        return checkBox;
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
            ShowValidation(_labels.EmptyText);
            _ = _findText.Focus(FocusState.Programmatic);
            return;
        }

        FindDialogOptions options = Options;
        if (!options.LookAtKeys && !options.LookAtValues && !options.LookAtData)
        {
            ShowValidation(_labels.NoScope);
            return;
        }

        ShowValidation(null);

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        _searching = true;
        SetPrimaryEnabled(false);
        SetStatus(_labels.Searching);

        EventHandler<FindDialogRequestEventArgs>? handler = FindRequested;
        if (handler is null)
        {
            _searching = false;
            SetPrimaryEnabled(true);
            SetStatus(string.Empty);
            return;
        }

        handler(
            this,
            new FindDialogRequestEventArgs(options, _searchCts.Token));
    }

    private void OnFindKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Cancel();
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                StartFind();
                break;
            case VirtualKey.F3:
                e.Handled = true;
                StartFind();
                break;
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
        if (_primaryButton is not null)
        {
            _primaryButton.IsEnabled = isEnabled;
        }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
    }

    private Button? FindButton(string automationId)
    {
        if (Content is not DependencyObject root)
        {
            return null;
        }

        return FindDescendant<Button>(
            root,
            button => string.Equals(
                AutomationProperties.GetAutomationId(button),
                automationId,
                StringComparison.Ordinal));
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index += 1)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && predicate(typed))
            {
                return typed;
            }

            T? nested = FindDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
