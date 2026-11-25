using Bwc.UI.Console.Shell;
using Bwc.UI.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Console.Options;

/// <summary>
/// The choices of the Options dialog that BWC honors. The other parts of the classic page (the console name, the icon, the
/// console mode and the not-saving box) belong to console authoring, which BWC does not have, and are fixed.
/// </summary>
/// <param name="AllowCustomizeViews">
/// True when the user may customize views: View > Customize View is enabled. A first start allows it.
/// </param>
public sealed record ConsoleOptions(bool AllowCustomizeViews)
{
    /// <summary>The state of a first start.</summary>
    public static ConsoleOptions Default { get; } = new(true);
}

/// The Options dialog of the console frame (File > Options, mmc.exe RT_DIALOG 158, DLG-119): the read-only name box with
/// the disabled Change Icon button beside it, the disabled console mode drop-down with its description, the not-saving box
/// disabled Change Icon button beside it, the disabled console mode drop-down with its description, the not-saving box
/// (checked and disabled), the customize-views box (the one real choice), OK and Cancel. Nothing reaches the host before
/// OK: Cancel and Escape close the dialog and apply nothing. The window has the automation id <c>ConsoleOptions</c> and its
/// parts <c>ConsoleOptions.&lt;Part&gt;</c> (docs/failure-modes/console-options.md).
/// </summary>
public sealed class ConsoleOptionsDialog : ClassicDialog
{
    internal const string RootId = "ConsoleOptions";

    private const string Prefix = RootId + ".";
    private const int StaticId = 65535;
    private const int NameId = 1027;
    private const int ChangeIconId = 1025;
    private const int ModeId = 1022;
    private const int DescriptionId = 1023;
    private const int NoSaveId = 1024;
    private const int CustomizeViewsId = 1029;
    private const int GroupDescriptionId = 1030;

    private readonly CheckBox _customizeViews;

    /// <param name="state">The options as last applied: the box opens on them.</param>
    /// <param name="toolName">The window title of the tool, shown in the read-only name box.</param>
    public ConsoleOptionsDialog(ConsoleOptions state, string toolName)
        : base(BuildTemplate(state))
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(toolName);

        // The window is found by this id; the base names the root of every classic dialog DialogRoot.
        if (Content is FrameworkElement root)
        {
            AutomationProperties.SetAutomationId(root, RootId);
            AutomationProperties.SetName(root, ConsoleStrings.Get("ConsoleOptionsCaption"));
        }

        RequireControl<TextBox>(Prefix + "Name").Text = toolName;
        RequireControl<TextBox>(Prefix + "ModeDescription").Text = ConsoleStrings.Get("ConsoleOptionsDescriptionText");
        ComboBox mode = RequireControl<ComboBox>(Prefix + "Mode");
        mode.Items.Add(ConsoleStrings.Get("ConsoleOptionsModeValue"));
        mode.SelectedIndex = 0;
        _customizeViews = RequireControl<CheckBox>(Prefix + "CustomizeViews");
    }

    /// <summary>The choice that OK approved. Null after Cancel or Escape.</summary>
    public ConsoleOptions? Result { get; private set; }

    protected override bool TryAccept()
    {
        Result = new ConsoleOptions(_customizeViews.IsChecked == true);
        return true;
    }

    // ---- the template -----------------------------------------------------------------------------

    private static ClassicDialogDefinition BuildTemplate(ConsoleOptions state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 252 x 250 dialog units: the 252 x 218 page of the classic dialog plus the button row. The reading order is the
        // tab order: name, mode, description, the two boxes, OK, Cancel. The disabled parts are no tab stops.
        ClassicControlDefinition[] controls =
        [
            new(ClassicControlKind.Label, StaticId, Prefix + "NameLabel", ConsoleStrings.Get("ConsoleOptionsNameLabel"), 7, 12, 26, 8),
            new(ClassicControlKind.Edit, NameId, Prefix + "Name", ConsoleStrings.Get("ConsoleOptionsNameName"), 35, 10, 138, 12) { TabStop = true, ReadOnly = true },
            new(ClassicControlKind.Button, ChangeIconId, Prefix + "ChangeIcon", ConsoleStrings.Get("ConsoleOptionsChangeIcon"), 180, 9, 65, 14) { Disabled = true },
            new(ClassicControlKind.Label, StaticId, Prefix + "ModeLabel", ConsoleStrings.Get("ConsoleOptionsModeLabel"), 7, 36, 238, 8),
            new(ClassicControlKind.ComboBox, ModeId, Prefix + "Mode", ConsoleStrings.Get("ConsoleOptionsModeName"), 7, 47, 238, 12) { Disabled = true },
            new(ClassicControlKind.GroupBox, GroupDescriptionId, Prefix + "GroupDescription", ConsoleStrings.Get("ConsoleOptionsGroupDescription"), 7, 70, 238, 94),
            new(ClassicControlKind.Edit, DescriptionId, Prefix + "ModeDescription", ConsoleStrings.Get("ConsoleOptionsDescriptionName"), 13, 82, 226, 76) { ReadOnly = true, Multiline = true },
            new(ClassicControlKind.CheckBox, NoSaveId, Prefix + "NoSave", ConsoleStrings.Get("ConsoleOptionsNoSave"), 7, 172, 238, 10) { Checked = true, Disabled = true },
            new(ClassicControlKind.CheckBox, CustomizeViewsId, Prefix + "CustomizeViews", ConsoleStrings.Get("ConsoleOptionsCustomizeViews"), 7, 188, 238, 10) { TabStop = true, Checked = state.AllowCustomizeViews },
            new(ClassicControlKind.Button, 1, Prefix + "Ok", ConsoleStrings.Get("ConsoleDialogOk"), 141, 230, 50, 14) { TabStop = true, IsDefault = true },
            new(ClassicControlKind.Button, 2, Prefix + "Cancel", ConsoleStrings.Get("ConsoleDialogCancel"), 195, 230, 50, 14) { TabStop = true },
        ];
        return new ClassicDialogDefinition(
            ConsoleStrings.Get("ConsoleOptionsCaption"),
            "Options dialog of the console frame",
            252,
            250,
            controls);
    }
}
