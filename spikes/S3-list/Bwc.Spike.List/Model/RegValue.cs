namespace Bwc.Spike.ListApp.Model;

/// <summary>One synthetic registry value (Name / Type / Data).</summary>
public sealed class RegValue
{
    public RegValue(string name, string type, string data)
    {
        Name = name;
        Type = type;
        Data = data;
    }

    public string Name { get; }

    public string Type { get; }

    public string Data { get; }
}
