using System.Diagnostics.CodeAnalysis;

namespace Bwc.Gpo.Pol;

/// <summary>
/// The ordered model of one Registry.pol file. Keys and value names compare case-insensitively. Records inside a key keep
/// the order that docs/contracts/policy-engine.md describes (load in file order, overwrite in place, recreate at the
/// end); keys are placed by the sort rule when written. Not thread safe.
/// </summary>
public sealed class PolDocument
{
    private readonly Dictionary<string, Bucket> _keys = new(StringComparer.OrdinalIgnoreCase);
    private int _recordCount;
    private byte[] _baseline = PolOrderRules.EmptyDocumentBytes();

    private PolDocument()
    {
    }

    /// <summary>The number of records in the document, instructions and unknown records included.</summary>
    public int RecordCount => _recordCount;

    /// <summary>True when the document holds no records.</summary>
    public bool IsEmpty => _recordCount == 0;

    /// <summary>
    /// Starts at 0 after <see cref="Create"/> or parse and increases by one for every call that changed the model. A call
    /// that changes nothing (a set with identical data, a removal that found nothing) leaves it alone.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// True when the bytes this document would write differ from the bytes it would have written right after it was
    /// created or parsed. An edit that was undone gives false, so a store can skip a write that changes nothing.
    /// </summary>
    public bool HasChanges => !ToBytes().AsSpan().SequenceEqual(_baseline);

    /// <summary>Creates a document with no records.</summary>
    /// <returns>A new empty document.</returns>
    public static PolDocument Create() => new();

    /// <summary>Parses a Registry.pol file.</summary>
    /// <param name="bytes">The complete file.</param>
    /// <returns>The model.</returns>
    /// <exception cref="PolFormatException">
    /// The bytes are not a valid Registry.pol file. No other exception is thrown for any input.
    /// </exception>
    public static PolDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var document = new PolDocument();
        var failure = PolReader.Read(bytes, document);
        if (failure is not null)
        {
            throw failure;
        }

