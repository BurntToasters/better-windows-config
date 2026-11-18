namespace Bwc.Policy.Model;

/// <summary>
/// The value of one policy element, as read from a document or as entered in the setting dialog. An element that is not in
/// a set of values is empty. The shape must match the kind of the element (see <see cref="PolicyEditValidator"/>).
/// </summary>
public abstract record ElementValue
{
    /// <summary>A check box value for a boolean element.</summary>
    /// <param name="isChecked">True when the box is checked.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromBoolean(bool isChecked) => new BooleanValue(isChecked);

    /// <summary>A number for a decimal or longDecimal element.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromNumber(ulong number) => new NumberValue(number);

    /// <summary>A string for a text element. The empty string is empty.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromText(string text) => new TextValue(text);

    /// <summary>The lines of a multiText element.</summary>
    /// <param name="lines">The lines in order.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromLines(params string[] lines) => new MultiTextValue(lines);

    /// <summary>The selected item of an enum element.</summary>
    /// <param name="itemIndex">The zero-based index into the element's items.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromChoice(int itemIndex) => new ChoiceValue(itemIndex);

    /// <summary>The entries of a list element.</summary>
    /// <param name="entries">The entries in order.</param>
    /// <returns>The value.</returns>
    public static ElementValue FromList(params ListEntry[] entries) => new ListValue(entries);
}

/// <summary>A boolean element value.</summary>
/// <param name="Checked">True when the box is checked.</param>
public sealed record BooleanValue(bool Checked) : ElementValue;

/// <summary>A decimal or longDecimal element value.</summary>
/// <param name="Number">The number.</param>
public sealed record NumberValue(ulong Number) : ElementValue;

/// <summary>A text element value.</summary>
/// <param name="Text">The text.</param>
public sealed record TextValue(string Text) : ElementValue;

/// <summary>A multiText element value.</summary>
/// <param name="Lines">The lines in order.</param>
public sealed record MultiTextValue(IReadOnlyList<string> Lines) : ElementValue;

/// <summary>An enum element value.</summary>
/// <param name="ItemIndex">The zero-based index into the element's items.</param>
public sealed record ChoiceValue(int ItemIndex) : ElementValue;

/// <summary>A list element value.</summary>
/// <param name="Entries">The entries in order.</param>
public sealed record ListValue(IReadOnlyList<ListEntry> Entries) : ElementValue;

/// <summary>One row of a list element.</summary>
/// <param name="Name">The value name column of an explicitValue list; null for other lists.</param>
/// <param name="Data">The value (data) column.</param>
public sealed record ListEntry(string? Name, string Data)
{
    /// <summary>An entry of a list that has no name column.</summary>
    /// <param name="data">The data.</param>
    /// <returns>The entry.</returns>
    public static ListEntry Of(string data) => new(null, data);
}
