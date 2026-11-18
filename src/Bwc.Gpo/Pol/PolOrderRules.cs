namespace Bwc.Gpo.Pol;

/// <summary>
/// Every record-order and file-shape rule of the model, in one place. Each rule here is *verify against oracle*
/// (docs/contracts/policy-engine.md, "Ordered model"); a correction from the classic probe results changes this file.
/// </summary>
internal static class PolOrderRules
{
    /// <summary>
    /// Verify against oracle: the order keys are written in. A tree walk: key paths split on backslash, each segment
    /// compared ordinally by uppercase, a key before its descendants. Ties between distinct spellings that compare equal
    /// are broken ordinally so the output is deterministic. The regedit display comparer (NLS, user locale) is not used
    /// because saved bytes must not depend on the locale.
    /// </summary>
    public static IComparer<string> KeyComparer { get; } = new RegistryKeyComparer();

    /// <summary>
    /// Verify against oracle: value-name prefixes of the instructions that belong to one value and are removed with it
    /// by <see cref="PolDocument.RemoveInstructions"/>.
    /// </summary>
    public static readonly string[] PerValueInstructionPrefixes = [PolFormat.DeletePrefix, PolFormat.SoftPrefix];

    /// <summary>
    /// Verify against oracle: the bytes written for a document with no records. The choice is the bare header; classic
    /// might write nothing or delete the file instead.
    /// </summary>
    public static byte[] EmptyDocumentBytes() => PolFormat.Header.ToArray();

    /// <summary>
    /// Classic probe (2026-11-18): setting a value to different data removes its record and appends the new one at the
    /// end of its key. Setting the same type and data changes nothing, see <see cref="IsSameContent"/>.
    /// </summary>
    public static LinkedListNode<PolRecord> ReplaceByAppend(
        LinkedList<PolRecord> recordsOfKey,
        LinkedListNode<PolRecord> existing,
        PolRecord replacement)
    {
        recordsOfKey.Remove(existing);
        return recordsOfKey.AddLast(replacement);
    }

    /// <summary>Classic probe (2026-11-18): a set with the same type and data bytes is not a change.</summary>
    public static bool IsSameContent(PolRecord existing, uint type, ReadOnlySpan<byte> data) =>
        existing.Type == type && existing.Data.Span.SequenceEqual(data);

    /// <summary>
    /// A new value, including one created again after it was deleted, goes to the end of its key.
    /// </summary>
    public static LinkedListNode<PolRecord> AppendToKey(LinkedList<PolRecord> recordsOfKey, PolRecord record) =>
        recordsOfKey.AddLast(record);

    /// <summary>
    /// Verify against oracle: <c>**delvals.</c> goes to the front of its key, before every other record. A record in front of
    /// it would be deleted when the file is applied, so the front keeps every existing record meaningful. A record that
    /// already exists is moved to the front too (the old node is removed first). Classic may place it right before the first
    /// list entry instead.
    /// </summary>
    public static LinkedListNode<PolRecord> PlaceDeleteValues(
        LinkedList<PolRecord> recordsOfKey,
        LinkedListNode<PolRecord>? existing,
        PolRecord record)
    {
        if (existing is not null)
        {
            recordsOfKey.Remove(existing);
        }

        return recordsOfKey.AddFirst(record);
    }

    private sealed class RegistryKeyComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var i = 0;
            var j = 0;
            while (true)
            {
                var endX = x.IndexOf('\\', i);
                if (endX < 0)
                {
                    endX = x.Length;
                }

                var endY = y.IndexOf('\\', j);
                if (endY < 0)
                {
                    endY = y.Length;
                }

                var result = x.AsSpan(i, endX - i).CompareTo(y.AsSpan(j, endY - j), StringComparison.OrdinalIgnoreCase);
                if (result != 0)
                {
                    return result;
                }

                var xDone = endX >= x.Length;
                var yDone = endY >= y.Length;
                if (xDone || yDone)
                {
                    if (xDone && yDone)
                    {
                        return string.CompareOrdinal(x, y);
                    }

                    return xDone ? -1 : 1;
                }

                i = endX + 1;
                j = endY + 1;
            }
        }
    }
}
