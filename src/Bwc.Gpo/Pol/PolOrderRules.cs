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
    public static readonly string[] PerValueInstructionPrefixes = [PolFormat.DeletePrefix, "**soft."];

    /// <summary>
    /// Verify against oracle: the bytes written for a document with no records. The choice is the bare header; classic
    /// might write nothing or delete the file instead.
    /// </summary>
    public static byte[] EmptyDocumentBytes() => PolFormat.Header.ToArray();

    /// <summary>Verify against oracle: overwriting an existing value keeps its position in the key.</summary>
    public static void ReplaceInPlace(LinkedListNode<PolRecord> existing, PolRecord replacement) => existing.Value = replacement;

    /// <summary>
    /// Verify against oracle: a new value, including one created again after it was deleted, goes to the end of its key.
    /// </summary>
    public static LinkedListNode<PolRecord> AppendToKey(LinkedList<PolRecord> recordsOfKey, PolRecord record) =>
        recordsOfKey.AddLast(record);

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
