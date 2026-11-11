using System.Text.Json.Serialization;

namespace Bwc.Hub;

public sealed class HubSettings
{
    public WindowBounds? WindowBounds { get; set; }

    /// <summary>"stable" or "beta". Null and unknown values mean stable.</summary>
    public string? UpdateChannel { get; set; }

    /// <summary>When true, the Hub checks for updates once after the window opens. Off by default.</summary>
    public bool CheckForUpdatesOnStartup { get; set; }

    /// <summary>The manifest URL. Null means the build has no update server configured.</summary>
    public string? UpdateManifestUrl { get; set; }
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
