using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.E2E.ValueDisplay;

/// <summary>
/// Compares the Name, Type, and Data cells of the BWC value list with classic regedit for the same sandbox key, in
/// the same run. Each case seeds one key, reads BWC through UI Automation, reads classic regedit (opened on the key
/// through its LastKey applet value), and writes both lists to classic-compare-&lt;case&gt;-&lt;rid&gt;.json under
/// artifacts/e2e/value-display. The run key is removed in finally.
/// </summary>
[TestClass]
public sealed class ValueDisplayTests
{
    private static int _checkStart;

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_ListMatchesClassicForEveryValueType()
    {
        RunCase("types", "Types", SeedEveryValueType, "valuedisplay.types");
        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_ListMatchesClassicForLiteralDefaultWithDefault()
    {
        RunCase("literal-with-default", "LiteralWithDefault", (_, sandbox, key) => SeedLiteralWithDefault(sandbox, key), "valuedisplay.literal.withdefault");
        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_ListMatchesClassicForLiteralDefaultWithoutDefault()
    {
        RunCase("literal-without-default", "LiteralWithoutDefault", (_, sandbox, key) => SeedLiteralWithoutDefault(sandbox, key), "valuedisplay.literal.nodefault");
        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_ListMatchesClassicForLongNameAndData()
    {
        RunCase("long", "LongValues", SeedLongValues, "valuedisplay.long");
        FailIfNeeded();
    }

    [TestMethod]
    [TestCategory("ValueDisplay")]
    [TestCategory("Mutating")]
    public void ValueDisplay_ListMatchesClassicForLongestValueName()
    {
        RunCase("long-name", "LongName", (_, sandbox, key) => SeedLongName(sandbox, key), "valuedisplay.longname", noteWhenDifferent: true);
        FailIfNeeded();
    }

    private static void RunCase(
        string caseId,
        string childKey,
        Action<E2EConfig, SandboxRegistry, string> seed,
        string checkId,
        bool noteWhenDifferent = false)
    {
        _checkStart = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireMutatingFlavor(config);

        try
        {
            config.EnsureExecutablesExist();
        }
        catch (Exception ex)
        {
            E2EResults.Record("valuedisplay.publish", false, ex.Message);
            Assert.Fail(ex.Message);
            return;
        }

        var sandbox = new SandboxRegistry(config);
        try
        {
            sandbox.Cleanup();
            seed(config, sandbox, childKey);

            ValueRow[] bwc;
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("value-display"),
                ["BWC_E2E_SANDBOX_ROOT"] = sandbox.RunRootDisplayPath,
            };

            using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
            {
                bool navigated = ControlTestSupport.NavigateToSandboxKey(session, config, childKey);
                E2EGuards.RequireSandboxNavigation(session, sandbox, navigated, $"{checkId}.navigate");

                bool listed = ControlTestSupport.WaitUntil(
                    () => ClassicValueCompare.ReadBwcRows(session).Length > 0,
                    TimeSpan.FromSeconds(15));
                E2EGuards.RequireSandboxNavigation(session, sandbox, listed, $"{checkId}.list");
                bwc = ClassicValueCompare.ReadBwcRows(session);
                session.Close();
            }

            ValueRow[] classic = ClassicValueCompare.ReadClassicRows(config, ClassicValueCompare.DisplayPathFor(config, childKey));
            _ = ClassicValueCompare.Compare(config, caseId, checkId, bwc, classic, noteWhenDifferent);
        }
        finally
        {
            ControlTestSupport.CleanupRegistry(config);
        }

        FailIfNeeded();
    }

    /// <summary>
    /// The managed types are written through the harness. The other types come through BWC's own silent import, which
    /// the sandbox write policy allows for every type except REG_LINK (SandboxWritePolicy refuses link values).
    /// </summary>
    private static void SeedEveryValueType(E2EConfig config, SandboxRegistry sandbox, string key)
    {
        sandbox.SetString(key, string.Empty, "default text");
        sandbox.SetString(key, "AlphaString", "alpha");
        sandbox.SetString(key, "EmptyString", string.Empty);
        sandbox.SetValueOfKind(key, "Expandable", @"%SystemRoot%\x", RegistryValueKind.ExpandString);
        sandbox.SetValueOfKind(key, "MultiStrings", new[] { "one", string.Empty, "two" }, RegistryValueKind.MultiString);
        sandbox.SetValueOfKind(key, "MultiEmptyFirst", new[] { string.Empty, "after" }, RegistryValueKind.MultiString);
        sandbox.SetValueOfKind(key, "MultiEmpty", Array.Empty<string>(), RegistryValueKind.MultiString);
        sandbox.SetBinary(key, "BinaryEmpty", Array.Empty<byte>());
        sandbox.SetBinary(key, "BinaryShort", new byte[] { 0x01, 0xAB });
        sandbox.SetBinary(key, "BinaryLong", Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        sandbox.SetDword(key, "Dword5000", 5000);
        sandbox.SetValueOfKind(key, "Qword5000", 5000L, RegistryValueKind.QWord);
        sandbox.SetValueOfKind(key, "QwordLarge", unchecked((long)0x0123456789ABCDEF), RegistryValueKind.QWord);
        // The types the managed API cannot write come through BWC's own silent import.
        byte[] resource = { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0 };
        _ = RegFileSeed.Import(config, sandbox, key, "types-exotic.reg", new[]
        {
            new RegFileSeed.SeedValue("NoneEmpty", 0, Array.Empty<byte>()),
            new RegFileSeed.SeedValue("NoneData", 0, new byte[] { 0x0A, 0xDE, 0xAD }),
            new RegFileSeed.SeedValue("DwordBigEndian", 5, new byte[] { 0x11, 0x22, 0x33, 0x44 }),
            new RegFileSeed.SeedValue("ResourceList", 8, resource),
            new RegFileSeed.SeedValue("FullResource", 9, resource),
            new RegFileSeed.SeedValue("Requirements", 10, resource),
            new RegFileSeed.SeedValue("Unknown1234", 0x1234, new byte[] { 0x01, 0x02 }),
            new RegFileSeed.SeedValue("UnknownABCD", 0xABCD, new byte[] { 0x01, 0x02 }),
        });
    }

    /// <summary>A real default value beside a literal (Default) value, and symbol names around them.</summary>
    private static void SeedLiteralWithDefault(SandboxRegistry sandbox, string key)
    {
        sandbox.SetString(key, string.Empty, "default text");
        sandbox.SetString(key, "(Default)", "literal text");
        sandbox.SetString(key, "#hash", "hash");
        sandbox.SetString(key, "(x)", "paren x");
        sandbox.SetString(key, "[y]", "bracket y");
        sandbox.SetString(key, "_under", "underscore");
        sandbox.SetString(key, "zeta", "zeta");
    }

    /// <summary>A literal (Default) value and no real default: the synthetic (Default) row is listed first.</summary>
    private static void SeedLiteralWithoutDefault(SandboxRegistry sandbox, string key)
    {
        sandbox.SetString(key, "(Default)", "literal only");
        sandbox.SetString(key, "Other", "named");
    }

    /// <summary>The longest value name, alone in its key, so no other row can hide it.</summary>
    private static void SeedLongName(SandboxRegistry sandbox, string key)
    {
        sandbox.SetString(key, new string('N', 16383), "long name value");
    }

    private static void SeedLongValues(E2EConfig config, SandboxRegistry sandbox, string key)
    {
        sandbox.SetString(key, "Text192", Pattern(192));
        sandbox.SetString(key, "Text193", Pattern(193));
        sandbox.SetString(key, "Text2500", Pattern(2500));
        string[] many = Enumerable.Range(0, 60).Select(i => "s" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        sandbox.SetValueOfKind(key, "MultiMany", many, RegistryValueKind.MultiString);
    }

    private static string Pattern(int length)
    {
        const string Source = "abcdefghij";
        var builder = new System.Text.StringBuilder(length);
        while (builder.Length < length)
        {
            _ = builder.Append(Source);
        }

        return builder.ToString(0, length);
    }

    private static void FailIfNeeded()
    {
        var failures = E2EResults.FailedChecksSince(_checkStart);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Value display checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
