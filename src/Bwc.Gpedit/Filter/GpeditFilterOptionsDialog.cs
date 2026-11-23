using Bwc.Core;
using Bwc.Policy.Admx;
using Bwc.Policy.Filter;
using Bwc.UI;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinRT.Interop;

namespace Bwc.Gpedit.Filter;

/// <summary>
/// The Filter Options dialog of the Local Group Policy Editor: three Any, Yes and No boxes (Managed, Configured,
/// Commented), a keyword filter with the words, how to match them and where to look, and a requirements filter with the
/// platforms of the loaded templates. OK checks the options with <see cref="FilterOptions.Validate"/>; a refused option
/// shows a message box of its own and the dialog stays open. The dialog is a window of its own, with the automation id
/// <c>GpeditFilterOptions</c>, and its parts have the ids <c>GpeditFilterOptions.&lt;Part&gt;</c>
/// (docs/failure-modes/gpedit-filter.md, sections 1 to 3).
/// </summary>
internal sealed class GpeditFilterOptionsDialog : ClassicDialog
{
    internal const string RootId = "GpeditFilterOptions";
    internal const string MessageId = "GpeditFilterOptions.Message";

    private const string Prefix = "GpeditFilterOptions.";
    private const int StaticId = 65535;
    private const int SelectAllId = 102;
    private const int ClearAllId = 103;

    private readonly ComboBox _managed;
    private readonly ComboBox _configured;
    private readonly ComboBox _commented;
    private readonly CheckBox _keywordsEnabled;
    private readonly TextBox _keywordText;
    private readonly ComboBox _keywordMatch;
    private readonly CheckBox _inTitle;
    private readonly CheckBox _inHelp;
    private readonly CheckBox _inComment;
    private readonly CheckBox _requirementsEnabled;
    private readonly ComboBox _platformMatch;
    private readonly ListBox _platformList;
    private readonly List<FilterPlatformItem> _platforms = [];
    private readonly FrameworkElement _selectAll;
    private readonly FrameworkElement _clearAll;
    private bool _messageOpen;

    internal GpeditFilterOptionsDialog(FilterOptions initial, IReadOnlyList<FilterPlatform> platforms)
        : base(BuildTemplate())
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(platforms);

