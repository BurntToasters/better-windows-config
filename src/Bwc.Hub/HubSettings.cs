using System.Text.Json.Serialization;

namespace Bwc.Hub;

public sealed class HubSettings
{
    /// <summary>The settings version this build writes. Files below it are migrated once on load.</summary>
    public const int CurrentSettingsVersion = 2;

    /// <summary>
    /// 0 when the file has no version, which is what 0.1.0 wrote. Not defaulted to the current version, so that
    /// <see cref="Migrate"/> can tell an old file from a new one.
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>The restored window rectangle in physical pixels. Kept while the window is maximized.</summary>
    public WindowBounds? WindowBounds { get; set; }

    /// <summary>True when the window was maximized at close. The start maximizes it again.</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>"stable" or "beta". Null and unknown values mean stable.</summary>
    public string? UpdateChannel { get; set; }

    /// <summary>When true, the Hub checks for updates once after the window opens. On by default.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>The manifest URL. Null means the build has no update server configured.</summary>
    public string? UpdateManifestUrl { get; set; }

    /// <summary>
    /// Brings settings from an older version up to <see cref="CurrentSettingsVersion"/>. 0.1.0 always saved the
    /// startup check off, because that was its default and not a choice, so version 0 turns it on once. Later
    /// choices are kept. Returns true when a value changed.
    /// </summary>
    public bool Migrate()
    {
        if (SettingsVersion >= CurrentSettingsVersion)
        {
            return false;
        }

        if (SettingsVersion < 2)
        {
            CheckForUpdatesOnStartup = true;
        }

        SettingsVersion = CurrentSettingsVersion;
        return true;
    }
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
