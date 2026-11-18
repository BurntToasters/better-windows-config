using System.Globalization;
using Bwc.Gpedit.Policy;
using Bwc.Gpedit.Tree;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using Bwc.UI.Console.Extended;
using Bwc.UI.Console.List;
using Bwc.UI.Console.Shell;
using Bwc.UI.Icons;
using Bwc.UI.Tree;

namespace Bwc.Gpedit.List;

/// <summary>The two column sets of the result list: names only, or Setting, State and Comment.</summary>
internal enum GpeditListLayout
{
    /// <summary>One Name column (the root, the configuration nodes, Windows Settings).</summary>
    Navigation,

    /// <summary>Setting, State and Comment (a category and All Settings).</summary>
    Settings,
}

/// <summary>What a result row stands for.</summary>
internal enum GpeditRowKind
{
    /// <summary>A child node (a folder row).</summary>
    Folder,

    /// <summary>A policy.</summary>
    Policy,
}

/// <summary>The tag of a result row: its kind, and the policy or the child tree node it opens.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Side">The side of the listed node.</param>
/// <param name="Policy">The policy of a policy row.</param>
/// <param name="Target">The child tree node of a folder row.</param>
internal sealed record GpeditRowTag(GpeditRowKind Kind, GpoSide Side, PolicyDefinition? Policy, TreeNode? Target);

/// <summary>What the result pane shows for one tree node.</summary>
internal sealed class GpeditListContent
{
    /// <summary>True when the result pane shows the "not available yet" page instead of the list.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>The column set of the list.</summary>
    public GpeditListLayout Layout { get; init; }

    /// <summary>The rows in display order.</summary>
    public IReadOnlyList<ConsoleListRow> Rows { get; init; } = [];

    /// <summary>The number of folder rows.</summary>
    public int FolderCount { get; init; }

    /// <summary>The number of policy rows.</summary>
    public int PolicyCount { get; init; }

    /// <summary>The status bar text of the node.</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>The description of the node itself (shown when no row is selected).</summary>
    public required ConsoleDescriptionModel Description { get; init; }
}

/// <summary>
/// Builds what the result pane shows for a tree node, and the description of a selected row: folder rows first (the child
/// nodes), then the policies of a category with their State and Comment (docs/failure-modes/gpedit-tree-list.md, sections
/// 6 to 9). Pure: it reads the model and the tree, never the disk.
/// </summary>
internal static class GpeditListBuilder
{
    /// <summary>The UI Automation name of a row: a policy is "&lt;display name&gt; setting", a folder its plain name.</summary>
    internal static string RowName(ConsoleListRow row) =>
        row.Tag is GpeditRowTag { Kind: GpeditRowKind.Policy }
            ? string.Format(CultureInfo.CurrentCulture, GpeditStrings.Tree("GpeditPolicyRowName"), row.Text)
            : row.Text;

    /// <summary>Builds the content of a node. <paramref name="model"/> is null while the templates are still loading.</summary>
    internal static GpeditListContent Build(TreeNode node, GpeditModel? model, GpeditTreeSource source)
    {
        GpeditNodeInfo? info = GpeditTreeSource.InfoOf(node);
        if (info is null)
        {
            return Empty(node);
        }

        if (info.IsPlaceholder)
        {
            return new GpeditListContent
            {
                IsPlaceholder = true,
                Description = NodeDescription(node, info),
            };
        }

        GpoSide side = info.Side ?? GpoSide.Machine;
        switch (info.Kind)
        {
            case GpeditNodeKind.Root:
            case GpeditNodeKind.Configuration:
            case GpeditNodeKind.WindowsSettings:
                return Navigation(node, info, side);
            case GpeditNodeKind.AdministrativeTemplates:
            case GpeditNodeKind.Category:
            case GpeditNodeKind.AllSettings:
                return model is null ? Loading(node, info) : Settings(node, info, side, model, source);
            default:
                return Empty(node);
        }
    }

    /// <summary>The description of the selected row, or null when it has none.</summary>
    internal static ConsoleDescriptionModel? DescribeRow(ConsoleListRow row)
    {
        if (row.Tag is not GpeditRowTag tag)
        {
            return null;
        }

        if (tag is { Kind: GpeditRowKind.Policy, Policy: { } policy })
        {
            return new ConsoleDescriptionModel(
                policy.DisplayName,
                [
                    new ConsoleDescriptionSection("requirements", ConsoleStrings.Get("ConsoleDescriptionRequirements"), policy.SupportedOnText),
                    new ConsoleDescriptionSection("description", ConsoleStrings.Get("ConsoleDescriptionHelpHeading"), policy.ExplainText),
                ],
                [new ConsoleDescriptionLink("edit", ConsoleStrings.Get("ConsoleExtendedEditPolicyLink"))]);
        }

        if (tag.Target is { } target && GpeditTreeSource.InfoOf(target) is { } info)
        {
            return NodeDescription(target, info);
        }

        return null;
    }

