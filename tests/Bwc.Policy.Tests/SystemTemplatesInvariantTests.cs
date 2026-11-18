using System.Diagnostics;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

/// <summary>
/// Invariants over the real system template folder. The template set differs between machines and CI images, so no
/// text, name or count is asserted: only properties that must hold for any set.
/// </summary>
[TestClass]
public sealed class SystemTemplatesInvariantTests
{
    private const string SystemFolder = @"C:\Windows\PolicyDefinitions";

    // Measured on the development machine (Windows 11 ARM64): 223 files (the 222 shipped ones plus a BWC probe
    // template) and 3,406 policies. Five loads inside the test process, after the other tests, took 101 to 141 ms,
    // typically about 115 ms.
    private const int MeasuredMilliseconds = 115;

    // About three times the measured time, rounded up. A slower machine that fails this is a regression signal.
    private const int LimitMilliseconds = MeasuredMilliseconds * 3;

    private static TemplateSet Load(out long elapsedMilliseconds)
    {
        if (!Directory.Exists(SystemFolder))
        {
            Assert.Inconclusive("No system template folder on this machine.");
        }

        var watch = Stopwatch.StartNew();
        var set = TemplateLoader.Load([SystemFolder], "en-US");
        elapsedMilliseconds = watch.ElapsedMilliseconds;
        return set;
    }

    [TestMethod]
    public void SystemFolder_LoadsWithoutAnExceptionAndWithTemplates()
    {
        var set = Load(out _);
        Assert.IsTrue(set.Files.Count > 0);
        Assert.IsTrue(set.Policies.Count > 0);
        Assert.IsTrue(set.Categories.Count > 0);
        Assert.IsTrue(set.RootCategories.Count > 0);
    }

    [TestMethod]
    public void SystemFolder_EveryPolicyHasACategoryOrARecordedFailure()
    {
        var set = Load(out _);
        foreach (var policy in set.Policies.Where(p => p.Category is null))
        {
            Assert.IsTrue(
                set.Failures.Any(f =>
                    f.FilePath == policy.File.AdmxPath
                    && f.Line == policy.Line
                    && f.Code is TemplateFailureCode.MissingCategory or TemplateFailureCode.UndefinedCategory or TemplateFailureCode.UndefinedPrefix),
                policy.FullName);
        }
    }

    [TestMethod]
    public void SystemFolder_TreeInvariantsHold()
    {
        var set = Load(out _);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in set.Policies)
        {
            Assert.IsTrue(names.Add(policy.FullName), "duplicate " + policy.FullName);
        }

        foreach (var category in set.Categories)
        {
            var current = category;
            var steps = 0;
            while (current.Parent is not null)
            {
                current = current.Parent;
                Assert.IsTrue(++steps <= set.Categories.Count, "parent chain loops");
            }

            Assert.IsTrue(current.IsRoot || category.IsDetached || current.IsDetached, category.Namespace + ":" + category.Name);
        }

        for (var i = 0; i < set.Policies.Count; i++)
        {
            Assert.AreEqual(i, set.Policies[i].SourceOrder);
        }

        Assert.AreEqual(
            set.Policies.Count(p => p.Category is not null),
            set.Categories.Sum(c => c.Policies.Count));
    }

    [TestMethod]
    public void SystemFolder_EveryResolvedTextIsNonNull_AndPresentationControlsAreLinkedOrRecorded()
    {
        var set = Load(out _);
        foreach (var policy in set.Policies)
        {
            Assert.IsFalse(string.IsNullOrEmpty(policy.DisplayName), policy.FullName);
            Assert.IsNotNull(policy.ExplainText);
            Assert.IsFalse(string.IsNullOrEmpty(policy.Key), policy.FullName);
        }

        var unlinked = set.Policies
            .Where(p => p.Presentation is not null)
            .SelectMany(p => p.Presentation!.Controls.Where(c => c.RefId is not null && c.Element is null).Select(_ => p))
            .Count();
        var recorded = set.Failures.Count(f => f.Code == TemplateFailureCode.UndefinedPresentationRef);
        Assert.AreEqual(unlinked, recorded);
    }

    [TestMethod]
    public void SystemFolder_LoadingTwiceGivesTheSameOrder()
    {
        var first = Load(out _);
        var second = Load(out _);
        CollectionAssert.AreEqual(first.Policies.Select(p => p.FullName).ToArray(), second.Policies.Select(p => p.FullName).ToArray());
        CollectionAssert.AreEqual(first.Failures.ToArray(), second.Failures.ToArray());
    }

    [TestMethod]
    public void SystemFolder_LoadTimeStaysUnderTheLimit()
    {
        var times = new List<long>();
        TemplateSet? set = null;
        for (var i = 0; i < 5; i++)
        {
            set = Load(out var elapsed);
            times.Add(elapsed);
        }

        var message = $"files={set!.Files.Count} policies={set.Policies.Count} categories={set.Categories.Count} "
            + $"failures={set.Failures.Count} runs_ms={string.Join(",", times)} limit_ms={LimitMilliseconds}";
        Assert.IsTrue(times[0] <= LimitMilliseconds, message);
    }

    [TestMethod]
    public void SystemFolder_NoShippedFileIsRejected()
    {
        var set = Load(out _);
        var admx = Directory.GetFiles(SystemFolder, "*.admx").Length;
        Assert.AreEqual(admx, set.Files.Count, FailureAssert.Describe(set));
    }

    [TestMethod]
    public void SystemFolder_OnlyDanglingReferencesAreFailures()
    {
        var set = Load(out _);
        var allowed = new[]
        {
            TemplateFailureCode.UndefinedUsing,
            TemplateFailureCode.UndefinedPrefix,
            TemplateFailureCode.UndefinedCategory,
            TemplateFailureCode.MissingCategory,
            TemplateFailureCode.UndefinedSupportedOn,
        };
        var unexpected = set.Failures.Where(f => !allowed.Contains(f.Code)).ToList();
        Assert.AreEqual(0, unexpected.Count, FailureAssert.Describe(set));
    }
}
