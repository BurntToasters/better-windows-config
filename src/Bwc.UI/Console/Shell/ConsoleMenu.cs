using Bwc.UI.Menu;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// What a snap-in adds to the frame's menus. The lists keep the places the classic menus give them (see
/// <see cref="ConsoleMenu"/>). The handlers run the frame's entries; an entry without one stays in the menu, disabled.
/// </summary>
public sealed record ConsoleMenuExtensions
{
    /// <summary>Entries of the File menu, before Options and Exit.</summary>
    public IReadOnlyList<ClassicCommandItem> FileEntries { get; init; } = [];

    /// <summary>Entries of the Action menu, before the frame's Export List.</summary>
    public IReadOnlyList<ClassicCommandItem> ActionEntries { get; init; } = [];

    /// <summary>
    /// Entries of the Action menu after the frame's Export List (the separator and Help of the editor). The host adds its own
    /// separator first: the frame adds none between Export List and these.
    /// </summary>
    public IReadOnlyList<ClassicCommandItem> ActionTailEntries { get; init; } = [];

    /// <summary>The view modes: the group of the View menu between Add/Remove Columns and Customize View.</summary>
    public IReadOnlyList<ClassicCommandItem> ViewEntries { get; init; } = [];

    /// <summary>
    /// Entries of the View menu after the view modes and the separator the frame adds after them, right before Customize View
    /// (the filter entries of the editor). The host adds a separator of its own after them when it wants one.
    /// </summary>
    public IReadOnlyList<ClassicCommandItem> ViewTailEntries { get; init; } = [];

    /// <summary>Entries of the Help menu, before Help Topics.</summary>
    public IReadOnlyList<ClassicCommandItem> HelpEntries { get; init; } = [];

    /// <summary>File > Options.</summary>
    public Action? Options { get; init; }

    /// <summary>File > Exit.</summary>
    public Action? Exit { get; init; }

    /// <summary>View > Add/Remove Columns.</summary>
    public Action? AddRemoveColumns { get; init; }

    /// <summary>View > Customize View.</summary>
    public Action? CustomizeView { get; init; }

    /// <summary>Action > Export List.</summary>
    public Action? ExportList { get; init; }

    /// <summary>Help > Help Topics.</summary>
    public Action? HelpTopics { get; init; }
}

/// <summary>The menu table of the frame and the access keys it had to give up.</summary>
/// <param name="Commands">The four top-level menus, ready for <see cref="ClassicMenuBuilder.Populate"/>.</param>
/// <param name="Collisions">One line for each frame entry that lost its access key to a snap-in entry.</param>
public sealed record ConsoleMenuTable(IReadOnlyList<ClassicCommand> Commands, IReadOnlyList<string> Collisions);

/// <summary>
/// Builds the menu skeleton of the user-mode console (mmc.exe RT_MENU 194): File, Action, View and Help. The File and Help
/// popups hold the snap-in's entries first and the frame's after them. The Action popup holds the snap-in's entries, the
/// frame's Export List and the snap-in's tail. The View popup is Add/Remove Columns, a separator, the view modes, a
/// separator, the snap-in's tail and Customize View (the console tree, toolbar and status bar switches are in
/// Customize View, not in the menu). Nothing here knows a snap-in.
/// </summary>
public static class ConsoleMenu
{
    private static readonly int[] FrameIds =
    [
        ConsoleCommandIds.FileMenu,
        ConsoleCommandIds.ActionMenu,
        ConsoleCommandIds.ViewMenu,
        ConsoleCommandIds.HelpMenu,
        ConsoleCommandIds.Options,
        ConsoleCommandIds.Exit,
        ConsoleCommandIds.AddRemoveColumns,
        ConsoleCommandIds.CustomizeView,
        ConsoleCommandIds.ExportList,
        ConsoleCommandIds.HelpTopics,
    ];

