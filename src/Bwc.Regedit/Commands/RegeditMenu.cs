using System.Globalization;
using Bwc.UI.Menu;
using Bwc.UI.Shell;
using Bwc.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Bwc.Regedit.Commands;

/// <summary>
/// The full RT_MENU 103 table from docs/parity/oracle/regedit-resources.json, plus conversion to
/// WinUI menu controls. Every visible string comes from Strings/en-US/Resources.resw so the oracle
/// text, including the ampersand mnemonic and the literal backslash-t accelerator separator, is
/// preserved exactly.
/// </summary>
public static class RegeditMenu
{
    /// <summary>Oracle command id of View &gt; Address Bar (RT_MENU 103, id 668), the one toggle item.</summary>
    private const int AddressBarCommandId = 668;

    /// <summary>The View > Theme submenu (BWC addition, outside the oracle ids) and its three radio items.</summary>
    internal const int ThemeMenuCommandId = 9100;
    internal const int ThemeSystemCommandId = 9101;
    internal const int ThemeLightCommandId = 9102;
    internal const int ThemeDarkCommandId = 9103;

    private const string ThemeRadioGroupName = "BwcTheme";

    /// <summary>The AutomationId prefix for the dynamic favorite entries after the two commands.</summary>
    internal const string FavoriteAutomationPrefix = "MenuCommand_Favorite_";

    /// <summary>Oracle command id of the Edit menu (RT_MENU 103, id 513).</summary>
    private const int EditCommandId = 513;

    /// <summary>The Edit menu starts with Modify, Modify Binary Data, and a separator. These are the value items.</summary>
    private const int ValueItemCount = 3;

    private static readonly ClassicCommandItem[] NoChildren = Array.Empty<ClassicCommandItem>();

