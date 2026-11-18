namespace Bwc.Policy.Admx;

/// <summary>The kind of a presentation control.</summary>
public enum PresentationControlKind
{
    /// <summary>A static text line.</summary>
    Text,

    /// <summary>A check box.</summary>
    CheckBox,

    /// <summary>A single-line text box.</summary>
    TextBox,

    /// <summary>A number box.</summary>
    DecimalTextBox,

    /// <summary>A 64-bit number box.</summary>
    LongDecimalTextBox,

    /// <summary>A combo box with suggestions.</summary>
    ComboBox,

    /// <summary>A drop-down list.</summary>
    DropdownList,

    /// <summary>A list box with a Show button.</summary>
    ListBox,

    /// <summary>A multi-line text box.</summary>
    MultiTextBox,
}

/// <summary>One control of a presentation. Absent attributes stay null.</summary>
public sealed class PresentationControl
{
    internal PresentationControl(PresentationControlKind kind, string? refId)
    {
        Kind = kind;
        RefId = refId;
    }

    /// <summary>The kind of the control.</summary>
    public PresentationControlKind Kind { get; }

    /// <summary>The refId attribute, or null for a static text.</summary>
    public string? RefId { get; }

    /// <summary>The element of the policy that the control edits, or null when it has none.</summary>
    public PolicyElement? Element { get; internal init; }

    /// <summary>The label text, exactly as the XML reader returns it, or null.</summary>
    public string? Label { get; internal init; }

    /// <summary>The default text or number, or null.</summary>
    public string? DefaultValue { get; internal init; }

    /// <summary>The defaultChecked attribute, or null.</summary>
    public bool? DefaultChecked { get; internal init; }

    /// <summary>The spin attribute, or null.</summary>
    public bool? Spin { get; internal init; }

    /// <summary>The spinStep attribute, or null.</summary>
    public ulong? SpinStep { get; internal init; }

    /// <summary>The noSort attribute, or null.</summary>
    public bool? NoSort { get; internal init; }

    /// <summary>The defaultItem attribute, or null.</summary>
    public ulong? DefaultItem { get; internal init; }

    /// <summary>The required attribute, or null.</summary>
    public bool? Required { get; internal init; }

    /// <summary>The defaultHeight attribute, or null.</summary>
    public ulong? DefaultHeight { get; internal init; }

    /// <summary>The suggestions of a combo box, in document order.</summary>
    public IReadOnlyList<string> Suggestions { get; internal init; } = [];

    internal PresentationControl WithElement(PolicyElement? element) => new(Kind, RefId)
    {
        Element = element,
        Label = Label,
        DefaultValue = DefaultValue,
        DefaultChecked = DefaultChecked,
        Spin = Spin,
        SpinStep = SpinStep,
        NoSort = NoSort,
        DefaultItem = DefaultItem,
        Required = Required,
        DefaultHeight = DefaultHeight,
        Suggestions = Suggestions,
    };
}

/// <summary>The controls of a policy setting page, in the order of the ADML.</summary>
public sealed class PolicyPresentation
{
    internal PolicyPresentation(string id, IReadOnlyList<PresentationControl> controls)
    {
        Id = id;
        Controls = controls;
    }

    /// <summary>The presentation id.</summary>
    public string Id { get; }

    /// <summary>The controls in document order.</summary>
    public IReadOnlyList<PresentationControl> Controls { get; }
}