    /// <summary>
    /// Builds the table. A snap-in entry wins an access key: a frame entry in the same popup that uses the same
    /// letter loses its key (it stays reachable by the arrow keys), and the loss is listed in the result. A snap-in
    /// entry with a frame command id throws.
    /// </summary>
    public static ConsoleMenuTable Build(ConsoleMenuExtensions extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        foreach (IReadOnlyList<ClassicCommandItem> list in new[]
        {
            extensions.FileEntries,
            extensions.ActionEntries,
            extensions.ActionTailEntries,
            extensions.ViewEntries,
            extensions.ViewTailEntries,
            extensions.HelpEntries,
        })
        {
            RejectFrameIds(list);
        }

        var collisions = new List<string>();
        ClassicCommand file = Popup(
            ConsoleCommandIds.FileMenu,
            "ConsoleMenuFile",
            [
                .. extensions.FileEntries,
                .. SeparatorBefore(extensions.FileEntries),
                Frame("ConsoleMenuFileOptions", ConsoleCommandIds.Options, extensions.Options),
                ClassicCommandItem.Separator,
                Frame("ConsoleMenuFileExit", ConsoleCommandIds.Exit, extensions.Exit),
            ],
            [extensions.FileEntries],
            collisions);
        ClassicCommand action = Popup(
            ConsoleCommandIds.ActionMenu,
            "ConsoleMenuAction",
            [
                .. extensions.ActionEntries,
                Frame("ConsoleMenuActionExportList", ConsoleCommandIds.ExportList, extensions.ExportList),
                .. extensions.ActionTailEntries,
            ],
            [extensions.ActionEntries, extensions.ActionTailEntries],
            collisions);
        ClassicCommand view = Popup(
            ConsoleCommandIds.ViewMenu,
            "ConsoleMenuView",
            [
                Frame("ConsoleMenuViewColumns", ConsoleCommandIds.AddRemoveColumns, extensions.AddRemoveColumns),
                .. SeparatorBefore(extensions.ViewEntries),
                .. extensions.ViewEntries,
                ClassicCommandItem.Separator,
                .. extensions.ViewTailEntries,
                Frame("ConsoleMenuViewCustomize", ConsoleCommandIds.CustomizeView, extensions.CustomizeView),
            ],
            [extensions.ViewEntries, extensions.ViewTailEntries],
            collisions);
        ClassicCommand help = Popup(
            ConsoleCommandIds.HelpMenu,
            "ConsoleMenuHelp",
            [
                .. extensions.HelpEntries,
                .. SeparatorBefore(extensions.HelpEntries),
                Frame("ConsoleMenuHelpTopics", ConsoleCommandIds.HelpTopics, extensions.HelpTopics),
            ],
            [extensions.HelpEntries],
            collisions);

        return new ConsoleMenuTable([file, action, view, help], collisions);
    }

    private static ClassicCommand Frame(string labelKey, int id, Action? invoke) =>
        ClassicMenuBuilder.Command(ConsoleStrings.Get(labelKey), id, isEnabled: invoke is not null, invoke: invoke);

    /// <summary>A separator for the group of entries around it, or nothing when the group is empty.</summary>
    private static ClassicCommandItem[] SeparatorBefore(IReadOnlyList<ClassicCommandItem> group) =>
        group.Count > 0 ? [ClassicCommandItem.Separator] : [];

    /// <summary>
    /// One popup. <paramref name="hostLists"/> are the snap-in's entries in it: the access keys they use are theirs, and a
    /// frame entry of the popup that uses one of the keys loses it.
    /// </summary>
    private static ClassicCommand Popup(
        int id,
        string labelKey,
        IReadOnlyList<ClassicCommandItem> items,
        IReadOnlyList<IReadOnlyList<ClassicCommandItem>> hostLists,
        List<string> collisions)
    {
        ClassicCommand top = ClassicMenuBuilder.Command(ConsoleStrings.Get(labelKey), id, isEnabled: true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyList<ClassicCommandItem> list in hostLists)
        {
            foreach (ClassicCommandItem item in list)
            {
                if (item is ClassicCommand { AccessKey: { } key })
                {
                    _ = used.Add(key);
                }
            }
        }

        var children = new List<ClassicCommandItem>(items.Count);
        foreach (ClassicCommandItem item in items)
        {
            if (item is ClassicCommand { AccessKey: { } key } command && Array.IndexOf(FrameIds, command.Id) >= 0 && used.Contains(key))
            {
                collisions.Add($"{top.Text}: '{command.Text}' lost access key '{key}'.");
                children.Add(command with { AccessKey = null });
            }
            else
            {
                children.Add(item);
            }
        }

        return top with { Children = children };
    }

    private static void RejectFrameIds(IReadOnlyList<ClassicCommandItem> items)
    {
        foreach (ClassicCommandItem item in items)
        {
            if (item is not ClassicCommand command)
            {
                continue;
            }

            if (Array.IndexOf(FrameIds, command.Id) >= 0)
            {
                throw new ArgumentException($"Menu command id {command.Id} belongs to the console frame.", nameof(items));
            }

            RejectFrameIds(command.Children);
        }
    }
}
