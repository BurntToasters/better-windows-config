#if BWC_E2E
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.Registry.Tests;

/// <summary>
/// Golden text export. The seed <c>bwc-oracle-text-seed.reg</c> is imported under a sandbox run key, exported with
/// <see cref="RegistryTextExporter"/>, and compared byte for byte with <c>bwc-oracle-text.txt</c>, a real classic
/// regedit Text Files export of the same data. Only the Last Write Time values are masked on both sides of the
/// comparison, as the golden masks them. See docs/failure-modes/export.md, section 2.
/// </summary>
[TestClass]
public sealed class RegistryTextGoldenTests
{
    private const string SandboxBase = @"Software\BWC-E2E";
    private const string OracleKeyText = @"HKEY_CURRENT_USER\Software\BWC-Oracle";
    private const string KeyNamePrefix = "Key Name:          ";
    private const string LastWritePrefix = "Last Write Time:   ";
    private const string LastWriteMasked = LastWritePrefix + "<TIME>";
    private const string Crlf = "\r\n";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        RegistryPath sandboxBase = RegistryPath.Parse($@"HKCU\{SandboxBase}");
        var store = new RegistryStore(new SandboxWritePolicy(sandboxBase));
        store.CreateKey(sandboxBase);
        Assert.IsTrue(store.Reader.KeyExists(sandboxBase), "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void SeedExportMatchesTheClassicTextGoldenByteForByte()
    {
        string runId = "golden-" + Guid.NewGuid().ToString("N");
        RegistryPath runRoot = RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");
        RegistryPath oracleKey = runRoot.Append("BWC-Oracle");
        string sandboxKeyText = $@"HKEY_CURRENT_USER\{SandboxBase}\{runId}\BWC-Oracle";
        string seedFile = Path.Combine(AppContext.BaseDirectory, "oracle", "bwc-oracle-text-seed.reg");
        string goldenFile = Path.Combine(AppContext.BaseDirectory, "oracle", "bwc-oracle-text.txt");
        string importFile = Path.Combine(Path.GetTempPath(), "bwc-golden-" + runId + ".reg");

        try
        {
            var store = new RegistryStore(new LinkValueSandboxPolicy(runRoot));
            store.CreateKey(runRoot);

            string seedText = File.ReadAllText(seedFile, Encoding.Unicode)
                .Replace(OracleKeyText, sandboxKeyText, StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(importFile, WithBom(seedText));

            RegFileImportResult result = RegFileImporter.ApplyFile(store, importFile, CancellationToken.None);
            Assert.IsFalse(result.IsRefused, $"The seed was refused: {result.Refusal}.");
            Assert.IsFalse(result.HasWriteFailures, "The seed import had write failures: " + DescribeFailures(result));

            var writer = new StringWriter();
            RegistryTextExporter.Export(store.Reader, oracleKey, writer, CancellationToken.None);
            string actualText = MaskAndRewrite(writer.ToString(), sandboxKeyText);

            byte[] goldenBytes = File.ReadAllBytes(goldenFile);
            Assert.IsTrue(
                goldenBytes.Length >= 2 && goldenBytes[0] == 0xFF && goldenBytes[1] == 0xFE,
                "The golden must be UTF-16LE with a byte order mark.");
            string goldenText = Encoding.Unicode.GetString(goldenBytes, 2, goldenBytes.Length - 2);

            AssertSameLines(goldenText, actualText);
            CollectionAssert.AreEqual(
                goldenBytes,
                WithBom(actualText),
                "The text matches line for line, but the bytes differ (BOM or line endings).");
        }
        finally
        {
            if (File.Exists(importFile))
            {
                File.Delete(importFile);
            }

            SandboxRunCleanup.RemoveRunTree(runId);
        }
    }

    // The sandbox policy refuses REG_LINK values, and the seed has one (OddTypes\Link6). This wrapper allows that
    // value type and nothing else. Every path is still decided by SandboxWritePolicy, so writes stay in the run key.
    private sealed class LinkValueSandboxPolicy : IRegistryWritePolicy
    {
        private readonly SandboxWritePolicy _inner;

        public LinkValueSandboxPolicy(RegistryPath sandboxRoot)
        {
            _inner = new SandboxWritePolicy(sandboxRoot);
        }

        public bool AllowsLinkValues => true;

        public bool RejectsSymbolicLinks => _inner.RejectsSymbolicLinks;

        public void EnsureWriteAllowed(string? computerName, RegistryPath path, RegistryWriteOperation operation) =>
            _inner.EnsureWriteAllowed(computerName, path, operation);
    }

    private static byte[] WithBom(string text)
    {
        byte[] body = Encoding.Unicode.GetBytes(text);
        byte[] bom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetPreamble();
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);
        return bytes;
    }

    // Masks every Last Write Time value and writes the sandbox key path as the golden spells it.
    private static string MaskAndRewrite(string text, string sandboxKeyText)
    {
        string[] lines = text.Split(Crlf);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.StartsWith(LastWritePrefix, StringComparison.Ordinal))
            {
                lines[index] = LastWriteMasked;
            }
            else if (line.StartsWith(KeyNamePrefix, StringComparison.Ordinal))
            {
                lines[index] = line.Replace(sandboxKeyText, OracleKeyText, StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Join(Crlf, lines);
    }

    // Names the first line that differs, with both sides, so a mismatch is readable without a diff tool.
    private static void AssertSameLines(string golden, string actual)
    {
        string[] expectedLines = golden.Split(Crlf);
        string[] actualLines = actual.Split(Crlf);
        int count = Math.Max(expectedLines.Length, actualLines.Length);

        for (int index = 0; index < count; index++)
        {
            string expectedLine = index < expectedLines.Length ? expectedLines[index] : "<end of golden>";
            string actualLine = index < actualLines.Length ? actualLines[index] : "<end of export>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"The text export differs from the golden at line {index + 1}." + Environment.NewLine +
                    $"  golden: [{expectedLine}]" + Environment.NewLine +
                    $"  export: [{actualLine}]");
            }
        }
    }

    private static string DescribeFailures(RegFileImportResult result) =>
        string.Join(
            "; ",
            result.Failures.Select(failure => $"line {failure.Line} {failure.Kind}: {failure.Message}"));
}
#endif
