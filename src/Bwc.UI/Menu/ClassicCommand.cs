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

/// <summary>How a leaf command shows its state in the menu.</summary>
public enum ClassicCommandKind
{
    /// <summary>A plain item with no check mark.</summary>
    Normal,

    /// <summary>A toggle item: a <see cref="CheckMenuFlyoutItem"/> that shows a check mark while it is checked.</summary>
    Check,

    /// <summary>A radio item: a <c>RadioMenuFlyoutItem</c> in the command's radio group, one bullet per group.</summary>
    Radio,
}

/// <summary>
/// A menu command from a tool's command table. Text, access key, and accelerator are already
/// parsed and contain no mnemonic marker or tab escape.
/// </summary>
/// <param name="Id">Command id, unique in the menu. Separators do not use an id.</param>
/// <param name="Text">Visible text without the mnemonic marker.</param>
/// <param name="AccessKey">Single character access key, or null when the command has none.</param>
/// <param name="AcceleratorText">Accelerator text such as Ctrl+P, or null.</param>
/// <param name="IsEnabled">True only for commands the tool implements.</param>
/// <param name="Children">Nested items. Empty for a leaf command.</param>
/// <param name="Invoke">Action for an implemented leaf command, otherwise null.</param>
/// <param name="Kind">Plain, check, or radio. Only a leaf uses it.</param>
/// <param name="RadioGroup">The radio group name of a <see cref="ClassicCommandKind.Radio"/> command.</param>
public sealed record ClassicCommand(
    int Id,
    string Text,
    string? AccessKey,
    string? AcceleratorText,
    bool IsEnabled,
    IReadOnlyList<ClassicCommandItem> Children,
    Action? Invoke,
    ClassicCommandKind Kind = ClassicCommandKind.Normal,
    string? RadioGroup = null) : ClassicCommandItem;
