#if BWC_E2E
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Bwc.Registry.Tests;

[TestClass]
public sealed class RegistryFinderTests
{
    private const string SandboxBase = @"Software\BWC-E2E";

    [ClassInitialize]
    public static void EnsureSandboxParent(TestContext testContext)
    {
        _ = testContext;

        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.CreateSubKey(SandboxBase, writable: true);
        Assert.IsNotNull(parent, "The sandbox parent key could not be created.");
    }

    [TestMethod]
    public void KeyOnlySearchFollowsPreOrderAndStopsWithoutWrapping()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "Alpha",
                lookAtKeys: true,
                roots: new[] { runRoot });

            List<RegistryFindHit> hits = Collect(finder, query);

            Assert.AreEqual(2, hits.Count);
            AssertHit(hits[0], runRoot.Append("Alpha"), null, RegistryFindHitKind.KeyName, "Alpha");
            AssertHit(hits[1], runRoot.Append("Alphabet"), null, RegistryFindHitKind.KeyName, "Alphabet");

            var childQuery = new RegistryFindQuery(
                "Child",
                lookAtKeys: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> childHits = Collect(finder, childQuery);

            Assert.AreEqual(1, childHits.Count);
            AssertHit(
                childHits[0],
                runRoot.Append("Alpha").Append("Child"),
                null,
                RegistryFindHitKind.KeyName,
                "Child");

            var fullPathQuery = new RegistryFindQuery(
                @"Alpha\Child",
                lookAtKeys: true,
                roots: new[] { runRoot });
            Assert.AreEqual(0, Collect(finder, fullPathQuery).Count);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ValueOnlySearchFindsRootAndNestedValuesInDisplayOrder()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "Value",
                lookAtValues: true,
                roots: new[] { runRoot });

            List<RegistryFindHit> hits = Collect(finder, query);

            Assert.AreEqual(3, hits.Count);
            AssertHit(hits[0], runRoot, "AlphaValue", RegistryFindHitKind.ValueName, "AlphaValue");
            AssertHit(hits[1], runRoot, "BetaValue", RegistryFindHitKind.ValueName, "BetaValue");
            AssertHit(
                hits[2],
                runRoot.Append("Alpha").Append("Child"),
                "ChildValue",
                RegistryFindHitKind.ValueName,
                "ChildValue");

            var defaultNameQuery = new RegistryFindQuery(
                "Default",
                lookAtValues: true,
                roots: new[] { runRoot });
            Assert.AreEqual(0, Collect(finder, defaultNameQuery).Count);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void DataSearchFindsDefaultStringsAndMultiStringInExactOrder()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            List<RegistryFindHit> hits = Collect(finder, query);

            Assert.AreEqual(4, hits.Count);
            AssertHit(hits[0], runRoot, string.Empty, RegistryFindHitKind.ValueData, "default needle");
            AssertHit(hits[1], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
            AssertHit(hits[2], runRoot, "BetaValue", RegistryFindHitKind.ValueData, "NEEDLE inside");
            AssertHit(
                hits[3],
                runRoot.Append("Zeta"),
                "Multi",
                RegistryFindHitKind.ValueData,
                "one needle two");

            Assert.IsNull(finder.FindNext(query, hits[^1]), "The search wrapped after the last hit.");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void WholeStringMatchingUsesEqualityInsteadOfSubstring()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();

            var needleQuery = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                matchWholeStringOnly: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> needleHits = Collect(finder, needleQuery);

            // The multi-string "one needle two" does not equal the whole string "needle".
            Assert.AreEqual(1, needleHits.Count);
            AssertHit(needleHits[0], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");

            var phraseQuery = new RegistryFindQuery(
                "NEEDLE inside",
                lookAtData: true,
                matchWholeStringOnly: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> phraseHits = Collect(finder, phraseQuery);

            Assert.AreEqual(1, phraseHits.Count);
            AssertHit(phraseHits[0], runRoot, "BetaValue", RegistryFindHitKind.ValueData, "NEEDLE inside");

            var substringQuery = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });
            Assert.AreEqual(4, Collect(finder, substringQuery).Count);

            var multiWholeQuery = new RegistryFindQuery(
                "one needle two",
                lookAtData: true,
                matchWholeStringOnly: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> multiHits = Collect(finder, multiWholeQuery);

            Assert.AreEqual(1, multiHits.Count);
            AssertHit(
                multiHits[0],
                runRoot.Append("Zeta"),
                "Multi",
                RegistryFindHitKind.ValueData,
                "one needle two");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void DwordQwordBinaryAndNoneDataAreNotSearched()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetDword(runRoot, "DwordAb", 0xAB);
            store.SetQword(runRoot, "QwordAb", 0xAB);
            store.SetBinary(runRoot, "BinaryAb", new byte[] { 0xAB, 0xCD });
            store.SetValue(runRoot, "NoneAb", RegistryValueType.None, new byte[] { 0xAB, 0xCD });

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery("ab", lookAtData: true, roots: new[] { runRoot });
            List<RegistryFindHit> hits = Collect(finder, query);

            Assert.AreEqual(
                0,
                hits.Count,
                "DWORD, QWORD, binary, or REG_NONE data produced a Find hit: " +
                string.Join(", ", hits.Select(hit => hit.DisplayText)));

            // The old implementation searched the decimal and hex renderings of numbers.
            Assert.AreEqual(0, Collect(
                finder,
                new RegistryFindQuery("171", lookAtData: true, roots: new[] { runRoot })).Count);
            Assert.AreEqual(0, Collect(
                finder,
                new RegistryFindQuery("0x000000ab", lookAtData: true, roots: new[] { runRoot })).Count);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void StringDataIsSearchedAndWholeStringUsesTheDisplayText()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, "szembedded", "xxabxx");
            store.SetString(runRoot, "szexact", "ab");
            store.SetExpandString(runRoot, "ex", "%ab%");
            store.SetMultiString(runRoot, "ms", new[] { "zz", "ab" });

            var finder = new RegistryFinder();
            var substringQuery = new RegistryFindQuery(
                "ab",
                lookAtData: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> substringHits = Collect(finder, substringQuery);

            Assert.AreEqual(4, substringHits.Count);
            AssertHit(substringHits[0], runRoot, "ex", RegistryFindHitKind.ValueData, "%ab%");
            AssertHit(substringHits[1], runRoot, "ms", RegistryFindHitKind.ValueData, "zz ab");
            AssertHit(substringHits[2], runRoot, "szembedded", RegistryFindHitKind.ValueData, "xxabxx");
            AssertHit(substringHits[3], runRoot, "szexact", RegistryFindHitKind.ValueData, "ab");

            // Whole-string matching compares the Data-column text. The multi-string item "ab"
            // is only one part of the joined text "zz ab", so it must not match.
            var wholeQuery = new RegistryFindQuery(
                "ab",
                lookAtData: true,
                matchWholeStringOnly: true,
                roots: new[] { runRoot });
            List<RegistryFindHit> wholeHits = Collect(finder, wholeQuery);

            Assert.AreEqual(1, wholeHits.Count);
            AssertHit(wholeHits[0], runRoot, "szexact", RegistryFindHitKind.ValueData, "ab");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void SearchStartsAfterTheSelectedKeyOrValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath alpha = runRoot.Append("Alpha");

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var dataQuery = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? afterValue = finder.Find(
                dataQuery,
                RegistryFindCursor.FromValueSelection(runRoot, "AlphaValue"));
            Assert.IsNotNull(afterValue);
            AssertHit(afterValue!, runRoot, "BetaValue", RegistryFindHitKind.ValueData, "NEEDLE inside");

            var valueQuery = new RegistryFindQuery(
                "Value",
                lookAtValues: true,
                roots: new[] { runRoot });
            RegistryFindHit? afterKey = finder.Find(
                valueQuery,
                RegistryFindCursor.FromKeySelection(alpha));

            Assert.IsNotNull(afterKey);
            AssertHit(
                afterKey!,
                runRoot.Append("Alpha").Append("Child"),
                "ChildValue",
                RegistryFindHitKind.ValueName,
                "ChildValue");

            RegistryFindHit? afterRootKey = finder.Find(
                dataQuery,
                RegistryFindCursor.FromKeySelection(runRoot));
            Assert.IsNotNull(afterRootKey);
            AssertHit(
                afterRootKey!,
                runRoot,
                string.Empty,
                RegistryFindHitKind.ValueData,
                "default needle");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void SearchInSubtreeFalseStillSearchesDescendants()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath zeta = runRoot.Append("Zeta");

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();

            var rootQuery = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                searchInSubtree: false,
                roots: new[] { runRoot });
            List<RegistryFindHit> rootHits = Collect(finder, rootQuery);

            // The oracle's keys-values-data-no-subtree run still reached the seeded
            // descendants: the unchecked box does not mean "starting key only".
            Assert.AreEqual(4, rootHits.Count);
            AssertHit(rootHits[0], runRoot, string.Empty, RegistryFindHitKind.ValueData, "default needle");
            AssertHit(rootHits[1], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
            AssertHit(rootHits[2], runRoot, "BetaValue", RegistryFindHitKind.ValueData, "NEEDLE inside");
            AssertHit(
                rootHits[3],
                zeta,
                "Multi",
                RegistryFindHitKind.ValueData,
                "one needle two");

            RegistryFindHit? singleKeyHit = finder.Find(
                rootQuery,
                RegistryFindCursor.FromKeySelection(zeta));
            Assert.IsNotNull(singleKeyHit);
            AssertHit(singleKeyHit!, zeta, "Multi", RegistryFindHitKind.ValueData, "one needle two");
            Assert.IsNull(finder.FindNext(rootQuery, singleKeyHit!));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void MultiStringIsSearchedAsItsJoinedDisplayText()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath zeta = runRoot.Append("Zeta");

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();

            List<RegistryFindHit> joinedHits = Collect(
                finder,
                new RegistryFindQuery("one needle", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, joinedHits.Count);
            AssertHit(
                joinedHits[0],
                zeta,
                "Multi",
                RegistryFindHitKind.ValueData,
                "one needle two");

            // A whole-string search sees "one needle two", so the item "needle" alone does not
            // hit. This is the oracle's keys-values-data-whole behavior for REG_MULTI_SZ.
            List<RegistryFindHit> itemHits = Collect(
                finder,
                new RegistryFindQuery(
                    "needle",
                    lookAtData: true,
                    matchWholeStringOnly: true,
                    roots: new[] { runRoot }));
            Assert.AreEqual(1, itemHits.Count);
            AssertHit(itemHits[0], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void PreCancelledTokenThrowsBeforeAnyRegistryAccess()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsExactly<OperationCanceledException>(
                () => finder.Find(query, after: null, cancellation.Token));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void InvalidQueriesAndCursorsThrow()
    {
        RegistryPath runRoot = RunRoot(NewRunId());
        var finder = new RegistryFinder();

        Assert.ThrowsExactly<ArgumentException>(
            () => new RegistryFindQuery(string.Empty, lookAtKeys: true));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new RegistryFindQuery(null!, lookAtKeys: true));
        Assert.ThrowsExactly<ArgumentException>(
            () => new RegistryFindQuery("needle"));
        Assert.ThrowsExactly<ArgumentException>(
            () => new RegistryFindQuery("needle", lookAtKeys: true, roots: Array.Empty<RegistryPath>()));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => finder.Find(null!));
        Assert.ThrowsExactly<ArgumentException>(
            () => new RegistryFindCursor(runRoot, RegistryFindCursorPhase.AfterValueName));
        Assert.ThrowsExactly<ArgumentException>(
            () => new RegistryFindCursor(runRoot, RegistryFindCursorPhase.AfterKeyName, "value"));

        var query = new RegistryFindQuery("needle", lookAtData: true, roots: new[] { runRoot });
        Assert.ThrowsExactly<ArgumentNullException>(
            () => finder.FindNext(query, null!));
    }

    [TestMethod]
    public void CursorAfterDeletedKeyResumesAtNearestKey()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath alpha = runRoot.Append("Alpha");
        RegistryPath beta = runRoot.Append("Beta");
        RegistryPath gamma = runRoot.Append("Gamma");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);

            foreach (RegistryPath key in new[] { alpha, beta, gamma })
            {
                store.CreateKey(key);
                store.SetString(key, "v", "needle");
            }

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? hit = finder.Find(query);
            Assert.IsNotNull(hit);
            AssertHit(hit!, alpha, "v", RegistryFindHitKind.ValueData, "needle");

            // The cursor key disappears. Classic continues at the first key that sorts after
            // the old name instead of reporting that the search finished.
            store.DeleteKey(alpha, recursive: true);
            hit = finder.FindNext(query, hit!);
            Assert.IsNotNull(hit, "The search stopped after the cursor key was deleted.");
            AssertHit(hit!, beta, "v", RegistryFindHitKind.ValueData, "needle");

            store.DeleteKey(beta, recursive: true);
            hit = finder.FindNext(query, hit!);
            Assert.IsNotNull(hit, "The search stopped after the second cursor key was deleted.");
            AssertHit(hit!, gamma, "v", RegistryFindHitKind.ValueData, "needle");

            // No key sorts after Gamma, so this is a real finish.
            store.DeleteKey(gamma, recursive: true);
            Assert.IsNull(finder.FindNext(query, hit!));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void CursorAfterDeletedAncestorResumesAtNearestKey()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath alpha = runRoot.Append("Alpha");
        RegistryPath inner = alpha.Append("Inner");
        RegistryPath zulu = runRoot.Append("Zulu");

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.CreateKey(alpha);
            store.CreateKey(inner);
            store.SetString(inner, "v", "needle");
            store.CreateKey(zulu);
            store.SetString(zulu, "v", "needle");

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? hit = finder.Find(query);
            Assert.IsNotNull(hit);
            AssertHit(hit!, inner, "v", RegistryFindHitKind.ValueData, "needle");

            // The cursor's parent disappears too, so the nearest position is found by climbing
            // to the root and inserting the old Alpha leaf at its NLS position.
            store.DeleteKey(alpha, recursive: true);
            hit = finder.FindNext(query, hit!);
            Assert.IsNotNull(hit, "The search stopped after the cursor's ancestor was deleted.");
            AssertHit(hit!, zulu, "v", RegistryFindHitKind.ValueData, "needle");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void CursorAfterDeletedValueResumesAtNearestValue()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, "aa", "needle");
            store.SetString(runRoot, "mm", "needle");
            store.SetString(runRoot, "zz", "needle");

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? hit = finder.Find(query);
            Assert.IsNotNull(hit);
            AssertHit(hit!, runRoot, "aa", RegistryFindHitKind.ValueData, "needle");

            store.DeleteValue(runRoot, "aa");
            hit = finder.FindNext(query, hit!);
            Assert.IsNotNull(hit, "The search stopped after the cursor value was deleted.");
            AssertHit(hit!, runRoot, "mm", RegistryFindHitKind.ValueData, "needle");

            store.DeleteValue(runRoot, "mm");
            hit = finder.FindNext(query, hit!);
            Assert.IsNotNull(hit, "The search stopped after the second cursor value was deleted.");
            AssertHit(hit!, runRoot, "zz", RegistryFindHitKind.ValueData, "needle");

            store.DeleteValue(runRoot, "zz");
            Assert.IsNull(finder.FindNext(query, hit!));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void ValueHitIsReportedOnceWhenNameAndDataBothMatch()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, "ab", "AB");

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "ab",
                lookAtValues: true,
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? hit = finder.Find(query);
            Assert.IsNotNull(hit);
            AssertHit(hit!, runRoot, "ab", RegistryFindHitKind.ValueName, "ab");
            Assert.IsNull(
                finder.FindNext(query, hit!),
                "The same value produced a second hit for its data.");

            RegistryFindHit? dataOnly = finder.Find(
                query,
                new RegistryFindCursor(
                    runRoot,
                    RegistryFindCursorPhase.AfterValueName,
                    "ab"));
            Assert.IsNotNull(dataOnly);
            AssertHit(dataOnly!, runRoot, "ab", RegistryFindHitKind.ValueData, "AB");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void FindFlagsRoundTripUsesClassicBits()
    {
        Assert.AreEqual(RegistryFindFlags.MatchWholeString, RegistryFindFlagsCodec.Decode(0x0001));
        Assert.AreEqual(RegistryFindFlags.Keys, RegistryFindFlagsCodec.Decode(0x0002));
        Assert.AreEqual(RegistryFindFlags.Values, RegistryFindFlagsCodec.Decode(0x0004));
        Assert.AreEqual(RegistryFindFlags.Data, RegistryFindFlagsCodec.Decode(0x0008));
        Assert.AreEqual(RegistryFindFlags.SearchInSubtree, RegistryFindFlagsCodec.Decode(0x0010));

        RegistryFindFlags all = RegistryFindFlags.MatchWholeString
            | RegistryFindFlags.Keys
            | RegistryFindFlags.Values
            | RegistryFindFlags.Data
            | RegistryFindFlags.SearchInSubtree;

        Assert.AreEqual(0x1Fu, RegistryFindFlagsCodec.Encode(all));
        Assert.AreEqual(
            RegistryFindFlags.Keys | RegistryFindFlags.Values | RegistryFindFlags.Data,
            RegistryFindFlagsCodec.Decode(0xFFFF000Eu),
            "Only the classic low word is part of the codec.");

        Assert.AreEqual(
            RegistryFindFlags.Default,
            RegistryFindFlagsCodec.DecodeValue(null));
        Assert.AreEqual(
            RegistryFindFlags.Default,
            RegistryFindFlagsCodec.DecodeValue(new RegistryValueInfo(
                RegistryFindFlagsCodec.ValueName,
                (uint)RegistryValueType.String,
                RegistryValueCodec.EncodeString("x"))));
        Assert.AreEqual(
            RegistryFindFlags.Default,
            RegistryFindFlagsCodec.DecodeValue(new RegistryValueInfo(
                RegistryFindFlagsCodec.ValueName,
                (uint)RegistryValueType.Dword,
                new byte[] { 0x1E })));

        RegistryPath runRoot = RegistryPath.Parse(@"HKCU\Software\BWC-E2E\find-flags");
        var query = new RegistryFindQuery(
            "ab",
            RegistryFindFlags.Keys | RegistryFindFlags.Data | RegistryFindFlags.MatchWholeString
                | RegistryFindFlags.SearchInSubtree,
            new[] { runRoot });

        uint dword = RegistryFindFlagsCodec.FromQuery(query);
        Assert.AreEqual(0x001Bu, dword);

        RegistryFindQuery restored = RegistryFindFlagsCodec.CreateQuery("ab", dword, new[] { runRoot });
        Assert.AreEqual(query.SearchText, restored.SearchText);
        Assert.AreEqual(query.LookAtKeys, restored.LookAtKeys);
        Assert.AreEqual(query.LookAtValues, restored.LookAtValues);
        Assert.AreEqual(query.LookAtData, restored.LookAtData);
        Assert.AreEqual(query.MatchWholeStringOnly, restored.MatchWholeStringOnly);
        Assert.AreEqual(query.SearchInSubtree, restored.SearchInSubtree);

        Assert.ThrowsExactly<ArgumentException>(
            () => RegistryFindFlagsCodec.CreateQuery("ab", 0, new[] { runRoot }));
    }

    [TestMethod]
    public void FindFlagsCodecMatchesCapturedOracleBits()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "oracle", "find-sequence.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var mappedOptions = new HashSet<string>(StringComparer.Ordinal);
        bool anyNonZeroFlags = false;

        foreach (JsonElement run in document.RootElement.GetProperty("runs").EnumerateArray())
        {
            string runName = run.GetProperty("name").GetString()!;
            uint findFlags = run.GetProperty("findFlags").GetUInt32();
            anyNonZeroFlags |= findFlags != 0;
            JsonElement flags = run.GetProperty("flags");
            JsonElement layout = run.GetProperty("findFlagsLayout");

            foreach (string option in new[]
            {
                "wholeString",
                "keys",
                "values",
                "data",
                "searchInSubtree",
            })
            {
                JsonElement entry = layout.GetProperty(option);

                if (!entry.TryGetProperty("bit", out JsonElement bitElement))
                {
                    // Classic did not persist this option. Its candidate list must stay empty.
                    Assert.AreEqual(
                        0,
                        entry.GetProperty("candidateBits").GetArrayLength(),
                        $"{runName}: {option} has ambiguous captured bits.");
                    continue;
                }

                uint bit = ParseCapturedBit(bitElement.GetString()!);
                bool optionState = flags.GetProperty(option).GetBoolean();
                Assert.AreEqual(
                    optionState,
                    (findFlags & bit) != 0,
                    $"{runName}: captured {option} bit {bitElement.GetString()} does not match the option.");
                Assert.AreEqual(
                    optionState,
                    (RegistryFindFlagsCodec.Decode(findFlags) & FindFlagForOption(option)) != 0,
                    $"{runName}: RegistryFindFlags does not use the captured {option} bit.");
                mappedOptions.Add(option);
            }

            Assert.AreEqual(
                0,
                layout.GetProperty("unmappedBits").GetArrayLength(),
                $"{runName}: classic wrote an unmapped FindFlags bit.");
        }

        JsonElement dialogMask = document.RootElement.GetProperty("findFlagsDialogMask");
        foreach (string option in new[]
        {
            "wholeString",
            "keys",
            "values",
            "data",
            "searchInSubtree",
        })
        {
            Assert.AreEqual(
                ParseCapturedBit(dialogMask.GetProperty(option).GetString()!),
                RegistryFindFlagsCodec.Encode(FindFlagForOption(option)),
                $"RegistryFindFlags does not use the captured {option} bit.");
        }

        Assert.AreEqual(
            document.RootElement.GetProperty("findFlagsWrittenByClassic").GetBoolean(),
            anyNonZeroFlags,
            "The capture note disagrees with the recorded FindFlags DWORDs.");

        if (!anyNonZeroFlags)
        {
            // Windows 11 26100.8457 does not update the persisted DWORD. The codec layout is
            // confirmed by the dialog's mask table and covered by the round-trip test.
            Assert.AreEqual(
                0,
                mappedOptions.Count,
                "A zero FindFlags DWORD unexpectedly mapped an option bit.");
            return;
        }

        Assert.IsTrue(mappedOptions.Contains("wholeString"), "The capture did not map the whole-string bit.");
        Assert.IsTrue(mappedOptions.Contains("keys"), "The capture did not map the keys bit.");
        Assert.IsTrue(mappedOptions.Contains("values"), "The capture did not map the values bit.");
        Assert.IsTrue(mappedOptions.Contains("data"), "The capture did not map the data bit.");
    }

    private static uint ParseCapturedBit(string text)
    {
        Assert.IsTrue(
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase),
            $"The captured bit '{text}' is not hexadecimal.");
        return Convert.ToUInt32(text[2..], 16);
    }

    private static RegistryFindFlags FindFlagForOption(string option) => option switch
    {
        "wholeString" => RegistryFindFlags.MatchWholeString,
        "keys" => RegistryFindFlags.Keys,
        "values" => RegistryFindFlags.Values,
        "data" => RegistryFindFlags.Data,
        "searchInSubtree" => RegistryFindFlags.SearchInSubtree,
        _ => throw new ArgumentOutOfRangeException(nameof(option)),
    };

    [TestMethod]
    public void FindReturnsNullWhenNothingMatches()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "bwc-no-such-text-" + Guid.NewGuid().ToString("N"),
                lookAtKeys: true,
                lookAtValues: true,
                lookAtData: true,
                roots: new[] { runRoot });

            Assert.IsNull(finder.Find(query));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void OracleSequenceReplaysForEveryCapturedRun()
    {
        string runId = NewRunId();
        RegistryPath runBase = RunRoot(runId);
        RegistryPath runRoot = runBase.Append("Find");
        FindOracleSequence oracle = LoadFindOracleSequence();

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runBase));
            SeedFindOracleTree(store, runRoot, oracle.Seed);

            var finder = new RegistryFinder();
            var results = new List<FindParityRunResult>();

            foreach (FindOracleRun run in oracle.Runs)
            {
                var query = new RegistryFindQuery(
                    oracle.Needle,
                    run.Keys,
                    run.Values,
                    run.Data,
                    run.WholeString,
                    run.SearchInSubtree,
                    new[] { runRoot });

                List<RegistryFindHit> hits = Collect(
                    finder,
                    query,
                    RegistryFindCursor.FromKeySelection(runRoot));
                var actual = hits
                    .Select(hit => new FindOracleHit(
                        OracleRelativeKey(runRoot, hit.KeyPath),
                        hit.ValueName ?? string.Empty))
                    .ToList();

                Assert.AreEqual(
                    run.Hits.Count,
                    actual.Count,
                    $"{run.Name}: expected {run.Hits.Count} hits, got {actual.Count}: " +
                    string.Join(", ", actual.Select(hit => $"{hit.Key} : {hit.Value}")));

                for (int index = 0; index < run.Hits.Count; index += 1)
                {
                    Assert.AreEqual(
                        run.Hits[index].Key,
                        actual[index].Key,
                        $"{run.Name} hit {index}: key");
                    Assert.AreEqual(
                        run.Hits[index].Value,
                        actual[index].Value,
                        $"{run.Name} hit {index}: value");
                }

                results.Add(new FindParityRunResult(
                    run.Name,
                    run.FindFlags,
                    run.Hits.Count,
                    actual.Count,
                    Passed: true,
                    actual));
            }

            WriteFindParityArtifact(oracle.Needle, results);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void OracleSortOrderMatchesTheCapturedClassicOrder()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        (List<string> insertionOrder, List<string> classicOrder) = LoadOracleSortOrder();

        try
        {
            var store = new RegistryStore(new SandboxWritePolicy(runRoot));
            store.CreateKey(runRoot);
            store.SetString(runRoot, string.Empty, "needle");

            foreach (string name in insertionOrder)
            {
                store.SetString(runRoot, name, "needle");
            }

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                matchWholeStringOnly: true,
                roots: new[] { runRoot });

            List<RegistryFindHit> hits = Collect(finder, query);
            List<string> expected = classicOrder
                .Select(name => string.Equals(name, "(Default)", StringComparison.Ordinal)
                    ? string.Empty
                    : name)
                .ToList();
            List<string> actual = hits.Select(hit => hit.ValueName!).ToList();

            CollectionAssert.AreEqual(
                expected,
                actual,
                "The finder did not report values in the captured classic order.");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    private static FindOracleSequence LoadFindOracleSequence()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "oracle", "find-sequence.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        var seed = new List<FindSeedKey>();
        foreach (JsonElement key in root.GetProperty("seed").EnumerateArray())
        {
            var values = new List<FindSeedValue>();
            foreach (JsonElement value in key.GetProperty("values").EnumerateArray())
            {
                values.Add(new FindSeedValue(
                    value.GetProperty("name").GetString()!,
                    value.GetProperty("type").GetString()!,
                    value.GetProperty("data").GetString()!));
            }

            seed.Add(new FindSeedKey(key.GetProperty("key").GetString()!, values));
        }

        var runs = new List<FindOracleRun>();
        foreach (JsonElement run in root.GetProperty("runs").EnumerateArray())
        {
            JsonElement flags = run.GetProperty("flags");
            var hits = new List<FindOracleHit>();

            foreach (JsonElement hit in run.GetProperty("hits").EnumerateArray())
            {
                hits.Add(new FindOracleHit(
                    hit.GetProperty("key").GetString()!,
                    hit.GetProperty("value").GetString()!));
            }

            runs.Add(new FindOracleRun(
                run.GetProperty("name").GetString()!,
                flags.GetProperty("keys").GetBoolean(),
                flags.GetProperty("values").GetBoolean(),
                flags.GetProperty("data").GetBoolean(),
                flags.GetProperty("wholeString").GetBoolean(),
                flags.GetProperty("searchInSubtree").GetBoolean(),
                run.GetProperty("findFlags").GetUInt32(),
                hits));
        }

        return new FindOracleSequence(
            root.GetProperty("needle").GetString()!,
            seed,
            runs);
    }

    private static void SeedFindOracleTree(
        RegistryStore store,
        RegistryPath root,
        IReadOnlyList<FindSeedKey> keys)
    {
        store.CreateKey(root.Parent!);
        store.CreateKey(root);

        foreach (FindSeedKey key in keys)
        {
            RegistryPath path = root;
            string[] components = key.Key.Split('\\');

            // The capture's CreateSubKey created missing intermediate keys. Find\\Zed\\
            // Inner\\abracadabra is seeded without separate Find\\Zed and Find\\Zed\\Inner
            // records, so every component is created here.
            for (int index = 1; index < components.Length; index += 1)
            {
                path = path.Append(components[index]);
                store.CreateKey(path);
            }

            foreach (FindSeedValue value in key.Values)
            {
                switch (value.Type)
                {
                    case "REG_SZ":
                        store.SetString(path, value.Name, value.Data);
                        break;
                    case "REG_EXPAND_SZ":
                        store.SetExpandString(path, value.Name, value.Data);
                        break;
                    case "REG_MULTI_SZ":
                        store.SetMultiString(
                            path,
                            value.Name,
                            value.Data.Length == 0
                                ? Array.Empty<string>()
                                : value.Data.Split('|'));
                        break;
                    case "REG_DWORD":
                        store.SetValue(
                            path,
                            value.Name,
                            RegistryValueType.Dword,
                            Convert.FromHexString(value.Data));
                        break;
                    case "REG_BINARY":
                        store.SetBinary(path, value.Name, Convert.FromHexString(value.Data));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"The find oracle uses an unsupported type '{value.Type}'.");
                }
            }
        }
    }

    private static string OracleRelativeKey(RegistryPath root, RegistryPath hitPath)
    {
        if (hitPath.Equals(root))
        {
            return "Find";
        }

        Assert.IsTrue(hitPath.IsDescendantOf(root), $"'{hitPath}' is outside the oracle root.");
        return "Find\\" + string.Join('\\', hitPath.Components.Skip(root.Components.Count));
    }

    private static (List<string> InsertionOrder, List<string> ClassicOrder) LoadOracleSortOrder()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "oracle", "sort-order.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        return (
            root.GetProperty("insertionOrder").EnumerateArray()
                .Select(item => item.GetString()!)
                .ToList(),
            root.GetProperty("classicOrder").EnumerateArray()
                .Select(item => item.GetString()!)
                .ToList());
    }

    private static void WriteFindParityArtifact(
        string needle,
        IReadOnlyList<FindParityRunResult> results)
    {
        string? repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
        {
            return;
        }

        var payload = new
        {
            generatedBy = "RegistryFinderTests.OracleSequenceReplaysForEveryCapturedRun",
            oracle = "docs/parity/oracle/find-sequence.json",
            needle,
            runs = results,
            tests = new[]
            {
                "OracleSequenceReplaysForEveryCapturedRun",
                "OracleSortOrderMatchesTheCapturedClassicOrder",
                "CursorAfterDeletedKeyResumesAtNearestKey",
                "CursorAfterDeletedAncestorResumesAtNearestKey",
                "CursorAfterDeletedValueResumesAtNearestValue",
                "ValueHitIsReportedOnceWhenNameAndDataBothMatch",
                "DwordQwordBinaryAndNoneDataAreNotSearched",
                "StringDataIsSearchedAndWholeStringUsesTheDisplayText",
                "FindFlagsRoundTripUsesClassicBits",
                "FindFlagsCodecMatchesCapturedOracleBits",
            },
        };

        try
        {
            string directory = Path.Combine(repositoryRoot, "artifacts", "find-parity");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "result.json"),
                JsonSerializer.Serialize(
                    payload,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }));
        }
        catch (IOException)
        {
            // The artifact is diagnostic. A read-only test output tree must not fail the run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                current.FullName,
                "docs",
                "parity",
                "oracle",
                "find-sequence.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private sealed record FindOracleSequence(
        string Needle,
        IReadOnlyList<FindSeedKey> Seed,
        IReadOnlyList<FindOracleRun> Runs);

    private sealed record FindSeedKey(string Key, IReadOnlyList<FindSeedValue> Values);

    private sealed record FindSeedValue(string Name, string Type, string Data);

    private sealed record FindOracleRun(
        string Name,
        bool Keys,
        bool Values,
        bool Data,
        bool WholeString,
        bool SearchInSubtree,
        uint FindFlags,
        IReadOnlyList<FindOracleHit> Hits);

    private sealed record FindOracleHit(string Key, string Value);

    private sealed record FindParityRunResult(
        string Name,
        uint FindFlags,
        int ExpectedHits,
        int ActualHits,
        bool Passed,
        IReadOnlyList<FindOracleHit> Hits);

    private static RegistryStore SeedTree(RegistryPath runRoot)
    {
        var store = new RegistryStore(new SandboxWritePolicy(runRoot));
        store.CreateKey(runRoot);

        store.SetString(runRoot, string.Empty, "default needle");
        store.SetString(runRoot, "AlphaValue", "needle");
        store.SetString(runRoot, "BetaValue", "NEEDLE inside");

        RegistryPath alpha = runRoot.Append("Alpha");
        store.CreateKey(alpha);
        RegistryPath child = alpha.Append("Child");
        store.CreateKey(child);
        store.SetString(child, "ChildValue", "child text");

        store.CreateKey(runRoot.Append("Alphabet"));
        store.CreateKey(runRoot.Append("Beta"));

        RegistryPath zeta = runRoot.Append("Zeta");
        store.CreateKey(zeta);
        store.SetBinary(zeta, "Binary", new byte[] { 0xAE, 0x01 });
        store.SetDword(zeta, "Dword", 42u);
        store.SetMultiString(zeta, "Multi", new[] { "one", "needle", "two" });
        store.SetQword(zeta, "Qword", 0x1234UL);

        // Bytes that spell "ne" as UTF-16LE. A hex search for "ne" must not see this as text.
        store.SetBinary(zeta, "Utf16Blob", new byte[] { 0x6E, 0x00, 0x65, 0x00 });

        return store;
    }

    private static List<RegistryFindHit> Collect(
        RegistryFinder finder,
        RegistryFindQuery query,
        RegistryFindCursor? firstCursor = null)
    {
        const int Limit = 100;
        var hits = new List<RegistryFindHit>();
        RegistryFindHit? hit = finder.Find(query, firstCursor);

        while (hit is not null)
        {
            hits.Add(hit);
            Assert.IsTrue(hits.Count <= Limit, "The finder produced too many hits, likely a repeat loop.");

            if (hits.Count == Limit)
            {
                break;
            }

            hit = finder.FindNext(query, hit);
        }

        return hits;
    }

    private static void AssertHit(
        RegistryFindHit hit,
        RegistryPath keyPath,
        string? valueName,
        RegistryFindHitKind kind,
        string matchedText)
    {
        Assert.AreEqual(keyPath, hit.KeyPath, "KeyPath");
        Assert.AreEqual(valueName, hit.ValueName, "ValueName");
        Assert.AreEqual(kind, hit.Kind, "Kind");
        Assert.AreEqual(matchedText, hit.MatchedText, "MatchedText");
    }

    private static string NewRunId() => "find-" + Guid.NewGuid().ToString("N");

    private static RegistryPath RunRoot(string runId) =>
        RegistryPath.Parse($@"HKCU\{SandboxBase}\{runId}");

    private static void DeleteRunRoot(string runId)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? parent = hkcu.OpenSubKey(SandboxBase, writable: true);
        parent?.DeleteSubKeyTree(runId, throwOnMissingSubKey: false);
    }
}
#endif
