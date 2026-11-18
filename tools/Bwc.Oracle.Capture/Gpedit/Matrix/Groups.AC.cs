using Bwc.Oracle.Capture.Gpedit.Driver;
using static Bwc.Oracle.Capture.Gpedit.Matrix.MatrixOps;

namespace Bwc.Oracle.Capture.Gpedit.Matrix;

internal static partial class MatrixGroups
{
    // Group A: the first save into an empty GroupPolicy folder, one case per way of saving.
    internal static IEnumerable<MatrixCase> GroupA(MatrixContext c)
    {
        PolicyInfo machine = c.Fixture.Policy("Pol_Class_Machine");
        PolicyInfo user = c.Fixture.Policy("Pol_Class_User");
        PolicyInfo both = c.Fixture.Policy("Pol_Class_Both");

        MatrixCase One(string id, string description, PolicyInfo policy, string root, string? state, string? comment, string[]? press = null) =>
            new CaseBuilder("A", id, description)
                .Mark("start")
                .Step("saved", Edit(policy, root, state, comment, press: press))
                .Build();

        yield return One("Machine_Enabled", "first save: a machine policy Enabled", machine, "Computer", "Enabled", null);
        yield return One("User_Enabled", "first save: a user policy Enabled", user, "User", "Enabled", null);
        yield return One("Both_UnderComputer_Enabled", "first save: a class Both policy Enabled under Computer Configuration", both, "Computer", "Enabled", null);
        yield return One("Both_UnderUser_Enabled", "first save: a class Both policy Enabled under User Configuration", both, "User", "Enabled", null);
        yield return One("Machine_Disabled", "first save: a machine policy Disabled", machine, "Computer", "Disabled", null);
        yield return One("User_Disabled", "first save: a user policy Disabled", user, "User", "Disabled", null);
        yield return One("Machine_NotConfigured", "OK on a Not Configured policy in an empty folder (does classic write anything)", machine, "Computer", "Not Configured", null, new[] { "OK" });
        yield return One("Machine_CommentOnly", "first save: only a comment on a machine policy, state stays Not Configured", machine, "Computer", "Not Configured", "bwc comment one");
        yield return One("User_CommentOnly", "first save: only a comment on a user policy", user, "User", "Not Configured", "bwc comment one");
        yield return One("Both_UnderComputer_CommentOnly", "first save: only a comment on a class Both policy under Computer Configuration", both, "Computer", "Not Configured", "bwc comment one");
        yield return One("Both_UnderUser_CommentOnly", "first save: only a comment on a class Both policy under User Configuration", both, "User", "Not Configured", "bwc comment one");
        yield return One("Machine_EnabledWithComment", "first save: Enabled with a comment (machine)", machine, "Computer", "Enabled", "bwc comment one");
        yield return One("User_EnabledWithComment", "first save: Enabled with a comment (user)", user, "User", "Enabled", "bwc comment one");
        yield return One("Machine_Apply_Cancel", "first save by Apply, then Cancel of the dialog", machine, "Computer", "Enabled", null, new[] { "Apply", "Cancel" });
        yield return One("Machine_Apply_OK", "first save by Apply, then OK with no further change", machine, "Computer", "Enabled", null, new[] { "Apply", "OK" });
        yield return One("Machine_Cancel", "Cancel after choosing Enabled writes nothing", machine, "Computer", "Enabled", null, new[] { "Cancel" });

        // Machine then user in one empty folder: which half of the gpt.ini version each save bumps.
        yield return new CaseBuilder("A", "Machine_Then_User", "empty folder, a machine save and then a user save")
            .Mark("start")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("user", Edit(user, "User", "Enabled"))
            .Build();
        yield return new CaseBuilder("A", "User_Then_Machine", "empty folder, a user save and then a machine save")
            .Mark("start")
            .Step("user", Edit(user, "User", "Enabled"))
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Build();
        yield return new CaseBuilder("A", "Both_Computer_Then_User", "empty folder, a class Both policy saved under Computer Configuration and then under User Configuration")
            .Mark("start")
            .Step("computer", Edit(both, "Computer", "Enabled"))
            .Step("user", Edit(both, "User", "Enabled"))
            .Build();
    }

