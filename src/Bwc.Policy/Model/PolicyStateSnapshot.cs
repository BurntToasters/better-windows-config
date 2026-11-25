namespace Bwc.Policy.Model;

/// <summary>A record of a document that a policy owns.</summary>
/// <param name="Key">The key as stored in the record.</param>
/// <param name="ValueName">The value name as stored, including a <c>**del.</c> or <c>**soft.</c> prefix.</param>
public sealed record PolicyOwnedRecord(string Key, string ValueName);

/// <summary>The state of one policy in one document, as classic shows it. A snapshot that does not follow edits.</summary>
public sealed class PolicyStateSnapshot
{
    internal PolicyStateSnapshot(
        PolicyState state,
        IReadOnlyDictionary<string, ElementValue> elements,
        IReadOnlyList<PolicyOwnedRecord> ownedRecords)
    {
        State = state;
        Elements = elements;
        OwnedRecords = ownedRecords;
    }

    /// <summary>The state.</summary>
    public PolicyState State { get; }

    /// <summary>
    /// The values of the elements that have one, by element id (case-insensitive). Empty unless the state is Enabled. An
    /// element with no record is missing, which is normal for a partial file.
    /// </summary>
    public IReadOnlyDictionary<string, ElementValue> Elements { get; }

    /// <summary>
    /// The records of the document that belong to the policy: value records at names the policy writes, their delete and
    /// soft instructions, and every record of a list key. Records that no policy names are not here.
    /// </summary>
    public IReadOnlyList<PolicyOwnedRecord> OwnedRecords { get; }
}
