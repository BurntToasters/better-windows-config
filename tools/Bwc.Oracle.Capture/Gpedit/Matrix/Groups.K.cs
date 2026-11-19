using System.Text.RegularExpressions;
using Bwc.Oracle.Capture.Gpedit.Driver;
using Bwc.Oracle.Capture.Gpedit.Session;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;
using static Bwc.Oracle.Capture.Gpedit.Matrix.PregWriter;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group K: key edge cases. Each edge fixture (docs/parity/oracle/gpedit/fixture/edge/<Name>) is a valid template with one machine policy
    // whose key is spelled in an unusual way. A case installs one next to the good fixture (its own session, like group H). Per fixture:
    //   <Name>_Enable        Enabled with OK, then Not Configured with OK: the bytes classic writes for that key
    //   <Name>_ReadTrimmed   a Registry.pol that holds the value under the key without the odd backslash: does classic read the state
    //   <Name>_ReadAsWritten a Registry.pol that holds the value under the key exactly as the template spells it
    // The two read cases end like group D: one unrelated save and clear, so the golden shows what classic keeps of the start file.
    internal static IEnumerable<MatrixCase> GroupK(MatrixContext c)
    {
        string edgeDir = Path.Combine(FixtureModel.FixtureDir(c.Repo), "edge");
        foreach (string dir in Directory.GetDirectories(edgeDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            TemplateSet set = TemplateSet.FromDirectory(dir);
            string key = EdgeKey(dir);
            string trimmed = key.Trim('\\');
            PolicyInfo policy = EdgePolicy(dir, key);

            void Install(CaseBuilder builder)
            {
                foreach (TemplateSet.Item item in set.Items)
                {
                    builder.Template(item.SourcePath, item.RelativePath);
                    builder.Allow(HarvestStrings(item.SourcePath));
                }
            }

            var enable = new CaseBuilder("K", name + "_Enable", $"the edge fixture {name} (key {key}): Enabled with OK, then Not Configured with OK");
            Install(enable);
            enable.Step("enabled", Edit(policy, "Computer", "Enabled", press: new[] { "OK" }));
            enable.Step("not-configured", Edit(policy, "Computer", "Not Configured", press: new[] { "OK" }));
            yield return enable.Build();

            foreach ((string suffix, string recordKey, string what) in new[]
            {
                ("ReadTrimmed", trimmed, "the key without a leading or trailing backslash"),
                ("ReadAsWritten", key, "the key exactly as the template spells it"),
            })
            {
                string description = $"the edge fixture {name} (key {key}): Registry.pol holds EdgeValue 1 under {what}";
                MatrixCase read = BuildCase(
                    c.Fixture,
                    name + "_" + suffix,
                    description,
                    Crafted.Machine(description, Dword(recordKey, "EdgeValue", 1)),
                    new[] { policy.Name },
                    "Computer",
                    "Pol_Sort_Zulu",
                    group: "K",
                    affectedPolicy: _ => policy);
                foreach (TemplateSet.Item item in set.Items)
                {
                    read.ExtraTemplates.Add((item.SourcePath, item.RelativePath));
                    read.Authored.UnionWith(HarvestStrings(item.SourcePath));
                }

                yield return read;
            }
        }
    }

    // The policy key of the edge fixture, as written in its ADMX (not trimmed).
    private static string EdgeKey(string dir)
    {
        foreach (string admx in Directory.GetFiles(dir, "*.admx"))
        {
            Match m = Regex.Match(File.ReadAllText(admx), "<policy [^>]*\\bkey=\"([^\"]*)\"");
            if (m.Success)
            {
                return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            }
        }

        throw new InvalidDataException($"the edge fixture {dir} has no policy with a key attribute.");
    }

    // A stand-in PolicyInfo for the edge fixture policy: the display names come from its ADML (strings EdgeRoot, EdgeCat and Pol_Edge).
    private static PolicyInfo EdgePolicy(string dir, string key)
    {
        string StringOf(string id)
        {
            foreach (string adml in Directory.GetFiles(Path.Combine(dir, "en-US"), "*.adml"))
            {
                Match m = Regex.Match(File.ReadAllText(adml), $"<string id=\"{id}\">([^<]*)</string>");
                if (m.Success)
                {
                    return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                }
            }

            throw new InvalidDataException($"the edge fixture {dir} has no ADML string '{id}'.");
        }

        return new PolicyInfo
        {
            Name = "Pol_Edge",
            Class = "Machine",
            DisplayName = StringOf("Pol_Edge"),
            Key = key,
            ValueName = "EdgeValue",
            CategoryPath = new List<string> { StringOf("EdgeRoot"), StringOf("EdgeCat") },
        };
    }
}
