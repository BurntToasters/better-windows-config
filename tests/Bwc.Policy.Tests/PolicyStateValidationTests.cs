using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>Typed validation problems for element values, and the rule that an invalid edit writes nothing.</summary>
[TestClass]
public sealed class PolicyStateValidationTests
{
    private static IReadOnlyList<PolicyEditProblem> Validate(string policy, params (string Id, ElementValue Value)[] values) =>
        PolicyEditValidator.Validate(P(policy), PolicyEdit.Enabled(values));

    private static void AssertSingle(IReadOnlyList<PolicyEditProblem> problems, string id, PolicyProblemKind kind, ulong? limit = null)
    {
        Assert.AreEqual(1, problems.Count, string.Join(", ", problems));
        Assert.AreEqual(new PolicyEditProblem(id, kind, limit), problems[0]);
    }

    [TestMethod]
    public void Decimal_BelowMinimum_AndAboveMaximum()
    {
        AssertSingle(Validate("Pol_Decimal_MinMax", ("MinMaxNumber", ElementValue.FromNumber(9))), "MinMaxNumber", PolicyProblemKind.BelowMinimum, 10);
        AssertSingle(Validate("Pol_Decimal_MinMax", ("MinMaxNumber", ElementValue.FromNumber(501))), "MinMaxNumber", PolicyProblemKind.AboveMaximum, 500);
        Assert.AreEqual(0, Validate("Pol_Decimal_MinMax", ("MinMaxNumber", ElementValue.FromNumber(10))).Count);
        Assert.AreEqual(0, Validate("Pol_Decimal_MinMax", ("MinMaxNumber", ElementValue.FromNumber(500))).Count);
    }

    [TestMethod]
    public void Decimal_WithoutLimits_IsBoundedByTheDwordRange()
    {
        Assert.AreEqual(0, Validate("Pol_Value_NoValueName_ElementsOnly", ("OnlyNumber", ElementValue.FromNumber(uint.MaxValue))).Count);
        AssertSingle(
            Validate("Pol_Value_NoValueName_ElementsOnly", ("OnlyNumber", ElementValue.FromNumber((ulong)uint.MaxValue + 1))),
            "OnlyNumber",
            PolicyProblemKind.AboveMaximum,
            uint.MaxValue);
    }

    [TestMethod]
    public void Decimal_Soft_StillHasItsRangeChecked()
    {
        AssertSingle(Validate("Pol_Decimal_Soft", ("SoftNumber", ElementValue.FromNumber(101))), "SoftNumber", PolicyProblemKind.AboveMaximum, 100);
        AssertSingle(Validate("Pol_Decimal_Soft", ("SoftNumber", ElementValue.FromNumber(0))), "SoftNumber", PolicyProblemKind.BelowMinimum, 1);
    }

    [TestMethod]
    public void LongDecimal_IsBoundedByItsOwnMaximum()
    {
        Assert.AreEqual(0, Validate("Pol_LongDecimal_Basic", ("LongNumber", ElementValue.FromNumber(9_000_000_000_000))).Count);
        AssertSingle(
            Validate("Pol_LongDecimal_Basic", ("LongNumber", ElementValue.FromNumber(9_000_000_000_001))),
            "LongNumber",
            PolicyProblemKind.AboveMaximum,
            9_000_000_000_000);
    }

    [TestMethod]
    public void RequiredElements_AreProblemsWhenEmpty()
    {
        AssertSingle(Validate("Pol_Decimal_Required"), "RequiredNumber", PolicyProblemKind.Required);
        AssertSingle(Validate("Pol_Text_Required"), "RequiredText", PolicyProblemKind.Required);
        AssertSingle(Validate("Pol_Text_Required", ("RequiredText", ElementValue.FromText(string.Empty))), "RequiredText", PolicyProblemKind.Required);
        AssertSingle(Validate("Pol_Enum_Required"), "RequiredChoice", PolicyProblemKind.Required);
        AssertSingle(Validate("Pol_MultiText_Limits"), "LimitedLines", PolicyProblemKind.Required);
        AssertSingle(
            Validate("Pol_MultiText_Limits", ("LimitedLines", ElementValue.FromLines(string.Empty, string.Empty))),
            "LimitedLines",
            PolicyProblemKind.Required);
        AssertSingle(Validate("Pol_LongDecimal_RequiredSoftText"), "LongTextNumber", PolicyProblemKind.Required);
    }


    [TestMethod]
    public void OptionalElements_MayBeEmpty()
    {
        Assert.AreEqual(0, Validate("Pol_Text_Plain").Count);
        Assert.AreEqual(0, Validate("Pol_Decimal_MinMax").Count);
        Assert.AreEqual(0, Validate("Pol_Enum_DecimalItems").Count);
        Assert.AreEqual(0, Validate("Pol_List_Plain").Count);
        Assert.AreEqual(0, Validate("Pol_Bool_TrueFalseValues").Count);
    }

    [TestMethod]
    public void Text_TooLong_UsesMaxLength_OrTheDefault()
    {
        AssertSingle(Validate("Pol_Text_MaxLength", ("ShortText", ElementValue.FromText("1234567890123"))), "ShortText", PolicyProblemKind.TooLong, 12);
        Assert.AreEqual(0, Validate("Pol_Text_MaxLength", ("ShortText", ElementValue.FromText("123456789012"))).Count);
        AssertSingle(
            Validate("Pol_Text_Plain", ("PlainText", ElementValue.FromText(new string('x', (int)PolicyRules.DefaultTextMaxLength + 1)))),
            "PlainText",
            PolicyProblemKind.TooLong,
            PolicyRules.DefaultTextMaxLength);
    }

