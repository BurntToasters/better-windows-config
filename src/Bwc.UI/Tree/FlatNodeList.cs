using System.Collections.Specialized;

namespace Bwc.UI.Tree;

/// <summary>
/// The flattened list of visible tree nodes. It is a plain List so a chunk insert or collapse is one
/// range operation, and it raises one notification per block so ItemsRepeater re-lays out once.
/// Every mutation goes through the Notify methods, which also keep a node-to-row index current so
/// <see cref="IndexOfNode"/> is a dictionary lookup instead of a scan of the whole list.
/// </summary>
internal sealed class FlatNodeList : List<TreeNode>, INotifyCollectionChanged
{
    private readonly Dictionary<TreeNode, int> _index = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>The row index of a visible node, or -1 when the node is not visible.</summary>
    public int IndexOfNode(TreeNode? node) =>
        node is not null && _index.TryGetValue(node, out var index) ? index : -1;

    public void InsertRangeNotify(int index, IReadOnlyList<TreeNode> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var payload = new TreeNode[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            payload[i] = items[i];
        }

        InsertRange(index, payload);
        ReindexFrom(index);
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, payload, index));
    }

    public void RemoveRangeNotify(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var removed = GetRange(index, count);
        RemoveRange(index, count);
        foreach (var node in removed)
        {
            _index.Remove(node);
        }

        ReindexFrom(index);
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void ResetNotify(IEnumerable<TreeNode> rows)
    {
        Clear();
        _index.Clear();
        AddRange(rows);
        ReindexFrom(0);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void ReindexFrom(int start)
    {
        for (var i = Math.Max(0, start); i < Count; i++)
        {
            _index[this[i]] = i;
        }
    }
}
