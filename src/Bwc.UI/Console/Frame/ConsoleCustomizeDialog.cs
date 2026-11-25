using Bwc.UI.Console.Shell;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Console.Frame;

/// <summary>
/// The five switches of Customize View that BWC honors. The four parts it does not have (standard menus, taskpad tabs,
/// action pane, snap-in menus) show in the dialog disabled and are not part of the state.
/// </summary>
/// <param name="ConsoleTree">The console tree and its splitter.</param>
/// <param name="StandardToolbar">The eight standard toolbar buttons.</param>
/// <param name="StatusBar">The status bar.</param>
/// <param name="DescriptionBar">The description pane of the Extended tab.</param>
/// <param name="SnapInToolbars">The toolbar buttons of the host (the extension buttons).</param>
public sealed record ConsoleViewSwitches(bool ConsoleTree, bool StandardToolbar, bool StatusBar, bool DescriptionBar, bool SnapInToolbars)
{
    /// <summary>Everything shows. This is the state of a first start.</summary>
    public static ConsoleViewSwitches AllShown { get; } = new(true, true, true, true, true);
}

/// <summary>
/// The Customize View dialog of the console frame (mmcndmgr.dll RT_DIALOG 1086, DLG-073, DLG-117): nine check boxes in two
/// groups and an OK button only. Each box acts when it changes, before OK closes the dialog; there is no Cancel, so Escape
/// closes the dialog and keeps what was changed. The four boxes for parts BWC does not have are disabled. The window has
/// the automation id <c>ConsoleCustomize</c> and its parts <c>ConsoleCustomize.&lt;Part&gt;</c>
/// (docs/failure-modes/console-frame.md, section 4).
/// </summary>
public sealed class ConsoleCustomizeDialog : ClassicDialog
{
    internal const string RootId = "ConsoleCustomize";

    private const string Prefix = RootId + ".";
    private const int GroupConsoleId = 4191;
    private const int GroupSnapInId = 4192;
    private const int IntroId = 4171;
    private const int TreeId = 4143;
    private const int StandardMenusId = 4144;
    private const int ToolbarId = 4139;
    private const int StatusBarId = 4141;
    private const int DescriptionBarId = 4142;
    private const int TaskpadTabsId = 4145;
    private const int ActionPaneId = 4172;
    private const int SnapInMenusId = 4138;
    private const int SnapInToolbarsId = 4140;

    private readonly Action<ConsoleViewSwitches> _changed;
    private readonly CheckBox _tree;
    private readonly CheckBox _toolbar;
    private readonly CheckBox _statusBar;
    private readonly CheckBox _descriptionBar;
    private readonly CheckBox _snapInToolbars;
    private ConsoleViewSwitches _state;

    /// <param name="state">The switches as they stand now: the boxes open on them.</param>
    /// <param name="changed">Called with the whole state each time a box changes, so the window acts at once.</param>
    public ConsoleCustomizeDialog(ConsoleViewSwitches state, Action<ConsoleViewSwitches> changed)
        : base(BuildTemplate(state))
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(changed);
        _state = state;
        _changed = changed;

        // The window is found by this id; the base names the root of every classic dialog DialogRoot.
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, ConsoleStrings.Get("ConsoleCustomizeCaption"));
        }

        _tree = RequireControl<CheckBox>(Prefix + "ConsoleTree");
        _toolbar = RequireControl<CheckBox>(Prefix + "StandardToolbar");
        _statusBar = RequireControl<CheckBox>(Prefix + "StatusBar");
        _descriptionBar = RequireControl<CheckBox>(Prefix + "DescriptionBar");
        _snapInToolbars = RequireControl<CheckBox>(Prefix + "SnapInToolbars");
        foreach (CheckBox box in new[] { _tree, _toolbar, _statusBar, _descriptionBar, _snapInToolbars })
        {
            box.Checked += OnBoxChanged;
            box.Unchecked += OnBoxChanged;
        }
    }

    /// <summary>The switches as the boxes show them now.</summary>
    public ConsoleViewSwitches State => _state;

    private void OnBoxChanged(object sender, RoutedEventArgs e)
    {
        var now = new ConsoleViewSwitches(
            _tree.IsChecked == true,
            _toolbar.IsChecked == true,
            _statusBar.IsChecked == true,
            _descriptionBar.IsChecked == true,
            _snapInToolbars.IsChecked == true);
        if (now == _state)
        {
            return;
        }

        _state = now;
        _changed(now);
    }

    // ---- the template -----------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate(ConsoleViewSwitches state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 214 x 223 dialog units. The tab order of the classic dialog is the order of the boxes, then OK.
        ClassicControlDefinition[] controls =
        [
            new(ClassicControlKind.Label, IntroId, Prefix + "Intro", ConsoleStrings.Get("ConsoleCustomizeIntro"), 7, 7, 194, 22),
            new(ClassicControlKind.GroupBox, GroupConsoleId, Prefix + "GroupConsole", ConsoleStrings.Get("ConsoleCustomizeGroupConsole"), 7, 31, 200, 111),
            Box(TreeId, "ConsoleTree", "ConsoleCustomizeConsoleTree", 42, state.ConsoleTree, enabled: true),
            Box(StandardMenusId, "StandardMenus", "ConsoleCustomizeStandardMenus", 56, true, enabled: false),
            Box(ToolbarId, "StandardToolbar", "ConsoleCustomizeStandardToolbar", 70, state.StandardToolbar, enabled: true),
            Box(StatusBarId, "StatusBar", "ConsoleCustomizeStatusBar", 84, state.StatusBar, enabled: true),
            Box(DescriptionBarId, "DescriptionBar", "ConsoleCustomizeDescriptionBar", 98, state.DescriptionBar, enabled: true),
            Box(TaskpadTabsId, "TaskpadTabs", "ConsoleCustomizeTaskpadTabs", 112, false, enabled: false),
            Box(ActionPaneId, "ActionPane", "ConsoleCustomizeActionPane", 126, false, enabled: false),
            new(ClassicControlKind.GroupBox, GroupSnapInId, Prefix + "GroupSnapIn", ConsoleStrings.Get("ConsoleCustomizeGroupSnapIn"), 7, 150, 200, 42),
            Box(SnapInMenusId, "SnapInMenus", "ConsoleCustomizeSnapInMenus", 162, false, enabled: false),
            Box(SnapInToolbarsId, "SnapInToolbars", "ConsoleCustomizeSnapInToolbars", 176, state.SnapInToolbars, enabled: true),
            new(ClassicControlKind.Button, 1, Prefix + "Ok", ConsoleStrings.Get("ConsoleDialogOk"), 157, 200, 50, 14) { TabStop = true, IsDefault = true },
        ];
        return new ClassicDialogDefinition(
            ConsoleStrings.Get("ConsoleCustomizeCaption"),
            "Customize View dialog of the console frame",
            214,
            223,
            controls);
    }

    private static ClassicControlDefinition Box(int id, string part, string textKey, int y, bool isChecked, bool enabled) =>
        new(ClassicControlKind.CheckBox, id, Prefix + part, ConsoleStrings.Get(textKey), 13, y, 188, 10)
        {
            TabStop = enabled,
            Checked = isChecked,
            Disabled = !enabled,
        };
}
