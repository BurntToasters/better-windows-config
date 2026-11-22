using System.Globalization;
using System.Text;

namespace Bwc.Gpo.Store;

/// <summary>The gpt.ini content cannot be read or edited without guessing.</summary>
public sealed class GptIniFormatException(string message) : Exception(message);

/// <summary>
/// The model of gpt.ini. Bytes are handled one byte per character (ISO-8859-1), so every existing byte survives
/// whatever the ANSI code page is; everything added is ASCII. Each line keeps its own line ending, unknown lines and
/// sections stay in place, and an unedited file serializes to the bytes it was parsed from. In memory only; the file
/// is written by <see cref="GpoFileWriter"/>.
/// </summary>
public sealed class GptIni
{
    /// <summary>The size cap; a larger file is refused.</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly List<Line> _lines;
    private readonly bool _hasBom;
    private readonly byte[] _baseline;

    private GptIni(List<Line> lines, bool hasBom)
    {
        _lines = lines;
        _hasBom = hasBom;
        _baseline = ToBytes();
    }

    /// <summary>The 32-bit version: user half in the high 16 bits, machine half in the low 16 bits. 0 when absent.</summary>
    public uint Version
    {
        get
        {
            var index = FindKey(GptIniRules.VersionKey);
            return index < 0 ? 0 : ParseVersion(ValueOf(_lines[index].Text));
        }
    }

    /// <summary>The Computer Configuration version (low 16 bits).</summary>
    public ushort MachineVersion => (ushort)(Version & 0xFFFF);

    /// <summary>The User Configuration version (high 16 bits).</summary>
    public ushort UserVersion => (ushort)(Version >> 16);

    /// <summary>True when the bytes differ from what was parsed.</summary>
    public bool HasChanges => !ToBytes().AsSpan().SequenceEqual(_baseline);

    /// <summary>Starts a new file: a <c>[General]</c> header and nothing else.</summary>
    /// <returns>A new model.</returns>
    public static GptIni CreateNew() => new([new Line("[General]", "\r\n")], false);

