using System.Text.Json.Nodes;

namespace Bwc.Oracle.Capture.Gpedit.Driver;

// A node that the filter hides is a result, not a failure (failure mode 42 of the driver document). The operations that name a node select it
// first; when that fails because the node is missing under a parent that exists and Filter On is checked, they record that the node is hidden
// and go on from the nearest visible ancestor, or (readList) from where the selection is.
internal sealed partial class GpeditDriver
{
    // Whether View > Filter On is checked, read from the menu without activating anything; null when it cannot be read.
    internal bool? ReadFilterOn()
    {
        try
        {
            return MenuItemInfo("View", FilterOnItem)["checked"]!.GetValue<bool>();
        }
        catch (DriverException ex) when (!ex.Fatal)
        {
            return null;
        }
    }

    // The node must be the selected one; it is selected first when it is not (as EnsureNode of the runner always did). Null when the node is
    // selected. When selecting fails because the node is hidden by a filter, the description of that (HiddenNode.Describe) is returned; with
    // selectAncestor the nearest ancestor that can be selected is selected first and named as selectedInstead. Any other failure is thrown.
    internal JsonObject? EnsureNodeOrHidden(string root, IReadOnlyList<string> path, JsonObject entry, bool selectAncestor)
    {
        if (IsSelected(root, path))
        {
            entry["node"] = "already selected: " + root + "/" + string.Join("/", path);
            return null;
        }

        try
        {
            entry["node"] = Phases.Run("nodeSelection", () => SelectNode(root, path));
            return null;
        }
        catch (DriverException ex) when (ex.Gap is { } gap)
        {
            bool? filterOn = Phases.Run("readFilterOn", ReadFilterOn);
            if (!HiddenNode.IsHidden(gap, filterOn))
            {
                throw;
            }

            JsonObject hidden = HiddenNode.Describe(gap, SelectedBelowRoot());
            if (selectAncestor)
            {
                IReadOnlyList<string>? chosen = Phases.Run("selectAncestor", () => SelectNearestVisible(root, gap));
                if (chosen is null)
                {
                    throw;
                }

                hidden["selectedInstead"] = HiddenNode.Names(chosen);
            }

            entry["node"] = "hidden by the filter: " + root + "/" + string.Join("/", path);
            return hidden;
        }
    }

    // The selected node below the root item (the names after Local Computer Policy), or null when nothing is selected or it cannot be read.
    private List<string>? SelectedBelowRoot()
    {
        try
        {
            return SelectedPath() is { } selected ? selected.Skip(1).ToList() : null;
        }
        catch (DriverException)
        {
            return null;
        }
    }

    // Selects the parent of the hidden node, or the nearest ancestor of it that exists; the one that is selected afterwards, null when none could be.
    private IReadOnlyList<string>? SelectNearestVisible(string root, TreeGap gap)
    {
        foreach (IReadOnlyList<string> ancestor in HiddenNode.AncestorsNearestFirst(gap))
        {
            if (IsSelected(root, ancestor))
            {
                return ancestor;
            }

            try
            {
                SelectNode(root, ancestor);
                return ancestor;
            }
            catch (DriverException inner) when (inner.Gap is not null)
            {
                // This ancestor is hidden too: one level up.
            }
        }

        return null;
    }
}
