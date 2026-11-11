using System.Text;
using Bwc.Core;
using Bwc.Hub.Tools;
using Bwc.UI.Menu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Bwc.Hub;

/// <summary>
/// The classic menu bar: File (Launch, Exit), Tools (one item per tool), and Help (Check for
/// Updates, Update Settings, About). Text and mnemonics come from the resource file. Alt+letter
/// routing is ClassicMenuKeyboard, the same router the Registry Editor uses.
/// </summary>
public sealed partial class MainWindow
{
    private const string MenuBarItemStyleKey = "BwcMenuBarItemStyle";
    private const string MenuFlyoutItemStyleKey = "BwcMenuFlyoutItemStyle";
    private const string MenuFlyoutSeparatorStyleKey = "BwcMenuFlyoutSeparatorStyle";

    private void BuildMenuBar()
    {
        MenuBarItem file = TopLevel("MenuFile", "MenuCommand_File");
        file.Items.Add(FlyoutItem(HubStrings.Get("MenuFileLaunch"), "MenuItem_Launch", "Enter", LaunchSelected));
        file.Items.Add(Separator());
        file.Items.Add(FlyoutItem(HubStrings.Get("MenuFileExit"), "MenuItem_Exit", "Alt+F4", Close));

        MenuBarItem tools = TopLevel("MenuTools", "MenuCommand_Tools");
        foreach (ToolCatalog.Tool tool in HubTools.All)
        {
            ToolCatalog.Tool captured = tool;
            tools.Items.Add(FlyoutItem(
                WithMnemonic(tool.DisplayName),
                "MenuItem_Tool_" + tool.Id,
                null,
                () => Launch(captured)));
        }

        MenuBarItem help = TopLevel("MenuHelp", "MenuCommand_Help");
        help.Items.Add(FlyoutItem(HubStrings.Get("MenuHelpCheckForUpdates"), "MenuItem_CheckForUpdates", null, () => _ = CheckForUpdatesAsync(interactive: true)));
        help.Items.Add(FlyoutItem(HubStrings.Get("MenuHelpUpdateSettings"), "MenuItem_UpdateSettings", null, () => _ = ShowUpdateSettingsAsync()));
        help.Items.Add(Separator());
        help.Items.Add(FlyoutItem(HubStrings.Get("MenuHelpAbout"), "MenuItem_About", null, () => _ = ShowAboutAsync()));

        HubMenuBar.Items.Clear();
        HubMenuBar.Items.Add(file);
        HubMenuBar.Items.Add(tools);
        HubMenuBar.Items.Add(help);
        ConfigureMenuArrowRing(HubMenuBar.Items.OfType<MenuBarItem>().ToArray());
        HubMenuBar.PreviewKeyDown += OnMenuPreviewKeyDown;
    }

    /// <summary>Creates the Alt-mnemonic router once the window handle and the menu items exist.</summary>
    private ClassicMenuKeyboard CreateMenuKeyboard()
    {
        MenuBarItem[] items = HubMenuBar.Items.OfType<MenuBarItem>().ToArray();
        var keyboard = new ClassicMenuKeyboard(_hwnd, items);

        // Island child windows can appear after the first layout pass. Hook them once layout settles.
        _ = DispatcherQueue.TryEnqueue(() => _menuKeyboard?.HookTree(_hwnd));
        return keyboard;
    }

    private void OnMenuPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_menuKeyboard is not null && _menuKeyboard.HandleKeyFromXaml(e.Key))
        {
            e.Handled = true;
        }
    }

    /// <summary>Left and Right wrap through XYFocus; Down is handled by OnMenuPreviewKeyDown.</summary>
    private static void ConfigureMenuArrowRing(IReadOnlyList<MenuBarItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            MenuBarItem item = items[i];
            item.XYFocusLeft = items[(i + items.Count - 1) % items.Count];
            item.XYFocusRight = items[(i + 1) % items.Count];
            item.XYFocusUp = item;
            item.XYFocusDown = item;
        }
    }

    private static MenuBarItem TopLevel(string resourceKey, string automationId)
    {
        (string text, string? accessKey) = ParseMnemonic(HubStrings.Get(resourceKey));
        var item = new MenuBarItem
        {
            Title = text,
        };
        if (accessKey is not null)
        {
            item.AccessKey = accessKey;
        }

        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, text);
        ApplyStyle(item, MenuBarItemStyleKey);
        return item;
    }

    private static MenuFlyoutItem FlyoutItem(
        string raw,
        string automationId,
        string? acceleratorText,
        Action invoke)
    {
        (string text, string? accessKey) = ParseMnemonic(raw);
        var item = new MenuFlyoutItem
        {
            Text = text,
        };
        if (accessKey is not null)
        {
            item.AccessKey = accessKey;
        }

        if (acceleratorText is not null)
        {
            item.KeyboardAcceleratorTextOverride = acceleratorText;
        }

        AutomationProperties.SetAutomationId(item, automationId);
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => invoke();
        ApplyStyle(item, MenuFlyoutItemStyleKey);
        return item;
    }

    private static MenuFlyoutSeparator Separator()
    {
        var separator = new MenuFlyoutSeparator();
        ApplyStyle(separator, MenuFlyoutSeparatorStyleKey);
        return separator;
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Style style)
        {
            element.Style = style;
        }
    }

    /// <summary>Puts an ampersand before the first letter when a name has none, so every tool has a mnemonic.</summary>
    private static string WithMnemonic(string label)
    {
        if (label.Contains('&', StringComparison.Ordinal))
        {
            return label;
        }

        for (int i = 0; i < label.Length; i++)
        {
            if (char.IsLetterOrDigit(label[i]))
            {
                return label.Insert(i, "&");
            }
        }

        return label;
    }

    /// <summary>Splits "E&amp;xit" into "Exit" and the access key "x". A doubled ampersand is a literal.</summary>
    private static (string Text, string? AccessKey) ParseMnemonic(string raw)
    {
        string? accessKey = null;
        var builder = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char current = raw[i];
            if (current != '&')
            {
                builder.Append(current);
                continue;
            }

            if (i + 1 < raw.Length && raw[i + 1] == '&')
            {
                builder.Append('&');
                i++;
                continue;
            }

            if (i + 1 < raw.Length)
            {
                accessKey ??= raw[i + 1].ToString();
                builder.Append(raw[i + 1]);
                i++;
            }
        }

        return (builder.ToString(), accessKey);
    }
}
