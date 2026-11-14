namespace Bwc.UI.List;

/// <summary>
/// One registry-shaped value row. Rows are immutable so a background sort can read them. A row is identified by its
/// raw name, never by its display text: a literal value named (Default) and the unnamed default value both display
/// as (Default).
/// </summary>
public sealed class DetailsRow
{
    public DetailsRow(string name, string rawName, string type, string data, bool renameable = true, bool isSynthetic = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rawName);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(data);
        Name = name;
        RawName = rawName;
        Type = type;
        Data = data;
        Renameable = renameable;
        IsSynthetic = isSynthetic;
    }

    /// <summary>Display name in the Name column. The unnamed value shows (Default).</summary>
    public string Name { get; }

    /// <summary>Registry value name. Empty for the unnamed default value and for the synthetic row.</summary>
    public string RawName { get; }

    /// <summary>
    /// True for the unnamed default value and for the synthetic row. The default row is always first in the list.
    /// </summary>
    public bool IsDefault => RawName.Length == 0;

    /// <summary>True for the placeholder of a key with no unnamed value. It has no registry value.</summary>
    public bool IsSynthetic { get; }

    /// <summary>Registry type such as REG_SZ or REG_DWORD.</summary>
    public string Type { get; }

    /// <summary>Formatted data text.</summary>
    public string Data { get; }

    /// <summary>True when F2 may start a rename for this row.</summary>
    public bool Renameable { get; }
}
