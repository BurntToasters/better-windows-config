#if BWC_E2E
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Replays every case in docs/parity/oracle/import-behavior.json. Each file is rewritten so its
/// BWC-Oracle header lands in a fresh sandbox run root, imported through the file path, and the
/// resulting tree is compared with the tree classic regedit /s produced. Refused cases must leave
/// the base tree unchanged.
/// </summary>
[TestClass]
public sealed class RegFileOracleImportTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const string OracleRootPrefix = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string Separator = "\u001f";
    private const int ErrorFileNotFound = 2;

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        RegistryPath sandboxBase = RegistryPath.Parse($@"HKCU\{SandboxBase}");
        var store = new RegistryStore(new SandboxWritePolicy(sandboxBase));
        store.CreateKey(sandboxBase);
        Assert.IsTrue(store.Reader.KeyExists(sandboxBase), "The sandbox parent key could not be created.");
    }

    public static IEnumerable<object[]> OracleCaseNames()
    {
        foreach (OracleCase oracleCase in LoadCases())
        {
            yield return new object[] { oracleCase.Name };
        }
    }

    [TestMethod]
    public void OracleFileIsTheRecordedClassicRun()
    {
        IReadOnlyList<OracleCase> cases = LoadCases();

        // The file holds 29 recorded cases. The README in docs/parity/oracle says 30.
        Assert.AreEqual(29, cases.Count);
        Assert.AreEqual(cases.Count, cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    [DynamicData(nameof(OracleCaseNames))]
    public void ReplayOracleCase(string caseName)
    {
        OracleCase oracleCase = LoadCases().Single(c => c.Name == caseName);
        string runId = "oracle-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RunRoot(runId);
        string file = Path.Combine(Path.GetTempPath(), "bwc-oracle-" + runId + ".reg");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            SeedBaseTree(store, runRoot);

            string eol = ChooseLineEnding(oracleCase);
            byte[] sandboxed = Encode(
                RewriteRoot(oracleCase.Lines, runId),
                eol,
                oracleCase.Encoding);
            File.WriteAllBytes(file, sandboxed);

            RegFileImportResult result = RegFileImporter.ApplyFile(store, file, CancellationToken.None);

            if (oracleCase.Refused is not null)
            {
                // The capture tool refused to run these, so there is no classic tree. The sandbox
                // must still end with the base tree: the file is refused, or every denied write
                // is reported and none lands.
                Assert.AreEqual(0, result.KeysWritten, "A refused file must write no key.");
                Assert.AreEqual(0, result.ValuesWritten, "A refused file must write no value.");
                Assert.IsTrue(
                    result.IsRefused || result.HasWriteFailures,
                    "A refused file must be refused or report the denied writes.");
                AssertTree(
                    ReadTree(store, runRoot),
                    BaseTreeExpectation(),
                    caseName);
                return;
            }

            Assert.IsTrue(oracleCase.Tree.HasValue, "A case without a refusal must record a tree.");
            Assert.IsTrue(oracleCase.Tree.Value.GetProperty("present").GetBoolean());

            if (result.IsRefused)
            {
                // Classic imported nothing from a file it could not read as a registry file. BWC
                // refuses the same files with the same outcome: the tree is still the base tree.
                Assert.AreEqual(
                    RegFileRefusal.NotRegistryFile,
                    result.Refusal,
                    $"{caseName}: only a file that is not a registry file may be refused here.");
            }
            else
            {
                Assert.IsFalse(
                    result.HasWriteFailures,
                    "Every write in the sandbox must succeed: " + DescribeFailures(result));
            }

            AssertTree(
                ReadTree(store, runRoot),
                ExpectationFromOracle(oracleCase.Tree.Value),
                caseName);
        }
        finally
        {
            File.Delete(file);
            DeleteRunRoot(runId);
        }
    }

    private static IReadOnlyList<OracleCase> LoadCases()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "oracle", "import-behavior.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));

        var cases = new List<OracleCase>();
        foreach (JsonElement element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string? refused = element.TryGetProperty("refused", out JsonElement refusedElement)
                ? refusedElement.GetString()
                : null;
            JsonElement? tree = element.TryGetProperty("tree", out JsonElement treeElement)
                ? treeElement.Clone()
                : (JsonElement?)null;

            cases.Add(new OracleCase(
                element.GetProperty("name").GetString()!,
                element.GetProperty("encoding").GetString()!,
                element.GetProperty("bytes").GetInt32(),
                element.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray(),
                refused,
                tree));
        }

        return cases;
    }

    // The recorded byte count decides the line ending: the oracle files are CRLF except the one
    // whose description says LF. The chosen ending must reproduce the recorded size exactly.
    private static string ChooseLineEnding(OracleCase oracleCase)
    {
        byte[] crlf = Encode(oracleCase.Lines, "\r\n", oracleCase.Encoding);

        if (crlf.Length == oracleCase.Bytes)
        {
            return "\r\n";
        }

        byte[] lf = Encode(oracleCase.Lines, "\n", oracleCase.Encoding);
        Assert.AreEqual(
            oracleCase.Bytes,
            lf.Length,
            $"{oracleCase.Name}: neither CRLF nor LF reproduces the recorded byte count.");
        return "\n";
    }

    // Every line, including the last, ends with the line ending. The one file with no content at
    // all (a single empty line) is zero bytes.
    private static byte[] Encode(IReadOnlyList<string> lines, string eol, string encoding)
    {
        string text = string.Join(eol, lines);

        if (text.Length > 0)
        {
            text += eol;
        }

        return encoding switch
        {
            "Utf16LeBom" => Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(text)),
            "Utf16LeNoBom" => Encoding.Unicode.GetBytes(text),
            "Utf8Bom" => Concat(new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(false).GetBytes(text)),
            "Utf8" => new UTF8Encoding(false).GetBytes(text),
            "Ansi" => Encoding.Latin1.GetBytes(text),
            _ => throw new InvalidOperationException($"Unknown oracle encoding '{encoding}'."),
        };
    }

    private static byte[] Concat(byte[] prefix, byte[] body)
    {
        var bytes = new byte[prefix.Length + body.Length];
        prefix.CopyTo(bytes, 0);
        body.CopyTo(bytes, prefix.Length);
        return bytes;
    }

    private static IReadOnlyList<string> RewriteRoot(IReadOnlyList<string> lines, string runId)
    {
        string sandboxPrefix = $@"HKEY_CURRENT_USER\{SandboxBase}\{runId}";

        return lines
            .Select(line => line.Replace(OracleRootPrefix, sandboxPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static void SeedBaseTree(RegistryStore store, RegistryPath runRoot)
    {
        RegistryPath import = runRoot.Append("Import");
        RegistryPath a = import.Append("A");
        RegistryPath sub = a.Append("Sub");
        RegistryPath b = import.Append("B");

        store.CreateKey(runRoot);
        store.CreateKey(import);
        store.CreateKey(a);
        store.SetString(a, "Keep", "1");
        store.CreateKey(sub);
        store.SetString(sub, "S", "x");
        store.CreateKey(b);
        store.SetDword(b, "Other", 1);
    }

    private static IReadOnlyList<TreeRow> BaseTreeExpectation() =>
        new List<TreeRow>
        {
            new("", null),
            new("Import", null),
            new("Import\\A", null),
            new("Import\\A", "Keep|String|1"),
            new("Import\\A\\Sub", null),
            new("Import\\A\\Sub", "S|String|x"),
            new("Import\\B", null),
            new("Import\\B", "Other|DWord|1"),
        };

    private static IReadOnlyList<TreeRow> ExpectationFromOracle(JsonElement tree)
    {
        var rows = new List<TreeRow>();

        foreach (JsonElement key in tree.GetProperty("keys").EnumerateArray())
        {
            string path = key.GetProperty("path").GetString()!;
            rows.Add(new TreeRow(path, null));

            foreach (JsonElement value in key.GetProperty("values").EnumerateArray())
            {
                rows.Add(new TreeRow(path, OracleValue(value)));
            }
        }

        return rows;
    }

    private static string OracleValue(JsonElement value)
    {
        string name = value.GetProperty("name").GetString()!;
        string kind = value.GetProperty("kind").GetString()!;
        JsonElement data = value.GetProperty("data");

        string canonical = kind switch
        {
            "String" => data.GetString()!,
            "DWord" => data.GetUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "QWord" => data.GetString()!,
            "Binary" => data.GetString()!,
            "MultiString" => string.Join(Separator, data.EnumerateArray().Select(item => item.GetString()!)),
            _ => throw new InvalidOperationException($"Unknown oracle kind '{kind}'."),
        };

        return $"{name}|{kind}|{canonical}";
    }

    private static IReadOnlyList<TreeRow> ReadTree(RegistryStore store, RegistryPath runRoot)
    {
        var rows = new List<TreeRow>();
        Visit(store, runRoot, string.Empty, rows);
        return rows;
    }

    private static void Visit(RegistryStore store, RegistryPath key, string relative, List<TreeRow> rows)
    {
        rows.Add(new TreeRow(relative, null));

        foreach (RegistryValueInfo value in store.GetValues(key))
        {
            rows.Add(new TreeRow(relative, ActualValue(value)));
        }

        foreach (string child in store.Reader.EnumerateSubKeyNames(key))
        {
            string childRelative = relative.Length == 0 ? child : relative + "\\" + child;
            Visit(store, key.Append(child), childRelative, rows);
        }
    }

    private static string ActualValue(RegistryValueInfo value)
    {
        string kind = value.Type switch
        {
            1 => "String",
            3 => "Binary",
            4 => "DWord",
            7 => "MultiString",
            11 => "QWord",
            _ => "Type" + value.Type.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        string canonical = value.Type switch
        {
            1 => FirstString(Encoding.Unicode.GetString(value.Data)),
            4 => BitConverter.ToUInt32(value.Data).ToString(System.Globalization.CultureInfo.InvariantCulture),
            7 => string.Join(
                Separator,
                Encoding.Unicode.GetString(value.Data).TrimEnd('\0').Split('\0')),
            11 => BitConverter.ToUInt64(value.Data).ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            3 => Convert.ToHexString(value.Data).ToLowerInvariant(),
            _ => Convert.ToHexString(value.Data).ToLowerInvariant(),
        };

        return $"{value.Name}|{kind}|{canonical}";
    }

    private static string FirstString(string text)
    {
        int nul = text.IndexOf('\0', StringComparison.Ordinal);
        return nul >= 0 ? text[..nul] : text;
    }

    private static void AssertTree(
        IReadOnlyList<TreeRow> actual,
        IReadOnlyList<TreeRow> expected,
        string caseName)
    {
        CollectionAssert.AreEqual(
            expected.Select(row => row.ToString()).OrderBy(text => text, StringComparer.Ordinal).ToList(),
            actual.Select(row => row.ToString()).OrderBy(text => text, StringComparer.Ordinal).ToList(),
            $"{caseName}: the sandbox tree differs from the classic result.");
    }

    private static string DescribeFailures(RegFileImportResult result) =>
        string.Join(
            "; ",
            result.Failures.Select(f => $"line {f.Line} {f.Kind}: {f.Message}"));

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static void DeleteRunRoot(string runId)
    {
        RegistryPath runRoot = RunRoot(runId);
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));

        try
        {
            store.DeleteKey(runRoot, recursive: true);
        }
        catch (RegistryOperationException error) when (error.Win32Error == ErrorFileNotFound)
        {
            // The run never created its root, so there is nothing to remove.
        }
    }

    private sealed record OracleCase(
        string Name,
        string Encoding,
        int Bytes,
        IReadOnlyList<string> Lines,
        string? Refused,
        JsonElement? Tree);

    private sealed record TreeRow(string Path, string? Value)
    {
        public override string ToString() => Value is null ? $"K|{Path}" : $"V|{Path}|{Value}";
    }
}
#endif
