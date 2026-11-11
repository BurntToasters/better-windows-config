using System.Runtime.InteropServices;

namespace Bwc.Update;

public enum UpdateChannel
{
    Stable,
    Beta,
}

public static class UpdateChannelNames
{
    public static bool TryParse(string? text, out UpdateChannel channel)
    {
        switch (text)
        {
            case "stable":
                channel = UpdateChannel.Stable;
                return true;
            case "beta":
                channel = UpdateChannel.Beta;
                return true;
            default:
                channel = UpdateChannel.Stable;
                return false;
        }
    }

    public static string ToText(UpdateChannel channel) => channel == UpdateChannel.Stable ? "stable" : "beta";
}

/// <summary>The installer targets. These are the only architecture names a manifest may use.</summary>
public static class Architectures
{
    public const string X64 = "win-x64";
    public const string Arm64 = "win-arm64";

    public static bool IsKnown(string? value) => value is X64 or Arm64;

    /// <summary>The architecture of the running process, or null when the build does not ship it.</summary>
    public static string? Current() => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => X64,
        System.Runtime.InteropServices.Architecture.Arm64 => Arm64,
        _ => null,
    };
}
