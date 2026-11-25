using System.Diagnostics;
using System.Text;
using Bwc.Policy.Admx;

namespace Bwc.Policy.Tests;

/// <summary>
/// A template with a flood of faulty items (docs/failure-modes/policy-admx.md, section 47): the failures of one file are
/// capped and the file text is read at most once.
/// </summary>
[TestClass]
public sealed class FailureFloodTests
{
    private const int FaultyItems = 10_000;

    // Stated limit for loading the flood template. The capped load takes well under a second on the development machine;
    // before the fix each failure re-read and re-scanned the whole file.
    private const int LimitMilliseconds = 5_000;

    /// <summary>A policy with half the faults as list items with no key and half as booleans with no valueName.</summary>
    private static string FloodPolicy()
    {
        var text = new StringBuilder("""<policy name="Flood" class="Machine" displayName="$(string.Flood)" key="K"><parentCategory ref="Root" /><enabledList>""");
        for (var i = 0; i < FaultyItems / 2; i++)
        {
            text.Append("<item valueName=\"V").Append(i).Append("\" />");
        }

        text.Append("</enabledList><elements>");
        for (var i = 0; i < FaultyItems / 2; i++)
        {
            text.Append("<boolean id=\"B").Append(i).Append("\" />");
        }

        return text.Append("</elements></policy>").ToString();
    }

    private static string WriteFlood(TempTemplates temp)
    {
        temp.AddSimple("Flood", "T.Flood", FloodPolicy(), """<string id="Flood">Flood</string>""");
        return Path.Combine(temp.Root, "Flood.admx");
    }

    [TestMethod]
    public void TenThousandFaultyItems_LoadWithinTheLimit_AndRecordAtMostTheCap()
    {
        using var temp = new TempTemplates();
        var admx = WriteFlood(temp);

        var watch = Stopwatch.StartNew();
        var set = temp.Load();
        var elapsed = watch.ElapsedMilliseconds;

        var message = $"elapsed_ms={elapsed} limit_ms={LimitMilliseconds} failures={set.Failures.Count}";
        Assert.IsTrue(elapsed <= LimitMilliseconds, message);
        Assert.AreEqual(AdmxLimits.MaxFailuresPerFile + 1, set.Failures.Count, message);
        Assert.IsTrue(set.Failures.All(f => f.FilePath == admx), message);
        var last = set.Failures[^1];
        Assert.AreEqual(TemplateFailureCode.TooManyFailures, last.Code);
        Assert.AreEqual(1, set.Failures.Count(f => f.Code == TemplateFailureCode.TooManyFailures));
        Assert.IsTrue(
            set.Failures.Take(AdmxLimits.MaxFailuresPerFile).All(f => f.Code == TemplateFailureCode.MissingAttribute),
            FailureAssert.Describe(set));
        FailureAssert.NothingLoaded(set);
    }

    [TestMethod]
    public void TheFileText_IsReadOnceMore_NotOncePerFailure()
    {
        using var temp = new TempTemplates();
        var admx = WriteFlood(temp);
        var adml = Path.Combine(temp.Root, "en-US", "Flood.adml");
        var sink = new FailureSink(AdmxLimits.MaxFailuresPerFile);
        var reads = new List<string>();
        string? Counting(string path)
        {
            reads.Add(path);
            return SafeXml.ReadText(path);
        }

        var root = SafeXml.Load(admx, sink)!;
        var context = new ParseContext(admx, sink, Counting);
        var file = AdmxParser.ReadHeader(root, admx, context)!;
        var parsedAdml = AdmlParser.Parse(SafeXml.Load(adml, sink)!, new ParseContext(adml, sink))!;
        AdmxParser.ParseBody(root, file, parsedAdml, context);

        Assert.AreEqual(AdmxLimits.MaxFailuresPerFile + 1, sink.Items.Count);
        CollectionAssert.AreEqual(new[] { admx }, reads, "the ADMX text is read once, and only the ADMX");
    }

    [TestMethod]
    public void ReadText_RefusesWhatTheXmlLoadRefuses()
    {
        using var temp = new TempTemplates();
        var good = temp.Write("Good.txt", "a\r\nb");
        var big = Path.Combine(temp.Root, "Big.txt");
        using (var stream = new FileStream(big, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(AdmxLimits.MaxFileBytes + 1);
        }

        Assert.AreEqual("a\r\nb", SafeXml.ReadText(good));
        Assert.IsNull(SafeXml.ReadText(big), "over the size cap");
        Assert.IsNull(SafeXml.ReadText(Path.Combine(temp.Root, "missing.txt")), "missing");
        Assert.IsNull(SafeXml.ReadText(temp.Root), "a directory");
        Assert.IsNull(SafeXml.ReadText(good, _ => FileAttributes.ReparsePoint), "a reparse point");
    }

    [TestMethod]
    public void FaultsBelowTheCap_AreAllRecordedWithNoFinalEntry()
    {
        using var temp = new TempTemplates();
        var items = string.Concat(Enumerable.Range(0, 3).Select(i => $"""<item valueName="V{i}" />"""));
        temp.AddSimple(
            "Few",
            "T.Few",
            $"""<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /><enabledList>{items}</enabledList></policy>""",
            """<string id="P">P</string>""");

        var set = temp.Load();

        Assert.AreEqual(3, set.Failures.Count, FailureAssert.Describe(set));
        Assert.IsTrue(set.Failures.All(f => f.Code == TemplateFailureCode.MissingAttribute));
    }

    [TestMethod]
    public void TheCap_IsPerFile_AndAGoodSiblingStillLoads()
    {
        using var temp = new TempTemplates();
        WriteFlood(temp);
        temp.AddSimple(
            "Good",
            "T.Good",
            """<policy name="P" class="Machine" displayName="$(string.P)" key="K"><parentCategory ref="Root" /></policy>""",
            """<string id="P">P</string>""");

        var set = temp.Load();

        Assert.AreEqual(1, set.Files.Count);
        Assert.AreEqual("T.Good", set.Files[0].Namespace);
        Assert.AreEqual(1, set.Policies.Count);
        Assert.AreEqual(AdmxLimits.MaxFailuresPerFile + 1, set.Failures.Count);
    }
}
