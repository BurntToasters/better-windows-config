using System.Text.Json.Serialization;

namespace Bwc.Hub;

public sealed class HubSettings
{
    public WindowBounds? WindowBounds { get; set; }
}

public sealed class WindowBounds
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HubSettings))]
internal partial class HubSettingsContext : JsonSerializerContext;