        // The window is found by this id; the base names the root of every classic dialog DialogRoot.
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, GpeditStrings.Filter("FilterDialogCaption"));
        }

        _managed = Combo("Managed", ["FilterAny", "FilterYes", "FilterNo"]);
        _configured = Combo("Configured", ["FilterAny", "FilterYes", "FilterNo"]);
        _commented = Combo("Commented", ["FilterAny", "FilterYes", "FilterNo"]);
        _keywordMatch = Combo("KeywordMatch", ["FilterAny", "FilterAll", "FilterExact"]);
        _platformMatch = Combo("PlatformMatch", ["FilterAny", "FilterAll"]);
        _keywordsEnabled = RequireControl<CheckBox>(Prefix + "KeywordsEnabled");
        _keywordText = RequireControl<TextBox>(Prefix + "KeywordText");
        _inTitle = RequireControl<CheckBox>(Prefix + "InTitle");
        _inHelp = RequireControl<CheckBox>(Prefix + "InHelp");
        _inComment = RequireControl<CheckBox>(Prefix + "InComment");
        _requirementsEnabled = RequireControl<CheckBox>(Prefix + "RequirementsEnabled");
        _platformList = RequireControl<ListBox>(Prefix + "PlatformList");
        _selectAll = RequireControl<FrameworkElement>(Prefix + "SelectAll");
        _clearAll = RequireControl<FrameworkElement>(Prefix + "ClearAll");

        var selected = new HashSet<string>(initial.Requirements.PlatformKeys ?? [], StringComparer.OrdinalIgnoreCase);
        int number = 0;
        foreach (FilterPlatform platform in platforms)
        {
            var item = new FilterPlatformItem(platform.Key, platform.DisplayName, $"{Prefix}PlatformList.Item{number++}")
            {
                Style = _platformList.ItemContainerStyle,
                Ticked = selected.Contains(platform.Key),
            };
            _platforms.Add(item);
            _platformList.Items.Add(item);
        }

        _managed.SelectedIndex = (int)initial.Managed;
        _configured.SelectedIndex = (int)initial.Configured;
        _commented.SelectedIndex = (int)initial.Commented;
        _keywordsEnabled.IsChecked = initial.Keyword.Enabled;
        _keywordText.Text = initial.Keyword.Text ?? string.Empty;
        _keywordMatch.SelectedIndex = (int)initial.Keyword.Mode;
        _inTitle.IsChecked = initial.Keyword.InTitle;
        _inHelp.IsChecked = initial.Keyword.InHelpText;
        _inComment.IsChecked = initial.Keyword.InComment;
        _requirementsEnabled.IsChecked = initial.Requirements.Enabled;
        _platformMatch.SelectedIndex = (int)initial.Requirements.Mode;

        _keywordsEnabled.Checked += OnPartToggled;
        _keywordsEnabled.Unchecked += OnPartToggled;
        _requirementsEnabled.Checked += OnPartToggled;
        _requirementsEnabled.Unchecked += OnPartToggled;
        _platformList.KeyDown += OnPlatformListKeyDown;
        UpdateEnabled();
    }

    /// <summary>The options as the dialog stands now.</summary>
    internal FilterOptions Options => new(
        (FilterTriState)Math.Max(0, _managed.SelectedIndex),
        (FilterTriState)Math.Max(0, _configured.SelectedIndex),
        (FilterTriState)Math.Max(0, _commented.SelectedIndex),
        new KeywordFilter(
            _keywordsEnabled.IsChecked == true,
            _keywordText.Text ?? string.Empty,
            (KeywordMatchMode)Math.Max(0, _keywordMatch.SelectedIndex),
            _inTitle.IsChecked == true,
            _inHelp.IsChecked == true,
            _inComment.IsChecked == true),
        new RequirementsFilter(
            _requirementsEnabled.IsChecked == true,
            _platforms.Where(p => p.Ticked).Select(p => p.Key).ToArray(),
            (PlatformMatchMode)Math.Max(0, _platformMatch.SelectedIndex)));

    /// <summary>The options that OK accepted, or null when the dialog was cancelled.</summary>
    internal FilterOptions? AcceptedOptions { get; private set; }

    /// <summary>OK: checks the options. A refused option shows its message and keeps the dialog open.</summary>
    protected override bool TryAccept()
    {
        if (_messageOpen)
        {
            return false;
        }

        FilterOptions options = Options;
        IReadOnlyList<FilterProblem> problems = options.Validate();
        if (problems.Count > 0)
        {
            ShowRefusalAsync(problems[0]).Forget("filter refusal message");
            return false;
        }

        AcceptedOptions = options;
        return true;
    }

    protected override void OnCommand(int id, string automationId)
    {
        switch (id)
        {
            case SelectAllId:
                SetAllPlatforms(true);
                break;
            case ClearAllId:
                SetAllPlatforms(false);
                break;
            default:
                base.OnCommand(id, automationId);
                break;
        }
    }

    private void SetAllPlatforms(bool ticked)
    {
        foreach (FilterPlatformItem item in _platforms)
        {
            item.Ticked = ticked;
        }
    }

    private void OnPartToggled(object sender, RoutedEventArgs e) => UpdateEnabled();

    /// <summary>
    /// Tab inside the platform list. The focus is on a list item, and the dialog's own tab order does not see that as the
    /// list, so it would start over at the first control. The list answers Tab and Shift+Tab itself: Select All follows
    /// the list and the platform match box precedes it, as in the template order.
    /// </summary>
    private void OnPlatformListKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        bool backward = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        FrameworkElement target = backward ? _platformMatch : _selectAll;
        e.Handled = true;
        _ = target.Focus(FocusState.Keyboard);
    }

    /// <summary>The details of a part are available while its check box is ticked.</summary>
    private void UpdateEnabled()
    {
        bool keywords = _keywordsEnabled.IsChecked == true;
        _keywordText.IsEnabled = keywords;
        _keywordMatch.IsEnabled = keywords;
        _inTitle.IsEnabled = keywords;
        _inHelp.IsEnabled = keywords;
        _inComment.IsEnabled = keywords;

        bool requirements = _requirementsEnabled.IsChecked == true;
        _platformMatch.IsEnabled = requirements;
        _platformList.IsEnabled = requirements;
        if (_selectAll is Control selectAll)
        {
            selectAll.IsEnabled = requirements;
        }

        if (_clearAll is Control clearAll)
        {
            clearAll.IsEnabled = requirements;
        }
    }

    private async Task ShowRefusalAsync(FilterProblem problem)
    {
        _messageOpen = true;
        string key = problem switch
        {
            FilterProblem.KeywordTextMissing => "FilterRefusalKeywordText",
            FilterProblem.KeywordWithinMissing => "FilterRefusalKeywordWithin",
            FilterProblem.KeywordTooLong => "FilterRefusalKeywordTooLong",
            _ => "FilterRefusalPlatforms",
        };
        FrameworkElement refocus = problem switch
        {
            FilterProblem.KeywordTextMissing or FilterProblem.KeywordTooLong => _keywordText,
            FilterProblem.KeywordWithinMissing => _inTitle,
            _ => _platformList,
        };
        try
        {
            var box = new ClassicMessageBox(
                GpeditStrings.Filter("FilterDialogCaption"),
                GpeditStrings.Filter(key),
                ClassicMessageIcon.Warning,
                ClassicMessageButtons.Ok,
                textAutomationId: MessageId);
            _ = await box.ShowAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UiDiagnostics.Warn?.Invoke("A message of the Filter Options dialog could not be shown: " + ex.Message);
        }
        finally
        {
            _messageOpen = false;
            if (refocus is Control control)
            {
                _ = control.Focus(FocusState.Programmatic);
            }
        }
    }

    private ComboBox Combo(string part, string[] itemKeys)
    {
        ComboBox combo = RequireControl<ComboBox>(Prefix + part);
        foreach (string key in itemKeys)
        {
            combo.Items.Add(GpeditStrings.Filter(key));
        }

        return combo;
    }

    // ---- the template -------------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate()
    {
        // The controls are in the tab order of the dialog; the static parts come where they read. 330 x 351 dialog units.
        ClassicControlDefinition[] controls =
        [
            Group("KeywordGroup", "FilterGroupKeywords", 7, 66, 316, 108),
            Group("RequirementsGroup", "FilterGroupRequirements", 7, 178, 316, 138),
            Label("LabelManaged", "FilterLabelManaged", 7, 9, 72, 8),
            ComboDef("Managed", "FilterNameManaged", 82, 7, 120, 14),
            Label("LabelConfigured", "FilterLabelConfigured", 7, 27, 72, 8),
            ComboDef("Configured", "FilterNameConfigured", 82, 25, 120, 14),
            Label("LabelCommented", "FilterLabelCommented", 7, 45, 72, 8),
            ComboDef("Commented", "FilterNameCommented", 82, 43, 120, 14),
            Check("KeywordsEnabled", "FilterKeywordsEnabled", 15, 78, 200, 10),
            Label("LabelKeywordText", "FilterLabelKeywordText", 24, 92, 150, 8),
            Edit("KeywordText", "FilterNameKeywordText", 24, 102, 292, 12),
            Label("LabelKeywordMatch", "FilterLabelKeywordMatch", 24, 122, 40, 8),
            ComboDef("KeywordMatch", "FilterNameKeywordMatch", 68, 120, 100, 14),
            Label("LabelWithin", "FilterLabelWithin", 24, 149, 44, 8),
            Check("InTitle", "FilterInTitle", 70, 148, 100, 10),
            Check("InHelp", "FilterInHelp", 172, 148, 62, 10),
            Check("InComment", "FilterInComment", 238, 148, 76, 10),
            Check("RequirementsEnabled", "FilterRequirementsEnabled", 15, 190, 200, 10),
            Label("LabelPlatformMatch", "FilterLabelPlatformMatch", 24, 207, 62, 8),
            ComboDef("PlatformMatch", "FilterNamePlatformMatch", 90, 204, 100, 14),
            Label("LabelPlatforms", "FilterLabelPlatforms", 24, 224, 100, 8),
            ListDef("PlatformList", "FilterNamePlatformList", 24, 234, 200, 70),
            Button(SelectAllId, "SelectAll", "FilterSelectAll", 230, 234, 86, 14),
            Button(ClearAllId, "ClearAll", "FilterClearAll", 230, 252, 86, 14),
            Button(1, "Ok", "FilterOk", 166, 326, 76, 14, isDefault: true),
            Button(2, "Cancel", "FilterCancel", 247, 326, 76, 14),
        ];
        return new ClassicDialogDefinition(
            GpeditStrings.Filter("FilterDialogCaption"),
            "Filter Options dialog of the Local Group Policy Editor",
            330,
            351,
            controls);
    }

    private static ClassicControlDefinition Group(string part, string textKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.GroupBox, StaticId, Prefix + part, GpeditStrings.Filter(textKey), x, y, cx, cy);

    private static ClassicControlDefinition Label(string part, string textKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.Label, StaticId, Prefix + part, GpeditStrings.Filter(textKey), x, y, cx, cy);

    private static ClassicControlDefinition ComboDef(string part, string nameKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.ComboBox, StaticId, Prefix + part, GpeditStrings.Filter(nameKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition Check(string part, string textKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.CheckBox, StaticId, Prefix + part, GpeditStrings.Filter(textKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition Edit(string part, string nameKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.Edit, StaticId, Prefix + part, GpeditStrings.Filter(nameKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition ListDef(string part, string nameKey, int x, int y, int cx, int cy) =>
        new(ClassicControlKind.ListBox, StaticId, Prefix + part, GpeditStrings.Filter(nameKey), x, y, cx, cy) { TabStop = true };

    private static ClassicControlDefinition Button(int id, string part, string textKey, int x, int y, int cx, int cy, bool isDefault = false) =>
        new(ClassicControlKind.Button, id, Prefix + part, GpeditStrings.Filter(textKey), x, y, cx, cy) { TabStop = true, IsDefault = isDefault };
}
