namespace Bwc.UI.List;

/// <summary>One details column. Width is in DIPs and is clamped by <see cref="DetailsList"/>.</summary>
public sealed class DetailsColumn
{
    public DetailsColumn(string key, string header, double width)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(header);
        Key = key;
        Header = header;
        Width = width;
    }

    /// <summary>Stable identifier such as Name, Type, or Data.</summary>
    public string Key { get; }

    /// <summary>Visible header text.</summary>
    public string Header { get; set; }

    /// <summary>Width in DIPs. <see cref="DetailsList"/> clamps interactive changes to 40 DIP.</summary>
    public double Width { get; set; }
}
