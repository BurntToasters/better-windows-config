using System.Text.RegularExpressions;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Session;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group G: refresh. After OK, Apply and Not Configured, does the live registry change, and when. The registry is read by the driver's
    // settle after the press, and again after a fixed 4 s and 8 s, so a late apply is not missed. Registry content is part of case.json here.
    internal static IEnumerable<MatrixCase> GroupG(MatrixContext c)
    {
        FixtureModel f = c.Fixture;

        MatrixCase Sequence(string id, string description, PolicyInfo policy, string root, params (string Label, string? State, string? Comment, string[]? Press, List<OptionSpec>? Options)[] edits)
        {
            var b = new CaseBuilder("G", id, description).RegistryObserved();
            b.Add(Plain("snapshot", "registry at start"));
            foreach ((string label, string? state, string? comment, string[]? press, List<OptionSpec>? options) in edits)
            {
                b.Step(label, Edit(policy, root, state, comment, options, press));
                b.Add(Wait(4000, "wait 4 s"));
                b.Add(Plain("snapshot", label + ": registry 4 s later"));
                b.Add(Wait(4000, "wait 4 s more"));
                b.Add(Plain("snapshot", label + ": registry 8 s later"));
            }

            return b.Build();
        }

        PolicyInfo machine = f.Policy("Pol_Class_Machine");
        PolicyInfo user = f.Policy("Pol_Class_User");
        PolicyInfo both = f.Policy("Pol_Class_Both");

        foreach ((PolicyInfo policy, string root, string name) in new[]
        {
            (machine, "Computer", "Machine"), (user, "User", "User"), (both, "Computer", "Both_Computer"), (both, "User", "Both_User"),
        })
        {
            yield return Sequence(name + "_Ok", $"{policy.Name} under {root}: Enabled with OK, then OK again with no change", policy, root,
                ("enabled", "Enabled", null, null, null), ("enabled-again", "Enabled", null, new[] { "OK" }, null));
            yield return Sequence(name + "_Apply", $"{policy.Name} under {root}: Enabled with Apply and then Cancel", policy, root,
                ("apply", "Enabled", null, new[] { "Apply", "Cancel" }, null));
            yield return Sequence(name + "_Disabled", $"{policy.Name} under {root}: Enabled, then Disabled", policy, root,
                ("enabled", "Enabled", null, null, null), ("disabled", "Disabled", null, null, null));
            yield return Sequence(name + "_NotConfigured", $"{policy.Name} under {root}: Enabled, then Not Configured (is the applied value removed)", policy, root,
                ("enabled", "Enabled", null, null, null), ("not-configured", "Not Configured", null, new[] { "OK" }, null));
            yield return Sequence(name + "_CommentOnly", $"{policy.Name} under {root}: a comment only, then Enabled with the comment", policy, root,
                ("comment", "Not Configured", "bwc comment one", null, null), ("enabled", "Enabled", "bwc comment one", null, null));
        }

        // Values with elements and the key outside the policy roots.
        PolicyInfo minMax = f.Policy("Pol_Decimal_MinMax");
        PolicyInfo storeText = f.Policy("Pol_Decimal_StoreAsText");
        PolicyInfo expandText = f.Policy("Pol_Text_Expandable");
        PolicyInfo multiText = f.Policy("Pol_MultiText_Basic");
        PolicyInfo explicitList = f.Policy("Pol_List_ExplicitValue");
        PolicyInfo outside = f.Policy("Pol_Outside_KeyOutsideRoots");
        PolicyInfo enumValueList = f.Policy("Pol_Enum_ItemValueList");
        PolicyInfo longNumber = f.Policy("Pol_LongDecimal_Basic");
        PolicyInfo valueDecimal = f.Policy("Pol_Value_Decimal");

        foreach (PolicyInfo policy in new[] { minMax, storeText, expandText, multiText, explicitList, enumValueList, longNumber })
        {
            List<OptionSpec> alt = OptionChoices.Steps(policy)[0].Specs;
            yield return Sequence("Elements_" + policy.Name, $"{policy.Name}: Enabled with values (registry types), then Not Configured", policy, "Computer",
                ("enabled", "Enabled", null, null, alt), ("not-configured", "Not Configured", null, new[] { "OK" }, null));
        }

        yield return Sequence("Outside_KeyOutsideRoots", "a policy whose key is outside the policy roots: Enabled, then Not Configured", outside, "Computer",
            ("enabled", "Enabled", null, null, null), ("not-configured", "Not Configured", null, new[] { "OK" }, null));
        yield return Sequence("Value_EnabledDisabledValues", "a policy with enabledValue 5 and disabledValue 9: Enabled, Disabled, Not Configured", valueDecimal, "Computer",
            ("enabled", "Enabled", null, null, null), ("disabled", "Disabled", null, null, null), ("not-configured", "Not Configured", null, new[] { "OK" }, null));
    }

    // Group H: template load errors. Each case installs one bad fixture next to the good one (its own session, so the bad files never
    // outlive the case), starts classic, captures the error UI and the resulting tree.
    internal static IEnumerable<MatrixCase> GroupH(MatrixContext c)
    {
        string badDir = Path.Combine(FixtureModel.FixtureDir(c.Repo), "bad");
        foreach (string dir in Directory.GetDirectories(badDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            TemplateSet set = TemplateSet.FromDirectory(dir);
            var builder = new CaseBuilder("H", name, $"the bad fixture {name} installed next to the good fixture: error UI and tree");
            foreach (TemplateSet.Item item in set.Items)
            {
                builder.Template(item.SourcePath, item.RelativePath);
                builder.Allow(HarvestStrings(item.SourcePath));
            }

            string category = HarvestCategory(dir) ?? "BadRoot";
            string policy = "Bad fixture policy";
            builder.Add(Plain("dismissDialogs", "error UI present at start"));
            builder.Add(Shot("start"));
            builder.Add(Select("Computer", new[] { "Administrative Templates" }), isOptional: true);
            builder.Add(Plain("dismissDialogs", "error UI after selecting Administrative Templates"));
            builder.Add(Children("Computer", new[] { "Administrative Templates" }, "children of Administrative Templates"), isOptional: true);
            builder.Add(Children("User", new[] { "Administrative Templates" }, "children of Administrative Templates under User"), isOptional: true);
            builder.Add(Children("Computer", new[] { "Administrative Templates", "BWC Oracle" }, "the good fixture still loads"), isOptional: true);
            builder.Add(Select("Computer", new[] { "Administrative Templates", category }), isOptional: true);
            builder.Add(ReadList("Computer", new[] { "Administrative Templates", category }, label: "rows of the bad category"), isOptional: true);
            builder.Add(Edit(MatrixPolicy(policy, category), "Computer", label: "open the bad policy"), isOptional: true);
            builder.Add(Plain("dismissDialogs", "error UI after browsing"));
            builder.Add(Shot("end"));
            yield return builder.Build();
        }
    }

    // A stand-in PolicyInfo for a bad fixture policy (only its display name and node path are used).
    private static PolicyInfo MatrixPolicy(string displayName, string category) => new()
    {
        Name = "Pol_Bad",
        Class = "Machine",
        DisplayName = displayName,
        CategoryPath = new List<string> { category },
    };

    private static readonly Regex TextNode = new(@">([^<>]+)<", RegexOptions.Compiled);
    private static readonly Regex AttributeValue = new("=\"([^\"]*)\"", RegexOptions.Compiled);

    // Every attribute value and text node of a file, found by pattern so a malformed file works too. Used only to let the scrubber keep BWC text.
    private static IEnumerable<string> HarvestStrings(string path)
    {
        string text = File.ReadAllText(path);
        foreach (Match m in TextNode.Matches(text))
        {
            string value = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (value.Length > 0)
            {
                yield return value;
            }
        }

        foreach (Match m in AttributeValue.Matches(text))
        {
            yield return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        }
    }

    // The display name of the bad fixture's root category, from its ADML (the string BadRoot).
    private static string? HarvestCategory(string dir)
    {
        string enUs = Path.Combine(dir, "en-US");
        if (!Directory.Exists(enUs))
        {
            return null;
        }

        foreach (string adml in Directory.GetFiles(enUs, "*.adml"))
        {
            Match m = Regex.Match(File.ReadAllText(adml), "<string id=\"BadRoot\">([^<]*)</string>");
            if (m.Success)
            {
                return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            }
        }

        return null;
    }
}
