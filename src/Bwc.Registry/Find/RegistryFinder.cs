namespace Bwc.Registry;

/// <summary>
/// Classic regedit Find over the real registry. The walk is depth-first pre-order with an
/// explicit stack, uses the Windows NLS collation for matching and ordering, checks the key
/// name, then each value name and its data, and continues across the configured roots without
/// wrapping. A cursor whose key or value disappeared resumes at the nearest later position.
/// Reads use <see cref="RegistryReader"/> only and may run on any thread.
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

            if (TryCreateContinuationStack(
                _cursor,
                out Stack<Frame> continuation,
                out bool cursorKeyExists))
            {
                if (!cursorKeyExists)
                {
                    // The cursor key is gone. Its position in pre-order has passed, so
                    // every frame in the continuation is after the cursor.
                    _pastCursor = true;
                }

                return WalkStack(continuation);
            }

            // The cursor key is outside every root or its continuation cannot be placed.
            // Scan from the first root. A cursor that is never reached returns null instead
            // of wrapping.
            return WalkStack(CreateRootStack());
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
            out Stack<Frame> stack,
            out bool cursorKeyExists)
        {
            stack = new Stack<Frame>();
            cursorKeyExists = false;

            int rootIndex = FindRootIndex(cursor.KeyPath);
            if (rootIndex < 0)
            {
                return false;
            }

            cursorKeyExists = cursor.KeyPath.Equals(_query.Roots[rootIndex]);

            // Each level holds the siblings that follow the cursor branch at that level. Level
            // zero is the cursor key's own next siblings; the last level is the next siblings of
            // the search root's child. Ancestors themselves were already considered before the
            // cursor and are not pushed again. A level whose parent is unreadable contributes no
            // later siblings; the climb continues at the parent's parent.
            var continuationLevels = new List<List<RegistryPath>>();
            RegistryPath root = _query.Roots[rootIndex];
            RegistryPath current = cursor.KeyPath;
            bool atCursorLevel = true;

            while (!current.Equals(root))
            {
                RegistryPath parent = current.Parent!;
                string leaf = current.Components[^1];

                _cancellationToken.ThrowIfCancellationRequested();

                if (TryGetSortedChildNames(parent, out List<string> sortedNames))
                {
                    int index = sortedNames.FindIndex(
                        name => string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase));
                    int laterStart;

                    if (index >= 0)
                    {
                        if (atCursorLevel)
                        {
                            cursorKeyExists = true;
                        }

                        laterStart = index + 1;
                    }
                    else
                    {
                        // The key was deleted or renamed. Insert its old leaf name at the
                        // nearest NLS position and continue with the siblings after it.
                        laterStart = LowerBound(sortedNames, leaf);

                        if (atCursorLevel)
                        {
                            cursorKeyExists = false;
                        }
                    }

                    var laterSiblings = new List<RegistryPath>(sortedNames.Count - laterStart);

                    for (int siblingIndex = laterStart; siblingIndex < sortedNames.Count; siblingIndex += 1)
                    {
                        RegistryPath? sibling = TryAppend(parent, sortedNames[siblingIndex]);
                        if (sibling is not null)
                        {
                            laterSiblings.Add(sibling);
                        }
                    }

                    continuationLevels.Add(laterSiblings);
                }

                current = parent;
                atCursorLevel = false;
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

            if (cursorKeyExists)
            {
                stack.Push(new Frame(cursor.KeyPath));
            }

            return true;
        }

        private bool TryGetSortedChildNames(RegistryPath parent, out List<string> sortedNames)
        {
            try
            {
                sortedNames = _reader
                    .EnumerateSubKeyNames(parent, _cancellationToken)
                    .OrderBy(name => name, RegistryFindCollation.Comparer)
                    .ToList();
                return true;
            }
            catch (RegistryOperationException)
            {
                sortedNames = new List<string>();
                return false;
            }
        }

        private static int LowerBound(IReadOnlyList<string> sortedNames, string name)
        {
            int low = 0;
            int high = sortedNames.Count;

            while (low < high)
            {
                int middle = low + ((high - low) / 2);

                if (RegistryFindCollation.Compare(sortedNames[middle], name) <= 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
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
            if (IsCursorKeyPosition(path))
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
            bool cursorValueInThisKey = !_pastCursor
                && _cursor is not null
                && _cursor.ValueName is not null
                && _cursor.KeyPath.Equals(path);

            if (!cursorValueInThisKey)
            {
                if (!_pastCursor || (!_query.LookAtValues && !_query.LookAtData))
                {
                    return null;
                }
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
                // hit; its values and children are simply unavailable. If the cursor value lives
                // here, the nearest reachable position is the key's children.
                if (cursorValueInThisKey)
                {
                    _pastCursor = true;
                }

                return null;
            }

            List<RegistryValueInfo> sortedValues = values
                .OrderBy(item => item.Name.Length == 0 ? 0 : 1)
                .ThenBy(item => item.Name, RegistryFindCollation.Comparer)
                .ToList();
            int startIndex = 0;
            int cursorIndex = -1;

            if (cursorValueInThisKey)
            {
                string cursorValueName = _cursor!.ValueName!;
                cursorIndex = sortedValues.FindIndex(
                    item => string.Equals(
                        item.Name,
                        cursorValueName,
                        StringComparison.OrdinalIgnoreCase));

                if (cursorIndex >= 0)
                {
                    if (_cursor.Phase == RegistryFindCursorPhase.AfterValueData)
                    {
                        startIndex = cursorIndex + 1;
                        _pastCursor = true;
                    }
                    else
                    {
                        // AfterValueName: this value's name was the last position considered.
                        // Its data is still checked below.
                        startIndex = cursorIndex;
                    }
                }
                else
                {
                    // The cursor value disappeared or was renamed. Resume at the first value
                    // that sorts after its old name.
                    startIndex = sortedValues.FindIndex(
                        item => RegistryFindCollation.Compare(item.Name, cursorValueName) > 0);

                    if (startIndex < 0)
                    {
                        startIndex = sortedValues.Count;
                    }

                    _pastCursor = true;
                }
            }

            for (int index = startIndex; index < sortedValues.Count; index += 1)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                RegistryValueInfo value = sortedValues[index];

                if (cursorValueInThisKey
                    && index == cursorIndex
                    && _cursor!.Phase == RegistryFindCursorPhase.AfterValueName)
                {
                    _pastCursor = true;

                    if (_query.LookAtData && TryMatchData(value, out string dataText))
                    {
                        return new RegistryFindHit(
                            path,
                            value.Name,
                            RegistryFindHitKind.ValueData,
                            dataText);
                    }

                    continue;
                }

                if (!_pastCursor)
                {
                    continue;
                }

                // Classic reports a value at most once per step. The name wins when both the
                // name and the data match.
                if (_query.LookAtValues && Matches(value.Name))
                {
                    return new RegistryFindHit(
                        path,
                        value.Name,
                        RegistryFindHitKind.ValueName,
                        value.Name);
                }

                if (_query.LookAtData && TryMatchData(value, out string matchedText))
                {
                    return new RegistryFindHit(
                        path,
                        value.Name,
                        RegistryFindHitKind.ValueData,
                        matchedText);
                }
            }

            if (cursorValueInThisKey && !_pastCursor)
            {
                // The cursor value sorted after every value that still exists.
                _pastCursor = true;
            }

            return null;
        }

        private bool IsCursorKeyPosition(RegistryPath path) =>
            !_pastCursor
            && _cursor is not null
            && _cursor.Phase == RegistryFindCursorPhase.AfterKeyName
            && _cursor.ValueName is null
            && _cursor.KeyPath.Equals(path);

        private IReadOnlyList<string> EnumerateChildren(RegistryPath path)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            try
            {
                IReadOnlyList<string> names = _reader.EnumerateSubKeyNames(path, _cancellationToken);
                return names.OrderBy(name => name, RegistryFindCollation.Comparer).ToArray();
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

            string candidate;

            switch (value.Type)
            {
                case (uint)RegistryValueType.String:
                case (uint)RegistryValueType.ExpandString:
                    candidate = RegistryValueCodec.DecodeString(value.Data);
                    break;

                case (uint)RegistryValueType.MultiString:
                    // The Data column joins the items with spaces. The oracle's whole-string
                    // run proves that individual items are not separate candidates.
                    candidate = RegistryValueDisplay.FormatData(value, int.MaxValue);
                    break;

                default:
                    // The oracle's Data hits never include DWORD, QWORD, binary, link, none,
                    // or unknown types. Classic searches only the three string types.
                    return false;
            }

            if (candidate.Length == 0 || !Matches(candidate))
            {
                return false;
            }

            matchedText = candidate;
            return true;
        }

        private bool Matches(string text) => _query.MatchWholeStringOnly
            ? RegistryFindCollation.Equals(text, _query.SearchText)
            : RegistryFindCollation.Contains(text, _query.SearchText);

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
