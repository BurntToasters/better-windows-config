using Bwc.Policy.Admx;
using static Bwc.Policy.Tests.Blind.BlindKit;

namespace Bwc.Policy.Tests.Blind;

/// <summary>
/// Blind test of the system template folder (piece E2, docs/failure-modes/policy-admx.md section 34), invariants only. It
/// reads the folder and writes nothing, and is kept apart from the tests that write temporary templates so the policy
/// writer guard sees no write next to the folder name.
/// </summary>
[TestClass]
public sealed class SystemFolderBlindTests
{
    // ---------------------------------------------------------------- 34. System folder (invariants only)

    [TestMethod]
    public void Sec34_SystemFolder_OnlyDanglingReferenceCodes_AndEveryFileIsLoaded()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");
        if (!Directory.Exists(folder))
        {
            Assert.Inconclusive("No system template folder on this machine.");
        }

        var set = TemplateLoader.Load([folder], "en-US");
        var allowed = new HashSet<TemplateFailureCode>
        {
            TemplateFailureCode.UndefinedUsing, TemplateFailureCode.UndefinedPrefix, TemplateFailureCode.UndefinedCategory,
            TemplateFailureCode.MissingCategory, TemplateFailureCode.UndefinedSupportedOn,
        };
        var offenders = set.Failures.Where(f => !allowed.Contains(f.Code)).ToList();
        Assert.AreEqual(0, offenders.Count, FailureAssert.Describe(set));
        var count = Directory.EnumerateFiles(folder, "*.admx", SearchOption.TopDirectoryOnly).Count();
        Assert.AreEqual(count, set.Files.Count);
    }
}