    /// <summary>
    /// Builds the command table. File Import, File Export, File Load Hive, File Unload Hive,
    /// Connect Network Registry, Disconnect Network Registry, File Print, File Exit, View Address
    /// Bar, View Split, View Display Binary Data, the New subtree, Delete, Rename, Find, Find Next,
    /// and the two Favorites commands are implemented. The host updates Export, Print, Unload Hive,
    /// Disconnect Network Registry, Delete, Rename, Display Binary Data, and the dynamic favorite
    /// list as the selection and store change.
    /// </summary>
    public static IReadOnlyList<ClassicCommand> Create(
        ResourceLoader loader,
        ClassicWindowShell shell,
        IRegeditEditCommands editCommands,
        Action exit,
        Action? import = null,
        Action? export = null,
        Action? displayBinaryData = null,
        Action? addFavorite = null,
        Action? removeFavorite = null,
        Action? permissions = null,
        Action? print = null,
        Action? loadHive = null,
        Action? unloadHive = null,
        Action? connectNetworkRegistry = null,
        Action? disconnectNetworkRegistry = null,
        Action? font = null,
        Action? about = null,
        Action<BwcThemeOverride>? setTheme = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(editCommands);
        ArgumentNullException.ThrowIfNull(exit);

        var editNew = Command(
            loader,
            516,
            "MenuEditNew",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 663, "MenuEditNewKey", invoke: editCommands.NewKey),
                ClassicCommandItem.Separator,
                Command(loader, 664, "MenuEditNewStringValue", invoke: editCommands.NewStringValue),
                Command(loader, 665, "MenuEditNewBinaryValue", invoke: editCommands.NewBinaryValue),
                Command(loader, 674, "MenuEditNewDwordValue", invoke: editCommands.NewDwordValue),
                Command(loader, 688, "MenuEditNewQwordValue", invoke: editCommands.NewQwordValue),
                Command(loader, 676, "MenuEditNewMultiStringValue", invoke: editCommands.NewMultiStringValue),
                Command(loader, 677, "MenuEditNewExpandableStringValue", invoke: editCommands.NewExpandableStringValue),
            });

        var file = Command(
            loader,
            512,
            "MenuFile",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 658, "MenuFileImport", isEnabled: true, NoChildren, import),
                Command(loader, 659, "MenuFileExport", isEnabled: false, NoChildren, export),
                ClassicCommandItem.Separator,
                Command(loader, 680, "MenuFileLoadHive", isEnabled: true, NoChildren, loadHive),
                Command(loader, 681, "MenuFileUnloadHive", isEnabled: false, NoChildren, unloadHive),
                ClassicCommandItem.Separator,
                Command(loader, 657, "MenuFileConnectNetworkRegistry", isEnabled: true, NoChildren, connectNetworkRegistry),
                Command(loader, 768, "MenuFileDisconnectNetworkRegistry", isEnabled: false, NoChildren, disconnectNetworkRegistry),
                ClassicCommandItem.Separator,
                Command(loader, 660, "MenuFilePrint", isEnabled: false, NoChildren, print),
                ClassicCommandItem.Separator,
                Command(loader, 661, "MenuFileExit", isEnabled: true, NoChildren, exit),
            });

        var edit = Command(
            loader,
            EditCommandId,
            "MenuEdit",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                // The two value items come first. SetValueCommandsVisible hides them unless a value is selected.
                Command(loader, 912, "ContextModify", isEnabled: false, NoChildren, editCommands.ModifySelectedValue),
                Command(loader, 917, "ContextModifyBinary", isEnabled: false, NoChildren, editCommands.ModifyBinaryData),
                ClassicCommandItem.Separator,
                editNew,
                ClassicCommandItem.Separator,
                Command(loader, 678, "MenuEditPermissions", isEnabled: false, NoChildren, permissions),
                ClassicCommandItem.Separator,
                Command(loader, 913, "MenuEditDelete", invoke: editCommands.DeleteSelection),
                Command(loader, 914, "MenuEditRename", invoke: editCommands.RenameSelection),
                ClassicCommandItem.Separator,
                Command(loader, 675, "MenuEditCopyKeyName", isEnabled: false, NoChildren, editCommands.CopyKeyName),
                ClassicCommandItem.Separator,
                Command(loader, 662, "MenuEditFind", isEnabled: true, NoChildren, editCommands.Find),
                Command(loader, 671, "MenuEditFindNext", isEnabled: true, NoChildren, editCommands.FindNext),
            });

        var view = Command(
            loader,
            514,
            "MenuView",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, AddressBarCommandId, "MenuViewAddressBar", isEnabled: true, NoChildren, shell.ToggleAddressBar, ClassicCommandKind.Check),
                ClassicCommandItem.Separator,
                Command(loader, 670, "MenuViewSplit", isEnabled: true, NoChildren, shell.BeginSplitKeyboardMode),
                ClassicCommandItem.Separator,
                Command(loader, 679, "MenuViewDisplayBinaryData", isEnabled: false, children: NoChildren, invoke: displayBinaryData),
                ClassicCommandItem.Separator,
                Command(loader, 648, "MenuViewRefresh", isEnabled: true, NoChildren, editCommands.Refresh),
                Command(loader, 699, "MenuViewFont", isEnabled: true, NoChildren, font),
                ClassicCommandItem.Separator,
                Command(
                    loader,
                    ThemeMenuCommandId,
                    "MenuViewTheme",
                    isEnabled: true,
                    new ClassicCommandItem[]
                    {
                        Command(loader, ThemeSystemCommandId, "MenuViewThemeSystem", isEnabled: true, NoChildren, () => setTheme?.Invoke(BwcThemeOverride.System), ClassicCommandKind.Radio, ThemeRadioGroupName),
                        Command(loader, ThemeLightCommandId, "MenuViewThemeLight", isEnabled: true, NoChildren, () => setTheme?.Invoke(BwcThemeOverride.Light), ClassicCommandKind.Radio, ThemeRadioGroupName),
                        Command(loader, ThemeDarkCommandId, "MenuViewThemeDark", isEnabled: true, NoChildren, () => setTheme?.Invoke(BwcThemeOverride.Dark), ClassicCommandKind.Radio, ThemeRadioGroupName),
                    }),
            });

        var favorites = Command(
            loader,
            517,
            "MenuFavorites",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 1281, "MenuFavoritesAdd", isEnabled: true, NoChildren, addFavorite),
                Command(loader, 1280, "MenuFavoritesRemove", isEnabled: false, NoChildren, removeFavorite),
            });

        var help = Command(
            loader,
            515,
            "MenuHelp",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 667, "MenuHelpAbout", isEnabled: true, NoChildren, about),
            });

        return new[] { file, edit, view, favorites, help };
    }

    /// <summary>Clears the menu bar and adds one top-level item per command.</summary>
    public static void Populate(MenuBar menuBar, IReadOnlyList<ClassicCommand> commands) =>
        ClassicMenuBuilder.Populate(menuBar, commands);

    /// <summary>Finds one top-level menu by oracle command id. Returns null for an unknown id.</summary>
    public static MenuBarItem? FindTopLevelItem(MenuBar menuBar, int commandId) =>
        ClassicMenuBuilder.FindTopLevelItem(menuBar, commandId);

    /// <summary>
    /// Builds one dynamic favorite leaf. The entries have no oracle id, so they use a stable
    /// AutomationId prefix plus their sorted index and take their text directly from the store.
    /// </summary>
    internal static MenuFlyoutItem BuildFavoriteItem(string name, int index, Action invoke)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(invoke);

        var item = new MenuFlyoutItem
        {
            Text = name,
        };

        AutomationProperties.SetAutomationId(
            item,
            FavoriteAutomationPrefix + index.ToString(CultureInfo.InvariantCulture));

        item.Click += (_, _) => invoke();
        ClassicMenuBuilder.ApplyItemStyle(item);
        return item;
    }

    /// <summary>
    /// Enables or disables one command by oracle id. The flyout controls carry the id in Tag so the
    /// caller does not need to keep a parallel dictionary of generated controls.
    /// </summary>
    public static void SetCommandEnabled(MenuBar menuBar, int commandId, bool isEnabled) =>
        ClassicMenuBuilder.SetCommandEnabled(menuBar, commandId, isEnabled);

    /// <summary>
    /// Shows or hides the value items at the top of Edit (Modify, Modify Binary Data, and their
    /// separator). Classic regedit shows them only while a value is selected. The items stay in the
    /// menu, so their order and enabled state are kept while they are hidden.
    /// </summary>
    public static void SetValueCommandsVisible(MenuBar menuBar, bool visible)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        MenuBarItem? edit = FindTopLevelItem(menuBar, EditCommandId);
        if (edit is null || edit.Items.Count < ValueItemCount)
        {
            return;
        }

        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < ValueItemCount; index += 1)
        {
            edit.Items[index].Visibility = visibility;
        }
    }

    /// <summary>
    /// Splits oracle menu text into visible text, access key, and accelerator.
    /// The resource holds the oracle TAB between text and accelerator. A literal backslash-t is
    /// also accepted so an older resource file keeps working.
    /// </summary>
    public static (string Text, string? AccessKey, string? AcceleratorText) ParseMenuText(string? raw) =>
        ClassicMenuBuilder.ParseMenuText(raw);

    private static ClassicCommand Command(
        ResourceLoader loader,
        int id,
        string resourceKey,
        bool isEnabled = false,
        IReadOnlyList<ClassicCommandItem>? children = null,
        Action? invoke = null,
        ClassicCommandKind kind = ClassicCommandKind.Normal,
        string? radioGroup = null) =>
        ClassicMenuBuilder.Command(loader.GetString(resourceKey), id, isEnabled, children, invoke, kind, radioGroup);

    internal static MenuFlyoutItemBase BuildFlyoutItem(
        ClassicCommandItem entry,
        string automationPrefix = "MenuCommand_") =>
        ClassicMenuBuilder.BuildFlyoutItem(entry, automationPrefix);

    /// <summary>
    /// Marks the View > Theme radio item that matches the override. Call after the menu is built and
    /// after each change.
    /// </summary>
    public static void SetThemeChecked(MenuBar menuBar, BwcThemeOverride value)
    {
        int checkedId = value switch
        {
            BwcThemeOverride.Light => ThemeLightCommandId,
            BwcThemeOverride.Dark => ThemeDarkCommandId,
            _ => ThemeSystemCommandId,
        };

        ClassicMenuBuilder.SetRadioChecked(menuBar, ThemeRadioGroupName, checkedId);
    }
}
