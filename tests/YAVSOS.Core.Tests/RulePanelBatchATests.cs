using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>Phase 10 batch A: the message strip (P10-25), undo and redo (P10-07, X26) and rule suggestions (P10-08, X22).</summary>
public sealed class RulePanelBatchATests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    private static string Write(string current, ChestRuleEdit? edit)
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(current, current, Taxonomy, edit, out var updated));
        return updated!;
    }

    // A rule with a part the editor can't show (a second filtered alternative) and overflow (D086 D5).
    private static string Hidden() => ContainerPolicyEnvelope.Serialize(7, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] {
            new ClassificationFilterRule(Taxonomy, new[] { "food" }),
            new ClassificationFilterRule(Taxonomy, new[] { "materials" }, new[] { new KeyValuePair<string, IEnumerable<string>>("usage", new[] { "fuel" }) }) },
        includes: new[] { "Wood" }, priority: 3, overflow: true));

    // ---- X26: the restore path (A2-09, B2-10).

    [Fact]
    public void Restore_PutsBackAClearedRuleWhole_HiddenPartsIncluded()
    {
        var stored = Hidden();
        Assert.Equal(2, ChestRuleEdit.From(ContainerPolicyEnvelope.Read(stored, Taxonomy).Rules!, withFilters: true).HiddenParts);
        var cleared = Write(stored, null);
        Assert.Equal("", cleared);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(cleared, cleared, stored, out var restored));
        var read = ContainerPolicyEnvelope.Read(restored!, Taxonomy);
        var before = ContainerPolicyEnvelope.Read(stored, Taxonomy);
        Assert.Equal(before.Rules!.Fingerprint, read.Rules!.Fingerprint); // Every part: both alternatives, Always, priority, overflow.
        Assert.True(read.Rules.Overflow); Assert.Equal(3, read.Rules.Priority); Assert.Equal(2, read.Rules.FullRules.Count);
        Assert.Equal(8UL, read.Revision); // Above the restored payload's own revision, so it is a new payload.
        Assert.NotEqual(stored, restored);
    }

    [Fact]
    public void Restore_IsACompareAndSet_AndRefusesAfterAnotherChange()
    {
        var first = Write("", new ChestRuleEdit(null, new[] { "Wood" }, null));
        var second = Write(first, new ChestRuleEdit(null, new[] { "Stone" }, null));
        var foreign = Write(second, new ChestRuleEdit(null, new[] { "Tin" }, null)); // Another player's (or version's) save.
        Assert.Equal(TransferRefusal.PolicyChanged, ContainerPolicyEnvelope.TryRestore(foreign, second, first, out var none));
        Assert.Null(none);
        // Undoing the first edit on a chest without a rule removes the rule again.
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(first, first, "", out var empty));
        Assert.Equal("", empty);
    }

    [Fact]
    public void Restore_RevisionIsAboveBothPayloads()
    {
        var old = ContainerPolicyEnvelope.Serialize(2, new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }));
        var now = ContainerPolicyEnvelope.Serialize(40, new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Tin" }));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(now, now, old, out var restored));
        var read = ContainerPolicyEnvelope.Read(restored!, Taxonomy);
        Assert.Equal(41UL, read.Revision); Assert.Equal(new[] { "Wood" }, read.Rules!.Includes);
        // At the last revision a payload is written back as it was.
        var last = ContainerPolicyEnvelope.Serialize(ulong.MaxValue, new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore("", "", last, out var same));
        Assert.Equal(last, same);
    }

    [Fact]
    public void Restore_PutsBackARuleThisVersionCantRead_AsItWas()
    {
        var future = "{\"schemaVersion\":2,\"revision\":\"5\",\"policy\":{}}";
        var unknownFormat = ContainerPolicyEnvelope.Serialize(4, new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }))
            .Replace("yavsos-chest-rules-xml-1", "yavsos-chest-rules-xml-2");
        foreach (var unreadable in new[] { future, unknownFormat })
        {
            Assert.Equal(ContainerPolicyReadState.Unsupported, ContainerPolicyEnvelope.Read(unreadable, Taxonomy).State);
            var cleared = Write(unreadable, null); // Clear rule works on a rule from another version.
            Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(cleared, cleared, unreadable, out var restored));
            Assert.Equal(unreadable, restored);
        }
        // A schema-1 envelope whose rules this version can't parse keeps its rules text unread, at a new revision.
        var odd = ContainerPolicyEnvelope.Serialize(4, new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { "Wood" }))
            .Replace("yavsos-chest-rules-xml-1\",\"rulesXml\":\"", "yavsos-chest-rules-xml-1\",\"rulesXml\":\"x");
        Assert.Equal(ContainerPolicyReadState.Unsupported, ContainerPolicyEnvelope.Read(odd, Taxonomy).State);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore("", "", odd, out var kept));
        Assert.Equal(odd.Replace("\"revision\":\"4\"", "\"revision\":\"5\""), kept);
    }

    // ---- X26: the per-chest record (D130).

    [Fact]
    public void History_KeepsEachChestsLastChange_AndTurnsUndoIntoRedo()
    {
        var history = new RuleHistory<int>();
        Assert.Null(history.For(1));
        history.Changed(1, "", "a");
        history.Changed(2, "x", "y");
        history.Changed(1, "a", "b"); // The last change replaces the record.
        history.Changed(3, "same", "same"); // Nothing changed: no record.
        Assert.Equal(2, history.Count);
        var record = history.For(1)!;
        Assert.Equal(("a", "b", false), (record.Before, record.After, record.Redo));
        history.Restored(1, "a2"); // The undo wrote a2 (a at a new revision).
        record = history.For(1)!;
        Assert.Equal(("b", "a2", true), (record.Before, record.After, record.Redo));
        history.Restored(1, "b2"); // The redo.
        Assert.Equal(("a2", "b2", false), (history.For(1)!.Before, history.For(1)!.After, history.For(1)!.Redo));
        Assert.Equal("y", history.For(2)!.After); // Other chests keep theirs.
        history.Drop(1);
        Assert.Null(history.For(1));
        history.Restored(1, "z"); // No record: nothing to turn.
        Assert.Null(history.For(1));
        history.Clear(); // The world ended.
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void UndoThenRedo_ThroughTheEnvelope_PutsEachRuleBack()
    {
        // As RuleControls does it: every save records (before, after); undo and redo restore through compare-and-set.
        var history = new RuleHistory<string>();
        var stored = Hidden();
        var cleared = Write(stored, null);
        history.Changed("chest", stored, cleared);
        var record = history.For("chest")!;
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(cleared, record.After, record.Before, out var undone));
        history.Restored("chest", undone!);
        Assert.Equal(ContainerPolicyEnvelope.Read(stored, Taxonomy).Rules!.Fingerprint, ContainerPolicyEnvelope.Read(undone!, Taxonomy).Rules!.Fingerprint);
        record = history.For("chest")!;
        Assert.True(record.Redo);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryRestore(undone!, record.After, record.Before, out var redone));
        Assert.Equal("", redone);
        history.Restored("chest", redone!);
        // Another change after the redo: the undo is refused and the record dropped (the panel says why).
        var foreign = Write(redone!, new ChestRuleEdit(null, new[] { "Tin" }, null));
        record = history.For("chest")!;
        Assert.Equal(TransferRefusal.PolicyChanged, ContainerPolicyEnvelope.TryRestore(foreign, record.After, record.Before, out _));
    }

    [Fact]
    public void UndoButton_Tooltip_SaysWhatAClickPutsBack()
    {
        var key = KeyNames.Format("Z", new[] { "LeftControl" });
        Assert.Equal(("Undo", "Nothing to undo on this chest."), EnglishText.Of(UndoButton.Tip(null, true, null, key)));
        var history = new RuleHistory<int>();
        history.Changed(1, "", "a");
        Assert.Equal(("Undo [Ctrl + Z]", "Puts back this chest's rule from before your last change:\nNo rule"), EnglishText.Of(UndoButton.Tip(history.For(1), true, null, key)));
        Assert.Equal(("Undo [Ctrl + Z]", "Another player is using this chest. Undo once they close it."), EnglishText.Of(UndoButton.Tip(history.For(1), false, null, key)));
        history.Restored(1, "");
        Assert.Equal(("Redo [Ctrl + Z]", "Puts back the change you undid:\nWood"), EnglishText.Of(UndoButton.Tip(history.For(1), true, "Wood", key)));
        Assert.Equal("Another player is using this chest. Redo once they close it.", EnglishText.Of(UndoButton.Tip(history.For(1), false, "Wood", key)).Text);
        // The label is tinted by the game side, not marked up in Core.
        Assert.Equal(Message.Of("yavsos_redo_tip").With("rule", "Wood", Tone.Label), UndoButton.Tip(history.For(1), true, "Wood", key).Body);
        Assert.Equal("The rule changed after your edit, so you can't undo it.", EnglishText.Of(Message.Of("yavsos_edit_undo_changed")));
        Assert.Equal("The rule changed after your undo, so you can't redo it.", EnglishText.Of(Message.Of("yavsos_edit_redo_changed")));
    }

    // ---- P10-25: the strip (D132, variant B).

    [Fact]
    public void Strip_ShowsTheMessage_ThenABanner_ThenTheLabel()
    {
        var messages = new PanelMessages();
        var idle = PanelStrip.Of(messages, StripBanner.None, Stasher.Yavsos, "Ores", folded: false);
        Assert.Equal((StripKind.Label, "Ores", 2), (idle.Kind, idle.Label, idle.Lines));
        Assert.Equal(("Chest label", "Ores\nWhat players see when they point at this chest. Messages show here until your next action."), EnglishText.Of(idle.Tip));
        var none = PanelStrip.Of(messages, StripBanner.None, Stasher.Yavsos, null, folded: true);
        Assert.Equal(StripKind.NoRule, none.Kind);
        Assert.Equal("No rule", EnglishText.Of(none.Text!));
        Assert.Equal("What players see when they point at this chest. Unfold the panel to see or change its rule.", EnglishText.Of(none.Tip).Text);

        var busy = PanelStrip.Of(messages, StripBanner.InUse, Stasher.Yavsos, "Ores", false);
        Assert.Equal(StripKind.Banner, busy.Kind);
        Assert.Equal("Another player is using this chest. You can see its rule and edit it once they close it.", EnglishText.Of(busy.Text!));
        var other = PanelStrip.Of(messages, StripBanner.Unreadable, Stasher.AzuAutoStore, null, false);
        Assert.Equal((StripKind.Banner, PanelStrip.UnreadableLines), (other.Kind, other.Lines));
        Assert.Equal(Message.Of("yavsos_panel_banner_unreadable_azu"), other.Text);
        Assert.Equal(Message.Of("yavsos_panel_banner_unreadable"), PanelStrip.Of(messages, StripBanner.Unreadable, Stasher.Yavsos, null, false).Text);

        // A refusal comes first, even over a banner, and keeps the mode's height.
        messages.Say(PanelControl.Clear, Message.Of("yavsos_edit_chest_in_use"));
        var refused = PanelStrip.Of(messages, StripBanner.Unreadable, Stasher.Yavsos, "Ores", false);
        Assert.Equal((StripKind.Refusal, PanelStrip.UnreadableLines), (refused.Kind, refused.Lines));
        Assert.Equal(Message.Of("yavsos_edit_chest_in_use"), refused.Text);
        Assert.Equal("Another player is using this chest. Try again once they close it.", EnglishText.Of(refused.Tip).Text);
        messages.Say(PanelControl.Cleanup, RunReport.ForCleanup().Summary(), MessageKind.Summary);
        Assert.Equal(StripKind.Summary, PanelStrip.Of(messages, StripBanner.None, Stasher.Yavsos, "Ores", false).Kind);
        messages.Action(); // The next action: back to the label.
        Assert.Equal(StripKind.Label, PanelStrip.Of(messages, StripBanner.None, Stasher.Yavsos, "Ores", false).Kind);
    }

    // ---- X22: suggestions (D130, D132).

    private static SuggestionStack Stack(string prefab, string category, bool accepted = false, bool never = false) =>
        new SuggestionStack(prefab, new[] { category }, accepted, never);

    [Fact]
    public void Suggestion_ADeepCategoryWithSixtyPercentOfTheStacks()
    {
        // The wireframe's metals chest: Copper ×3, Tin ×2, Bronze ×2, Iron, Resin, Wood: 8 of 10 stacks are Metals.
        var stacks = new List<SuggestionStack>();
        foreach (var (prefab, n) in new[] { ("Copper", 3), ("Tin", 2), ("Bronze", 2), ("Iron", 1) })
            for (var i = 0; i < n; i++) stacks.Add(Stack(prefab, "materials.metal"));
        stacks.Add(Stack("Resin", "materials"));
        stacks.Add(Stack("Wood", "materials.wood"));
        var found = RuleSuggestion.Find(Taxonomy, stacks, hasRule: false);
        Assert.Equal("materials.metal", found.Category); // Deepest wins over Materials (10 of 10).
        Assert.Equal((8, 10), (found.Stacks, found.Total));
        Assert.Equal(new[] { "Copper", "Tin", "Bronze", "Iron", "Resin", "Wood" }, found.Missed); // No rule: everything, once each.

        // 3 of 5 is exactly 60 %; 2 of 4 is not.
        var three = new[] { Stack("Copper", "materials.metal"), Stack("Tin", "materials.metal"), Stack("Iron", "materials.metal"), Stack("Wood", "materials.wood"), Stack("Raspberry", "food") };
        Assert.Equal("materials.metal", RuleSuggestion.Find(Taxonomy, three, false).Category);
        var half = new[] { Stack("Copper", "materials.metal"), Stack("Tin", "materials.metal"), Stack("Raspberry", "food"), Stack("Honey", "food") };
        Assert.Null(RuleSuggestion.Find(Taxonomy, half, false).Category);
        // At least 1 stack: a chest with one stack suggests its category.
        var one = RuleSuggestion.Find(Taxonomy, new[] { Stack("Wood", "materials.wood") }, false);
        Assert.Equal(("materials.wood", 1, 1), (one.Category, one.Stacks, one.Total));
        Assert.False(RuleSuggestion.Find(Taxonomy, new SuggestionStack[0], false).Any);
        // The umbrella when no subcategory reaches 60 %: Wood 2, Metals 2, Stone 1 of 5 are all Materials.
        var mixed = new[] { Stack("Wood", "materials.wood"), Stack("FineWood", "materials.wood"), Stack("Copper", "materials.metal"), Stack("Tin", "materials.metal"), Stack("Stone", "materials.stone") };
        Assert.Equal("materials", RuleSuggestion.Find(Taxonomy, mixed, false).Category);
    }

    [Fact]
    public void Suggestion_OnAChestWithARule_OffersOnlyWhatItMisses()
    {
        var stacks = new[] {
            Stack("Copper", "materials.metal", accepted: true), Stack("Copper", "materials.metal", accepted: true), Stack("Tin", "materials.metal", accepted: true),
            Stack("Resin", "materials"), Stack("Resin", "materials"), Stack("Wood", "materials.wood", never: true) };
        var found = RuleSuggestion.Find(Taxonomy, stacks, hasRule: true);
        Assert.Null(found.Category); // The wireframe's choice: a category only for a chest without a rule.
        Assert.Equal(new[] { "Resin" }, found.Missed); // Accepted and Never items stay out, each item once.
        Assert.True(found.Any);
        var all = RuleSuggestion.Find(Taxonomy, stacks.Take(3).ToArray(), hasRule: true);
        Assert.False(all.Any); // The rule accepts everything here: nothing to offer.
        Assert.Same(RuleSuggestions.None, all);
        Assert.NotEqual(found.Signature, RuleSuggestion.Find(Taxonomy, stacks.Take(4).Concat(new[] { Stack("Honey", "food") }).ToArray(), true).Signature);
    }

    [Fact]
    public void Suggestion_Ties_GoToTheLargerShare_ThenTheSmallerCategory_ThenTheId()
    {
        // Two subcategories of the same depth, both at 60 % or more, can't both be: so test ties of share with the member count.
        var stacks = new[] { new SuggestionStack("Odd", new[] { "materials.wood", "materials.metal" }, false) };
        Assert.Equal("materials.metal", RuleSuggestion.Find(Taxonomy, stacks, false).Category); // Same share: the ID.
        Assert.Equal("materials.wood", RuleSuggestion.Find(Taxonomy, stacks, false, id => id == "materials.wood" ? 5 : 50).Category); // The smaller (D054).
    }

    [Fact]
    public void Suggestion_ChipsAndTooltips_InEnglish()
    {
        Assert.Equal("Set rule: Metals", EnglishText.Of(RuleSuggestion.SetText("Metals")));
        Assert.Equal(("Set rule: Metals", "8 of 10 stacks here are Metals. Gives this chest a rule that accepts Metals."), EnglishText.Of(RuleSuggestion.SetTip("Metals", 8, 10)));
        Assert.Equal(("Set rule: Wood", "The one stack here is Wood. Gives this chest a rule that accepts Wood."), EnglishText.Of(RuleSuggestion.SetTip("Wood", 1, 1)));
        Assert.Equal("Always: these items", EnglishText.Of(RuleSuggestion.AlwaysText));
        Assert.Equal(("Always: these items", "Gives this chest a rule that marks each item here Always:\nCopper, Tin"),
            EnglishText.Of(RuleSuggestion.AlwaysTip(new[] { "Copper", "Tin", "Copper" }, hasRule: false)));
        var many = Enumerable.Range(1, 15).Select(x => "Item" + x).ToList();
        Assert.EndsWith("Item12 +3 more", EnglishText.Of(RuleSuggestion.AlwaysTip(many, hasRule: true)).Text);
        Assert.StartsWith("Marks the items here that this rule doesn't accept Always, so it accepts them too:", EnglishText.Of(RuleSuggestion.AlwaysTip(many, true)).Text);
        Assert.Equal(("Not now", "Hides these suggestions on this chest until they change or you leave the world."), EnglishText.Of(RuleSuggestion.DismissTip));
    }

    [Fact]
    public void SuggestionRows_GiveEachChipItsOwnRow()
    {
        // Set rule and Always: Set rule beside ✕ on the first row, Always alone on the second; both keep their widths.
        Assert.Equal((120f, 200f, 1), RuleSuggestion.Rows(120, 200, 24, 402, 4));
        // A long category name: only Set rule is cut, to the row less ✕; Always keeps its width on its own row.
        Assert.Equal((402 - 24 - 4f, 150f, 1), RuleSuggestion.Rows(600, 150, 24, 402, 4));
        // Always alone: beside ✕ on the first row, the second empty.
        Assert.Equal((0f, 200f, 0), RuleSuggestion.Rows(0, 200, 24, 402, 4));
        Assert.Equal((0f, 402 - 24 - 4f, 0), RuleSuggestion.Rows(0, 600, 24, 402, 4));
        // On its own row Always may use the whole width.
        Assert.Equal((100f, 402f, 1), RuleSuggestion.Rows(100, 900, 24, 402, 4));
        Assert.Equal((0f, 0f, 0), RuleSuggestion.Rows(0, 0, 24, 402, 4));
    }

    [Fact]
    public void AcceptAll_MarksEachItemAlways()
    {
        var state = new RuleEditState();
        state.Choose("Wood", always: false); // Never
        state.AcceptAll(new[] { "Copper", "Wood" });
        Assert.Equal(new[] { "Copper", "Wood" }, state.Includes);
        Assert.Empty(state.Excludes);
    }
}
