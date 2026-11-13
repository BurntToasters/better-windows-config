using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Bwc.Oracle.Capture;

// `import`: runs classic `regedit /s` on malformed and unusual .reg files and records the resulting
// subtree and exit code. Every file is generated here, its key headers are validated before it can run,
// and every case starts from the same seeded base tree. Nothing outside HKCU\Software\BWC-Oracle is
// ever named in a file that runs. Refused files are recorded with the reason and never imported.
internal static class ImportCapture
{
    private const string Root = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string RootSub = @"Software\BWC-Oracle";
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(30);

    private enum Enc
    {
        Utf16LeBom,
        Utf8Bom,
        Utf8,
        Utf16LeNoBom,
        Ansi,
    }

    private sealed record Case(string Name, string Description, byte[] Bytes);

    internal static void Run(RepoPaths paths)
    {
        Directory.CreateDirectory(paths.ImportWorkDir);
        var cases = new JsonArray();

        foreach (Case c in BuildCases())
        {
            cases.Add(RunCase(paths, c));
        }

        Report.WriteJson(paths.ImportBehaviorJson, new JsonObject
        {
            ["schema"] = 1,
            ["generator"] = "Bwc.Oracle.Capture import",
            ["os"] = Report.OsInfo(),
            ["regedit"] = Report.RegeditInfo(),
            ["scope"] = "Every file targets HKEY_CURRENT_USER\\Software\\BWC-Oracle only. Each case starts from the same base tree: Import\\A (Keep = \"1\", Sub with S = \"x\") and Import\\B (Other = dword 1).",
            ["cases"] = cases,
        });
    }

    private static JsonObject RunCase(RepoPaths paths, Case c)
    {
        var record = new JsonObject
        {
            ["name"] = c.Name,
            ["description"] = c.Description,
            ["bytes"] = c.Bytes.Length,
        };

        Enc encoding = DetectEncoding(c.Bytes);
        record["encoding"] = encoding.ToString();

        string text = Decode(c.Bytes, encoding);
        record["lines"] = new JsonArray(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());

        string? refusal = ValidateHeaders(text);
        if (refusal is not null)
        {
            record["refused"] = refusal;
            Console.WriteLine($"import: {c.Name} refused: {refusal}");
            return record;
        }

        string path = Path.Combine(paths.ImportWorkDir, c.Name + ".reg");
        File.WriteAllBytes(path, c.Bytes);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(c.Bytes))
        {
            throw new InvalidDataException($"'{path}' does not hold the validated bytes.");
        }

        RegistryGuard.DeleteOracleTree();
        SeedBase();

        int exit = -1;
        bool timedOut = false;
        try
        {
            exit = RegeditProcess.RunToExit(new[] { "/s", path }, ImportTimeout);
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            Console.Error.WriteLine($"import: {c.Name}: {ex.Message}");
        }

