#if BWC_E2E
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
                "needle");

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

            Assert.AreEqual(2, needleHits.Count);
            AssertHit(needleHits[0], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
            AssertHit(
                needleHits[1],
                runRoot.Append("Zeta"),
                "Multi",
                RegistryFindHitKind.ValueData,
                "needle");

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
    public void DwordAndQwordSearchBothDecimalAndHexText()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath zeta = runRoot.Append("Zeta");

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();

            List<RegistryFindHit> decimalDword = Collect(
                finder,
                new RegistryFindQuery("42", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, decimalDword.Count);
            AssertHit(decimalDword[0], zeta, "Dword", RegistryFindHitKind.ValueData, "42");

            List<RegistryFindHit> hexDword = Collect(
                finder,
                new RegistryFindQuery("0x2A", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, hexDword.Count);
            AssertHit(hexDword[0], zeta, "Dword", RegistryFindHitKind.ValueData, "0x2A");

            List<RegistryFindHit> decimalQword = Collect(
                finder,
                new RegistryFindQuery("4660", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, decimalQword.Count);
            AssertHit(decimalQword[0], zeta, "Qword", RegistryFindHitKind.ValueData, "4660");

            List<RegistryFindHit> hexQword = Collect(
                finder,
                new RegistryFindQuery("0x1234", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, hexQword.Count);
            AssertHit(hexQword[0], zeta, "Qword", RegistryFindHitKind.ValueData, "0x1234");

            List<RegistryFindHit> bothPrefixes = Collect(
                finder,
                new RegistryFindQuery("0x", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(2, bothPrefixes.Count);
            AssertHit(bothPrefixes[0], zeta, "Dword", RegistryFindHitKind.ValueData, "0x2A");
            AssertHit(bothPrefixes[1], zeta, "Qword", RegistryFindHitKind.ValueData, "0x1234");
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void BinarySearchUsesHexDisplayAndNeverDecodesArbitraryBytesAsText()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath zeta = runRoot.Append("Zeta");

        try
        {
            SeedTree(runRoot);
            var finder = new RegistryFinder();

            List<RegistryFindHit> hexHits = Collect(
                finder,
                new RegistryFindQuery("AE 01", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, hexHits.Count);
            AssertHit(hexHits[0], zeta, "Binary", RegistryFindHitKind.ValueData, "AE 01");

            List<RegistryFindHit> shortHexHits = Collect(
                finder,
                new RegistryFindQuery("ae", lookAtData: true, roots: new[] { runRoot }));
            Assert.AreEqual(1, shortHexHits.Count);
            AssertHit(shortHexHits[0], zeta, "Binary", RegistryFindHitKind.ValueData, "AE 01");

            List<RegistryFindHit> textHits = Collect(
                finder,
                new RegistryFindQuery("ne", lookAtData: true, roots: new[] { runRoot }));

            Assert.IsFalse(
                textHits.Any(hit => string.Equals(hit.ValueName, "Utf16Blob", StringComparison.Ordinal)),
                "A binary value was decoded as UTF-16 text.");
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
    public void SearchInSubtreeFalseChecksOnlyTheStartingKey()
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

            Assert.AreEqual(3, rootHits.Count);
            AssertHit(rootHits[0], runRoot, string.Empty, RegistryFindHitKind.ValueData, "default needle");
            AssertHit(rootHits[1], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
            AssertHit(rootHits[2], runRoot, "BetaValue", RegistryFindHitKind.ValueData, "NEEDLE inside");

            RegistryFindHit? singleKeyHit = finder.Find(
                rootQuery,
                RegistryFindCursor.FromKeySelection(zeta));
            Assert.IsNotNull(singleKeyHit);
            AssertHit(singleKeyHit!, zeta, "Multi", RegistryFindHitKind.ValueData, "needle");
            Assert.IsNull(finder.FindNext(rootQuery, singleKeyHit!));
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

    [TestMethod]
    public void MultiStringMatchesIndividualItemsAndTheJoinedDisplay()
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

            List<RegistryFindHit> itemHits = Collect(
                finder,
                new RegistryFindQuery(
                    "needle",
                    lookAtData: true,
                    matchWholeStringOnly: true,
                    roots: new[] { runRoot }));
            Assert.AreEqual(2, itemHits.Count);
            AssertHit(itemHits[0], runRoot, "AlphaValue", RegistryFindHitKind.ValueData, "needle");
            AssertHit(itemHits[1], zeta, "Multi", RegistryFindHitKind.ValueData, "needle");
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
    public void CursorAfterDeletedKeyReturnsNullInsteadOfWrapping()
    {
        string runId = NewRunId();
        RegistryPath runRoot = RunRoot(runId);
        RegistryPath alpha = runRoot.Append("Alpha");

        try
        {
            var store = SeedTree(runRoot);
            store.DeleteKey(alpha, recursive: true);

            var finder = new RegistryFinder();
            var query = new RegistryFindQuery(
                "needle",
                lookAtData: true,
                roots: new[] { runRoot });

            RegistryFindHit? hit = finder.Find(query, RegistryFindCursor.FromKeySelection(alpha));

            Assert.IsNull(hit);
        }
        finally
        {
            DeleteRunRoot(runId);
        }
    }

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

    private static List<RegistryFindHit> Collect(RegistryFinder finder, RegistryFindQuery query)
    {
        const int Limit = 100;
        var hits = new List<RegistryFindHit>();
        RegistryFindHit? hit = finder.Find(query);

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
