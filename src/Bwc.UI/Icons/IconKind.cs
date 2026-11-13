namespace Bwc.UI.Icons;

/// <summary>The icons the classic Registry Editor shows in its tree and details list.</summary>
public enum IconKind
{
    /// <summary>The virtual Computer root.</summary>
    Computer,

    /// <summary>A key. Classic uses the closed folder for every key, including the selected one.</summary>
    ClosedFolder,

    /// <summary>REG_SZ, REG_EXPAND_SZ and REG_MULTI_SZ values.</summary>
    StringValue,

    /// <summary>Every other value type.</summary>
    BinaryValue,
}
