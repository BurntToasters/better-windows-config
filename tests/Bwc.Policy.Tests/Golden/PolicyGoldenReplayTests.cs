namespace Bwc.Policy.Tests.Golden;

/// <summary>The cases on awaiting-recapture.txt: capture folders whose golden is stale until the user
/// recaptures them.</summary>
internal static class AwaitingRecapture
{
    internal static IReadOnlyList<string> Read()
    {
        if (!File.Exists(GoldenRepo.AwaitingList))
        {
            throw new AssertFailedException("The awaiting-recapture list is missing: " + GoldenRepo.AwaitingList);
        }

        var ids = File.ReadAllLines(GoldenRepo.AwaitingList)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        var duplicate = ids.GroupBy(i => i, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new AssertFailedException("The awaiting-recapture list names a case twice: " + duplicate.Key);
        }

        return ids;
    }
}

/// <summary>
/// The golden replay (piece E6a, docs/failure-modes/policy-save.md, section R): each case of groups A and
/// K is copied into a sandbox GPO folder, its structured operations are saved through <see
/// cref="Bwc.Policy.Model.PolicySave"/> and every file of every recorded step must equal the capture byte
/// for byte. A missing case, a damaged golden or a CAP-4 step fails; none skips.
/// </summary>
[TestClass]
public sealed class PolicyGoldenReplayTests
{
    internal static readonly string[] GroupA =
    [
        "Both_Computer_Then_User", "Both_UnderComputer_CommentOnly", "Both_UnderComputer_Enabled", "Both_UnderUser_CommentOnly",
        "Both_UnderUser_Enabled", "Machine_Apply_Cancel", "Machine_Apply_OK", "Machine_Cancel", "Machine_CommentOnly", "Machine_Disabled",
        "Machine_Enabled", "Machine_EnabledWithComment", "Machine_NotConfigured", "Machine_Then_User", "User_CommentOnly", "User_Disabled",
        "User_Enabled", "User_EnabledWithComment", "User_Then_Machine",
    ];

    internal static readonly string[] GroupK =
    [
        "KeyLeadingBackslash_Enable", "KeyLeadingBackslash_ReadAsWritten", "KeyLeadingBackslash_ReadTrimmed", "KeyTrailingBackslash_Enable",
        "KeyTrailingBackslash_ReadAsWritten", "KeyTrailingBackslash_ReadTrimmed",
    ];

    private static IEnumerable<string> ReplayedCases() =>
        GroupA.Select(c => "A/" + c).Concat(GroupK.Select(c => "K/" + c));

    public static IEnumerable<string[]> Rows() =>
        ReplayedCases().Concat(AwaitingRecapture.Read()).Distinct(StringComparer.Ordinal).Select(c => new[] { c });

    [TestMethod]
    [DynamicData(nameof(Rows))]
    public void Replay_WritesWhatClassicWrote(string caseId)
    {
        if (AwaitingRecapture.Read().Contains(caseId, StringComparer.Ordinal))
        {
            Assert.Inconclusive($"awaiting recapture: {caseId} (tests/Bwc.Policy.Tests/Golden/awaiting-recapture.txt)");
        }

        GoldenReplay.Run(GoldenCase.Load(caseId));
    }

    [TestMethod]
    public void ReplayRowsCoverEveryCaseOfTheGroups()
    {
        var expected = ReplayedCases().Order(StringComparer.Ordinal).ToArray();
        var onDisk = new[] { "A", "K" }
            .SelectMany(g => Directory.GetDirectories(Path.Combine(GoldenRepo.Goldens, g)).Select(d => g + "/" + Path.GetFileName(d)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected, onDisk, "Every case folder of groups A and K needs a replay row, and every row a folder.");
    }

    [TestMethod]
    public void AwaitingRecaptureEntries_AreRealCasesThatStillShowCap4()
    {
        foreach (var id in AwaitingRecapture.Read())
        {
            Assert.IsTrue(GoldenCase.Exists(id), $"The awaiting-recapture list names {id}, which has no capture folder.");
            var golden = GoldenCase.Load(id);
            Assert.IsNotNull(
                UnnoticedEditRule.FindInCase(golden),
                $"{id} no longer shows the CAP-4 pattern (it was recaptured): remove it from the awaiting-recapture list.");
        }
    }

    [TestMethod]
    [TestCategory("ReleaseGate")]
    public void AwaitingRecaptureListIsEmpty()
    {
        var left = AwaitingRecapture.Read();
        Assert.AreEqual(0, left.Count, $"{left.Count} capture case(s) still await a recapture: {string.Join(", ", left)}");
    }
}
