namespace Bwc.UI.List;

/// <summary>One registry-shaped value row. Rows are immutable so a background sort can read them.</summary>
public sealed class DetailsRow
{
    public DetailsRow(string name, string type, string data, bool renameable = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(data);
        Name = name;
        Type = type;
        Data = data;
        Renameable = renameable;
    }

    /// <summary>Value name. The default value is displayed as (Default).</summary>
    public string Name { get; }

    /// <summary>Registry type such as REG_SZ or REG_DWORD.</summary>
    public string Type { get; }

    /// <summary>Formatted data text.</summary>
    public string Data { get; }

    /// <summary>True when F2 may start a rename for this row.</summary>
    public bool Renameable { get; }
}
