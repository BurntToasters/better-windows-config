namespace Bwc.Gpo.Store;

/// <summary>The two halves of a local GPO: Computer Configuration and User Configuration.</summary>
public enum GpoSide
{
    /// <summary>Computer Configuration: the <c>Machine</c> folder and the low 16 bits of the gpt.ini version.</summary>
    Machine,

    /// <summary>User Configuration: the <c>User</c> folder and the high 16 bits of the gpt.ini version.</summary>
    User,
}
