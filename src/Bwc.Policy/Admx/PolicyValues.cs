namespace Bwc.Policy.Admx;

/// <summary>Which hive files a policy belongs to.</summary>
public enum PolicyClass
{
    /// <summary>Computer Configuration only.</summary>
    Machine,

    /// <summary>User Configuration only.</summary>
    User,

    /// <summary>Both roots. The policy writes to the matching hive file of each.</summary>
    Both,
}

/// <summary>The kind of a literal value in a template.</summary>
public enum PolicyValueKind
{
    /// <summary>A decimal value (stored as a 32-bit number).</summary>
    Decimal,

    /// <summary>A long decimal value (stored as a 64-bit number).</summary>
    LongDecimal,

    /// <summary>A string value.</summary>
    String,

    /// <summary>The delete marker: the value is removed.</summary>
    Delete,
}

/// <summary>A literal value that a template writes, such as an enabled value or a list item value.</summary>
public sealed class PolicyValue
{
    internal PolicyValue(PolicyValueKind kind, ulong number, string? text)
    {
        Kind = kind;
        Number = number;
        Text = text;
    }

    /// <summary>The kind of the value.</summary>
    public PolicyValueKind Kind { get; }

    /// <summary>The number, for <see cref="PolicyValueKind.Decimal"/> and <see cref="PolicyValueKind.LongDecimal"/>.</summary>
    public ulong Number { get; }

    /// <summary>The text, for <see cref="PolicyValueKind.String"/>; otherwise null.</summary>
    public string? Text { get; }
}

/// <summary>One value of an enabled list, a disabled list, a boolean list or an enum item value list.</summary>
public sealed class PolicyValueListItem
{
    internal PolicyValueListItem(string? key, string effectiveKey, string valueName, PolicyValue? value)
    {
        Key = key;
        EffectiveKey = effectiveKey;
        ValueName = valueName;
        Value = value;
    }

    /// <summary>The key attribute of the item, or null when it has none.</summary>
    public string? Key { get; }

    /// <summary>The item key, else the list default key, else the key of the owner (policy or element).</summary>
    public string EffectiveKey { get; }

    /// <summary>The registry value name.</summary>
    public string ValueName { get; }

    /// <summary>The value to write, or null when the template gives none.</summary>
    public PolicyValue? Value { get; }
}

/// <summary>An ordered list of values with an optional default key.</summary>
public sealed class PolicyValueList
{
    internal PolicyValueList(string? defaultKey, IReadOnlyList<PolicyValueListItem> items)
    {
        DefaultKey = defaultKey;
        Items = items;
    }

    /// <summary>The defaultKey attribute, or null when absent.</summary>
    public string? DefaultKey { get; }

    /// <summary>The items in document order.</summary>
    public IReadOnlyList<PolicyValueListItem> Items { get; }
}