        record["exitCode"] = exit;
        record["timedOut"] = timedOut;
        record["tree"] = ReadTree();
        Console.WriteLine($"import: {c.Name}: exit {exit}{(timedOut ? " (timed out, killed)" : string.Empty)}");
        return record;
    }

    private static void SeedBase()
    {
        using (RegistryGuard.CreateOracleKey(string.Empty))
        {
        }

        using (RegistryKey a = RegistryGuard.CreateOracleKey(@"Import\A"))
        {
            RegistryGuard.SetRawValue(a, OracleFull(@"Import\A"), "Keep", RegType.Sz, SeedSpec.Sz("1"));
        }

        using (RegistryKey sub = RegistryGuard.CreateOracleKey(@"Import\A\Sub"))
        {
            RegistryGuard.SetRawValue(sub, OracleFull(@"Import\A\Sub"), "S", RegType.Sz, SeedSpec.Sz("x"));
        }

        using (RegistryKey b = RegistryGuard.CreateOracleKey(@"Import\B"))
        {
            RegistryGuard.SetRawValue(b, OracleFull(@"Import\B"), "Other", RegType.Dword, SeedSpec.Dword(1));
        }
    }

    private static string OracleFull(string relative) => RegistryGuard.OracleRoot + @"\" + relative;

    // Read-only walk of the whole BWC-Oracle tree.
    private static JsonObject ReadTree()
    {
        using RegistryKey? root = Registry.CurrentUser.OpenSubKey(RootSub, writable: false);
        if (root is null)
        {
            return new JsonObject { ["present"] = false, ["keys"] = new JsonArray() };
        }

        var keys = new List<(string Path, JsonObject Json)>();
        Walk(root, string.Empty, keys);
        return new JsonObject
        {
            ["present"] = true,
            ["keys"] = new JsonArray(keys.OrderBy(k => k.Path, StringComparer.Ordinal).Select(k => (JsonNode)k.Json).ToArray()),
        };
    }

    private static void Walk(RegistryKey key, string relative, List<(string Path, JsonObject Json)> acc)
    {
        var values = new JsonArray();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            RegistryValueKind kind = key.GetValueKind(name);
            object? data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(new JsonObject
            {
                ["name"] = name,
                ["kind"] = kind.ToString(),
                ["data"] = FormatData(kind, data),
            });
        }

        acc.Add((relative, new JsonObject { ["path"] = relative, ["values"] = values }));
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using RegistryKey? child = key.OpenSubKey(sub, writable: false);
            if (child is not null)
            {
                Walk(child, relative.Length == 0 ? sub : relative + @"\" + sub, acc);
            }
        }
    }

    private static JsonNode? FormatData(RegistryValueKind kind, object? data)
    {
        return (kind, data) switch
        {
            (RegistryValueKind.DWord, int v) => JsonValue.Create((long)(uint)v),
            (RegistryValueKind.QWord, long v) => JsonValue.Create(v.ToString("x16")),
            (RegistryValueKind.MultiString, string[] v) => new JsonArray(v.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            (RegistryValueKind.String or RegistryValueKind.ExpandString, string v) => JsonValue.Create(v),
            (_, byte[] v) => JsonValue.Create(Convert.ToHexString(v).ToLowerInvariant()),
            (_, null) => null,
            _ => JsonValue.Create(data?.ToString()),
        };
    }

    // Accepts only header lines naming HKEY_CURRENT_USER\Software\BWC-Oracle (optionally deleted with a leading -).
    // Returns the reason the file must not run, or null when every header is inside the allowed root.
    private static string? ValidateHeaders(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('['))
            {
                continue;
            }

            string body = line[1..];
            if (body.StartsWith('-'))
            {
                body = body[1..];
            }

            if (body.EndsWith(']'))
            {
                body = body[..^1];
            }

            if (!body.Equals(Root, StringComparison.OrdinalIgnoreCase) &&
                !body.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase))
            {
                return $"header '{line}' is outside {Root}";
            }

            foreach (string segment in body[Root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or ".." || segment.Contains('[') || segment.Contains(']'))
                {
                    return $"header '{line}' has a path segment '{segment}' that is not allowed";
                }
            }

            if (body.Contains("HKEY_", StringComparison.OrdinalIgnoreCase) && body.IndexOf("HKEY_", 1, StringComparison.OrdinalIgnoreCase) > 0)
            {
                return $"header '{line}' names a second root";
            }
        }

        return null;
    }

    private static Enc DetectEncoding(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return Enc.Utf16LeBom;
        }

        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return Enc.Utf8Bom;
        }

        if (b.Length >= 2 && b[0] != 0 && b[1] == 0)
        {
            return Enc.Utf16LeNoBom;
        }

        return b.Any(x => x > 0x7F) ? Enc.Utf8 : Enc.Ansi;
    }

    private static string Decode(byte[] b, Enc enc) => enc switch
    {
        Enc.Utf16LeBom => Encoding.Unicode.GetString(b, 2, b.Length - 2),
        Enc.Utf8Bom => Encoding.UTF8.GetString(b, 3, b.Length - 3),
        Enc.Utf16LeNoBom => Encoding.Unicode.GetString(b),
        _ => Encoding.UTF8.GetString(b),
    };

    private static byte[] U16(string text, bool bom) =>
        (bom ? new byte[] { 0xFF, 0xFE } : Array.Empty<byte>()).Concat(Encoding.Unicode.GetBytes(text)).ToArray();

    private static byte[] U8(string text, bool bom) =>
        (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Section(string relative) => "[" + Root + (relative.Length == 0 ? string.Empty : @"\" + relative) + "]";

    private static IEnumerable<Case> BuildCases()
    {
        const string v5 = "Windows Registry Editor Version 5.00";
        const string ntxt = "[" + Root + @"\Import\C1]";

        yield return new Case("v5-utf16le-bom-crlf", "Version 5 header, UTF-16LE with BOM, CRLF, blank line after header, typed values.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C1")}\r\n\"Text\"=\"hello\"\r\n\"Num\"=dword:0000002a\r\n\"Bin\"=hex:01,02\r\n\"Multi\"=hex(7):61,00,00,00,00,00\r\n\"Qword\"=hex(b):01,00,00,00,00,00,00,00\r\n", bom: true));

        yield return new Case("v5-utf16le-bom-lf-no-blank", "Version 5 header with LF endings and no blank line after the header.",
            U16($"{v5}\n{Section(@"Import\C2")}\n\"Text\"=\"lf\"\n", bom: true));

        yield return new Case("v5-utf8-bom", "Version 5 header in UTF-8 with a byte-order mark.",
            U8($"{v5}\r\n\r\n{Section(@"Import\C3")}\r\n\"Text\"=\"utf8 bom ü\"\r\n", bom: true));

        yield return new Case("v5-utf8-no-bom", "Version 5 header in UTF-8 without a byte-order mark.",
            U8($"{v5}\r\n\r\n{Section(@"Import\C4")}\r\n\"Text\"=\"utf8 ü\"\r\n", bom: false));

        yield return new Case("v5-utf16le-no-bom", "Version 5 header in UTF-16LE without a byte-order mark.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C5")}\r\n\"Text\"=\"nobom\"\r\n", bom: false));

        yield return new Case("regedit4-ansi-crlf", "REGEDIT4 header, ASCII, CRLF, blank line after header.",
            Ascii($"REGEDIT4\r\n\r\n{Section(@"Import\C6")}\r\n\"Text\"=\"ansi\"\r\n"));

        yield return new Case("regedit4-no-blank-line", "REGEDIT4 header with no blank line after it.",
            Ascii($"REGEDIT4\r\n{Section(@"Import\C7")}\r\n\"Text\"=\"ansi2\"\r\n"));

        yield return new Case("header-version-typo", "Header reads Version 5.0 (missing trailing zero); no section is valid without a known header.",
            U16($"Windows Registry Editor Version 5.0\r\n\r\n{Section(@"Import\C8")}\r\n\"Text\"=\"typo\"\r\n", bom: true));

        yield return new Case("no-header", "No version header; the first line is a section.",
            U16($"{Section(@"Import\C9")}\r\n\"Text\"=\"noheader\"\r\n", bom: true));

        yield return new Case("empty-file", "Zero bytes.", Array.Empty<byte>());

        yield return new Case("missing-ancestors", "Section names keys whose parents do not exist yet; regedit must create the ancestors.",
            U16($"{v5}\r\n\r\n{Section(@"Import\Missing\Ancestor\Deep\Key")}\r\n\"Leaf\"=\"deep\"\r\n", bom: true));

        yield return new Case("root-section-only", "Only the BWC-Oracle root section, with a value and a default value.",
            U16($"{v5}\r\n\r\n{Section(string.Empty)}\r\n\"Root\"=\"value\"\r\n@=\"default value\"\r\n", bom: true));

        yield return new Case("bad-line-middle", "A line that is not a value sits between two good sections.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C10")}\r\n\"First\"=\"ok\"\r\nthis line is not a value\r\n{Section(@"Import\C10\After")}\r\n\"Second\"=\"after the bad line\"\r\n", bom: true));

        yield return new Case("header-missing-close-bracket", "Section header with no closing bracket, followed by a value.",
            U16($"{v5}\r\n\r\n[{Root}\\Import\\C11\r\n\"Text\"=\"noclose\"\r\n", bom: true));

        yield return new Case("header-extra-close-bracket", "Section header with a doubled closing bracket.",
            U16($"{v5}\r\n\r\n[{Root}\\Import\\C12]]\r\n\"Text\"=\"double\"\r\n", bom: true));

        yield return new Case("delete-subkey", "Deletes the Import\\A\\Sub key from the base tree.",
            U16($"{v5}\r\n\r\n[-{Root}\\Import\\A\\Sub]\r\n", bom: true));

        yield return new Case("delete-value", "Deletes the Keep value of Import\\A with \"Keep\"=-.",
            U16($"{v5}\r\n\r\n{Section(@"Import\A")}\r\n\"Keep\"=-\r\n", bom: true));

        yield return new Case("delete-key-then-recreate", "Deletes Import\\A and recreates it with one value.",
            U16($"{v5}\r\n\r\n[-{Root}\\Import\\A]\r\n\r\n{Section(@"Import\A")}\r\n\"Fresh\"=\"new\"\r\n", bom: true));

        yield return new Case("hex-continuation-indented", "Hex value continued on the next line with a leading space after the backslash.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C13")}\r\n\"Bin\"=hex:01,02,\\\r\n  03,04\r\n", bom: true));

        yield return new Case("hex-continuation-no-indent", "Hex value continued on the next line with no indent.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C14")}\r\n\"Bin\"=hex:01,02,\\\r\n03,04\r\n", bom: true));

        yield return new Case("hex-trailing-comma", "Hex value ending with a comma.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C15")}\r\n\"Bin\"=hex:01,02,\r\n", bom: true));

        yield return new Case("hex-empty", "Hex value with no bytes.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C16")}\r\n\"Bin\"=hex:\r\n", bom: true));

        yield return new Case("hex-bad-digit", "Hex value with a non-hex digit.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C17")}\r\n\"Bin\"=hex:zz\r\n\"After\"=\"still here\"\r\n", bom: true));

        yield return new Case("dword-short", "DWORD with one hex digit.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C18")}\r\n\"N\"=dword:1\r\n", bom: true));

        yield return new Case("value-before-section", "A value line before any section header.",
            U16($"{v5}\r\n\r\n\"Orphan\"=\"before\"\r\n{Section(@"Import\C19")}\r\n\"Text\"=\"after\"\r\n", bom: true));

        yield return new Case("duplicate-section", "The same section twice with different values.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C20")}\r\n\"V\"=\"one\"\r\n\r\n{Section(@"Import\C20")}\r\n\"V\"=\"two\"\r\n\"W\"=\"three\"\r\n", bom: true));

        yield return new Case("escaped-string", "Escaped backslash and quote in a string value.",
            U16($"{v5}\r\n\r\n{Section(@"Import\C21")}\r\n\"Esc\"=\"a\\\\b\\\"c\"\r\n", bom: true));

        yield return new Case("refused-dotdot-segment", "Header with a parent-directory segment that would leave the root. Must be refused before running.",
            U16($"{v5}\r\n\r\n[{Root}\\..\\..\\Microsoft]\r\n\"Bad\"=\"x\"\r\n", bom: true));

        yield return new Case("refused-other-root", "Header naming HKEY_LOCAL_MACHINE. Must be refused before running.",
            U16($"{v5}\r\n\r\n[HKEY_LOCAL_MACHINE\\Software\\BWC-Oracle\\Import]\r\n\"Bad\"=\"x\"\r\n", bom: true));

        _ = ntxt;
    }
}