        document._baseline = document.ToBytes();
        return document;
    }

    /// <summary>Reads a stream to its end and parses it as a Registry.pol file.</summary>
    /// <param name="stream">A readable stream positioned at the start of the file.</param>
    /// <returns>The model.</returns>
    /// <exception cref="PolFormatException">
    /// The content is invalid or longer than the size cap. Stream errors propagate unchanged.
    /// </exception>
    public static PolDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var failure = PolReader.ReadAll(stream, out var bytes);
        return failure is null ? Parse(bytes) : throw failure;
    }

    /// <summary>Parses a Registry.pol file without throwing for invalid content.</summary>
    /// <param name="bytes">The complete file.</param>
    /// <param name="document">The model on success.</param>
    /// <param name="failure">The reason and offset on failure.</param>
    /// <returns>True when the bytes were valid.</returns>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out PolDocument? document,
        [NotNullWhen(false)] out PolFormatException? failure)
    {
        var parsed = new PolDocument();
        failure = PolReader.Read(bytes, parsed);
        if (failure is null)
        {
            parsed._baseline = parsed.ToBytes();
        }

        document = failure is null ? parsed : null;
        return failure is null;
    }

    /// <summary>The keys that hold at least one record, in the order they are written, with their stored spelling.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<string> GetKeys() => SortedBuckets().Select(b => b.Spelling).ToArray();

    /// <summary>The records of one key in model order.</summary>
    /// <param name="key">The key path, compared case-insensitively.</param>
    /// <returns>A snapshot; empty when the key has no records.</returns>
    public IReadOnlyList<PolRecord> GetRecords(string key) =>
        _keys.TryGetValue(key, out var bucket) ? bucket.Records.ToArray() : [];

    /// <summary>Every record in the order they are written: keys sorted, records in model order.</summary>
    /// <returns>A snapshot.</returns>
    public IEnumerable<PolRecord> EnumerateRecords() => SortedBuckets().SelectMany(b => b.Records).ToArray();

    /// <summary>Finds one record by key and name, both case-insensitive.</summary>
    /// <param name="key">The key path.</param>
    /// <param name="name">The value name; the empty string is the default value.</param>
    /// <returns>The record, or null.</returns>
    public PolRecord? GetValue(string key, string name) =>
        _keys.TryGetValue(key, out var bucket) && bucket.ByName.TryGetValue(name, out var node) ? node.Value : null;

    /// <summary>
    /// Sets a value. A new value goes to the end of its key. An existing value with the same type and data is left
    /// untouched (no change). An existing value with different data is removed and appended at the end of its key
    /// (classic probe), keeping the stored key and name spelling.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">A value name that does not start with <c>**</c>. The empty string is the default value.</param>
    /// <param name="type">The value type.</param>
    /// <param name="data">
    /// The data bytes; copied. Shape must match the type (DWORD 4 bytes, QWORD 8, string types an even length ending in NUL).
    /// </param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key, name, type or data shape is invalid.</exception>
    public PolRecord SetValue(string key, string name, PolValueType type, ReadOnlySpan<byte> data)
    {
        ValidateKey(key);
        ValidateName(name);
        if (name.StartsWith(PolFormat.InstructionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A value name starting with ** is an instruction and cannot be set as a value.", nameof(name));
        }

        ValidateData(type, data);
        return Upsert(key, name, (uint)type, data);
    }

    /// <summary>
    /// Sets a value of an element. A new value goes to the end of its key like <see cref="SetValue"/>. An existing value keeps
    /// its place and takes the new type and data, which is how classic rewrites the values of the elements of a policy
    /// (captures C/Booleans_ReverseOrder step 2 and C/Elements_ReverseOrder step 3: the changed value stays where it was),
    /// while the value of the policy itself is removed and appended (C/TwoInKey_ChangeFirstAgain step 5, see
    /// <see cref="SetValue"/>). The stored key and name spelling stay.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">A value name that does not start with <c>**</c>.</param>
    /// <param name="type">The value type.</param>
    /// <param name="data">The data bytes; copied. Shape must match the type.</param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key, name, type or data shape is invalid.</exception>
    public PolRecord SetValueInPlace(string key, string name, PolValueType type, ReadOnlySpan<byte> data)
    {
        ValidateKey(key);
        ValidateName(name);
        if (name.StartsWith(PolFormat.InstructionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A value name starting with ** is an instruction and cannot be set as a value.", nameof(name));
        }

        ValidateData(type, data);
        if (_keys.TryGetValue(key, out var bucket) && bucket.ByName.TryGetValue(name, out var existing))
        {
            var old = existing.Value;
            if (PolOrderRules.IsSameContent(old, (uint)type, data))
            {
                return old;
            }

            var replacement = PolRecord.Create(old.Key, old.ValueName, (uint)type, data);
            existing.Value = replacement;
            Version++;
            return replacement;
        }

        return Upsert(key, name, (uint)type, data);
    }

    /// <summary>Removes one value record (Not Configured). Instruction records for the value stay.</summary>
    /// <param name="key">The key path.</param>
    /// <param name="name">The value name.</param>
    /// <returns>True when a record was removed.</returns>
    public bool RemoveValue(string key, string name)
    {
        if (!_keys.TryGetValue(key, out var bucket) || !bucket.ByName.TryGetValue(name, out var node))
        {
            return false;
        }

        bucket.Records.Remove(node);
        bucket.ByName.Remove(name);
        _recordCount--;
        Version++;
        if (bucket.Records.Count == 0)
        {
            _keys.Remove(key);
        }

        return true;
    }

    /// <summary>
    /// Adds the delete instruction for a value: <c>**del.&lt;name&gt;</c>, REG_SZ, size 4, data <c>20 00 00 00</c>.
    /// An existing instruction for the name is replaced in place. The value record itself is not touched.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">The name of the value to delete when the policy is applied.</param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key or name is invalid.</exception>
    public PolRecord AddDeleteInstruction(string key, string name)
    {
        ValidateKey(key);
        ValidateName(name);
        return Upsert(key, PolFormat.DeletePrefix + name, (uint)PolValueType.String, PolFormat.DeleteInstructionData);
    }

    /// <summary>
    /// Sets the soft value of an element: a record named <c>**soft.&lt;name&gt;</c> that carries the typed data of the value
    /// (capture B/Pol_LongDecimal_RequiredSoftText and B/Pol_Decimal_Soft: the number of a soft element is stored under the
    /// soft name and not under its own name). Place and spelling follow <see cref="SetValueInPlace"/>.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">The value name without the <c>**soft.</c> prefix.</param>
    /// <param name="type">The value type.</param>
    /// <param name="data">The data bytes; copied. Shape must match the type.</param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key, name, type or data shape is invalid.</exception>
    public PolRecord SetSoftValue(string key, string name, PolValueType type, ReadOnlySpan<byte> data)
    {
        ValidateKey(key);
        ValidateName(name);
        if (name.StartsWith(PolFormat.InstructionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A value name starting with ** cannot take the soft prefix.", nameof(name));
        }

        ValidateData(type, data);
        var softName = PolFormat.SoftPrefix + name;
        if (_keys.TryGetValue(key, out var bucket) && bucket.ByName.TryGetValue(softName, out var existing))
        {
            var old = existing.Value;
            if (PolOrderRules.IsSameContent(old, (uint)type, data))
            {
                return old;
            }

            var replacement = PolRecord.Create(old.Key, old.ValueName, (uint)type, data);
            existing.Value = replacement;
            Version++;
            return replacement;
        }

        return Upsert(key, softName, (uint)type, data);
    }

    /// <summary>
    /// Replaces the value record of an element by its delete instruction where it stands: the instruction takes the place of
    /// the value in the key (capture B/Pol_Bool_NoFalseValue and B/Pol_Amp_LabelSingle step 3, where the unchecked box sits
    /// between two other records). With no value record it is <see cref="AddDeleteInstruction"/>.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">The name of the value to delete when the policy is applied.</param>
    /// <returns>The stored instruction.</returns>
    /// <exception cref="ArgumentException">The key or name is invalid.</exception>
    public PolRecord AddDeleteInstructionInPlace(string key, string name)
    {
        ValidateKey(key);
        ValidateName(name);
        if (!_keys.TryGetValue(key, out var bucket) || !bucket.ByName.TryGetValue(name, out var valueNode))
        {
            return AddDeleteInstruction(key, name);
        }

        var instructionName = PolFormat.DeletePrefix + name;
        if (bucket.ByName.TryGetValue(instructionName, out var oldInstruction))
        {
            bucket.Records.Remove(oldInstruction);
            bucket.ByName.Remove(instructionName);
            _recordCount--;
        }

        var value = valueNode.Value;
        var instruction = PolRecord.Create(value.Key, instructionName, (uint)PolValueType.String, PolFormat.DeleteInstructionData);
        var node = bucket.Records.AddAfter(valueNode, instruction);
        bucket.Records.Remove(valueNode);
        bucket.ByName.Remove(name);
        bucket.ByName[instructionName] = node;
        Version++;
        return instruction;
    }

    /// <summary>
    /// Adds the delete-all-values instruction for a key: <c>**delvals.</c>, REG_SZ, size 4, data <c>20 00 00 00</c>. When
    /// the file is applied it deletes every value of the key that exists at that point, so a non-additive list writes it in
    /// front of its entries. The record goes to the front of the key (see <see cref="PolOrderRules.PlaceDeleteValues"/>).
    /// A record that is already first with these bytes is left alone (no change); one that is elsewhere or has other bytes
    /// is replaced at the front and keeps its stored spelling. It never stacks: there is one such record per key.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key is invalid.</exception>
    public PolRecord AddDeleteValuesInstruction(string key)
    {
        ValidateKey(key);
        if (!_keys.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(key);
            _keys.Add(key, bucket);
        }

        const uint type = (uint)PolValueType.String;
        var data = PolFormat.DeleteInstructionData;
        if (bucket.ByName.TryGetValue(PolFormat.DeleteValuesName, out var existing))
        {
            var old = existing.Value;
            if (ReferenceEquals(bucket.Records.First, existing) && PolOrderRules.IsSameContent(old, type, data))
            {
                return old;
            }

            var replacement = PolRecord.Create(old.Key, old.ValueName, type, data);
            bucket.ByName[PolFormat.DeleteValuesName] = PolOrderRules.PlaceDeleteValues(bucket.Records, existing, replacement);
            Version++;
            return replacement;
        }

        var record = PolRecord.Create(bucket.Spelling, PolFormat.DeleteValuesName, type, data);
        bucket.ByName[PolFormat.DeleteValuesName] = PolOrderRules.PlaceDeleteValues(bucket.Records, null, record);
        _recordCount++;
        Version++;
        return record;
    }

    /// <summary>Removes the <c>**delvals.</c> record of a key.</summary>
    /// <param name="key">The key path.</param>
    /// <returns>True when a record was removed.</returns>
    public bool RemoveDeleteValuesInstruction(string key) => RemoveValue(key, PolFormat.DeleteValuesName);

    /// <summary>
    /// Adds the soft instruction for a value: <c>**soft.&lt;name&gt;</c>, REG_SZ, size 4, data <c>20 00 00 00</c>. It makes
    /// the policy engine write the value only when it is absent. The record goes to the end of the key like
    /// <see cref="AddDeleteInstruction"/> (identical record: no change; other data: replaced by append). The value record
    /// itself is not touched and does not move.
    /// </summary>
    /// <param name="key">A key path without leading, trailing or doubled backslashes.</param>
    /// <param name="name">The name of the value that is written only when absent.</param>
    /// <returns>The stored record.</returns>
    /// <exception cref="ArgumentException">The key or name is invalid.</exception>
    public PolRecord AddSoftInstruction(string key, string name)
    {
        ValidateKey(key);
        ValidateName(name);
        return Upsert(key, PolFormat.SoftPrefix + name, (uint)PolValueType.String, PolFormat.DeleteInstructionData);
    }

    /// <summary>Removes the <c>**soft.&lt;name&gt;</c> record of a value. The value record stays.</summary>
    /// <param name="key">The key path.</param>
    /// <param name="name">The value name.</param>
    /// <returns>True when a record was removed.</returns>
    public bool RemoveSoftInstruction(string key, string name) => RemoveValue(key, PolFormat.SoftPrefix + name);

    /// <summary>Removes the instruction records of one value (<c>**del.</c> and <c>**soft.</c> plus the name).</summary>
    /// <param name="key">The key path.</param>
    /// <param name="name">The value name.</param>
    /// <returns>The number of records removed.</returns>
    public int RemoveInstructions(string key, string name)
    {
        var removed = 0;
        foreach (var prefix in PolOrderRules.PerValueInstructionPrefixes)
        {
            if (RemoveValue(key, prefix + name))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Removes every record of one key, instructions and unknown records included. Child keys stay.</summary>
    /// <param name="key">The key path.</param>
    /// <returns>The number of records removed.</returns>
    public int RemoveKey(string key)
    {
        if (!_keys.Remove(key, out var bucket))
        {
            return 0;
        }

        var count = bucket.Records.Count;
        _recordCount -= count;
        Version++;
        return count;
    }

    /// <summary>
    /// Rewrites every key that ends in a backslash without it, the way classic's model spells keys when it saves (capture
    /// K/KeyTrailingBackslash_ReadAsWritten: after the next save the record sits under the trimmed key). Records keep their
    /// order; records that land in a key that already exists are appended to it.
    /// </summary>
    /// <returns>The number of keys that were rewritten.</returns>
    public int TrimTrailingKeyBackslashes()
    {
        var rewritten = 0;
        foreach (var bucket in _keys.Values.Where(b => b.Spelling.EndsWith('\\')).ToList())
        {
            var trimmed = bucket.Spelling.TrimEnd('\\');
            if (trimmed.Length == 0)
            {
                continue;
            }

            _keys.Remove(bucket.Spelling);
            _recordCount -= bucket.Records.Count;
            foreach (var record in bucket.Records)
            {
                ReplayLoaded(PolRecord.Create(trimmed, record.ValueName, record.Type, record.Data.Span));
            }

            rewritten++;
            Version++;
        }

        return rewritten;
    }

    /// <summary>Serializes the document: header, keys in registry order, records in model order.</summary>
    /// <returns>A new array.</returns>
    public byte[] ToBytes() => IsEmpty ? PolOrderRules.EmptyDocumentBytes() : PolWriter.Write(SortedBuckets().SelectMany(b => b.Records));

    internal void ReplayLoaded(PolRecord record)
    {
        if (!_keys.TryGetValue(record.Key, out var bucket))
        {
            bucket = new Bucket(record.Key);
            _keys.Add(record.Key, bucket);
        }

        if (bucket.ByName.TryGetValue(record.ValueName, out var existing))
        {
            if (!PolOrderRules.IsSameContent(existing.Value, record.Type, record.Data.Span))
            {
                bucket.ByName[record.ValueName] = PolOrderRules.ReplaceByAppend(bucket.Records, existing, record);
            }
        }
        else
        {
            bucket.ByName[record.ValueName] = PolOrderRules.AppendToKey(bucket.Records, record);
            _recordCount++;
        }
    }

    private PolRecord Upsert(string key, string name, uint type, ReadOnlySpan<byte> data)
    {
        if (!_keys.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(key);
            _keys.Add(key, bucket);
        }

        if (bucket.ByName.TryGetValue(name, out var existing))
        {
            var old = existing.Value;
            if (PolOrderRules.IsSameContent(old, type, data))
            {
                return old;
            }

            var replacement = PolRecord.Create(old.Key, old.ValueName, type, data);
            bucket.ByName[name] = PolOrderRules.ReplaceByAppend(bucket.Records, existing, replacement);
            Version++;
            return replacement;
        }

        var record = PolRecord.Create(bucket.Spelling, name, type, data);
        bucket.ByName[name] = PolOrderRules.AppendToKey(bucket.Records, record);
        _recordCount++;
        Version++;
        return record;
    }

    private List<Bucket> SortedBuckets()
    {
        var buckets = new List<Bucket>(_keys.Values);
        buckets.Sort((a, b) => PolOrderRules.KeyComparer.Compare(a.Spelling, b.Spelling));
        return buckets;
    }

    private static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || key.Contains('\0') || key[0] == '\\' || key[^1] == '\\' || key.Contains("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("A key must be non-empty, contain no NUL and have no leading, trailing or empty path segment.", nameof(key));
        }
    }

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0'))
        {
            throw new ArgumentException("A value name must not contain NUL.", nameof(name));
        }
    }

    private static void ValidateData(PolValueType type, ReadOnlySpan<byte> data)
    {
        var ok = type switch
        {
            PolValueType.DWord => data.Length == 4,
            PolValueType.QWord => data.Length == 8,
            PolValueType.String or PolValueType.ExpandString or PolValueType.MultiString =>
                data.Length >= 2 && (data.Length & 1) == 0 && data[^1] == 0 && data[^2] == 0,
            PolValueType.Binary => true,
            _ => throw new ArgumentException("Only REG_SZ, REG_EXPAND_SZ, REG_BINARY, REG_DWORD, REG_MULTI_SZ and REG_QWORD can be set.", nameof(type)),
        };
        if (!ok)
        {
            throw new ArgumentException($"The data does not have the shape of {type}.", nameof(data));
        }
    }

    private sealed class Bucket(string spelling)
    {
        public string Spelling { get; } = spelling;

        public LinkedList<PolRecord> Records { get; } = new();

        public Dictionary<string, LinkedListNode<PolRecord>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