    /// <summary>Parses a gpt.ini file.</summary>
    /// <param name="bytes">The complete file.</param>
    /// <returns>The model.</returns>
    /// <exception cref="GptIniFormatException">Over the cap, UTF-16, or the Version is not a decimal uint.</exception>
    public static GptIni Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new GptIniFormatException($"gpt.ini is larger than {MaxBytes} bytes.");
        }

        if (bytes.StartsWith<byte>([0xFF, 0xFE]) || bytes.StartsWith<byte>([0xFE, 0xFF]))
        {
            throw new GptIniFormatException("gpt.ini is UTF-16; it is not rewritten as ANSI.");
        }

        var hasBom = bytes.StartsWith<byte>(Utf8Bom);
        var text = Latin1.GetString(hasBom ? bytes[Utf8Bom.Length..] : bytes);
        var ini = new GptIni(SplitLines(text), hasBom);
        var version = ini.FindKey(GptIniRules.VersionKey);
        if (version >= 0)
        {
            ParseVersion(ValueOf(ini._lines[version].Text));
        }

        return ini;
    }

    /// <summary>Adds one to the half of <paramref name="side"/> (see <see cref="GptIniRules.Bump"/>).</summary>
    /// <param name="side">The saved side.</param>
    public void BumpVersion(GpoSide side)
    {
        var next = GptIniRules.Bump(Version, side).ToString(CultureInfo.InvariantCulture);
        SetKey(GptIniRules.VersionKey, next);
    }

    /// <summary>Makes sure the registry extension and the side's snap-in GUID are listed for the side.</summary>
    /// <param name="side">The side.</param>
    /// <exception cref="GptIniFormatException">The existing extension line is not a list of GUID groups.</exception>
    public void EnsureExtensionPair(GpoSide side)
    {
        var key = GptIniRules.ExtensionKey(side);
        var index = FindKey(key);
        var groups = index < 0 ? [] : ParseGroups(ValueOf(_lines[index].Text), key);
        if (!GptIniRules.AddPair(groups, side))
        {
            return;
        }

        var value = string.Concat(groups.Select(g => "[" + string.Concat(g) + "]"));
        if (index >= 0 && ValueOf(_lines[index].Text).Trim().Length == 0)
        {
            _lines[index].Text = key + "=" + value;
            return;
        }

        SetKey(key, value);
    }

    /// <summary>
    /// Takes the registry pair out of the side's extension line after the side lost its last setting. When nothing else is
    /// listed the key stays and its value becomes one space (capture K/KeyTrailingBackslash_Enable step 2,
    /// C/GptIni_PairRemoval). When other groups remain the line keeps them and loses only the pair (capture
    /// C/GptIni_ForeignExtensions step 4). A missing line is not added.
    /// </summary>
    /// <param name="side">The side.</param>
    /// <exception cref="GptIniFormatException">The existing extension line is not a list of GUID groups.</exception>
    public void BlankExtensionPair(GpoSide side)
    {
        var key = GptIniRules.ExtensionKey(side);
        var index = FindKey(key);
        if (index < 0 || ValueOf(_lines[index].Text).Trim().Length == 0)
        {
            return;
        }

        var groups = ParseGroups(ValueOf(_lines[index].Text), key);
        if (!GptIniRules.RemovePair(groups, side))
        {
            return;
        }

        SetKey(key, groups.Count == 0 ? " " : string.Concat(groups.Select(g => "[" + string.Concat(g) + "]")));
    }

    /// <summary>Serializes the file.</summary>
    /// <returns>A new array.</returns>
    public byte[] ToBytes()
    {
        var text = new StringBuilder();
        foreach (var line in _lines)
        {
            text.Append(line.Text).Append(line.Terminator);
        }

        var body = Latin1.GetBytes(text.ToString());
        if (!_hasBom)
        {
            return body;
        }

        var all = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(all, 0);
        body.CopyTo(all, Utf8Bom.Length);
        return all;
    }

    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            string? terminator = null;
            if (text[i] == '\r')
            {
                terminator = i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
            }
            else if (text[i] == '\n')
            {
                terminator = "\n";
            }

            if (terminator is null)
            {
                continue;
            }

            lines.Add(new Line(text[start..i], terminator));
            i += terminator.Length - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new Line(text[start..], string.Empty));
        }

        return lines;
    }

    private static uint ParseVersion(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit) || !uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new GptIniFormatException($"Version '{trimmed}' is not a decimal number from 0 to 4294967295.");
        }

        return version;
    }

    private static List<List<string>> ParseGroups(string value, string key)
    {
        var groups = new List<List<string>>();
        var text = value.Trim();
        var position = 0;
        while (position < text.Length)
        {
            if (text[position] != '[')
            {
                throw Malformed(key);
            }

            var close = text.IndexOf(']', position);
            if (close < 0)
            {
                throw Malformed(key);
            }

            var group = new List<string>();
            var inner = text[(position + 1)..close];
            var cursor = 0;
            while (cursor < inner.Length)
            {
                if (inner[cursor] != '{' || cursor + 38 > inner.Length || inner[cursor + 37] != '}' || !IsGuidBody(inner.AsSpan(cursor + 1, 36)))
                {
                    throw Malformed(key);
                }

                group.Add(inner.Substring(cursor, 38));
                cursor += 38;
            }

            if (group.Count == 0)
            {
                throw Malformed(key);
            }

            groups.Add(group);
            position = close + 1;
        }

        return groups;
    }

    private static bool IsGuidBody(ReadOnlySpan<char> body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            var dash = i is 8 or 13 or 18 or 23;
            if (dash ? body[i] != '-' : !char.IsAsciiHexDigit(body[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static GptIniFormatException Malformed(string key) =>
        new($"The {key} line is not a list of bracketed GUID groups; it is not rewritten.");

    private static string ValueOf(string text) => text[(text.IndexOf('=') + 1)..];

    private static string? KeyOf(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
        {
            return null;
        }

        var equals = text.IndexOf('=');
        return equals <= 0 ? null : text[..equals].Trim();
    }

    private static string? SectionOf(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1].Trim() : null;
    }

    /// <summary>The range of the first General section: header index (or -1) and the index after its last line.</summary>
    private (int Header, int End) General()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (string.Equals(SectionOf(_lines[i].Text), GptIniRules.GeneralSection, StringComparison.OrdinalIgnoreCase))
            {
                var end = i + 1;
                while (end < _lines.Count && SectionOf(_lines[end].Text) is null)
                {
                    end++;
                }

                return (i, end);
            }
        }

        return (-1, -1);
    }

    private int FindKey(string key)
    {
        var (header, end) = General();
        for (var i = header + 1; header >= 0 && i < end; i++)
        {
            if (string.Equals(KeyOf(_lines[i].Text), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private string NewLine() => _lines.Select(l => l.Terminator).FirstOrDefault(t => t.Length > 0) ?? "\r\n";

    private void SetKey(string key, string value)
    {
        var index = FindKey(key);
        if (index >= 0)
        {
            var text = _lines[index].Text;
            var equals = text.IndexOf('=');
            var raw = text[(equals + 1)..];
            var trimmedStart = raw.TrimStart();
            var leading = raw[..(raw.Length - trimmedStart.Length)];
            var trailing = trimmedStart[trimmedStart.TrimEnd().Length..];
            _lines[index].Text = text[..(equals + 1)] + leading + value + trailing;
            return;
        }

        var newLine = NewLine();
        var (header, end) = General();
        if (header < 0)
        {
            _lines.Insert(0, new Line("[General]", newLine));
            (header, end) = (0, 1);
        }

        var keys = new List<string?>();
        var blank = new List<bool>();
        for (var i = header + 1; i < end; i++)
        {
            keys.Add(KeyOf(_lines[i].Text));
            blank.Add(_lines[i].Text.Trim().Length == 0);
        }

        var at = header + 1 + GptIniRules.InsertionIndex(keys, blank, key);
        if (at > 0 && _lines[at - 1].Terminator.Length == 0)
        {
            _lines[at - 1].Terminator = newLine;
        }

        _lines.Insert(at, new Line(key + "=" + value, newLine));
    }

    private sealed class Line(string text, string terminator)
    {
        public string Text { get; set; } = text;

        public string Terminator { get; set; } = terminator;
    }
}
