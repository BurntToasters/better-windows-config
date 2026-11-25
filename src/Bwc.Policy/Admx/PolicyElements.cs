namespace Bwc.Policy.Admx;

/// <summary>The kind of a policy element.</summary>
public enum PolicyElementKind
{
    /// <summary>A check box.</summary>
    Boolean,

    /// <summary>A 32-bit number.</summary>
    Decimal,

    /// <summary>A 64-bit number.</summary>
    LongDecimal,

    /// <summary>A string.</summary>
    Text,

    /// <summary>A multi-string.</summary>
    MultiText,

    /// <summary>A drop-down list.</summary>
    Enum,

    /// <summary>A table of values.</summary>
    List,
}

/// <summary>An option of a policy. The kind-specific facts are on the derived classes.</summary>
public abstract class PolicyElement
{
    internal PolicyElement(PolicyElementKind kind, string id, string? key, string effectiveKey, string? valueName)
    {
        Kind = kind;
        Id = id;
        Key = key;
        EffectiveKey = effectiveKey;
        ValueName = valueName;
    }

    /// <summary>The kind of the element.</summary>
    public PolicyElementKind Kind { get; }

    /// <summary>The element id, as written.</summary>
    public string Id { get; }

    /// <summary>The key attribute of the element, or null. It overrides the policy key for this element.</summary>
    public string? Key { get; }

    /// <summary>The element key when it has one, else the policy key.</summary>
    public string EffectiveKey { get; }

    /// <summary>The registry value name, or null when absent.</summary>
    public string? ValueName { get; }

    /// <summary>The required attribute (false when absent).</summary>
    public bool Required { get; internal init; }

    /// <summary>The clientExtension attribute, or null.</summary>
    public string? ClientExtension { get; internal init; }
}

/// <summary>A boolean element: a check box with true and false values or lists.</summary>
public sealed class BooleanElement : PolicyElement
{
    internal BooleanElement(string id, string? key, string effectiveKey, string? valueName)
        : base(PolicyElementKind.Boolean, id, key, effectiveKey, valueName)
    {
    }

    /// <summary>The trueValue, or null.</summary>
    public PolicyValue? TrueValue { get; internal init; }

    /// <summary>The falseValue, or null.</summary>
    public PolicyValue? FalseValue { get; internal init; }

    /// <summary>The trueList, or null.</summary>
    public PolicyValueList? TrueList { get; internal init; }

    /// <summary>The falseList, or null.</summary>
    public PolicyValueList? FalseList { get; internal init; }
}

/// <summary>A decimal or longDecimal element. Absent numbers stay null; no default is applied here.</summary>
public sealed class DecimalElement : PolicyElement
{
    internal DecimalElement(PolicyElementKind kind, string id, string? key, string effectiveKey, string? valueName)
        : base(kind, id, key, effectiveKey, valueName)
    {
    }

    /// <summary>The minValue attribute, or null.</summary>
    public ulong? MinValue { get; internal init; }

    /// <summary>The maxValue attribute, or null.</summary>
    public ulong? MaxValue { get; internal init; }

    /// <summary>The soft attribute (false when absent).</summary>
    public bool Soft { get; internal init; }

    /// <summary>The storeAsText attribute (false when absent): the number is written as text.</summary>
    public bool StoreAsText { get; internal init; }
}

/// <summary>A text element.</summary>
public sealed class TextElement : PolicyElement
{
    internal TextElement(string id, string? key, string effectiveKey, string? valueName)
        : base(PolicyElementKind.Text, id, key, effectiveKey, valueName)
    {
    }

    /// <summary>The expandable attribute (false when absent): the text is written as an expandable string.</summary>
    public bool Expandable { get; internal init; }

    /// <summary>The maxLength attribute, or null.</summary>
    public ulong? MaxLength { get; internal init; }
}

/// <summary>A multiText element.</summary>
public sealed class MultiTextElement : PolicyElement
{
    internal MultiTextElement(string id, string? key, string effectiveKey, string? valueName)
        : base(PolicyElementKind.MultiText, id, key, effectiveKey, valueName)
    {
    }

    /// <summary>The maxLength attribute, or null.</summary>
    public ulong? MaxLength { get; internal init; }

    /// <summary>The maxStrings attribute, or null.</summary>
    public ulong? MaxStrings { get; internal init; }
}

/// <summary>One choice of an enum element.</summary>
public sealed class EnumItem
{
    internal EnumItem(string displayName, PolicyValue? value, PolicyValueList? valueList)
    {
        DisplayName = displayName;
        Value = value;
        ValueList = valueList;
    }

    /// <summary>The resolved display name. An ampersand stays a literal ampersand.</summary>
    public string DisplayName { get; }

    /// <summary>The value written when the item is selected, or null.</summary>
    public PolicyValue? Value { get; }

    /// <summary>The extra values written when the item is selected, or null.</summary>
    public PolicyValueList? ValueList { get; }
}

/// <summary>An enum element (a drop-down list).</summary>
public sealed class EnumElement : PolicyElement
{
    internal EnumElement(string id, string? key, string effectiveKey, string? valueName, IReadOnlyList<EnumItem> items)
        : base(PolicyElementKind.Enum, id, key, effectiveKey, valueName)
    {
        Items = items;
    }

    /// <summary>The items in document order.</summary>
    public IReadOnlyList<EnumItem> Items { get; }
}

/// <summary>A list element (a table of values).</summary>
public sealed class ListElement : PolicyElement
{
    internal ListElement(string id, string? key, string effectiveKey, string? valueName)
        : base(PolicyElementKind.List, id, key, effectiveKey, valueName)
    {
    }

    /// <summary>The additive attribute (false when absent).</summary>
    public bool Additive { get; internal init; }

    /// <summary>The explicitValue attribute (false when absent): entries have a name and a value.</summary>
    public bool ExplicitValue { get; internal init; }

    /// <summary>The expandable attribute (false when absent).</summary>
    public bool Expandable { get; internal init; }

    /// <summary>The valuePrefix attribute. Null when absent, which differs from an empty string.</summary>
    public string? ValuePrefix { get; internal init; }
}
