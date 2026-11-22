using Bwc.Gpo.Pol;
using Bwc.Policy.Admx;
using Bwc.Policy.Model;
using static Bwc.Policy.Tests.PolicyModelTestSupport;

namespace Bwc.Policy.Tests;

/// <summary>
/// List policies and the <c>**delvals.</c> instruction (docs/failure-modes/policy-state.md section 7): non-additive
/// replace, additive keep, Disabled, Not Configured, shared keys and what the reader does with the instruction.
/// </summary>
[TestClass]
public sealed class PolicyListInstructionTests
{
    private static readonly string Plain = Lists + @"\Plain";
    private static readonly string Additive = Lists + @"\Additive";

    private static byte[] Delvals(string key) => Rec(key, "**delvals.", 1, [0x20, 0x00, 0x00, 0x00]);

    private static ElementValue L(params string[] data) => ElementValue.FromList(data.Select(ListEntry.Of).ToArray());

    private static PolicyEdit PlainEdit(params string[] data) => PolicyEdit.Enabled(("PlainList", L(data)));

    private static PolicyEdit AdditiveEdit(ListEntry[] entries, params ListEntry[] removed) =>
        PolicyEdit.Enabled(("AdditiveList", ElementValue.FromListWithRemoved(entries, removed)));

    private static string[] Entries(PolicyStateSnapshot snapshot, string id) =>
        ((ListValue)snapshot.Elements[id]).Entries.Select(e => e.Data).ToArray();

    [TestMethod]
    public void NonAdditive_Enabled_WritesDelvalsThenTheEntries()
    {
        var document = ApplyTwice("Pol_List_Plain", PlainEdit("b", "a"));

        AssertBytes(document, Delvals(Plain), RecSz(Plain, "b", "b"), RecSz(Plain, "a", "a"));
        var read = Read("Pol_List_Plain", document);
        Assert.AreEqual(PolicyState.Enabled, read.State);
        CollectionAssert.AreEqual(new[] { "b", "a" }, Entries(read, "PlainList"));
    }

    [TestMethod]
    public void NonAdditive_ChangingEntries_KeepsDelvalsFirstAndDoesNotStackIt()
    {
        var document = ApplyTwice("Pol_List_Plain", PlainEdit("a", "b"));

        Apply("Pol_List_Plain", document, PlainEdit("b", "c"));

        AssertBytes(document, Delvals(Plain), RecSz(Plain, "b", "b"), RecSz(Plain, "c", "c"));
        Assert.AreEqual(3, document.RecordCount);
    }

    [TestMethod]
    public void NonAdditive_ForeignValues_AreRemovedAndDelvalsComesFirst()
    {
        var document = Parse(RecDw(Plain, "number", 5), RecSz(Plain, "old", "old"));

        Apply("Pol_List_Plain", document, PlainEdit("new"));

        AssertBytes(document, Delvals(Plain), RecSz(Plain, "new", "new"));
    }

    [TestMethod]
    public void NonAdditive_DelvalsBehindTheEntries_IsMovedToTheFront()
    {
        var document = Parse(RecSz(Plain, "a", "a"), Delvals(Plain));

        var result = Apply("Pol_List_Plain", document, PlainEdit("a"));

        Assert.IsTrue(result.Changed);
        AssertBytes(document, Delvals(Plain), RecSz(Plain, "a", "a"));
    }

    [TestMethod]
    public void NonAdditive_EntriesAlreadyThere_OnlyAddsDelvals()
    {
        var document = Parse(RecSz(Plain, "a", "a"), RecSz(Plain, "b", "b"));

        Apply("Pol_List_Plain", document, PlainEdit("a", "b"));

        AssertBytes(document, Delvals(Plain), RecSz(Plain, "a", "a"), RecSz(Plain, "b", "b"));
    }

    [TestMethod]
    public void NonAdditive_UnknownInstructionsAndSoftRecords_StayThroughEnabledDisabledAndNotConfigured()
    {
        var mystery = Rec(Plain, "**Mystery", 9, [1, 2]);
        var soft = Rec(Plain, "**soft.zzz", 1, [0x20, 0, 0, 0]);
        var document = Parse(mystery, soft);

        Apply("Pol_List_Plain", document, PlainEdit("a"));
        AssertBytes(document, Delvals(Plain), mystery, soft, RecSz(Plain, "a", "a"));

        Apply("Pol_List_Plain", document, PolicyEdit.Disabled);
        AssertBytes(document, Delvals(Plain), mystery, soft);

        Apply("Pol_List_Plain", document, PolicyEdit.NotConfigured);
        AssertBytes(document, mystery, soft);
        Assert.AreEqual(2, Read("Pol_List_Plain", document).OwnedRecords.Count);
    }

    [TestMethod]
    public void NonAdditive_EnabledWithNoEntries_RemovesValuesAndDelvalsAndWritesNeither()
    {
        var document = Parse(Delvals(Plain), RecSz(Plain, "a", "a"));

        Apply("Pol_List_Plain", document, PolicyEdit.Enabled(("PlainList", ElementValue.FromList())));

        Assert.AreEqual(PolicyRules.EnabledEmptyListWritesDelvals, !document.IsEmpty);
        Assert.AreEqual(PolicyRules.EnabledEmptyListWritesDelvals ? PolicyState.Disabled : PolicyState.NotConfigured, Read("Pol_List_Plain", document).State);
    }