    private static GpeditListContent Navigation(TreeNode node, GpeditNodeInfo info, GpoSide side)
    {
        var rows = new List<ConsoleListRow>();
        foreach (TreeNode child in node.Children)
        {
            rows.Add(FolderRow(child, side, GpeditListLayout.Navigation));
        }

        return new GpeditListContent
        {
            Layout = GpeditListLayout.Navigation,
            Rows = rows,
            FolderCount = rows.Count,
            Description = NodeDescription(node, info),
        };
    }

    private static GpeditListContent Loading(TreeNode node, GpeditNodeInfo info) =>
        new()
        {
            Layout = GpeditListLayout.Settings,
            Description = NodeDescription(node, info),
        };

    private static GpeditListContent Settings(TreeNode node, GpeditNodeInfo info, GpoSide side, GpeditModel model, GpeditTreeSource source)
    {
        var rows = new List<ConsoleListRow>();
        int folders = 0;
        int policies = 0;
        if (info.Kind != GpeditNodeKind.AllSettings)
        {
            foreach (TreeNode child in source.EnsureChildren(node, model))
            {
                rows.Add(FolderRow(child, side, GpeditListLayout.Settings));
                folders++;
            }
        }

        IReadOnlyList<PolicyDefinition> list = info.Kind switch
        {
            GpeditNodeKind.Category when info.Category is { } category => model.PoliciesOf(category, side),
            GpeditNodeKind.AllSettings => model.AllPolicies(side),
            _ => [],
        };
        foreach (PolicyDefinition policy in list)
        {
            rows.Add(PolicyRow(policy, side, model));
            policies++;
        }

        // Classic shows a count only where policies are listed: a category or All Settings.
        string status = info.ListsPolicies
            ? string.Format(CultureInfo.CurrentCulture, GpeditStrings.Tree("GpeditStatusSettingCount"), policies)
            : string.Empty;
        return new GpeditListContent
        {
            Layout = GpeditListLayout.Settings,
            Rows = rows,
            FolderCount = folders,
            PolicyCount = policies,
            StatusText = status,
            Description = NodeDescription(node, info),
        };
    }

    private static ConsoleListRow FolderRow(TreeNode child, GpoSide side, GpeditListLayout layout)
    {
        IconReference icon = IconKind.ClosedFolder;
        var tag = new GpeditRowTag(GpeditRowKind.Folder, side, null, child);
        return layout == GpeditListLayout.Navigation
            ? new ConsoleListRow(child.Name) { SmallIcon = icon, LargeIcon = icon, Tag = tag }
            : new ConsoleListRow(child.Name, string.Empty, string.Empty) { SmallIcon = icon, LargeIcon = icon, Tag = tag };
    }

    private static ConsoleListRow PolicyRow(PolicyDefinition policy, GpoSide side, GpeditModel model)
    {
        PolicyState? state = model.StateOf(policy, side);
        string stateText = state switch
        {
            PolicyState.Enabled => GpeditStrings.Tree("GpeditStateEnabled"),
            PolicyState.Disabled => GpeditStrings.Tree("GpeditStateDisabled"),
            PolicyState.NotConfigured => GpeditStrings.Tree("GpeditStateNotConfigured"),
            _ => string.Empty,
        };
        string comment = GpeditStrings.Tree(model.HasComment(policy, side) ? "GpeditCommentYes" : "GpeditCommentNo");

        // Classic draws a policy setting with a page icon from admtmpl.dll; the glyph stands in when the module is absent.
        IconReference icon = IconReference.FromModule(ModuleIcon.ById("admtmpl.dll", 4), IconKind.StringValue);
        return new ConsoleListRow(policy.DisplayName, stateText, comment)
        {
            SmallIcon = icon,
            LargeIcon = icon,
            Tag = new GpeditRowTag(GpeditRowKind.Policy, side, policy, null),
        };
    }

    private static ConsoleDescriptionModel NodeDescription(TreeNode node, GpeditNodeInfo info)
    {
        string text;
        if (info.Category is { } category && category.ExplainText.Length > 0)
        {
            text = category.ExplainText;
        }
        else if (info.DescriptionKey is { } key)
        {
            text = GpeditStrings.Tree(key);
        }
        else
        {
            text = ConsoleStrings.Get("ConsoleDescriptionNoSelectionHint");
        }

        return new ConsoleDescriptionModel(node.Name, [new ConsoleDescriptionSection("hint", string.Empty, text)]);
    }

    private static GpeditListContent Empty(TreeNode node) =>
        new()
        {
            Layout = GpeditListLayout.Navigation,
            Description = new ConsoleDescriptionModel(node.Name),
        };
}
