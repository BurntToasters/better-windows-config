using Bwc.Oracle.Capture.Gpedit.Driver;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group E: validation. Each case types a bad value (or leaves a required one empty) and presses OK, Apply or Next. The driver captures any
    // message box (title, text and buttons go to the untracked log, hashes to case.json), answers it with its default button, cancels the dialog,
    // and the snapshot after shows whether any file was written.
    internal static IEnumerable<MatrixCase> GroupE(MatrixContext c)
    {
        FixtureModel f = c.Fixture;

        List<OptionSpec> One(PolicyInfo policy, string text, bool typed = false, int element = 0) =>
            new() { OptionChoices.Base(policy, policy.Elements[element]) with { Text = text, Typed = typed ? true : null } };

        // clearBetween: a recorded clear step (OK) before every step after the first. A number box with a range set without keys is not noticed on a
        // policy that is already Enabled, and typing an out-of-range value is another classic behaviour (Decimal_TypedAboveMax), so each later step
        // starts from a state other than Enabled and the state change carries the edit, as in the first step (failure mode 21 of the matrix document).
        // The clear is Not Configured, except on a policy with a soft decimal: classic keeps its record on Not Configured and reads it back as
        // Enabled (golden B/Pol_Decimal_Soft), so the clear there is Disabled.
        MatrixCase Build(bool clearBetween, string id, string description, PolicyInfo policy, string root, (string Label, List<OptionSpec>? Options, string[]? Press)[] steps)
        {
            var builder = new CaseBuilder("E", id, description);
            bool first = true;
            bool soft = policy.Elements.Any(e => e.Soft && e.Kind == "decimal");
            foreach ((string label, List<OptionSpec>? options, string[]? press) in steps)
            {
                if (clearBetween && !first)
                {
                    builder.Step(soft ? $"disable-before-{label}" : $"clear-before-{label}", Edit(policy, root, soft ? "Disabled" : "Not Configured", press: new[] { "OK" }));
                }

                first = false;
                builder.Step(label, Edit(policy, root, "Enabled", options: options, press: press), isOptional: true);
            }

            return builder.Build();
        }

        MatrixCase Case(string id, string description, PolicyInfo policy, string root, params (string Label, List<OptionSpec>? Options, string[]? Press)[] steps) =>
            Build(false, id, description, policy, root, steps);

        MatrixCase CaseClearing(string id, string description, PolicyInfo policy, string root, params (string Label, List<OptionSpec>? Options, string[]? Press)[] steps) =>
            Build(true, id, description, policy, root, steps);

        PolicyInfo minMax = f.Policy("Pol_Decimal_MinMax");
        PolicyInfo requiredNumber = f.Policy("Pol_Decimal_Required");
        PolicyInfo soft = f.Policy("Pol_Decimal_Soft");
        PolicyInfo storeText = f.Policy("Pol_Decimal_StoreAsText");
        PolicyInfo longNumber = f.Policy("Pol_LongDecimal_Basic");
        PolicyInfo longRequired = f.Policy("Pol_LongDecimal_RequiredSoftText");
        PolicyInfo shortText = f.Policy("Pol_Text_MaxLength");
        PolicyInfo requiredText = f.Policy("Pol_Text_Required");
        PolicyInfo comboRequired = f.Policy("Pol_Text_ComboBoxNoSortRequired");
        PolicyInfo multiLimits = f.Policy("Pol_MultiText_Limits");
        PolicyInfo enumRequired = f.Policy("Pol_Enum_Required");
        PolicyInfo explicitList = f.Policy("Pol_List_ExplicitValue");
        PolicyInfo plainList = f.Policy("Pol_List_Plain");

        yield return Case("Decimal_AboveMax", "decimal 10 to 500 given 9999, then OK", minMax, "Computer", ("ok", One(minMax, "9999"), null));
        yield return Case("Decimal_BelowMin", "decimal 10 to 500 given 1, then OK", minMax, "Computer", ("ok", One(minMax, "1"), null));
        yield return CaseClearing("Decimal_NotNumber", "decimal given letters, then OK", minMax, "Computer", ("letters", One(minMax, "abc"), null), ("negative", One(minMax, "-5"), null));
        yield return Case("Decimal_Empty", "decimal given an empty text, then OK", minMax, "Computer", ("empty", One(minMax, string.Empty), null));
        yield return CaseClearing("Decimal_EdgeValues", "decimal 10 to 500 at 10, 500 and 501", minMax, "Computer", ("min", One(minMax, "10"), null), ("max", One(minMax, "500"), null), ("max-plus-one", One(minMax, "501"), null));
        yield return Case("Decimal_TypedAboveMax", "decimal typed key by key: 9999", minMax, "Computer", ("typed", One(minMax, "9999", typed: true), null));
        yield return Case("Decimal_TypedLetters", "decimal typed key by key: 12ab", minMax, "Computer", ("typed", One(minMax, "12ab", typed: true), null));
        yield return Case("Decimal_Apply", "decimal out of range pressed with Apply, then Cancel", minMax, "Computer", ("apply", One(minMax, "9999"), new[] { "Apply", "Cancel" }));
        yield return Case("Decimal_Required_Empty", "required decimal left empty and given an empty text", requiredNumber, "User", ("defaults", null, null), ("empty", One(requiredNumber, string.Empty), null));
        yield return CaseClearing("Decimal_Soft_OutOfRange", "soft decimal 1 to 100 given 1000 and 0", soft, "Computer", ("above", One(soft, "1000"), null), ("below", One(soft, "0"), null));
        yield return CaseClearing("Decimal_StoreAsText_OutOfRange", "storeAsText decimal 0 to 65535 given 65536 and -1", storeText, "Computer", ("above", One(storeText, "65536"), null), ("negative", One(storeText, "-1"), null));
        yield return CaseClearing("LongDecimal_AboveMax", "long decimal given 9000000000001 and a 25 digit number", longNumber, "Computer", ("above", One(longNumber, "9000000000001"), null), ("huge", One(longNumber, "9999999999999999999999999"), null));
        yield return Case("LongDecimal_Required_Empty", "required long decimal given an empty text and a letter", longRequired, "User", ("defaults", null, null), ("empty", One(longRequired, string.Empty), null), ("letter", One(longRequired, "x"), null));
        yield return Case("Text_TooLong_Typed", "text with maxLength 12 typed with 20 characters", shortText, "User", ("typed", One(shortText, "abcdefghijklmnopqrst", typed: true), null));
        yield return Case("Text_TooLong_SetText", "text with maxLength 12 set with 20 characters without typing", shortText, "User", ("set", One(shortText, "abcdefghijklmnopqrst"), null));
        yield return Case("Text_Required_Empty", "required text left empty and given an empty text", requiredText, "Computer", ("defaults", null, null), ("empty", One(requiredText, string.Empty), null), ("spaces", One(requiredText, "   "), null));
        yield return Case("Combo_Required_Empty", "required combo box left empty and given an empty text", comboRequired, "Computer", ("defaults", null, null), ("empty", One(comboRequired, string.Empty), null));
        yield return Case("MultiText_Limits", "multiText with maxStrings 3 and maxLength 20: four lines, a 25 character line, nothing", multiLimits, "User",
            ("four-lines", new List<OptionSpec> { OptionChoices.Base(multiLimits, multiLimits.Elements[0]) with { Lines = new List<string> { "a", "b", "c", "d" } } }, null),
            ("long-line", new List<OptionSpec> { OptionChoices.Base(multiLimits, multiLimits.Elements[0]) with { Lines = new List<string> { new string('x', 25) } } }, null),
            ("empty", new List<OptionSpec> { OptionChoices.Base(multiLimits, multiLimits.Elements[0]) with { Lines = new List<string>() } }, null));
        yield return Case("Enum_Required_Defaults", "required enum with no choice made", enumRequired, "Computer", ("defaults", null, null));

        OptionSpec Rows(PolicyInfo policy, params string[][] rows) => OptionChoices.Base(policy, policy.Elements[0]) with
        {
            ClearRows = true,
            Rows = rows.Select(r => r.ToList()).ToList(),
        };

        yield return Case("List_Explicit_EmptyName", "name and value list with an empty name", explicitList, "Computer",
            ("empty-name", new List<OptionSpec> { Rows(explicitList, new[] { string.Empty, "value1" }) }, null));
        yield return Case("List_Explicit_EmptyValue", "name and value list with an empty value", explicitList, "Computer",
            ("empty-value", new List<OptionSpec> { Rows(explicitList, new[] { "name1", string.Empty }) }, null));
        yield return Case("List_Explicit_DuplicateNames", "name and value list with the same name twice", explicitList, "Computer",
            ("duplicate", new List<OptionSpec> { Rows(explicitList, new[] { "name1", "value1" }, new[] { "name1", "value2" }) }, null));
        yield return Case("List_Explicit_NameOnly", "name and value list with a one cell row", explicitList, "Computer",
            ("name-only", new List<OptionSpec> { Rows(explicitList, new[] { "name1" }) }, null));
        yield return Case("List_Plain_EmptyEntry", "plain list with an empty entry", plainList, "Computer",
            ("empty-entry", new List<OptionSpec> { Rows(plainList, new[] { string.Empty }) }, null));
        yield return Case("List_Plain_DuplicateEntries", "plain list with the same entry twice", plainList, "Computer",
            ("duplicate", new List<OptionSpec> { Rows(plainList, new[] { "same" }, new[] { "same" }) }, null));
        yield return Case("List_Plain_LongEntry", "plain list with a 300 character entry", plainList, "Computer",
            ("long", new List<OptionSpec> { Rows(plainList, new[] { new string('x', 300) }) }, null));
        yield return Case("List_Plain_SpecialCharacters", "plain list with quotes, commas and an ampersand", plainList, "Computer",
            ("special", new List<OptionSpec> { Rows(plainList, new[] { "a,b" }, new[] { "say \"hi\"" }, new[] { "x & y" }) }, null));

        yield return new CaseBuilder("E", "Next_WithInvalid", "required decimal left empty and Next Setting pressed, then Cancel")
            .Step("next", Edit(requiredNumber, "User", "Enabled", options: One(requiredNumber, string.Empty), press: new[] { "Next", "Cancel" }), isOptional: true)
            .Build();
        yield return new CaseBuilder("E", "Previous_WithInvalid", "required decimal left empty and Previous Setting pressed, then Cancel")
            .Step("previous", Edit(requiredNumber, "User", "Enabled", options: One(requiredNumber, string.Empty), press: new[] { "Previous", "Cancel" }), isOptional: true)
            .Build();
        yield return new CaseBuilder("E", "Ok_DefaultsOfRequired", "OK on every required policy with its defaults, one after another")
            .Step("decimal", Edit(requiredNumber, "User", "Enabled"), isOptional: true)
            .Step("long", Edit(longRequired, "User", "Enabled"), isOptional: true)
            .Step("text", Edit(requiredText, "Computer", "Enabled"), isOptional: true)
            .Step("combo", Edit(comboRequired, "Computer", "Enabled"), isOptional: true)
            .Step("enum", Edit(enumRequired, "Computer", "Enabled"), isOptional: true)
            .Step("multi", Edit(multiLimits, "User", "Enabled"), isOptional: true)
            .Build();
    }
}
