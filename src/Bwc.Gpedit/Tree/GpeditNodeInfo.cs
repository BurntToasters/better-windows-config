using Bwc.Gpo.Store;
using Bwc.Policy.Admx;

namespace Bwc.Gpedit.Tree;

/// <summary>The kinds of node of the console tree.</summary>
internal enum GpeditNodeKind
{
    /// <summary>Local Computer Policy.</summary>
    Root,

    /// <summary>Computer Configuration or User Configuration.</summary>
    Configuration,

    /// <summary>Software Settings. A placeholder: no extension of it is part of this release.</summary>
    SoftwareSettings,

    /// <summary>Windows Settings. Its children are placeholders.</summary>
    WindowsSettings,

    /// <summary>A child of Windows Settings. A placeholder.</summary>
    WindowsSettingsChild,

    /// <summary>Administrative Templates.</summary>
    AdministrativeTemplates,

    /// <summary>A category of the templates.</summary>
    Category,

    /// <summary>The All Settings node, the last child of Administrative Templates.</summary>
    AllSettings,
}

/// <summary>
/// The tool's own model of a tree node, carried in <c>TreeNode.Tag</c>: what the node is, the side it belongs to and, for a
/// category, the category. <c>DenseTree</c> never reads it.
/// </summary>
internal sealed class GpeditNodeInfo
{
    internal GpeditNodeInfo(GpeditNodeKind kind, GpoSide? side = null, PolicyCategory? category = null)
    {
        Kind = kind;
        Side = side;
        Category = category;
    }

    /// <summary>What the node is.</summary>
    public GpeditNodeKind Kind { get; }

    /// <summary>The side, or null for the root.</summary>
    public GpoSide? Side { get; }

    /// <summary>The category of a Category node.</summary>
    public PolicyCategory? Category { get; }

    /// <summary>The resource key of the description of a navigation node, or null.</summary>
    public string? DescriptionKey { get; init; }

    /// <summary>True for a node that opens the "not available yet" page.</summary>
    public bool IsPlaceholder => Kind is GpeditNodeKind.SoftwareSettings or GpeditNodeKind.WindowsSettingsChild;

    /// <summary>True for a node whose result list shows policy rows (a category or All Settings).</summary>
    public bool ListsPolicies => Kind is GpeditNodeKind.Category or GpeditNodeKind.AllSettings;
}
