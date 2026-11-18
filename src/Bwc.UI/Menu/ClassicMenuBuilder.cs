using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Bwc.UI.Menu;

/// <summary>
/// Builds the classic menu bar and popups from a tool's command table, and finds and updates the built
/// controls by command id. Nothing here knows a tool's ids or strings: the table (a list of
/// <see cref="ClassicCommand"/> with separators), including which leaves are check or radio items, comes
/// from the tool. Each built control carries its command id in <c>Tag</c> and the AutomationId
/// <c>prefix + id</c>, so a caller needs no parallel dictionary of controls.
/// </summary>
public static class ClassicMenuBuilder
{
    /// <summary>The default AutomationId prefix of a menu command (a context menu passes its own).</summary>
    public const string DefaultAutomationPrefix = "MenuCommand_";

    private const string MenuBarItemStyleKey = "BwcMenuBarItemStyle";
    private const string MenuFlyoutItemStyleKey = "BwcMenuFlyoutItemStyle";
    private const string MenuFlyoutSubItemStyleKey = "BwcMenuFlyoutSubItemStyle";
    private const string ToggleMenuFlyoutItemStyleKey = "BwcToggleMenuFlyoutItemStyle";
    private const string MenuFlyoutSeparatorStyleKey = "BwcMenuFlyoutSeparatorStyle";

    private static readonly ClassicCommandItem[] NoChildren = Array.Empty<ClassicCommandItem>();

    /// <summary>
    /// Makes a table entry from raw menu text. The text may hold an ampersand mnemonic and, after a tab, an
    /// accelerator; see <see cref="ParseMenuText"/>. A command with children is a submenu.
    /// </summary>
    public static ClassicCommand Command(
        string? rawText,
        int id,
        bool isEnabled = false,
        IReadOnlyList<ClassicCommandItem>? children = null,
        Action? invoke = null,
        ClassicCommandKind kind = ClassicCommandKind.Normal,
        string? radioGroup = null)
    {
        var parsed = ParseMenuText(rawText);
        return new ClassicCommand(
            id,
            parsed.Text,
            parsed.AccessKey,
            parsed.AcceleratorText,
            isEnabled,
            children ?? NoChildren,
            invoke,
            kind,
            radioGroup);
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

    /// <summary>Builds one top-level menu with its popup items.</summary>
    public static MenuBarItem BuildTopLevelItem(ClassicCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var item = new MenuBarItem
        {
            Title = command.Text,
            IsEnabled = command.IsEnabled,
            Tag = command.Id,
        };

        AutomationProperties.SetAutomationId(item, DefaultAutomationPrefix + command.Id);

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

    /// <summary>
    /// Builds one popup entry: a separator, a submenu (a command with children), or a leaf. A leaf is a plain
    /// item, a check item, or a radio item, as its <see cref="ClassicCommand.Kind"/> says.
    /// </summary>
    public static MenuFlyoutItemBase BuildFlyoutItem(
        ClassicCommandItem entry,
        string automationPrefix = DefaultAutomationPrefix)
    {
        ArgumentNullException.ThrowIfNull(entry);

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

            AutomationProperties.SetAutomationId(subItem, automationPrefix + command.Id);

            if (command.AccessKey is not null)
            {
                subItem.AccessKey = command.AccessKey;
            }

            foreach (var child in command.Children)
            {
                subItem.Items.Add(BuildFlyoutItem(child, automationPrefix));
            }

            ApplyStyle(subItem, MenuFlyoutSubItemStyleKey);
            return subItem;
        }

        MenuFlyoutItem item = command.Kind switch
        {
            ClassicCommandKind.Check => new CheckMenuFlyoutItem(),
            ClassicCommandKind.Radio => new RadioMenuFlyoutItem { GroupName = command.RadioGroup ?? string.Empty },
            _ => new MenuFlyoutItem(),
        };
        item.Text = command.Text;
        item.IsEnabled = command.IsEnabled;
        item.Tag = command.Id;

        AutomationProperties.SetAutomationId(item, automationPrefix + command.Id);

        if (command.AccessKey is not null)
        {
            item.AccessKey = command.AccessKey;
        }

        if (command.AcceleratorText is not null)
        {
            // The shortcut is already the item's text. WinUI must not add an accelerator tooltip on top.
            item.KeyboardAcceleratorTextOverride = command.AcceleratorText;
            item.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        }

        if (command.Invoke is not null)
        {
            var invoke = command.Invoke;
            item.Click += (_, _) => invoke();
            MenuItemActions.Register(item, invoke);
        }

        ApplyStyle(item, item is CheckMenuFlyoutItem ? ToggleMenuFlyoutItemStyleKey : MenuFlyoutItemStyleKey);
        return item;
    }

    /// <summary>Applies the shared plain item style to a leaf the tool builds itself, such as a dynamic list entry.</summary>
    public static void ApplyItemStyle(MenuFlyoutItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ApplyStyle(item, MenuFlyoutItemStyleKey);
    }

    /// <summary>Finds one top-level menu by command id. Returns null for an unknown id.</summary>
    public static MenuBarItem? FindTopLevelItem(MenuBar menuBar, int commandId)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            if (item.Tag is int id && id == commandId)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Enables or disables every control with this command id, in the bar, the popups and the submenus.</summary>
    public static void SetCommandEnabled(MenuBar menuBar, int commandId, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            if (item.Tag is int id && id == commandId)
            {
                item.IsEnabled = isEnabled;
            }

            WalkFlyout(item.Items, entry =>
            {
                if (entry.Tag is int entryId && entryId == commandId)
                {
                    entry.IsEnabled = isEnabled;
                }
            });
        }
    }

