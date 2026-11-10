using System;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace Bwc.Spike.Tree
{
    /// <summary>How expand and collapse tell the host that the list changed.</summary>
    public enum NotifyStyle
    {
        /// <summary>One range Add or Remove per expand or collapse, carrying the real rows (the default).</summary>
        Range,

        /// <summary>A Reset. The host rebuilds only the realized window.</summary>
        Reset,

        /// <summary>
        /// Range Add or Remove whose item payload is null placeholders of the right length. The host gets the
        /// index and count, and reads the rows from this list. Measures whether copying the real rows into the
        /// notification is what costs time.
        /// </summary>
        RangeCountOnly,
    }

    /// <summary>
    /// The flattened list of visible rows. It is a plain List so inserts and removes are range operations,
    /// and it raises one notification per expand or collapse (not one per row) so the host virtualizer
    /// re-lays out once.
    /// </summary>
    public sealed class FlatRowList : List<TreeRow>, INotifyCollectionChanged
    {
        public static NotifyStyle Style { get; set; } = NotifyStyle.Range;

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public void InsertRangeNotify(int index, List<TreeRow> items)
        {
            InsertRange(index, items);
            switch (Style)
            {
                case NotifyStyle.Reset:
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                    break;
                case NotifyStyle.RangeCountOnly:
                    CollectionChanged?.Invoke(this,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, new object?[items.Count], index));
                    break;
                default:
                    CollectionChanged?.Invoke(this,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items, index));
                    break;
            }
        }

        public void RemoveRangeNotify(int index, int count)
        {
            List<TreeRow> removed = GetRange(index, count);
            RemoveRange(index, count);
            switch (Style)
            {
                case NotifyStyle.Reset:
                    CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                    break;
                case NotifyStyle.RangeCountOnly:
                    CollectionChanged?.Invoke(this,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, new object?[count], index));
                    break;
                default:
                    CollectionChanged?.Invoke(this,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
                    break;
            }
        }
    }
}