    [TestMethod]
    public void Additive_Enabled_KeepsForeignValuesAndWritesNoDelvals()
    {
        var document = Parse(RecSz(Additive, "keep", "keep"));

        var again = ApplyTwice("Pol_List_Additive", AdditiveEdit([ListEntry.Of("a")]), document);

        AssertBytes(again, RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"));
        Assert.IsNull(again.GetValue(Additive, "**delvals."));
    }

    [TestMethod]
    public void Additive_RemovedEntries_AreRemovedOnlyWhenTheyStillHoldTheirData()
    {
        var document = Parse(RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"), RecSz(Additive, "b", "b"), RecSz(Additive, "c", "changed"));
        var removed = new[]
        {
            new ListEntry("b", "b"),
            ListEntry.Of("gone"),
            new ListEntry("c", "was"),
            new ListEntry("keep", "keep-with-other-data"),
        };

        var result = Apply("Pol_List_Additive", document, AdditiveEdit([ListEntry.Of("a")], removed));

        Assert.IsTrue(result.IsValid);
        AssertBytes(document, RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"), RecSz(Additive, "c", "changed"));
    }

    [TestMethod]
    public void Additive_ARemovedEntryThatIsAlsoAnEntry_IsNotRemoved()
    {
        var document = Parse(RecSz(Additive, "a", "a"));

        Apply("Pol_List_Additive", document, AdditiveEdit([ListEntry.Of("a")], ListEntry.Of("a")));

        AssertBytes(document, RecSz(Additive, "a", "a"));
    }

    [TestMethod]
    public void Additive_RemovedByTheNameItWasReadWith_RemovesAPrefixedEntry()
    {
        var document = Parse(RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"));
        var read = (ListValue)Read("Pol_List_Additive", document).Elements["AdditiveList"];
        var kept = new[] { read.Entries.First(e => e.Name == "keep") };
        var dropped = read.Entries.Where(e => e.Name == "a").ToArray();

        Apply("Pol_List_Additive", document, AdditiveEdit(kept, dropped));

        AssertBytes(document, RecSz(Additive, "keep", "keep"));
    }

    [TestMethod]
    public void Additive_RemovedOnANonAdditiveList_IsIgnored()
    {
        var document = Parse(RecSz(Plain, "a", "a"));

        Apply("Pol_List_Plain", document, PolicyEdit.Enabled(("PlainList", ElementValue.FromListWithRemoved([ListEntry.Of("b")], [ListEntry.Of("a")]))));

        AssertBytes(document, Delvals(Plain), RecSz(Plain, "b", "b"));
    }

    [TestMethod]
    public void Additive_Enabled_RemovesADelvalsOfItsKey()
    {
        var document = Parse(Delvals(Additive), RecSz(Additive, "keep", "keep"));

        ApplyTwice("Pol_List_Additive", AdditiveEdit([ListEntry.Of("a")]), document);

        AssertBytes(document, RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"));
    }

    [TestMethod]
    public void Additive_EnabledWithNoEntries_RemovesTheRemovedEntriesAndKeepsTheRest()
    {
        var document = Parse(RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"));

        Apply("Pol_List_Additive", document, AdditiveEdit([], ListEntry.Of("a")));

        AssertBytes(document, RecSz(Additive, "keep", "keep"));
    }

    [TestMethod]
    public void Disabled_OnAListOnlyPolicy_WritesDelvalsAndReadsDisabled()
    {
        var document = ApplyTwice("Pol_List_Plain", PlainEdit("a", "b"));

        Apply("Pol_List_Plain", document, PolicyEdit.Disabled);

        AssertBytes(document, Delvals(Plain));
        var read = Read("Pol_List_Plain", document);
        Assert.AreEqual(PolicyRules.DisabledListWritesDelvals ? PolicyState.Disabled : PolicyState.NotConfigured, read.State);
        Assert.AreEqual(0, read.Elements.Count);
        var again = Reload(document);
        Assert.IsFalse(Apply("Pol_List_Plain", again, PolicyEdit.Disabled).Changed);
        Assert.IsFalse(again.HasChanges);
    }

    [TestMethod]
    public void Disabled_OnAnAdditiveList_ClearsTheKeyLikeTheOtherLists()
    {
        var document = Parse(RecSz(Additive, "keep", "keep"), RecSz(Additive, "a", "a"));

        Apply("Pol_List_Additive", document, PolicyEdit.Disabled);

        AssertBytes(document, Delvals(Additive));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_Additive", document).State);
    }

    [TestMethod]
    public void NotConfigured_RemovesTheDelvalsAndTheEmptyKeyGoesAway()
    {
        var document = ApplyTwice("Pol_List_Plain", PlainEdit("a"));
        Apply("Pol_List_Plain", document, PolicyEdit.Disabled);

        Apply("Pol_List_Plain", document, PolicyEdit.NotConfigured);

        Assert.IsTrue(document.IsEmpty);
        Assert.AreEqual(PolicyState.NotConfigured, Read("Pol_List_Plain", document).State);
        Assert.AreEqual(0, Read("Pol_List_Plain", document).OwnedRecords.Count);
    }

    [TestMethod]
    public void NotConfigured_LeavesTheListKeysOfOtherPoliciesAlone()
    {
        var other = Lists + @"\Prefix";
        var document = Parse(Delvals(Plain), RecSz(Plain, "a", "a"), Delvals(other), RecSz(other, "Item1", "p"));

        Apply("Pol_List_Plain", document, PolicyEdit.NotConfigured);

        AssertBytes(document, Delvals(other), RecSz(other, "Item1", "p"));
    }

    [TestMethod]
    public void Reader_DelvalsBehindTheEntries_IsEnabledAndTheInstructionIsNotAnEntry()
    {
        var document = Parse(RecSz(Plain, "a", "one"), Delvals(Plain), RecSz(Plain, "b", "two"));

        var read = Read("Pol_List_Plain", document);

        Assert.AreEqual(PolicyState.Enabled, read.State);
        CollectionAssert.AreEqual(new[] { "one", "two" }, Entries(read, "PlainList"));
        CollectionAssert.AreEquivalent(new[] { "a", "**delvals.", "b" }, read.OwnedRecords.Select(r => r.ValueName).ToArray());
    }

    [TestMethod]
    public void Reader_AnUpperCaseDelvalsAsTheOnlyRecord_ReadsDisabledAndIsOwned()
    {
        var document = Parse(Rec(Plain.ToUpperInvariant(), "**DELVALS.", 1, [0x20, 0, 0, 0]));

        var read = Read("Pol_List_Plain", document);

        Assert.AreEqual(PolicyState.Disabled, read.State);
        Assert.AreEqual(1, read.OwnedRecords.Count);
    }

    [TestMethod]
    public void ValueNamePolicy_Disabled_WritesTheDeleteInstructionAndTheDelvalsOfItsList()
    {
        var key = Lists + @"\Switched";
        var document = ApplyTwice("Pol_List_PolicyValueName", PolicyEdit.Enabled(("SwitchedList", L("z"))));
        AssertBytes(document, RecDw(Lists, "ListSwitch", 1), Delvals(key), RecSz(key, "z", "z"));

        Apply("Pol_List_PolicyValueName", document, PolicyEdit.Disabled);

        // Capture A/Machine_Disabled: Disabled of a plain value-name policy writes the delete instruction, not DWORD 0.
        AssertBytes(document, Del(Lists, "ListSwitch"), Delvals(key));
        Assert.AreEqual(PolicyState.Disabled, Read("Pol_List_PolicyValueName", document).State);

        Apply("Pol_List_PolicyValueName", document, PolicyEdit.NotConfigured);
        Assert.IsTrue(document.IsEmpty);
    }

    [TestMethod]
    public void SharedKey_OneAdditiveOneNot_FollowsTheDocumentedSequence()
    {
        using var temp = new TempTemplates();
        temp.AddSimple(
            "Shared",
            "T.Shared",
            """
            <policy name="Replace" class="Machine" displayName="$(string.Replace)" key="Software\T">
              <parentCategory ref="Root" /><supportedOn ref="SUP" />
              <elements><list id="ReplaceList" key="Software\T\List" /></elements>
            </policy>
            <policy name="Add" class="Machine" displayName="$(string.Add)" key="Software\T">
              <parentCategory ref="Root" /><supportedOn ref="SUP" />
              <elements><list id="AddList" key="Software\T\List" additive="true" /></elements>
            </policy>
            """,
            """<string id="Replace">Replace</string><string id="Add">Add</string>""");
        var set = temp.Load("en-US");
        var replace = set.FindPolicy("T.Shared", "Replace") ?? throw new AssertFailedException("Replace");
        var add = set.FindPolicy("T.Shared", "Add") ?? throw new AssertFailedException("Add");
        const string key = @"Software\T\List";
        var document = NewDocument();

        PolicyStateWriter.Apply(replace, document, PolicyEdit.Enabled(("ReplaceList", L("x"))));
        AssertBytes(document, Delvals(key), RecSz(key, "x", "x"));

        PolicyStateWriter.Apply(add, document, PolicyEdit.Enabled(("AddList", ElementValue.FromListWithRemoved([ListEntry.Of("y")], []))));
        AssertBytes(document, RecSz(key, "x", "x"), RecSz(key, "y", "y"));
        Assert.AreEqual(PolicyState.Enabled, PolicyStateReader.Read(replace, document).State);
        CollectionAssert.AreEqual(new[] { "x", "y" }, Entries(PolicyStateReader.Read(replace, document), "ReplaceList"));

        PolicyStateWriter.Apply(replace, document, PolicyEdit.Enabled(("ReplaceList", L("x"))));
        AssertBytes(document, Delvals(key), RecSz(key, "x", "x"));

        PolicyStateWriter.Apply(add, document, PolicyEdit.NotConfigured);
        Assert.IsTrue(document.IsEmpty);
    }
}
