namespace Bwc.Core;

/// <summary>A process integrity level, ordered from lowest to highest. Unknown means it could not be read.</summary>
internal enum IntegrityLevel
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    System = 4,
}

/// <summary>
/// The rule the single-instance server applies to a connecting client (docs/failure-modes/core.md, "Client integrity
/// level and a squatted mutex"). It is pure so tests need no elevated process.
/// </summary>
internal static class IntegrityPolicy
{
    /// <summary>
    /// True when a client of <paramref name="client"/> may deliver a message to a server of <paramref name="server"/>.
    /// A client below the server is refused. An unknown client level, or an unknown server level, is refused.
    /// </summary>
    public static bool Accepts(IntegrityLevel server, IntegrityLevel client)
    {
        if (server == IntegrityLevel.Unknown || client == IntegrityLevel.Unknown)
        {
            return false;
        }

        return client >= server;
    }

    /// <summary>Maps the last sub-authority of a mandatory label SID to a level. Below the Low range is unknown.</summary>
    public static IntegrityLevel FromRid(uint rid)
    {
        return rid switch
        {
            >= 0x4000 => IntegrityLevel.System,
            >= 0x3000 => IntegrityLevel.High,
            >= 0x2000 => IntegrityLevel.Medium,
            >= 0x1000 => IntegrityLevel.Low,
            _ => IntegrityLevel.Unknown,
        };
    }

    /// <summary>The token used in the instance name for a level. It matches the names earlier builds produced.</summary>
    public static string ToSuffix(IntegrityLevel level)
    {
        return level switch
        {
            IntegrityLevel.System => "system",
            IntegrityLevel.High => "high",
            IntegrityLevel.Medium => "medium",
            IntegrityLevel.Low => "low",
            _ => "unknown",
        };
    }
}
