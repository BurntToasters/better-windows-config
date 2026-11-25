using Bwc.Policy.Admx;
using Bwc.Policy.Filter;
using Bwc.Policy.Model;

namespace Bwc.Policy.Tests;

/// <summary>A small template set with known titles, help texts, keys and platforms, built in a scratch folder.</summary>
internal static class FilterTestSupport
{
    private static readonly Lazy<TemplateSet> Cached = new(Build);

    internal static TemplateSet Set => Cached.Value;

    internal static PolicyDefinition P(string name) =>
        Set.FindPolicy("T.Filter", name) ?? throw new AssertFailedException("Filter fixture policy missing: " + name);

    internal static PolicyStateSnapshot State(PolicyState state) => new(state, new Dictionary<string, ElementValue>(), []);

    internal static FilterSubject Subject(string policy, PolicyState? state = PolicyState.NotConfigured, string? comment = null) =>
        new(P(policy), state is null ? null : State(state.Value), comment);

    internal static FilterOptions Options(
        FilterTriState managed = FilterTriState.Any,
        FilterTriState configured = FilterTriState.Any,
        FilterTriState commented = FilterTriState.Any,
        KeywordFilter? keyword = null,
        RequirementsFilter? requirements = null) =>
        new(managed, configured, commented, keyword ?? KeywordFilter.Off, requirements ?? RequirementsFilter.Off);

    internal static KeywordFilter Words(
        string text, KeywordMatchMode mode = KeywordMatchMode.Any, bool title = true, bool help = true, bool comment = true) =>
        new(true, text, mode, title, help, comment);

    internal static PolicyFilter Create(FilterOptions options)
    {
        Assert.IsTrue(PolicyFilter.TryCreate(options, out var filter, out var problems), string.Join(", ", problems));
        return filter!;
    }

    internal static bool Matches(FilterOptions options, FilterSubject subject) => Create(options).Matches(subject);

    private static TemplateSet Build()
    {
        var temp = new TempTemplates();
        try
        {
            temp.Add(
                "Filter",
                "T.Filter",
                "t",
                """
                <supportedOn><definitions>
                  <definition name="SUP1" displayName="$(string.SUP1)" />
                  <definition name="SUP2" displayName="$(string.SUP2)" />
                </definitions></supportedOn>
                <categories><category name="Root" displayName="$(string.Root)" /></categories>
                <policies>
                  <policy name="P1" class="Machine" displayName="$(string.P1)" explainText="$(string.P1_Help)" key="Software\Policies\Test" valueName="v">
                    <parentCategory ref="Root" /><supportedOn ref="SUP1" />
                  </policy>
                  <policy name="P2" class="User" displayName="$(string.P2)" key="Software\Vendor\Test" valueName="v">
                    <parentCategory ref="Root" /><supportedOn ref="SUP2" />
                  </policy>
                  <policy name="P3" class="Both" displayName="$(string.P3)" explainText="$(string.P3_Help)" key="Software\Microsoft\Windows\CurrentVersion\Policies\Explorer" valueName="v">
                  </policy>
                  <policy name="P4" class="Machine" displayName="$(string.P4)" key="Software\PoliciesX\Foo" valueName="v">
                    <parentCategory ref="Root" /><supportedOn ref="SUP1" />
                  </policy>
                </policies>
                """,
                """
                <string id="Root">Root</string>
                <string id="SUP1">At least Windows 10</string>
                <string id="SUP2">At least Windows 11</string>
                <string id="P1">Alpha Firewall</string>
                <string id="P1_Help">Controls the network firewall. Block all, inbound traffic.</string>
                <string id="P2">Beta Printer</string>
                <string id="P3">Gamma</string>
                <string id="P3_Help">alpha beta   gamma</string>
                <string id="P4">Delta</string>
                """);
            return temp.Load();
        }
        finally
        {
            temp.Dispose();
        }
    }
}
