using System.Text;
using Bwc.Gpedit.Filter;
using Bwc.Gpedit.Policy;
using Bwc.Gpo.Store;
using Bwc.Policy.Admx;
using Bwc.UI.Icons;
using Bwc.UI.Tree;

namespace Bwc.Gpedit.Tree;

/// <summary>
/// The console tree of the Local Group Policy Editor: Local Computer Policy with Computer Configuration and User
/// Configuration, each with Software Settings, Windows Settings (with placeholder children) and Administrative Templates.
/// The skeleton exists at once; the categories of the templates are created on first need, after the model arrived
/// (docs/failure-modes/gpedit-tree-list.md, sections 3 to 5). Nothing here reads the disk.
/// </summary>
internal sealed class GpeditTreeSource
{
    private const string NodeSeparator = " > ";

    // The probe settled the computer side. The user side is not in the probe; it is kept in this one table (see the
    // failure-mode document, "uncertain").
    private static readonly string[] ComputerWindowsSettings =
    [
        "GpeditPlaceholderNameResolutionPolicy",
        "GpeditPlaceholderScriptsStartup",
        "GpeditPlaceholderDeployedPrinters",
        "GpeditPlaceholderSecuritySettings",
        "GpeditPlaceholderPolicyBasedQos",
    ];

    private static readonly string[] UserWindowsSettings =
    [
        "GpeditPlaceholderScriptsLogon",
        "GpeditPlaceholderDeployedPrinters",
        "GpeditPlaceholderSecuritySettings",
        "GpeditPlaceholderPolicyBasedQos",
    ];

    private readonly Task<GpeditModel> _modelTask;
    private readonly GpeditFilterState? _filter;

    /// <param name="modelTask">The model, when it has loaded.</param>
    /// <param name="filter">
    /// The filter of the window, or null. While its Filter On is on, a category with no matching policy in itself or below
    /// is left out of the tree. The nodes are made once, so a change of the filter needs a new source.
    /// </param>
    internal GpeditTreeSource(Task<GpeditModel> modelTask, GpeditFilterState? filter = null)
    {
        _modelTask = modelTask;
        _filter = filter;
        Root = BuildSkeleton();
    }

    /// <summary>Local Computer Policy.</summary>
    internal TreeNode Root { get; }

    /// <summary>The info of a node, or null for a node that is not one of this tree.</summary>
    internal static GpeditNodeInfo? InfoOf(TreeNode? node) => node?.Tag as GpeditNodeInfo;

    /// <summary>The names from the root to the node, joined with " &gt; ". Empty for null.</summary>
    internal static string PathOf(TreeNode? node)
    {
        var names = new List<string>();
        for (TreeNode? current = node; current is not null; current = current.Parent)
        {
            names.Add(current.Name);
        }

        names.Reverse();
        return string.Join(NodeSeparator, names);
    }

    /// <summary>The names from the root to the node. Empty for null.</summary>
    internal static List<string> NamesOf(TreeNode? node)
    {
        var names = new List<string>();
        for (TreeNode? current = node; current is not null; current = current.Parent)
        {
            names.Add(current.Name);
        }

        names.Reverse();
        return names;
    }

    /// <summary>
    /// Finds the node with these names from the root, creating the category nodes on the way. When a name does not exist
    /// the nearest ancestor that does is returned, and <paramref name="matched"/> is smaller than the number of names.
    /// Null when even the root does not match.
    /// </summary>
    internal TreeNode? Resolve(IReadOnlyList<string> names, GpeditModel model, out int matched)
    {
        matched = 0;
        if (names.Count == 0 || !string.Equals(names[0], Root.Name, StringComparison.Ordinal))
        {
            return null;
        }

        TreeNode node = Root;
        matched = 1;
        while (matched < names.Count)
        {
            TreeNode? next = null;
            foreach (TreeNode child in EnsureChildren(node, model))
            {
                if (string.Equals(child.Name, names[matched], StringComparison.Ordinal))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                break;
            }

            node = next;
            matched++;
        }

        return node;
    }

    /// <summary>
    /// The children of a node, creating the category nodes of Administrative Templates and of a category the first time.
    /// A node that has children already (the skeleton, or a category built before) returns them unchanged.
    /// </summary>
    internal IReadOnlyList<TreeNode> EnsureChildren(TreeNode node, GpeditModel model)
    {
        if (node.Children.Count > 0 || InfoOf(node) is not { Side: { } side } info)
        {
            return node.Children;
        }

        switch (info.Kind)
        {
            case GpeditNodeKind.AdministrativeTemplates:
                foreach (PolicyCategory category in ChildCategories(model, null, side))
                {
                    _ = node.AddChild(CreateCategoryNode(category, side, model));
                }

                _ = node.AddChild(CreateNode("GpeditNodeAllSettings", new GpeditNodeInfo(GpeditNodeKind.AllSettings, side), IconKind.ClosedFolder));
                break;
            case GpeditNodeKind.Category when info.Category is { } parent:
                foreach (PolicyCategory category in ChildCategories(model, parent, side))
                {
                    _ = node.AddChild(CreateCategoryNode(category, side, model));
                }

                break;
        }

        return node.Children;
    }

