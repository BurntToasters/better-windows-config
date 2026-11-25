namespace Bwc.Gpo.Pol;

/// <summary>Serializes records already in write order: the header followed by each record's own bytes.</summary>
internal static class PolWriter
{
    public static byte[] Write(IEnumerable<PolRecord> recordsInWriteOrder)
    {
        var records = recordsInWriteOrder as IReadOnlyCollection<PolRecord> ?? recordsInWriteOrder.ToArray();
        long total = PolFormat.HeaderLength;
        foreach (var record in records)
        {
            total += record.RawBytes.Length;
        }

        if (total > Array.MaxLength)
        {
            throw new InvalidOperationException("The document is larger than a byte array can hold.");
        }

        var output = new byte[(int)total];
        PolFormat.Header.CopyTo(output);
        var pos = PolFormat.HeaderLength;
        foreach (var record in records)
        {
            record.RawBytes.Span.CopyTo(output.AsSpan(pos));
            pos += record.RawBytes.Length;
        }

        return output;
    }
}