    [TestMethod]
    public void Text_WithNul_IsAnInvalidCharacter()
    {
        AssertSingle(Validate("Pol_Text_Plain", ("PlainText", ElementValue.FromText("a\0b"))), "PlainText", PolicyProblemKind.InvalidCharacter);
    }

    [TestMethod]
    public void MultiText_TooManyLines_AndTooLong()
    {
        AssertSingle(
            Validate("Pol_MultiText_Limits", ("LimitedLines", ElementValue.FromLines("a", "b", "c", "d"))),
            "LimitedLines",
            PolicyProblemKind.TooManyLines,
            3);
        AssertSingle(
            Validate("Pol_MultiText_Limits", ("LimitedLines", ElementValue.FromLines("1234567890", "1234567890"))),
            "LimitedLines",
            PolicyProblemKind.TooLong,
            20);
        Assert.AreEqual(0, Validate("Pol_MultiText_Limits", ("LimitedLines", ElementValue.FromLines("123456789", "1234567890"))).Count);
    }

    [TestMethod]
    public void Enum_IndexOutsideTheItems_IsInvalid()
    {
        AssertSingle(Validate("Pol_Enum_DecimalItems", ("DecimalChoice", ElementValue.FromChoice(4))), "DecimalChoice", PolicyProblemKind.InvalidChoice);
        AssertSingle(Validate("Pol_Enum_DecimalItems", ("DecimalChoice", ElementValue.FromChoice(-1))), "DecimalChoice", PolicyProblemKind.InvalidChoice);
        Assert.AreEqual(0, Validate("Pol_Enum_DecimalItems", ("DecimalChoice", ElementValue.FromChoice(3))).Count);
    }

    [TestMethod]
    public void List_EntryProblems_CarryTheEntryIndex()
    {
        var plain = Validate("Pol_List_Plain", ("PlainList", ElementValue.FromList(ListEntry.Of("a"), ListEntry.Of(string.Empty), ListEntry.Of("A"))));
        var explicitList = Validate(
            "Pol_List_ExplicitValue",
            ("ExplicitList", ElementValue.FromList(new ListEntry("n", "v"), new ListEntry(null, "v"), new ListEntry("**del.x", "v"))));

        CollectionAssert.AreEqual(
            new[]
            {
                new PolicyEditProblem("PlainList", PolicyProblemKind.EntryEmpty, EntryIndex: 1),
                new PolicyEditProblem("PlainList", PolicyProblemKind.DuplicateEntry, EntryIndex: 2),
            },
            plain.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new PolicyEditProblem("ExplicitList", PolicyProblemKind.EntryNameMissing, EntryIndex: 1),
                new PolicyEditProblem("ExplicitList", PolicyProblemKind.InvalidCharacter, EntryIndex: 2),
            },
            explicitList.ToArray());
    }

    [TestMethod]
    public void WrongValueKind_AndUnknownElement()
    {
        AssertSingle(Validate("Pol_Decimal_MinMax", ("MinMaxNumber", ElementValue.FromText("x"))), "MinMaxNumber", PolicyProblemKind.WrongValueKind);
        AssertSingle(Validate("Pol_Text_Plain", ("Nope", ElementValue.FromText("x"))), "Nope", PolicyProblemKind.UnknownElement);
    }

    [TestMethod]
    public void ElementIds_MatchIgnoringCase()
    {
        Assert.AreEqual(0, Validate("Pol_Decimal_MinMax", ("minmaxnumber", ElementValue.FromNumber(20))).Count);
    }

    [TestMethod]
    public void DisabledAndNotConfiguredEdits_AreNotValidated()
    {
        Assert.AreEqual(0, PolicyEditValidator.Validate(P("Pol_Decimal_Required"), PolicyEdit.Disabled).Count);
        Assert.AreEqual(0, PolicyEditValidator.Validate(P("Pol_Decimal_Required"), PolicyEdit.NotConfigured).Count);
    }

    [TestMethod]
    public void ProblemsInSeveralElements_AreAllReportedInElementOrder()
    {
        var problems = Validate(
            "Pol_Shared_ElementsReverseOrder",
            ("AlphaElement", ElementValue.FromText("x")),
            ("ZetaElement", ElementValue.FromNumber(1)));

        CollectionAssert.AreEqual(
            new[]
            {
                new PolicyEditProblem("ZetaElement", PolicyProblemKind.WrongValueKind),
                new PolicyEditProblem("AlphaElement", PolicyProblemKind.WrongValueKind),
            },
            problems.ToArray());
    }

    [TestMethod]
    public void InvalidEdit_WritesNothing_AndReportsTheProblems()
    {
        var document = Parse(RecDw(Number, "MinMaxNumber", 20));

        var result = Apply("Pol_Decimal_MinMax", document, PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(5))));

        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.Changed);
        Assert.IsFalse(document.HasChanges);
        AssertSingle(result.Problems, "MinMaxNumber", PolicyProblemKind.BelowMinimum, 10);
    }

    [TestMethod]
    public void ValidEdit_ReportsNoProblems_AndMatchesTheValidator()
    {
        var edit = PolicyEdit.Enabled(("MinMaxNumber", ElementValue.FromNumber(20)));

        var result = Apply("Pol_Decimal_MinMax", NewDocument(), edit);

        Assert.IsTrue(result.IsValid);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(0, PolicyEditValidator.Validate(P("Pol_Decimal_MinMax"), edit).Count);
    }
}
