using System.Buffers.Binary;

namespace Bwc.Gpo.Pol;

/// <summary>
/// The strict, bounded Registry.pol reader. It reports every problem as a <see cref="PolFormatException"/> value and
/// never throws for any byte sequence. Strictness decisions are in docs/failure-modes/gpo-pol.md section 1.
/// </summary>
internal static class PolReader
{
    /// <summary>The largest stream the reader will buffer. Real files are a few kilobytes.</summary>
    public const long MaxStreamBytes = 64L * 1024 * 1024;

    /// <summary>Reads <paramref name="bytes"/> and replays every record into <paramref name="document"/>.</summary>
    /// <returns>Null on success, otherwise the failure.</returns>
    public static PolFormatException? Read(ReadOnlySpan<byte> bytes, PolDocument document)
    {
        if (bytes.Length == 0)
        {
            return new PolFormatException(0, PolFormatReason.Empty);
        }

        if (bytes.Length < PolFormat.HeaderLength)
        {
            return new PolFormatException(0, PolFormatReason.HeaderTruncated);
        }

        if (!bytes[..PolFormat.SignatureLength].SequenceEqual(PolFormat.Header[..PolFormat.SignatureLength]))
        {
            return new PolFormatException(0, PolFormatReason.BadSignature);
        }

        if (!bytes[PolFormat.SignatureLength..PolFormat.HeaderLength].SequenceEqual(PolFormat.Header[PolFormat.SignatureLength..]))
        {
            return new PolFormatException(PolFormat.SignatureLength, PolFormatReason.BadVersion);
        }

        var pos = PolFormat.HeaderLength;
        while (pos < bytes.Length)
        {
            var failure = ReadRecord(bytes, ref pos, out var record);
            if (failure is not null)
            {
                return failure;
            }

            document.ReplayLoaded(record!);
        }

        return null;
    }

    /// <summary>Buffers a stream (bounded by <see cref="MaxStreamBytes"/>) and returns its bytes or the size failure.</summary>
    public static PolFormatException? ReadAll(Stream stream, out byte[] bytes)
    {
        bytes = [];
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > MaxStreamBytes)
            {
                return new PolFormatException(0, PolFormatReason.FileTooLarge);
            }
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxStreamBytes)
            {
                return new PolFormatException(0, PolFormatReason.FileTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        bytes = buffer.ToArray();
        return null;
    }

    private static PolFormatException? ReadRecord(ReadOnlySpan<byte> b, ref int pos, out PolRecord? record)
    {
        record = null;
        var start = pos;
        if (b.Length - pos < 2 || ReadChar(b, pos) != PolFormat.RecordStart)
        {
            return new PolFormatException(pos, PolFormatReason.ExpectedRecordStart);
        }

        pos += 2;
        var failure = ReadString(b, ref pos, out var key);
        if (failure is not null)
        {
            return failure;
        }

        if (key.StartsWith('\\'))
        {
            return new PolFormatException(start, PolFormatReason.KeyLeadingBackslash);
        }

        failure = ExpectSeparator(b, ref pos);
        if (failure is not null)
        {
            return failure;
        }

        failure = ReadString(b, ref pos, out var name);
        if (failure is not null)
        {
            return failure;
        }

        failure = ExpectSeparator(b, ref pos);
        if (failure is not null)
        {
            return failure;
        }

        failure = ReadUInt32(b, ref pos, out var type);
        if (failure is not null)
        {
            return failure;
        }

        failure = ExpectSeparator(b, ref pos);
        if (failure is not null)
        {
            return failure;
        }

        var sizeOffset = pos;
        failure = ReadUInt32(b, ref pos, out var size);
        if (failure is not null)
        {
            return failure;
        }

        failure = ExpectSeparator(b, ref pos);
        if (failure is not null)
        {
            return failure;
        }

        var remaining = (long)b.Length - pos;
        if ((long)size + 2 > remaining)
        {
            return new PolFormatException(sizeOffset, PolFormatReason.SizePastEnd);
        }

        var dataOffsetInRecord = pos - start;
        pos += (int)size;
        if (ReadChar(b, pos) != PolFormat.RecordEnd)
        {
            return new PolFormatException(pos, PolFormatReason.MissingRecordEnd);
        }

        pos += 2;
        record = new PolRecord(key, name, type, b[start..pos].ToArray(), dataOffsetInRecord, (int)size);
        return null;
    }

    private static PolFormatException? ReadString(ReadOnlySpan<byte> b, ref int pos, out string text)
    {
        text = string.Empty;
        var start = pos;
        var units = 0;
        var cursor = pos;
        while (true)
        {
            if (b.Length - cursor < 2)
            {
                return new PolFormatException(start, PolFormatReason.UnterminatedString);
            }

            if (b[cursor] == 0 && b[cursor + 1] == 0)
            {
                break;
            }

            units++;
            cursor += 2;
        }

        text = string.Create(units, b.Slice(start, units * 2).ToArray(), static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)(source[i * 2] | (source[(i * 2) + 1] << 8));
            }
        });
        pos = cursor + 2;
        return null;
    }

    private static PolFormatException? ExpectSeparator(ReadOnlySpan<byte> b, ref int pos)
    {
        if (b.Length - pos < 2)
        {
            return new PolFormatException(pos, PolFormatReason.Truncated);
        }

        if (ReadChar(b, pos) != PolFormat.FieldSeparator)
        {
            return new PolFormatException(pos, PolFormatReason.MissingDelimiter);
        }

        pos += 2;
        return null;
    }

    private static PolFormatException? ReadUInt32(ReadOnlySpan<byte> b, ref int pos, out uint value)
    {
        value = 0;
        if (b.Length - pos < 4)
        {
            return new PolFormatException(pos, PolFormatReason.Truncated);
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
        pos += 4;
        return null;
    }

    private static char ReadChar(ReadOnlySpan<byte> b, int pos) => (char)(b[pos] | (b[pos + 1] << 8));
}
