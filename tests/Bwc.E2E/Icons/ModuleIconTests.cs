using System.Text.Json;
using Bwc.E2E.Controls;
using Bwc.E2E.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bwc.E2E.Icons;

/// <summary>
/// The module icon source of the shared icon provider. The E2E flavor of the Registry Editor runs a self check on its
/// first icon request when BWC_E2E_ICON_REPORT names a file (src/Bwc.UI/Icons/IconSelfCheck.cs) and writes a JSON
/// report. This test launches the shipped editor, waits for the report, and checks it: a regedit icon loaded through
/// the module path (by id and by index) is byte for byte the icon of the old IconKind path at 16 and 32 px, an absent
/// module, id, index or path-like name gives the generic fallback glyph, the cache is per size, and repeated loads do
/// not leak GDI or USER objects. The report is the repeatable artifact:
/// artifacts/e2e/visual/icons/bwc-rid-flavor-module-icons.json.
/// </summary>
[TestClass]
public sealed class ModuleIconTests
{
    private const int MaxHandleDrift = 10;
    private static readonly string[] RequiredFlags =
    [
        "indexMatchesId",
        "missingModuleIsFallback",
        "missingIdIsFallback",
        "indexPastEndIsFallback",
        "negativeIndexIsFallback",
        "zeroIdIsFallback",
        "pathNameIsFallback",
        "defaultIsFallback",
        "genericDiffersFromRegedit",
        "kindFallbackIsFolderGlyph",
    ];

    [ClassInitialize]
    public static void ClassInitialize(TestContext testContext)
    {
        _ = testContext;
        ControlTestSupport.PrepareDpi();
    }

    [TestMethod]
    [TestCategory("Icons")]
    [TestCategory("E2EOnly")]
    public void Icons_ModulePathMatchesRegeditPathAndFallsBack()
    {
        int start = E2EResults.CheckCount;
        var config = GuiGate.Config;
        E2EGuards.RequireE2EOnlyFlavor(config);
        config.EnsureExecutablesExist();

        string directory = Path.Combine(config.ArtifactsDir, "visual", "icons");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, $"bwc-{config.Rid}-{config.Flavor}-module-icons.json");
        File.Delete(report);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [E2EConfig.DataRootEnvironmentVariable] = config.DataRootFor("module-icons"),
            ["BWC_E2E_ICON_REPORT"] = report,
        };

        using (var session = new AppSession(config, config.RegeditExePath, environment: environment))
        {
            bool written = ControlTestSupport.WaitUntil(() => File.Exists(report), TimeSpan.FromSeconds(30));
            E2EResults.Record("icons.module.report", written, $"The editor wrote the self-check report={written}: {report}");
            if (written)
            {
                Check(report);
            }
        }

        var failures = E2EResults.FailedChecksSince(start);
        Assert.AreEqual(
            0,
            failures.Count,
            $"Module icon checks failed:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static void Check(string reportPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(reportPath));
        JsonElement root = document.RootElement;

        bool noError = !root.TryGetProperty("error", out JsonElement error);
        E2EResults.Record("icons.module.noerror", noError, noError ? "The self check ran to the end." : "Self check error: " + error.GetString());
        if (!noError)
        {
            return;
        }

        // The module path returns the same image as the old path for each regedit icon, at both sizes.
        foreach (JsonElement size in root.GetProperty("sizes").EnumerateArray())
        {
            int pixels = size.GetProperty("size").GetInt32();
            var icons = size.GetProperty("icons").EnumerateArray().ToArray();
            bool allLoaded = icons.Length == 4 && icons.All(icon => icon.GetProperty("loaded").GetBoolean());
            bool allIdentical = icons.Length == 4 && icons.All(icon => icon.GetProperty("identical").GetBoolean());
            int distinct = icons.Select(icon => icon.GetProperty("oldHash").GetString()).Distinct(StringComparer.Ordinal).Count();
            E2EResults.Record(
                $"icons.module.same.{pixels}",
                allLoaded && allIdentical && distinct == 4,
                $"At {pixels} px: all four loaded={allLoaded}, module path identical to the kind path={allIdentical}, distinct images={distinct} (required 4).");
        }

        int[] ids = root.GetProperty("regeditGroupIds").EnumerateArray().Select(id => id.GetInt32()).ToArray();
        bool hasIds = new[] { 201, 203, 205, 206 }.All(ids.Contains);
        E2EResults.Record("icons.module.groupids", hasIds, $"regedit.exe group icon ids=[{string.Join(",", ids)}] include 201, 203, 205, 206={hasIds}.");

        foreach (string flag in RequiredFlags)
        {
            bool value = root.GetProperty(flag).GetBoolean();
            E2EResults.Record($"icons.module.{flag}", value, $"{flag}={value} (required true).");
        }

        JsonElement cache = root.GetProperty("cache");
        bool sameInstance = cache.GetProperty("sameInstance").GetBoolean();
        bool otherSize = cache.GetProperty("otherSizeDiffers").GetBoolean();
        int width16 = cache.GetProperty("width16").GetInt32();
        int width32 = cache.GetProperty("width32").GetInt32();
        bool missingCached = cache.GetProperty("missingCachedAsFallback").GetBoolean();
        E2EResults.Record(
            "icons.module.cache",
            sameInstance && otherSize && width16 == 16 && width32 == 32 && missingCached,
            $"Same instance at one scale={sameInstance}; other scale differs={otherSize}; widths {width16} and {width32} (required 16 and 32); fallback cached={missingCached}.");

        JsonElement handles = root.GetProperty("handles");
        int gdi = handles.GetProperty("gdiAfter").GetInt32() - handles.GetProperty("gdiBefore").GetInt32();
        int user = handles.GetProperty("userAfter").GetInt32() - handles.GetProperty("userBefore").GetInt32();
        E2EResults.Record(
            "icons.module.handles",
            gdi <= MaxHandleDrift && user <= MaxHandleDrift,
            $"After {handles.GetProperty("loads").GetInt32()} loads: GDI object change={gdi}, USER object change={user} (each at most {MaxHandleDrift}).");
    }
}