    /// <summary>Shows or hides every popup entry with this command id. A hidden entry keeps its place and state.</summary>
    public static void SetCommandVisible(MenuBar menuBar, int commandId, bool isVisible)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        Visibility visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            WalkFlyout(item.Items, entry =>
            {
                if (entry.Tag is int entryId && entryId == commandId)
                {
                    entry.Visibility = visibility;
                }
            });
        }
    }

    /// <summary>Sets the check mark of every check item with this command id.</summary>
    public static void SetChecked(MenuBar menuBar, int commandId, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(menuBar);

        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            WalkFlyout(item.Items, entry =>
            {
                if (entry is CheckMenuFlyoutItem toggle && toggle.Tag is int id && id == commandId)
                {
                    toggle.IsChecked = isChecked;
                }
            });
        }
    }

    /// <summary>
    /// Marks the radio item of the group whose command id is <paramref name="checkedId"/> and clears the others in
    /// that group. Call after the menu is built and after each change.
    /// </summary>
    public static void SetRadioChecked(MenuBar menuBar, string radioGroup, int checkedId)
    {
        ArgumentNullException.ThrowIfNull(menuBar);
        ArgumentNullException.ThrowIfNull(radioGroup);

        foreach (var item in menuBar.Items.OfType<MenuBarItem>())
        {
            WalkFlyout(item.Items, entry =>
            {
                if (entry is RadioMenuFlyoutItem radio &&
                    string.Equals(radio.GroupName, radioGroup, StringComparison.Ordinal) &&
                    radio.Tag is int id)
                {
                    radio.IsChecked = id == checkedId;
                }
            });
        }
    }

    /// <summary>
    /// Splits menu text into visible text, access key, and accelerator.
    /// The text holds a TAB between text and accelerator. A literal backslash-t is
    /// also accepted so an older resource file keeps working. A doubled ampersand is a literal ampersand.
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

    /// <summary>Visits every popup entry, descending into submenus. A submenu is visited before its children.</summary>
    private static void WalkFlyout(IEnumerable<MenuFlyoutItemBase> items, Action<MenuFlyoutItemBase> visit)
    {
        foreach (var entry in items)
        {
            visit(entry);
            if (entry is MenuFlyoutSubItem subItem)
            {
                WalkFlyout(subItem.Items, visit);
            }
        }
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
