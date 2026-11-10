namespace Bwc.UI.Menu;

/// <summary>
/// One entry in a classic menu table. A separator is the shared <see cref="Separator"/> instance;
/// every other entry is a <see cref="ClassicCommand"/>.
/// </summary>
public abstract record ClassicCommandItem
{
    /// <summary>The shared separator singleton. Separators carry no id, text, or action.</summary>
    public static ClassicCommandItem Separator { get; } = new SeparatorCommandItem();

    private sealed record SeparatorCommandItem : ClassicCommandItem;
}

/// <summary>
/// A menu command from the oracle resource table. Text, access key, and accelerator are already
/// parsed and contain no mnemonic marker or tab escape.
/// </summary>
/// <param name="Id">Oracle command id. Separators do not use an id.</param>
/// <param name="Text">Visible text without the mnemonic marker.</param>
/// <param name="AccessKey">Single character access key, or null when the oracle has none.</param>
/// <param name="AcceleratorText">Accelerator text such as Ctrl+P, or null.</param>
/// <param name="IsEnabled">True only for commands this shell implements.</param>
/// <param name="Children">Nested items. Empty for a leaf command.</param>
/// <param name="Invoke">Action for an implemented leaf command, otherwise null.</param>
public sealed record ClassicCommand(
    int Id,
    string Text,
    string? AccessKey,
    string? AcceleratorText,
    bool IsEnabled,
    IReadOnlyList<ClassicCommandItem> Children,
    Action? Invoke) : ClassicCommandItem;