    /// <summary>
    /// The whole tree as indented text, one node per line, two spaces per level, every node expanded. E2E only: it walks the
    /// same code that builds the nodes when the user expands them.
    /// </summary>
    internal string DescribeShape(GpeditModel model)
    {
        var text = new StringBuilder();
        Describe(Root, 0, model, text);
        return text.ToString().TrimEnd('\n');
    }

    private void Describe(TreeNode node, int depth, GpeditModel model, StringBuilder text)
    {
        _ = text.Append(' ', depth * 2).Append(node.Name).Append('\n');
        foreach (TreeNode child in EnsureChildren(node, model))
        {
            Describe(child, depth + 1, model, text);
        }
    }

    /// <summary>The child categories of a category, less the ones the filter hides.</summary>
    private IReadOnlyList<PolicyCategory> ChildCategories(GpeditModel model, PolicyCategory? parent, GpoSide side)
    {
        IReadOnlyList<PolicyCategory> all = model.ChildCategories(parent, side);
        return _filter is not { IsOn: true } ? all : all.Where(category => _filter.Shows(category, side)).ToList();
    }

    private TreeNode CreateCategoryNode(PolicyCategory category, GpoSide side, GpeditModel model)
    {
        bool hasChildren = ChildCategories(model, category, side).Count > 0;
        var node = new TreeNode(category.DisplayName, null, hasChildren, LoadChildrenAsync)
        {
            Icon = IconKind.ClosedFolder,
            Tag = new GpeditNodeInfo(GpeditNodeKind.Category, side, category),
            CanRename = false,
        };
        return node;
    }

    private TreeNode BuildSkeleton()
    {
        TreeNode root = CreateNode("GpeditNodeRoot", new GpeditNodeInfo(GpeditNodeKind.Root), IconKind.Computer);
        root.IsExpanded = true;
        foreach (GpoSide side in new[] { GpoSide.Machine, GpoSide.User })
        {
            bool machine = side == GpoSide.Machine;
            TreeNode configuration = CreateNode(
                machine ? "GpeditNodeComputerConfiguration" : "GpeditNodeUserConfiguration",
                new GpeditNodeInfo(GpeditNodeKind.Configuration, side)
                {
                    DescriptionKey = machine ? "GpeditDescriptionComputerConfiguration" : "GpeditDescriptionUserConfiguration",
                },
                machine ? IconKind.Computer : IconKind.ClosedFolder);
            configuration.IsExpanded = true;

            _ = configuration.AddChild(CreateNode(
                "GpeditNodeSoftwareSettings",
                new GpeditNodeInfo(GpeditNodeKind.SoftwareSettings, side)
                {
                    DescriptionKey = machine ? "GpeditDescriptionSoftwareSettingsComputer" : "GpeditDescriptionSoftwareSettingsUser",
                },
                IconKind.ClosedFolder));

            TreeNode windows = CreateNode(
                "GpeditNodeWindowsSettings",
                new GpeditNodeInfo(GpeditNodeKind.WindowsSettings, side)
                {
                    DescriptionKey = machine ? "GpeditDescriptionWindowsSettingsComputer" : "GpeditDescriptionWindowsSettingsUser",
                },
                IconKind.ClosedFolder);
            foreach (string key in machine ? ComputerWindowsSettings : UserWindowsSettings)
            {
                _ = windows.AddChild(CreateNode(key, new GpeditNodeInfo(GpeditNodeKind.WindowsSettingsChild, side), IconKind.ClosedFolder));
            }

            _ = configuration.AddChild(windows);

            // Administrative Templates always shows an expander: its children need the model, which the loader awaits.
            var admin = new TreeNode(
                GpeditStrings.Tree("GpeditNodeAdministrativeTemplates"),
                null,
                hasChildren: true,
                loader: LoadChildrenAsync)
            {
                Icon = IconKind.ClosedFolder,
                Tag = new GpeditNodeInfo(GpeditNodeKind.AdministrativeTemplates, side)
                {
                    DescriptionKey = "GpeditDescriptionAdministrativeTemplates",
                },
                CanRename = false,
            };
            _ = configuration.AddChild(admin);
            _ = root.AddChild(configuration);
        }

        return root;
    }

    private async Task<IReadOnlyList<TreeNode>> LoadChildrenAsync(TreeNode node, CancellationToken cancellationToken)
    {
        GpeditModel model = await _modelTask.WaitAsync(cancellationToken).ConfigureAwait(true);
        return EnsureChildren(node, model);
    }

    private static TreeNode CreateNode(string nameKey, GpeditNodeInfo info, IconReference icon)
    {
        return new TreeNode(GpeditStrings.Tree(nameKey))
        {
            Icon = icon,
            Tag = info,
            CanRename = false,
        };
    }
}
