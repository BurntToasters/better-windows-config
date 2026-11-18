namespace Bwc.UI.Console.Shell;

/// <summary>
/// Ids of the standard toolbar buttons. A host extension button uses an id from <see cref="FirstExtension"/> up.
/// </summary>
public static class ConsoleToolbarIds
{
    public const int Back = 1;
    public const int Forward = 2;
    public const int UpOneLevel = 3;
    public const int ShowHideConsoleTree = 4;
    public const int Properties = 5;
    public const int Refresh = 6;
    public const int ExportList = 7;
    public const int Help = 8;

    /// <summary>The first id a host can use for an extension button. Ids below it belong to the frame.</summary>
    public const int FirstExtension = 100;

    internal static readonly int[] Standard =
    [
        Back,
        Forward,
        UpOneLevel,
        ShowHideConsoleTree,
        Properties,
        Refresh,
        ExportList,
        Help,
    ];
}

/// <summary>
/// Command ids of the frame's own menu entries. The numbers are the classic mmc.exe command ids (RT_MENU 194), so
/// a host's own ids must stay out of this set. The top-level ids are BWC's (a popup has no id in the resource).
/// </summary>
public static class ConsoleCommandIds
{
    public const int FileMenu = 59001;
    public const int ActionMenu = 59002;
    public const int ViewMenu = 59003;
    public const int HelpMenu = 59004;

    public const int Options = 13220;
    public const int Exit = 57665;
    public const int CustomizeView = 13231;
    public const int HelpTopics = 12804;
}

/// <summary>The pane that held the keyboard focus, or was hit, when a context menu was requested.</summary>
public enum ConsolePane
{
    /// <summary>The console tree.</summary>
    Tree,

    /// <summary>The result pane slot.</summary>
    Result,

    /// <summary>Neither pane: the menu bar, the toolbar or the splitter.</summary>
    Other,
}