    // Group C: sequences that decide the write rules (order, re-apply, comments, clearing, versions, shared keys, namespaces).
    internal static IEnumerable<MatrixCase> GroupC(MatrixContext c)
    {
        FixtureModel f = c.Fixture;
        PolicyInfo machine = f.Policy("Pol_Class_Machine");
        PolicyInfo decimalValue = f.Policy("Pol_Value_Decimal");
        PolicyInfo user = f.Policy("Pol_Class_User");
        PolicyInfo zeta = f.Policy("Pol_Shared_Zeta");
        PolicyInfo alpha = f.Policy("Pol_Shared_Alpha");
        PolicyInfo mid = f.Policy("Pol_Shared_Mid");
        PolicyInfo reverse = f.Policy("Pol_Shared_ElementsReverseOrder");
        PolicyInfo twoChecks = f.Policy("Pol_Bool_TwoChecksReverseOrder");
        PolicyInfo listOne = f.Policy("Pol_SharedList_One");
        PolicyInfo listTwo = f.Policy("Pol_SharedList_Two");
        PolicyInfo switched = f.Policy("Pol_List_PolicyValueName");

        yield return new CaseBuilder("C", "TwoInKey_ClassThenDecimal", "two policies in one key: MachineFlag first, ModeNumber second")
            .Step("first", Edit(machine, "Computer", "Enabled"))
            .Step("second", Edit(decimalValue, "Computer", "Enabled"))
            .Build();
        yield return new CaseBuilder("C", "TwoInKey_DecimalThenClass", "two policies in one key: ModeNumber first, MachineFlag second")
            .Step("first", Edit(decimalValue, "Computer", "Enabled"))
            .Step("second", Edit(machine, "Computer", "Enabled"))
            .Build();
        yield return new CaseBuilder("C", "TwoInKey_ChangeFirstAgain", "two policies, then the first one Disabled and Enabled again (does it move to the end)")
            .Step("first", Edit(machine, "Computer", "Enabled"))
            .Step("second", Edit(decimalValue, "Computer", "Enabled"))
            .Step("first-disabled", Edit(machine, "Computer", "Disabled"))
            .Step("first-enabled-again", Edit(machine, "Computer", "Enabled"))
            .Step("second-disabled", Edit(decimalValue, "Computer", "Disabled"))
            .Build();

        yield return new CaseBuilder("C", "Shared_ZetaAlphaMid", "three policies in one key, saved Zeta, Alpha, Mid, then Alpha cleared and set again")
            .Step("zeta", Edit(zeta, "Computer", "Enabled"))
            .Step("alpha", Edit(alpha, "Computer", "Enabled"))
            .Step("mid", Edit(mid, "Computer", "Enabled"))
            .Step("alpha-not-configured", Edit(alpha, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("alpha-enabled-again", Edit(alpha, "Computer", "Enabled"))
            .Build();
        yield return new CaseBuilder("C", "Shared_MidAlphaZeta", "three policies in one key, saved Mid, Alpha, Zeta, then all Disabled in the same order")
            .Step("mid", Edit(mid, "Computer", "Enabled"))
            .Step("alpha", Edit(alpha, "Computer", "Enabled"))
            .Step("zeta", Edit(zeta, "Computer", "Enabled"))
            .Step("mid-disabled", Edit(mid, "Computer", "Disabled"))
            .Step("alpha-disabled", Edit(alpha, "Computer", "Disabled"))
            .Step("zeta-disabled", Edit(zeta, "Computer", "Disabled"))
            .Build();
        yield return new CaseBuilder("C", "Shared_DeleteRecordsOrder", "Disabled records of three policies in one key, then Not Configured one by one")
            .Step("zeta-disabled", Edit(zeta, "Computer", "Disabled"))
            .Step("alpha-disabled", Edit(alpha, "Computer", "Disabled"))
            .Step("mid-disabled", Edit(mid, "Computer", "Disabled"))
            .Step("alpha-not-configured", Edit(alpha, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("zeta-not-configured", Edit(zeta, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("mid-not-configured", Edit(mid, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();

        // One policy with a value name and two elements defined in reverse name order (OQ-01).
        ElementInfo zetaElement = reverse.Elements.First(e => e.Id == "ZetaElement");
        ElementInfo alphaElement = reverse.Elements.First(e => e.Id == "AlphaElement");
        OptionSpec ZetaText(string text) => OptionChoices.Base(reverse, zetaElement) with { Text = text };
        OptionSpec AlphaNumber(string number) => OptionChoices.Base(reverse, alphaElement) with { Text = number };
        yield return new CaseBuilder("C", "Elements_ReverseOrder", "one policy writing a value and two elements, defined Zeta then Alpha")
            .Step("both-elements", Edit(reverse, "Computer", "Enabled", options: new List<OptionSpec> { ZetaText("zz"), AlphaNumber("5") }))
            .Step("alpha-changed", Edit(reverse, "Computer", "Enabled", options: new List<OptionSpec> { ZetaText("zz"), AlphaNumber("6") }))
            .Step("zeta-changed", Edit(reverse, "Computer", "Enabled", options: new List<OptionSpec> { ZetaText("yy"), AlphaNumber("6") }))
            .Step("disabled", Edit(reverse, "Computer", "Disabled"))
            .Step("not-configured", Edit(reverse, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();

        ElementInfo zetaCheck = twoChecks.Elements.First(e => e.Id == "ZetaCheck");
        ElementInfo alphaCheck = twoChecks.Elements.First(e => e.Id == "AlphaCheck");
        OptionSpec Check(ElementInfo e, bool on) => OptionChoices.Base(twoChecks, e) with { Checked = on };
        yield return new CaseBuilder("C", "Booleans_ReverseOrder", "two check boxes defined Zeta then Alpha: both on, Zeta off, Alpha off, then Zeta on")
            .Step("both-on", Edit(twoChecks, "Computer", "Enabled", options: new List<OptionSpec> { Check(zetaCheck, true), Check(alphaCheck, true) }))
            .Step("zeta-off", Edit(twoChecks, "Computer", "Enabled", options: new List<OptionSpec> { Check(zetaCheck, false), Check(alphaCheck, true) }))
            .Step("alpha-off", Edit(twoChecks, "Computer", "Enabled", options: new List<OptionSpec> { Check(zetaCheck, false), Check(alphaCheck, false) }))
            .Step("zeta-on", Edit(twoChecks, "Computer", "Enabled", options: new List<OptionSpec> { Check(zetaCheck, true), Check(alphaCheck, false) }))
            .Build();

        yield return new CaseBuilder("C", "ReApply_SameValues", "Enabled again with the same values (no change), then Apply and OK with no change")
            .Step("enabled", Edit(machine, "Computer", "Enabled"))
            .Step("enabled-again", Edit(machine, "Computer", "Enabled"))
            .Step("apply-ok-no-change", Edit(machine, "Computer", "Enabled", press: new[] { "Apply", "OK" }))
            .Step("apply-cancel-no-change", Edit(machine, "Computer", "Enabled", press: new[] { "Apply", "Cancel" }))
            .Build();
        yield return new CaseBuilder("C", "NoChange_Apply", "open and Apply with no change, then OK with no change")
            .Mark("start")
            .Step("apply-no-change", Edit(machine, "Computer", press: new[] { "Apply", "Cancel" }))
            .Step("ok-no-change", Edit(machine, "Computer", state: "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Apply_Then_OK", "Apply writes, OK after it writes nothing more")
            .Step("apply-then-ok", Edit(machine, "Computer", "Enabled", press: new[] { "Apply", "OK" }))
            .Step("apply-then-ok-disabled", Edit(machine, "Computer", "Disabled", press: new[] { "Apply", "OK" }))
            .Build();

        yield return new CaseBuilder("C", "Comment_NotConfigured_AddEditRemove", "a comment on a Not Configured policy: add, edit, remove")
            .Step("add", Edit(machine, "Computer", "Not Configured", "bwc comment one"))
            .Step("edit", Edit(machine, "Computer", "Not Configured", "bwc comment two"))
            .Step("remove", Edit(machine, "Computer", "Not Configured", string.Empty))
            .Build();
        yield return new CaseBuilder("C", "Comment_Enabled_AddEditRemove", "a comment on an Enabled policy: add, edit, remove, then clear the policy")
            .Step("enabled", Edit(machine, "Computer", "Enabled"))
            .Step("add", Edit(machine, "Computer", "Enabled", "bwc comment one"))
            .Step("edit", Edit(machine, "Computer", "Enabled", "bwc comment two"))
            .Step("remove", Edit(machine, "Computer", "Enabled", string.Empty))
            .Step("not-configured", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Comment_TwoPolicies", "comments on two policies, one removed, one edited")
            .Step("first", Edit(machine, "Computer", "Not Configured", "bwc comment one"))
            .Step("second", Edit(decimalValue, "Computer", "Not Configured", "bwc comment two"))
            .Step("first-removed", Edit(machine, "Computer", "Not Configured", string.Empty))
            .Step("second-edited", Edit(decimalValue, "Computer", "Not Configured", "bwc comment three"))
            .Step("second-removed", Edit(decimalValue, "Computer", "Not Configured", string.Empty))
            .Build();
        yield return new CaseBuilder("C", "Comment_User", "a comment on a user policy: add, edit, remove")
            .Step("add", Edit(user, "User", "Not Configured", "bwc comment one"))
            .Step("edit", Edit(user, "User", "Not Configured", "bwc comment two"))
            .Step("remove", Edit(user, "User", "Not Configured", string.Empty))
            .Build();
        yield return new CaseBuilder("C", "Comment_MultiLine", "a comment with two lines and with non-ASCII text")
            .Step("two-lines", Edit(machine, "Computer", "Not Configured", "line one\r\nline two"))
            .Step("non-ascii", Edit(machine, "Computer", "Not Configured", "café & <tag>"))
            .Build();
        yield return new CaseBuilder("C", "Comment_OnlyAfterSave", "a comment only change after a normal save: does the version change")
            .Step("enabled", Edit(machine, "Computer", "Enabled"))
            .Step("comment-only", Edit(machine, "Computer", "Enabled", "bwc comment one"))
            .Step("comment-only-user", Edit(user, "User", "Not Configured", "bwc comment one"))
            .Build();

        yield return new CaseBuilder("C", "Clear_LastSetting_Machine", "enable the only setting, then Not Configured: the emptied file and gpt.ini")
            .Step("enabled", Edit(machine, "Computer", "Enabled"))
            .Step("not-configured", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Clear_LastSetting_User", "the same for the user file")
            .Step("enabled", Edit(user, "User", "Enabled"))
            .Step("not-configured", Edit(user, "User", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Clear_LastSetting_Disabled", "Disabled (a delete record) and then Not Configured")
            .Step("disabled", Edit(machine, "Computer", "Disabled"))
            .Step("not-configured", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Clear_LastSetting_WithComment", "clear the last setting while a comment stays")
            .Step("enabled-with-comment", Edit(machine, "Computer", "Enabled", "bwc comment one"))
            .Step("not-configured-comment-kept", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("comment-removed", Edit(machine, "Computer", "Not Configured", string.Empty))
            .Build();
        yield return new CaseBuilder("C", "Clear_OneOfTwo", "two settings, clear the first, then the second")
            .Step("first", Edit(machine, "Computer", "Enabled"))
            .Step("second", Edit(decimalValue, "Computer", "Enabled"))
            .Step("first-cleared", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("second-cleared", Edit(decimalValue, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Clear_MachineAndUser", "settings in both files, machine cleared first, then user")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("user", Edit(user, "User", "Enabled"))
            .Step("machine-cleared", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("user-cleared", Edit(user, "User", "Not Configured", press: new[] { "OK" }))
            .Build();

        yield return new CaseBuilder("C", "Version_MachineUserCarry", "six saves alternating machine and user: how the two halves of Version count")
            .Step("machine-enabled", Edit(machine, "Computer", "Enabled"))
            .Step("user-enabled", Edit(user, "User", "Enabled"))
            .Step("machine-disabled", Edit(machine, "Computer", "Disabled"))
            .Step("user-disabled", Edit(user, "User", "Disabled"))
            .Step("machine-not-configured", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("user-not-configured", Edit(user, "User", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "Version_FromKnownStart", "start gpt.ini with Version 196613 (user 3, machine 5), then a machine and a user save")
            .StartWith(Crafted.Files("gpt.ini with Version=196613 and no extension lists", Crafted.Gpt("[General]", "Version=196613")))
            .Mark("start")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("user", Edit(user, "User", "Enabled"))
            .Build();
        yield return new CaseBuilder("C", "Version_FromHighMachine", "start gpt.ini with machine version 65535, one machine save (does the low half wrap into the high half)")
            .StartWith(Crafted.Files("gpt.ini with Version=65535", Crafted.Gpt("[General]", "Version=65535")))
            .Mark("start")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Build();
        yield return new CaseBuilder("C", "GptIni_ForeignExtensions", "start gpt.ini with made up extension pairs in both lists; saves must keep them and add the pair")
            .StartWith(Crafted.Files(
                "gpt.ini with made up gPCMachineExtensionNames and gPCUserExtensionNames and Version=2",
                Crafted.Gpt(
                    "[General]",
                    $"gPCMachineExtensionNames=[{Crafted.FakeGuidOne}{Crafted.FakeGuidTwo}]",
                    $"gPCUserExtensionNames=[{Crafted.FakeGuidTwo}{Crafted.FakeGuidOne}]",
                    "Version=2")))
            .Mark("start")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("user", Edit(user, "User", "Enabled"))
            .Step("machine-cleared", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("user-cleared", Edit(user, "User", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "GptIni_PairRemoval", "when does the registry pair leave the extension list: set and clear in both files")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("machine-cleared", Edit(machine, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("machine-comment", Edit(machine, "Computer", "Not Configured", "bwc comment one"))
            .Step("machine-comment-removed", Edit(machine, "Computer", "Not Configured", string.Empty))
            .Step("user", Edit(user, "User", "Enabled"))
            .Step("user-cleared", Edit(user, "User", "Not Configured", press: new[] { "OK" }))
            .Build();

        // Shared list keys (OQ-04, OQ-06): two policies writing one list key.
        OptionSpec ListRows(PolicyInfo policy, int count, string prefix) => OptionChoices.Base(policy, policy.Elements[0]) with
        {
            ClearRows = true,
            Rows = Enumerable.Range(1, count).Select(i => new List<string> { prefix + i }).ToList(),
        };
        OptionSpec ListNone(PolicyInfo policy) => OptionChoices.Base(policy, policy.Elements[0]) with { ClearRows = true };
        yield return new CaseBuilder("C", "SharedList_OneThenTwo", "two policies sharing one list key: One, Two, then One cleared, Two cleared")
            .Step("one", Edit(listOne, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listOne, 2, "a-") }))
            .Step("two", Edit(listTwo, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listTwo, 2, "b-") }))
            .Step("one-not-configured", Edit(listOne, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("two-not-configured", Edit(listTwo, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "SharedList_TwoThenOne", "the same, saved Two first and cleared Two first")
            .Step("two", Edit(listTwo, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listTwo, 2, "b-") }))
            .Step("one", Edit(listOne, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listOne, 2, "a-") }))
            .Step("two-not-configured", Edit(listTwo, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("one-not-configured", Edit(listOne, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "SharedList_DisableAndShrink", "shared list: One Enabled with three rows, Two with one row, One shrunk to one row, One Disabled, Two Disabled")
            .Step("one-three", Edit(listOne, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listOne, 3, "a-") }))
            .Step("two-one", Edit(listTwo, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listTwo, 1, "b-") }))
            .Step("one-shrunk", Edit(listOne, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(listOne, 1, "a-") }))
            .Step("one-disabled", Edit(listOne, "Computer", "Disabled"))
            .Step("two-disabled", Edit(listTwo, "Computer", "Disabled"))
            .Step("one-not-configured", Edit(listOne, "Computer", "Not Configured", press: new[] { "OK" }))
            .Step("two-not-configured", Edit(listTwo, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "SharedList_EmptyList", "shared list: Enabled with no rows (does classic warn or write an empty key)")
            .Step("one-empty", Edit(listOne, "Computer", "Enabled", options: new List<OptionSpec> { ListNone(listOne) }))
            .Step("one-not-configured", Edit(listOne, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();
        yield return new CaseBuilder("C", "List_PolicyValueName", "a list under a policy that has its own value name: Enabled, shrink, Disabled, Not Configured")
            .Step("enabled-two", Edit(switched, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(switched, 2, "s-") }))
            .Step("enabled-one", Edit(switched, "Computer", "Enabled", options: new List<OptionSpec> { ListRows(switched, 1, "s-") }))
            .Step("disabled", Edit(switched, "Computer", "Disabled"))
            .Step("not-configured", Edit(switched, "Computer", "Not Configured", press: new[] { "OK" }))
            .Build();

        // comment.cmtx namespace list on the first save (policy-engine contract: the list held only the edited policy's namespace).
        yield return new CaseBuilder("C", "Cmtx_FirstSave_ForeignNamespaces", "start comment.cmtx lists two made up namespaces; the first save, a second save, a comment")
            .StartWith(Crafted.Files("Machine comment.cmtx with two made up using entries", Crafted.Cmtx("BWC.Fake.One", "BWC.Fake.Two")))
            .Mark("start")
            .Step("first-save", Edit(machine, "Computer", "Enabled"))
            .Step("second-save", Edit(decimalValue, "Computer", "Enabled"))
            .Step("comment", Edit(machine, "Computer", "Enabled", "bwc comment one"))
            .Build();
        yield return new CaseBuilder("C", "Cmtx_FirstSave_ThenUser", "the same with a user save after the machine save")
            .StartWith(Crafted.Files("Machine comment.cmtx with one made up using entry", Crafted.Cmtx("BWC.Fake.One")))
            .Mark("start")
            .Step("machine", Edit(machine, "Computer", "Enabled"))
            .Step("user", Edit(user, "User", "Enabled"))
            .Step("user-comment", Edit(user, "User", "Enabled", "bwc comment one"))
            .Build();
        yield return new CaseBuilder("C", "Cmtx_NoStartFile", "no comment.cmtx at the start: the first plain save, then a comment on another policy")
            .Step("first-save", Edit(machine, "Computer", "Enabled"))
            .Step("comment-on-other", Edit(decimalValue, "Computer", "Not Configured", "bwc comment one"))
            .Step("second-save", Edit(decimalValue, "Computer", "Enabled"))
            .Build();
    }
}
