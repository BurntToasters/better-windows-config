using Bwc.UI.Menu;

namespace Bwc.UI.Console.Shell;

/// <summary>
/// What a snap-in adds to the frame's menus. Each list goes before the frame's own entries of that menu. The four
/// handlers run the frame's entries; an entry without a handler stays in the menu, disabled.
/// </summary>
public sealed record ConsoleMenuExtensions
{
    /// <summary>Entries of the File menu, before Options and Exit.</summary>
    public IReadOnlyList<ClassicCommandItem> FileEntries { get; init; } = [];

    /// <summary>Entries of the Action menu (the frame adds none).</summary>
    public IReadOnlyList<ClassicCommandItem> ActionEntries { get; init; } = [];

    /// <summary>Entries of the View menu, before Customize View.</summary>
    public IReadOnlyList<ClassicCommandItem> ViewEntries { get; init; } = [];

    /// <summary>Entries of the Help menu, before Help Topics.</summary>
    public IReadOnlyList<ClassicCommandItem> HelpEntries { get; init; } = [];

    /// <summary>File > Options.</summary>
    public Action? Options { get; init; }

    /// <summary>File > Exit.</summary>
    public Action? Exit { get; init; }

    /// <summary>View > Customize View.</summary>
    public Action? CustomizeView { get; init; }

    /// <summary>Help > Help Topics.</summary>
    public Action? HelpTopics { get; init; }
}

/// <summary>The menu table of the frame and the access keys it had to give up.</summary>
/// <param name="Commands">The four top-level menus, ready for <see cref="ClassicMenuBuilder.Populate"/>.</param>
/// <param name="Collisions">One line for each frame entry that lost its access key to a snap-in entry.</param>
public sealed record ConsoleMenuTable(IReadOnlyList<ClassicCommand> Commands, IReadOnlyList<string> Collisions);

/// <summary>
/// Builds the menu skeleton of the user-mode console (mmc.exe RT_MENU 194): File, Action, View and Help, with the
/// frame's entries after the snap-in's. Nothing here knows a snap-in.
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
        ConsoleCommandIds.CustomizeView,
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
        foreach (IReadOnlyList<ClassicCommandItem> list in new[] { extensions.FileEntries, extensions.ActionEntries, extensions.ViewEntries, extensions.HelpEntries })
        {
            RejectFrameIds(list);
        }

        var collisions = new List<string>();
        ClassicCommand file = Menu(
            ConsoleCommandIds.FileMenu,
            "ConsoleMenuFile",
            extensions.FileEntries,
            [
                Frame("ConsoleMenuFileOptions", ConsoleCommandIds.Options, extensions.Options),
                ClassicCommandItem.Separator,
                Frame("ConsoleMenuFileExit", ConsoleCommandIds.Exit, extensions.Exit),
            ],
            collisions);
        ClassicCommand action = Menu(ConsoleCommandIds.ActionMenu, "ConsoleMenuAction", extensions.ActionEntries, [], collisions);
        ClassicCommand view = Menu(
            ConsoleCommandIds.ViewMenu,
            "ConsoleMenuView",
            extensions.ViewEntries,
            [Frame("ConsoleMenuViewCustomize", ConsoleCommandIds.CustomizeView, extensions.CustomizeView)],
            collisions);
        ClassicCommand help = Menu(
            ConsoleCommandIds.HelpMenu,
            "ConsoleMenuHelp",
            extensions.HelpEntries,
            [Frame("ConsoleMenuHelpTopics", ConsoleCommandIds.HelpTopics, extensions.HelpTopics)],
            collisions);

        return new ConsoleMenuTable([file, action, view, help], collisions);
    }

    private static ClassicCommand Frame(string labelKey, int id, Action? invoke) =>
        ClassicMenuBuilder.Command(ConsoleStrings.Get(labelKey), id, isEnabled: invoke is not null, invoke: invoke);

    private static ClassicCommand Menu(
        int id,
        string labelKey,
        IReadOnlyList<ClassicCommandItem> extension,
        IReadOnlyList<ClassicCommandItem> frame,
        List<string> collisions)
    {
        ClassicCommand top = ClassicMenuBuilder.Command(ConsoleStrings.Get(labelKey), id, isEnabled: true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClassicCommandItem item in extension)
        {
            if (item is ClassicCommand { AccessKey: { } key })
            {
                _ = used.Add(key);
            }
        }

        var children = new List<ClassicCommandItem>(extension);
        if (extension.Count > 0 && frame.Count > 0)
        {
            children.Add(ClassicCommandItem.Separator);
        }

        foreach (ClassicCommandItem item in frame)
        {
            if (item is ClassicCommand { AccessKey: { } key } command && used.Contains(key))
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
