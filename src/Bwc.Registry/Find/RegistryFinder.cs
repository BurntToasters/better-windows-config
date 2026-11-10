using System.Globalization;
using System.Text;

namespace Bwc.Registry;

/// <summary>
/// Classic regedit Find over the real registry. The walk is depth-first pre-order with an
/// explicit stack, checks the key name, then each value name and its data in display order, and
/// continues across the configured roots without wrapping. Reads use <see cref="RegistryReader"/>
/// only and may run on any thread.
/// </summary>
public sealed class RegistryFinder
{
    private readonly RegistryReader _reader;

    public RegistryFinder(RegistryReader? reader = null)
    {
        _reader = reader ?? new RegistryReader();
    }

    /// <summary>
    /// Returns the next hit after <paramref name="after"/>, or null when the configured roots
    /// are exhausted. The search never wraps.
    /// </summary>
    public RegistryFindHit? Find(
        RegistryFindQuery query,
        RegistryFindCursor? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        return new Walk(query, after, _reader, cancellationToken).Execute();
    }

    /// <summary>F3: repeats the last search after the current hit.</summary>
    public RegistryFindHit? FindNext(
        RegistryFindQuery query,
        RegistryFindHit lastHit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lastHit);

        return Find(query, RegistryFindCursor.FromHit(lastHit), cancellationToken);
    }

    private sealed class Walk
    {
        private const StringComparison MatchComparison = StringComparison.CurrentCultureIgnoreCase;

        private static readonly StringComparer SortComparer =
            StringComparer.CurrentCultureIgnoreCase;

        private readonly RegistryFindQuery _query;
        private readonly RegistryFindCursor? _cursor;
        private readonly RegistryReader _reader;
        private readonly CancellationToken _cancellationToken;
        private bool _pastCursor;

        internal Walk(
            RegistryFindQuery query,
            RegistryFindCursor? cursor,
            RegistryReader reader,
            CancellationToken cancellationToken)
        {
            _query = query;
            _cursor = cursor;
            _reader = reader;
            _cancellationToken = cancellationToken;
        }

        internal RegistryFindHit? Execute()
        {
            _pastCursor = _cursor is null;

            if (_cursor is null)
            {
                return WalkStack(CreateRootStack());
            }

            if (_query.SearchInSubtree)
            {
                if (TryCreateContinuationStack(_cursor, out Stack<Frame> continuation))
                {
                    return WalkStack(continuation);
                }

                // The cursor key's continuation could not be built. Scan from the first root
                // so an unreadable ancestor cannot abort the search. A cursor that is never
                // reached returns null instead of wrapping.
                return WalkStack(CreateRootStack());
            }

            // OQ-10 is provisional: with Search in subtree unchecked, only the starting key is
            // in scope. Predefined roots have no values, so a cursor is normally required.
            if (!IsInsideAnyRoot(_cursor.KeyPath))
            {
                return null;
            }

            var singleKey = new Stack<Frame>();
            singleKey.Push(new Frame(_cursor.KeyPath));
            return WalkStack(singleKey);
        }

        private Stack<Frame> CreateRootStack()
        {
            var stack = new Stack<Frame>(_query.Roots.Count);

            for (int index = _query.Roots.Count - 1; index >= 0; index -= 1)
            {
                stack.Push(new Frame(_query.Roots[index]));
            }

            return stack;
        }

        private bool TryCreateContinuationStack(
            RegistryFindCursor cursor,
            out Stack<Frame> stack)
        {
            stack = new Stack<Frame>();

            int rootIndex = FindRootIndex(cursor.KeyPath);
            if (rootIndex < 0)
            {
                return false;
            }

            // Each level holds the siblings that follow the cursor branch at that level. Level
            // zero is the cursor key's own next siblings; the last level is the next siblings of
            // the search root's child. Ancestors themselves were already considered before the
            // cursor and are not pushed again.
            var continuationLevels = new List<List<RegistryPath>>();
            RegistryPath root = _query.Roots[rootIndex];
            RegistryPath current = cursor.KeyPath;

            while (!current.Equals(root))
            {
                RegistryPath parent = current.Parent!;
                IReadOnlyList<string> childNames;

                _cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    childNames = _reader.EnumerateSubKeyNames(parent, _cancellationToken);
                }
                catch (RegistryOperationException)
                {
                    return false;
                }

                List<string> sortedNames = childNames
                    .OrderBy(name => name, SortComparer)
                    .ToList();
                string leaf = current.Components[^1];
                int index = sortedNames.FindIndex(
                    name => string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase));

                if (index < 0)
                {
                    // The cursor key was deleted, renamed, or otherwise cannot be placed in
                    // pre-order. The caller falls back to a root walk and will stop at null if
                    // the cursor is never reached.
                    return false;
                }

                var laterSiblings = new List<RegistryPath>(sortedNames.Count - index - 1);

                for (int siblingIndex = index + 1; siblingIndex < sortedNames.Count; siblingIndex += 1)
                {
                    RegistryPath? sibling = TryAppend(parent, sortedNames[siblingIndex]);
                    if (sibling is not null)
                    {
                        laterSiblings.Add(sibling);
                    }
                }

                continuationLevels.Add(laterSiblings);
                current = parent;
            }

            for (int index = _query.Roots.Count - 1; index > rootIndex; index -= 1)
            {
                stack.Push(new Frame(_query.Roots[index]));
            }

            for (int level = continuationLevels.Count - 1; level >= 0; level -= 1)
            {
                List<RegistryPath> laterSiblings = continuationLevels[level];

                for (int index = laterSiblings.Count - 1; index >= 0; index -= 1)
                {
                    stack.Push(new Frame(laterSiblings[index]));
                }
            }

            stack.Push(new Frame(cursor.KeyPath));
            return true;
        }

        private RegistryFindHit? WalkStack(Stack<Frame> stack)
        {
            while (stack.Count > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                Frame frame = stack.Peek();

                if (!frame.KeyProcessed)
                {
                    frame.KeyProcessed = true;

                    RegistryFindHit? keyHit = CheckKeyName(frame.Path);
                    if (keyHit is not null)
                    {
                        return keyHit;
                    }

                    RegistryFindHit? valueHit = CheckValues(frame.Path);
                    if (valueHit is not null)
                    {
                        return valueHit;
                    }
                }

                if (!frame.ChildrenEnumerated)
                {
                    frame.ChildrenEnumerated = true;
                    frame.ChildNames = EnumerateChildren(frame.Path);
                }

                IReadOnlyList<string>? childNames = frame.ChildNames;

                if (childNames is not null && frame.NextChild < childNames.Count)
                {
                    string childName = childNames[frame.NextChild];
                    frame.NextChild += 1;

                    RegistryPath? childPath = TryAppend(frame.Path, childName);
                    if (childPath is not null)
                    {
                        stack.Push(new Frame(childPath));
                    }

                    continue;
                }

                stack.Pop();
            }

            return null;
        }

        private RegistryFindHit? CheckKeyName(RegistryPath path)
        {
            if (IsCursorPosition(path, null, RegistryFindCursorPhase.AfterKeyName))
            {
                _pastCursor = true;
                return null;
            }

            if (!_pastCursor || !_query.LookAtKeys || path.IsRoot)
            {
                return null;
            }

            string keyName = path.Components[^1];

            return Matches(keyName)
                ? new RegistryFindHit(path, null, RegistryFindHitKind.KeyName, keyName)
                : null;
        }

        private RegistryFindHit? CheckValues(RegistryPath path)
        {
            bool cursorNeedsValues = !_pastCursor
                && _cursor is not null
                && _cursor.ValueName is not null
                && _cursor.KeyPath.Equals(path);

            if (!_query.LookAtValues && !_query.LookAtData && !cursorNeedsValues)
            {
                return null;
            }

            _cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<RegistryValueInfo> values;

            try
            {
                values = _reader.EnumerateValues(path, _cancellationToken);
            }
            catch (RegistryOperationException)
            {
                // An unreadable key does not abort the search. Its name may already have been a
                // hit; its values and children are simply unavailable.
                return null;
            }

            foreach (RegistryValueInfo value in values
                .OrderBy(item => item.Name.Length == 0 ? 0 : 1)
                .ThenBy(item => item.Name, SortComparer))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (IsCursorPosition(path, value.Name, RegistryFindCursorPhase.AfterValueName))
                {
                    _pastCursor = true;
                }
                else if (_pastCursor && _query.LookAtValues && Matches(value.Name))
                {
                    return new RegistryFindHit(
                        path,
                        value.Name,
                        RegistryFindHitKind.ValueName,
                        value.Name);
                }

                if (IsCursorPosition(path, value.Name, RegistryFindCursorPhase.AfterValueData))
                {
                    _pastCursor = true;
                    continue;
                }

                if (!_pastCursor || !_query.LookAtData)
                {
                    continue;
                }

                if (TryMatchData(value, out string matchedText))
                {
                    return new RegistryFindHit(
                        path,
                        value.Name,
                        RegistryFindHitKind.ValueData,
                        matchedText);
                }
            }

            return null;
        }

        private bool IsCursorPosition(
            RegistryPath path,
            string? valueName,
            RegistryFindCursorPhase phase)
        {
            if (_pastCursor
                || _cursor is null
                || _cursor.Phase != phase
                || !_cursor.KeyPath.Equals(path))
            {
                return false;
            }

            if (valueName is null)
            {
                return _cursor.ValueName is null;
            }

            return _cursor.ValueName is not null
                && string.Equals(_cursor.ValueName, valueName, StringComparison.OrdinalIgnoreCase);
        }

        private IReadOnlyList<string> EnumerateChildren(RegistryPath path)
        {
            if (!_query.SearchInSubtree)
            {
                return Array.Empty<string>();
            }

            _cancellationToken.ThrowIfCancellationRequested();

            try
            {
                IReadOnlyList<string> names = _reader.EnumerateSubKeyNames(path, _cancellationToken);
                return names.OrderBy(name => name, SortComparer).ToArray();
            }
            catch (RegistryOperationException)
            {
                return Array.Empty<string>();
            }
        }

        private bool TryMatchData(RegistryValueInfo value, out string matchedText)
        {
            matchedText = string.Empty;

            if (value.DataLength == 0)
            {
                // The display placeholders for empty data are UI text, not stored data.
                return false;
            }

            switch (value.Type)
            {
                case (uint)RegistryValueType.String:
                case (uint)RegistryValueType.ExpandString:
                    return TryMatchText(RegistryValueCodec.DecodeString(value.Data), out matchedText);

                case (uint)RegistryValueType.MultiString:
                    return TryMatchMultiString(value.Data, out matchedText);

                case (uint)RegistryValueType.Dword:
                    if (!RegistryValueCodec.IsValidDword(value.Data))
                    {
                        return TryMatchText(FormatHex(value.Data), out matchedText);
                    }

                    return TryMatchNumber(
                        RegistryValueCodec.DecodeDword(value.Data),
                        out matchedText);

                case (uint)RegistryValueType.Qword:
                    if (!RegistryValueCodec.IsValidQword(value.Data))
                    {
                        return TryMatchText(FormatHex(value.Data), out matchedText);
                    }

                    return TryMatchNumber(
                        RegistryValueCodec.DecodeQword(value.Data),
                        out matchedText);

                case (uint)RegistryValueType.Link:
                    // Provisional: the display layer treats a link as text, but the find
                    // contract has no oracle capture for it. Search the raw bytes as hex.
                    return TryMatchText(FormatHex(value.Data), out matchedText);

                default:
                    return TryMatchText(
                        RegistryValueDisplay.FormatData(value, int.MaxValue),
                        out matchedText);
            }
        }

        private bool TryMatchMultiString(byte[] data, out string matchedText)
        {
            matchedText = string.Empty;
            MultiStringDecodeResult decoded = RegistryValueCodec.DecodeMultiString(data);
            var strings = decoded.Strings;

            foreach (string item in strings)
            {
                if (TryMatchText(item, out matchedText))
                {
                    return true;
                }
            }

            if (strings.Count > 1)
            {
                string joined = string.Join(' ', strings);
                if (TryMatchText(joined, out matchedText))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryMatchNumber(ulong number, out string matchedText)
        {
            string decimalText = number.ToString(CultureInfo.InvariantCulture);

            if (TryMatchText(decimalText, out matchedText))
            {
                return true;
            }

            string hexText = "0x" + number.ToString("X", CultureInfo.InvariantCulture);
            return TryMatchText(hexText, out matchedText);
        }

        private bool TryMatchText(string text, out string matchedText)
        {
            if (Matches(text))
            {
                matchedText = text;
                return true;
            }

            matchedText = string.Empty;
            return false;
        }

        private bool Matches(string text) => _query.MatchWholeStringOnly
            ? string.Equals(text, _query.SearchText, MatchComparison)
            : text.Contains(_query.SearchText, MatchComparison);

        private int FindRootIndex(RegistryPath path)
        {
            for (int index = 0; index < _query.Roots.Count; index += 1)
            {
                RegistryPath root = _query.Roots[index];

                if (path.Equals(root) || path.IsDescendantOf(root))
                {
                    return index;
                }
            }

            return -1;
        }

        private bool IsInsideAnyRoot(RegistryPath path) => FindRootIndex(path) >= 0;

        private static RegistryPath? TryAppend(RegistryPath parent, string component)
        {
            try
            {
                return parent.Append(component);
            }
            catch (ArgumentException)
            {
                // A name that RegistryPath cannot represent, such as "." or "..", is skipped
                // instead of aborting the whole search.
                return null;
            }
        }

        private static string FormatHex(byte[] data)
        {
            var builder = new StringBuilder(data.Length * 3);

            for (int index = 0; index < data.Length; index += 1)
            {
                if (index > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private sealed class Frame
        {
            internal Frame(RegistryPath path)
            {
                Path = path;
            }

            internal RegistryPath Path { get; }

            internal bool KeyProcessed { get; set; }

            internal bool ChildrenEnumerated { get; set; }

            internal IReadOnlyList<string>? ChildNames { get; set; }

            internal int NextChild { get; set; }
        }
    }
}
