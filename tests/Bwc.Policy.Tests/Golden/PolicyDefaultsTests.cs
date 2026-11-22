using System.Globalization;
using System.Text.Json.Nodes;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests.Golden;

/// <summary>One "Enabled, defaults" operation of a capture: what classic's dialog showed in every control right
/// after Enabled was chosen on a policy that was not Enabled.</summary>
internal sealed record DefaultsObservation(string CaseId, int OpIndex, string Namespace, string PolicyId, IReadOnlyList<JsonObject> Options)
{
    internal string Describe() => $"{CaseId} operation {OpIndex}";

    /// <summary>The members of the options that a defaults comparison uses, as one string two captures can be
    /// compared by.</summary>
    internal string Fingerprint() => string.Join("|", Options.Select(o => string.Join(",", new[] { "kind", "text", "checkState", "selectedIndex", "selectedText", "lines" }
        .Select(m => m + "=" + (o[m]?.ToJsonString() ?? "-")))));
}

/// <summary>Reads the default values out of every capture of every group (policy-defaults.md, section F).</summary>
internal static class CapturedDefaults
{
    private static readonly Lazy<IReadOnlyList<DefaultsObservation>> All = new(Read);

    internal static IReadOnlyList<DefaultsObservation> Observations => All.Value;

    /// <summary>The test of a capture text against the engine's text. A scrubbed capture holds a text that is not
    /// a fixture string as <c>#</c> plus the first 12 hex digits of its SHA-256.</summary>
    /// <param name="captured">The text of the capture.</param>
    /// <param name="actual">The text the engine produced.</param>
    /// <returns>True when they are the same text.</returns>
    internal static bool TextMatches(string captured, string actual) =>
        string.Equals(captured, actual, StringComparison.Ordinal)
        || (captured.Length == 13 && captured[0] == '#' && string.Equals(captured, UnnoticedEditRule.Hash(actual), StringComparison.Ordinal));

