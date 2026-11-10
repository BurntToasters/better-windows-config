namespace Bwc.Core;

public static class ToolCatalog
{
    public sealed record Tool(string Id, string DisplayName, string ExeName, string Description);

    public static IReadOnlyList<Tool> Tools { get; } =
    [
        new("regedit", "Registry Editor", "BwcRegedit.exe", "Browse and edit the Windows registry."),
    ];
}
