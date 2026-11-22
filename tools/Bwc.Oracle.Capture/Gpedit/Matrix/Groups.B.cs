using Bwc.Oracle.Capture.Gpedit.Driver;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group B: every fixture policy, from the empty folder: Enabled with defaults, Enabled with alternate values (one step per variant of
    // each element kind: both check states, every enum item, list rows 2, 1 and 0, combo text and suggestion), Disabled, Not Configured.
    // A class Both policy is run under each root. Every step records all five files.
    internal static IEnumerable<MatrixCase> GroupB(MatrixContext c)
    {
        foreach (PolicyInfo policy in c.Fixture.Policies)
        {
            foreach (string root in policy.Roots)
            {
                string id = policy.CaseId(root);
                string description = $"{policy.Name} under {(root == "Computer" ? "Computer" : "User")} Configuration: Enabled with defaults"
                    + (policy.Elements.Count > 0 ? ", Enabled with alternate values" : string.Empty) + ", Disabled, Not Configured";
                CaseBuilder builder = new("B", id, description);
                builder.Step("enabled-defaults", Edit(policy, root, "Enabled"));
                int number = 0;
                foreach ((string tag, List<OptionSpec> specs) in OptionChoices.Steps(policy, typeRanges: true))
                {
                    number++;
                    builder.Step($"enabled-alt{number}-{tag}", Edit(policy, root, "Enabled", options: specs));
                }

                builder.Step("disabled", Edit(policy, root, "Disabled"));
                builder.Step("not-configured", Edit(policy, root, "Not Configured", press: new[] { "OK" }));
                yield return builder.Build();
            }
        }
    }
}
