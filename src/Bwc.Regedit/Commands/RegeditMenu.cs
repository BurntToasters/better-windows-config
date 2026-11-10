using System.Text;
using Bwc.UI.Menu;
using Bwc.UI.Shell;
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
    private const string MenuBarItemStyleKey = "BwcMenuBarItemStyle";
    private const string MenuFlyoutItemStyleKey = "BwcMenuFlyoutItemStyle";
    private const string MenuFlyoutSeparatorStyleKey = "BwcMenuFlyoutSeparatorStyle";

    private static readonly ClassicCommandItem[] NoChildren = Array.Empty<ClassicCommandItem>();

    /// <summary>
    /// Builds the command table. File Import, File Export, File Exit, View Address Bar, View Split,
    /// View Display Binary Data, the New subtree, Delete, Rename, Find, and Find Next are implemented.
    /// The host updates Export, Delete, Rename, and Display Binary Data as the selection changes.
    /// </summary>
    public static IReadOnlyList<ClassicCommand> Create(
        ResourceLoader loader,
        ClassicWindowShell shell,
        IRegeditEditCommands editCommands,
        Action exit,
        Action? import = null,
        Action? export = null,
        Action? displayBinaryData = null)
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
                Command(loader, 680, "MenuFileLoadHive"),
                Command(loader, 681, "MenuFileUnloadHive"),
                ClassicCommandItem.Separator,
                Command(loader, 657, "MenuFileConnectNetworkRegistry"),
                Command(loader, 768, "MenuFileDisconnectNetworkRegistry"),
                ClassicCommandItem.Separator,
                Command(loader, 660, "MenuFilePrint"),
                ClassicCommandItem.Separator,
                Command(loader, 661, "MenuFileExit", isEnabled: true, NoChildren, exit),
            });

        var edit = Command(
            loader,
            513,
            "MenuEdit",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                editNew,
                ClassicCommandItem.Separator,
                Command(loader, 678, "MenuEditPermissions"),
                ClassicCommandItem.Separator,
                Command(loader, 913, "MenuEditDelete", invoke: editCommands.DeleteSelection),
                Command(loader, 914, "MenuEditRename", invoke: editCommands.RenameSelection),
                ClassicCommandItem.Separator,
                Command(loader, 675, "MenuEditCopyKeyName"),
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
                Command(loader, 668, "MenuViewAddressBar", isEnabled: true, NoChildren, shell.ToggleAddressBar),
                ClassicCommandItem.Separator,
                Command(loader, 670, "MenuViewSplit", isEnabled: true, NoChildren, shell.BeginSplitKeyboardMode),
                ClassicCommandItem.Separator,
                Command(loader, 679, "MenuViewDisplayBinaryData", isEnabled: false, children: NoChildren, invoke: displayBinaryData),
                ClassicCommandItem.Separator,
                Command(loader, 648, "MenuViewRefresh"),
                Command(loader, 699, "MenuViewFont"),
            });

        var favorites = Command(
            loader,
            517,
            "MenuFavorites",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 1281, "MenuFavoritesAdd"),
                Command(loader, 1280, "MenuFavoritesRemove"),
            });

        var help = Command(
            loader,
            515,
            "MenuHelp",
            isEnabled: true,
            new ClassicCommandItem[]
            {
                Command(loader, 667, "MenuHelpAbout"),
            });

        return new[] { file, edit, view, favorites, help };
    }

    /// <summary>Clears the menu bar and adds one top-level item per command.</summary>
    public static void Populate(MenuBar menuBar, IReadOnlyList<ClassicCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(menuBar);
        ArgumentNullException.ThrowIfNull(commands);

        menuBar.Items.Clear();
        foreach (var command in commands)
        {
            menuBar.Items.Add(BuildTopLevelItem(command));
        }
    }

    /// <summary>
    /// Enables or disables one command by oracle id. The flyout controls carry the id in Tag so the
    /// caller does not need to keep a parallel dictionary of generated controls.
    /// </summary>
    public static void SetCommandEnabled(MenuBar menuBar, int commandId, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            if (item.Tag is int id && id == commandId)
            {
                item.IsEnabled = isEnabled;
            }

            WalkFlyout(item.Items, commandId, isEnabled);
        }
    }

    private static void WalkFlyout(
        IEnumerable<MenuFlyoutItemBase> items,
        int commandId,
        bool isEnabled)
    {
        foreach (var entry in items)
        {
            if (entry is MenuFlyoutItem leaf)
            {
                if (leaf.Tag is int id && id == commandId)
                {
                    leaf.IsEnabled = isEnabled;
                }

                continue;
            }

            if (entry is MenuFlyoutSubItem subItem)
            {
                if (subItem.Tag is int id && id == commandId)
                {
                    subItem.IsEnabled = isEnabled;
                }

                WalkFlyout(subItem.Items, commandId, isEnabled);
            }
        }
    }

    /// <summary>
    /// Splits oracle menu text into visible text, access key, and accelerator.
    /// The resource holds the oracle TAB between text and accelerator. A literal backslash-t is
    /// also accepted so an older resource file keeps working.
    /// </summary>
    public static (string Text, string? AccessKey, string? AcceleratorText) ParseMenuText(string? raw)
    {
        var text = raw ?? string.Empty;
        string? accelerator = null;

        var tabIndex = text.IndexOf('\t');
        var separatorLength = 1;
        if (tabIndex < 0)
        {
            tabIndex = text.IndexOf("\\t", StringComparison.Ordinal);
            separatorLength = 2;
        }

        if (tabIndex >= 0)
        {
            accelerator = text[(tabIndex + separatorLength)..];
            text = text[..tabIndex];
        }

        string? accessKey = null;
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current != '&')
            {
                builder.Append(current);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                builder.Append('&');
                i++;
                continue;
            }

            if (i + 1 < text.Length)
            {
                accessKey = text[i + 1].ToString();
                builder.Append(text[i + 1]);
                i++;
                continue;
            }

            builder.Append('&');
        }

        if (string.IsNullOrEmpty(accelerator))
        {
            accelerator = null;
        }

        return (builder.ToString(), accessKey, accelerator);
    }

    private static ClassicCommand Command(
        ResourceLoader loader,
        int id,
        string resourceKey,
        bool isEnabled = false,
        IReadOnlyList<ClassicCommandItem>? children = null,
        Action? invoke = null)
    {
        var parsed = ParseMenuText(loader.GetString(resourceKey));
        return new ClassicCommand(
            id,
            parsed.Text,
            parsed.AccessKey,
            parsed.AcceleratorText,
            isEnabled,
            children ?? NoChildren,
            invoke);
    }

    private static MenuBarItem BuildTopLevelItem(ClassicCommand command)
    {
        var item = new MenuBarItem
        {
            Title = command.Text,
            IsEnabled = command.IsEnabled,
            Tag = command.Id,
        };

        AutomationProperties.SetAutomationId(item, "MenuCommand_" + command.Id);

        if (command.AccessKey is not null)
        {
            item.AccessKey = command.AccessKey;
        }

        ApplyStyle(item, MenuBarItemStyleKey);
        foreach (var child in command.Children)
        {
            item.Items.Add(BuildFlyoutItem(child));
        }

        return item;
    }

    private static MenuFlyoutItemBase BuildFlyoutItem(ClassicCommandItem entry)
    {
        if (entry is not ClassicCommand command)
        {
            var separator = new MenuFlyoutSeparator();
            ApplyStyle(separator, MenuFlyoutSeparatorStyleKey);
            return separator;
        }

        if (command.Children.Count > 0)
        {
            var subItem = new MenuFlyoutSubItem
            {
                Text = command.Text,
                IsEnabled = command.IsEnabled,
                Tag = command.Id,
            };

            AutomationProperties.SetAutomationId(subItem, "MenuCommand_" + command.Id);

            if (command.AccessKey is not null)
            {
                subItem.AccessKey = command.AccessKey;
            }

            foreach (var child in command.Children)
            {
                subItem.Items.Add(BuildFlyoutItem(child));
            }

            return subItem;
        }

        var item = new MenuFlyoutItem
        {
            Text = command.Text,
            IsEnabled = command.IsEnabled,
            Tag = command.Id,
        };

        AutomationProperties.SetAutomationId(item, "MenuCommand_" + command.Id);

        if (command.AccessKey is not null)
        {
            item.AccessKey = command.AccessKey;
        }

        if (command.AcceleratorText is not null)
        {
            item.KeyboardAcceleratorTextOverride = command.AcceleratorText;
        }

        if (command.Invoke is not null)
        {
            var invoke = command.Invoke;
            item.Click += (_, _) => invoke();
        }

        ApplyStyle(item, MenuFlyoutItemStyleKey);
        return item;
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        var style = FindStyle(key);
        if (style is not null)
        {
            element.Style = style;
        }
    }

    private static Style? FindStyle(string key)
    {
        var application = Application.Current;
        return application is null ? null : FindStyle(application.Resources, key);
    }

    private static Style? FindStyle(ResourceDictionary dictionary, string key)
    {
        if (dictionary.TryGetValue(key, out var value) && value is Style style)
        {
            return style;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var found = FindStyle(merged, key);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