    private static IReadOnlyList<DefaultsObservation> Read()
    {
        var found = new List<DefaultsObservation>();
        foreach (var caseJson in Directory.GetFiles(GoldenRepo.Goldens, "case.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetDirectoryName(caseJson)!;
            if (!File.Exists(Path.Combine(folder, "complete.json")) || JsonNode.Parse(File.ReadAllText(caseJson)) is not JsonObject root)
            {
                continue;
            }

            var caseId = Path.GetRelativePath(GoldenRepo.Goldens, folder).Replace('\\', '/');
            var observed = (root["observed"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            foreach (var op in (root["ops"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(o => o["kind"]?.GetValue<string>() == "editPolicy"))
            {
                var detail = op["op"] as JsonObject;
                if (detail?["state"]?.GetValue<string>() != "Enabled" || detail["options"] is JsonArray { Count: > 0 })
                {
                    continue;
                }

                var index = op["index"]!.GetValue<int>();
                var data = observed.FirstOrDefault(o => o["index"]?.GetValue<int>() == index)?["data"] as JsonObject;
                var before = data?["before"]?["state"]?.GetValue<string>();
                if (data?["afterSet"]?["options"] is not JsonArray options || before is null or "Enabled")
                {
                    continue;
                }

                found.Add(new DefaultsObservation(
                    caseId,
                    index,
                    detail["policyNamespace"]!.GetValue<string>(),
                    detail["policyId"]!.GetValue<string>(),
                    options.OfType<JsonObject>().ToList()));
            }
        }

        return found;
    }
}

/// <summary>
/// The defaults function against classic's dialog (piece E6b, docs/failure-modes/policy-defaults.md, section F). The
/// expected values are built from the captures alone: for every policy that any capture enabled with no option typed,
/// the engine's values must equal what each control showed.
/// </summary>
[TestClass]
public sealed class PolicyDefaultsTests
{
    private static readonly string[] AllKinds = ["staticText", "checkBox", "textBox", "spinner", "comboBox", "dropdownList", "multiText", "list"];

    private static readonly IReadOnlyDictionary<string, string> KindNames = AllKinds.ToDictionary(UnnoticedEditRule.Hash, k => k);

    private static string KindOf(PresentationControl control) =>
        control.Kind switch
        {
            PresentationControlKind.Text => "staticText",
            PresentationControlKind.CheckBox => "checkBox",
            PresentationControlKind.TextBox => "textBox",
            PresentationControlKind.DecimalTextBox or PresentationControlKind.LongDecimalTextBox => control.Spin == false ? "textBox" : "spinner",
            PresentationControlKind.ComboBox => "comboBox",
            PresentationControlKind.DropdownList => "dropdownList",
            PresentationControlKind.MultiTextBox => "multiText",
            PresentationControlKind.ListBox => "list",
            _ => "unknown",
        };

    private static IEnumerable<IGrouping<string, DefaultsObservation>> ByPolicy() =>
        CapturedDefaults.Observations.GroupBy(o => o.Namespace + ":" + o.PolicyId).OrderBy(g => g.Key, StringComparer.Ordinal);

    private static PolicyDefinition PolicyOf(IGrouping<string, DefaultsObservation> group) =>
        GoldenReplay.LoadedTemplates.FindPolicy(group.First().Namespace, group.First().PolicyId)
            ?? throw new AssertFailedException($"{group.Key} ({group.First().Describe()}) is not in the loaded fixture templates.");

    /// <summary>One expectation: what control i showed, as an element value that may be missing.</summary>
    private sealed record Expected(PolicyElement Element, Func<ElementValue?, string?> Check, bool HasEntry);

    private static List<Expected> ExpectedOf(PolicyDefinition policy, DefaultsObservation observation)
    {
        var controls = policy.Presentation?.Controls ?? [];
        if (controls.Count != observation.Options.Count)
        {
            throw new AssertFailedException(
                $"{observation.Describe()}: the dialog showed {observation.Options.Count} controls and the presentation of {policy.Name} has {controls.Count}.");
        }

        var expected = new List<Expected>();
        for (var i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            var option = observation.Options[i];
            var kind = KindNames.TryGetValue(option["kind"]?.GetValue<string>() ?? string.Empty, out var name) ? name : "unknown (" + option["kind"] + ")";
            if (kind != KindOf(control))
            {
                throw new AssertFailedException($"{observation.Describe()}: control {i} was a {kind} in the dialog and the presentation of {policy.Name} says {KindOf(control)}.");
            }

            if (control.Element is not { } element)
            {
                continue;
            }

            switch (control.Kind)
            {
                case PresentationControlKind.CheckBox:
                    var state = option["checkState"]!.GetValue<int>();
                    Assert.IsTrue(state is 0 or 1, $"{observation.Describe()}: check state {state} is neither checked nor unchecked.");
                    expected.Add(new Expected(element, v => v is BooleanValue b && b.Checked == (state == 1) ? null : $"expected checked={state == 1}, got {v}", true));
                    break;
                case PresentationControlKind.DecimalTextBox or PresentationControlKind.LongDecimalTextBox:
                    var digits = option["text"]!.GetValue<string>();
                    Assert.IsTrue(ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number), $"{observation.Describe()}: '{digits}' is not a number.");
                    expected.Add(new Expected(element, v => v is NumberValue n && n.Number == number ? null : $"expected number {number}, got {v}", true));
                    break;
                case PresentationControlKind.TextBox or PresentationControlKind.ComboBox:
                    var text = option["text"]!.GetValue<string>();
                    expected.Add(new Expected(element, v => v is TextValue t && CapturedDefaults.TextMatches(text, t.Text) ? null : $"expected text '{text}', got {v}", true));
                    break;
                case PresentationControlKind.DropdownList:
                    var selected = option["selectedIndex"]!.GetValue<int>();
                    if (selected < 0)
                    {
                        expected.Add(new Expected(element, v => v is null ? null : $"expected nothing selected, got {v}", false));
                        break;
                    }

                    var shown = option["selectedText"]!.GetValue<string>();
                    var items = ((EnumElement)element).Items;
                    var match = Enumerable.Range(0, items.Count).Where(n => CapturedDefaults.TextMatches(shown, items[n].DisplayName)).ToArray();
                    Assert.AreEqual(1, match.Length, $"{observation.Describe()}: the selected text '{shown}' names {match.Length} items of {element.Id}.");
                    expected.Add(new Expected(element, v => v is ChoiceValue c && c.ItemIndex == match[0] ? null : $"expected item {match[0]} ('{shown}'), got {v}", true));
                    break;
                case PresentationControlKind.ListBox:
                    expected.Add(new Expected(element, v => v is ListValue { Entries.Count: 0 } ? null : $"expected a list with no rows, got {v}", true));
                    break;
                case PresentationControlKind.MultiTextBox:
                    var lines = option["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray();
                    expected.Add(new Expected(
                        element,
                        v => v is MultiTextValue m && string.Join("\r", m.Lines) == string.Join("\r", lines) ? null : $"expected lines [{string.Join("|", lines)}], got {v}",
                        true));
                    break;
                default:
                    throw new AssertFailedException($"{observation.Describe()}: the control kind {control.Kind} has an element and no expectation.");
            }
        }

        return expected;
    }

    [TestMethod]
    public void F19_CapturedDefaults_CoverEveryPolicyOfTheFixture_AndAgreeWithThePresentations()
    {
        var groups = ByPolicy().ToList();
        Assert.IsTrue(groups.Count >= 60, $"only {groups.Count} policies were enabled with defaults in the captures; the data-driven test must not pass empty.");
        foreach (var group in groups)
        {
            var policy = PolicyOf(group);
            var prints = group.Select(o => o.Fingerprint()).Distinct().ToList();
            Assert.AreEqual(1, prints.Count, $"F16: the captures of {group.Key} disagree about its defaults: {string.Join(" and ", group.Select(o => o.Describe()).Take(3))}");
            _ = ExpectedOf(policy, group.First());
        }
    }

    [TestMethod]
    public void F19_TheCapturesShowEveryControlKindOfTheFixtureAtLeastOnce()
    {
        var checkedByDefault = ByPolicy().Select(PolicyOf).SelectMany(p => p.Presentation?.Controls ?? []).Where(c => c.DefaultChecked == true).ToList();
        Assert.IsTrue(checkedByDefault.Count > 0, "no captured policy has a check box that is checked by default");
        var kinds = ByPolicy().Select(PolicyOf).SelectMany(p => p.Presentation?.Controls ?? []).Select(c => c.Kind).Distinct().Order().ToArray();
        CollectionAssert.AreEqual(Enum.GetValues<PresentationControlKind>().Order().ToArray(), kinds, "every control kind needs a captured default");
    }

    [TestMethod]
    public void F18_TextMatches_ComparesPlainAndHashedCaptureText()
    {
        Assert.IsTrue(CapturedDefaults.TextMatches("Oslo", "Oslo"));
        Assert.IsFalse(CapturedDefaults.TextMatches("Oslo", "oslo"));
        Assert.IsTrue(CapturedDefaults.TextMatches(UnnoticedEditRule.Hash("plain default"), "plain default"));
        Assert.IsFalse(CapturedDefaults.TextMatches(UnnoticedEditRule.Hash("plain default"), "plain defaults"));
        Assert.IsFalse(CapturedDefaults.TextMatches("#not-a-hash", "plain default"));
        Assert.IsTrue(CapturedDefaults.TextMatches(string.Empty, string.Empty));
        Assert.IsFalse(CapturedDefaults.TextMatches(string.Empty, "x"));
    }

    [TestMethod]
    public void For_EqualsWhatClassicsDialogShowed()
    {
        var problems = new List<string>();
        foreach (var group in ByPolicy())
        {
            var policy = PolicyOf(group);
            var expected = ExpectedOf(policy, group.First());
            var actual = PolicyDefaults.For(policy);
            foreach (var one in expected)
            {
                actual.TryGetValue(one.Element.Id, out var value);
                if (one.Check(value) is { } problem)
                {
                    problems.Add($"{group.Key} element {one.Element.Id} ({group.First().Describe()}): {problem}");
                }
            }

            var wanted = expected.Where(e => e.HasEntry).Select(e => e.Element.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extra in actual.Keys.Where(k => !wanted.Contains(k)))
            {
                problems.Add($"{group.Key} element {extra} ({group.First().Describe()}): the dialog showed no value for it, the function gave {actual[extra]}");
            }
        }

        Assert.AreEqual(0, problems.Count, $"{problems.Count} default value(s) differ from classic's dialog:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [TestMethod]
    public void F14_For_ReturnsAFreshDictionaryWithTheSameContentEveryTime_AndLeavesThePolicyAlone()
    {
        var policy = GoldenReplay.LoadedTemplates.FindPolicy(Fixture.OracleNamespace, "Pol_Amp_LabelSingle")!;
        var controlsBefore = policy.Presentation!.Controls.ToArray();

        var first = PolicyDefaults.For(policy);
        var second = PolicyDefaults.For(policy);

        Assert.AreNotSame(first, second, "a caller that changes the result must not change the next call");
        CollectionAssert.AreEquivalent(first.Keys.ToArray(), second.Keys.ToArray());
        foreach (var pair in first)
        {
            Assert.AreEqual(pair.Value, second[pair.Key], pair.Key);
        }

        CollectionAssert.AreEqual(controlsBefore, policy.Presentation.Controls.ToArray(), "the presentation must not be changed");
    }

    [TestMethod]
    public void F13_For_APolicyWithNoPresentationOrNoElement_GivesAnEmptyDictionary()
    {
        var plain = GoldenReplay.LoadedTemplates.FindPolicy(Fixture.OracleNamespace, "Pol_Class_Machine")!;
        Assert.AreEqual(0, plain.Elements.Count);

        Assert.AreEqual(0, PolicyDefaults.For(plain).Count);
    }

    [TestMethod]
    public void F2_For_ANumberWithNoDefaultValueShowsOne_NotItsMinimum()
    {
        var policy = GoldenReplay.LoadedTemplates.FindPolicy(Fixture.OracleNamespace, "Pol_Decimal_Required")!;
        Assert.IsNull(policy.Presentation!.Controls.Single().DefaultValue, "the fixture presentation has no default value");

        var values = PolicyDefaults.For(policy);

        Assert.AreEqual(ElementValue.FromNumber(1), values["RequiredNumber"]);
    }

    [TestMethod]
    public void F7_For_AnEnumWithNoDefaultItemSelectsNothing()
    {
        var policy = GoldenReplay.LoadedTemplates.FindPolicy(Fixture.OracleNamespace, "Pol_Enum_StringItems")!;

        var values = PolicyDefaults.For(policy);

        Assert.AreEqual(0, values.Count, "classic shows no selection, so the element has no entry");
    }

    [TestMethod]
    public void F8_For_AnEnumWithDefaultItemSelectsTheItemOfTheTemplate()
    {
        var policy = GoldenReplay.LoadedTemplates.FindPolicy(Fixture.OracleNamespace, "Pol_Enum_NoSortDefaultItem")!;

        var values = PolicyDefaults.For(policy);

        Assert.AreEqual(ElementValue.FromChoice(1), values["UnsortedChoice"], "defaultItem 1 is the item Alpha of the template");
    }
}
