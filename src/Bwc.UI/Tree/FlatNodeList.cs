using System.Collections.Specialized;

namespace Bwc.UI.Tree;

/// <summary>
/// The flattened list of visible tree nodes. It is a plain List so a chunk insert or collapse is one
/// range operation, and it raises one notification per block so ItemsRepeater re-lays out once.
/// </summary>
internal sealed class FlatNodeList : List<TreeNode>, INotifyCollectionChanged
{
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

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
        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void ResetNotify(IEnumerable<TreeNode> rows)
    {
        Clear();
        AddRange(rows);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
