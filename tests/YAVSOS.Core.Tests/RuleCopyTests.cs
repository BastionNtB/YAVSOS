using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

// X2 (D116): copy a chest's rule and paste it on another, saved like any edit; the folded panel's line.
public sealed class RuleCopyTests
{
    private static readonly ClassificationTaxonomy Full = DefaultClassificationTaxonomy.Create();
    // Stands in for a category set that lacks "fish" (a mod removed, or another version, D050).
    private static readonly ClassificationTaxonomy Reduced = new ClassificationTaxonomy(Full.Categories.Values.Where(x => x.Id != "fish"));

    private static KeyValuePair<string, IEnumerable<string>> Filter(string dimension, params string[] values) =>
        new KeyValuePair<string, IEnumerable<string>>(dimension, values);

    private static RuleEditState Loaded(string payload, ClassificationTaxonomy taxonomy)
    {
        var state = new RuleEditState();
        var read = ContainerPolicyEnvelope.Read(payload, taxonomy);
        if (read.HasRule) state.Load(read.Rules!);
        return state;
    }

    private static string Paste(RuleCopy copy, string target, ClassificationTaxonomy taxonomy)
    {
        var state = Loaded(target, taxonomy);
        state.Paste(copy, taxonomy);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(target, target, taxonomy, state.Edit(), out var updated));
        return updated!;
    }

    [Fact]
    public void CopyThenPaste_CarriesTheRuleAndPriority_AndReplacesTheTargetsRule()
    {
        var legacy = new ClassificationFilterRule(Full, new[] { "weapons" }, new[] { Filter("upgraded", "yes"), Filter("rarity", "magic") });
        var source = ContainerPolicyEnvelope.Serialize(4, new ChestRules(Full, ChestRuleState.Configured, new[] { legacy },
            new[] { "Wood" }, new[] { "Club" }, priority: 3));
        var copy = RuleCopy.Of(Loaded(source, Full), "Weapons › magic · priority 3")!;
        Assert.Equal("Weapons › magic · priority 3", copy.Label);

        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full,
            new ChestRuleEdit(new[] { "food" }, new[] { "Stone" }, null, priority: -2), out var target));
        var rules = ContainerPolicyEnvelope.Read(Paste(copy, target!, Full), Full).Rules!;

        var only = Assert.Single(rules.FullRules); // The target's "food" is gone, not kept beside the paste.
        Assert.Equal(new[] { "weapons" }, only.Categories);
        // The old "upgraded" filter came over as upgrade levels (S1, P6-21).
        Assert.False(only.Filters.ContainsKey("upgraded"));
        Assert.Equal(new[] { "2", "3", "4", "5+" }, only.Filters["quality"]);
        Assert.Equal(new[] { "magic" }, only.Filters["rarity"]);
        Assert.Equal(new[] { "Wood" }, rules.Includes); Assert.Equal(new[] { "Club" }, rules.Excludes);
        Assert.Equal(3, rules.Priority);
    }

    [Fact]
    public void Paste_OnAChestWithoutARule_CreatesIt()
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full,
            new ChestRuleEdit(new[] { "food" }, null, new[] { "Club" }, priority: 5), out var source));
        var rules = ContainerPolicyEnvelope.Read(Paste(RuleCopy.Of(Loaded(source!, Full), null)!, "", Full), Full).Rules!;
        Assert.Equal(new[] { "food" }, Assert.Single(rules.FullRules).Categories);
        Assert.Equal(new[] { "Club" }, rules.Excludes); Assert.Equal(5, rules.Priority);
    }

    [Fact]
    public void UnknownCategories_ArePastedAsStored()
    {
        // Stored with "fish" in the full set, read in a game without it (D050).
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full,
            new ChestRuleEdit(new[] { "food", "fish" }, new[] { "Stone" }, null), out var source));
        var copy = RuleCopy.Of(Loaded(source!, Reduced), null)!;
        Assert.Equal(new[] { "fish" }, copy.Unknown);

        var pasted = ContainerPolicyEnvelope.Read(Paste(copy, "", Reduced), Reduced).Rules!;
        Assert.Equal(new[] { "fish" }, pasted.UnknownCategories);
        // Read where "fish" exists again, the category is there.
        var back = Loaded(ContainerPolicyEnvelope.Serialize(1, pasted), Full);
        Assert.Contains("fish", back.Categories); Assert.Contains("food", back.Categories);
    }

    [Fact]
    public void ACategoryGoneSinceTheCopy_IsKeptAsUnknown_SoThePasteIsNotRefused()
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full,
            new ChestRuleEdit(new[] { "fish" }, null, null), out var source));
        var copy = RuleCopy.Of(Loaded(source!, Full), null)!; // Copied while "fish" existed.
        var pasted = ContainerPolicyEnvelope.Read(Paste(copy, "", Reduced), Reduced).Rules!;
        Assert.Equal(new[] { "fish" }, pasted.UnknownCategories);
    }

    [Fact]
    public void TheCleanupOptOut_IsNotPartOfTheCopy()
    {
        // "Don't clean up this chest" is stored apart from the rule; the copy has no member for it, and the paste
        // writes only the rule payload, so each chest keeps its own.
        Assert.DoesNotContain(typeof(RuleCopy).GetProperties(), x => x.Name.Contains("Cleanup") || x.Name.Contains("OptOut"));
        Assert.DoesNotContain(typeof(ChestRuleEdit).GetProperties(), x => x.Name.Contains("Cleanup") || x.Name.Contains("OptOut"));
    }

    [Fact]
    public void Paste_KeepsTheTargetsPartsTheEditorCantShow_AsAnyEditDoes()
    {
        // Two filter sets with categories: the editor shows the unfiltered one, the other is a hidden part (D086 D5).
        var target = ContainerPolicyEnvelope.Serialize(2, new ChestRules(Full, ChestRuleState.Configured, new[]
        {
            new ClassificationFilterRule(Full, new[] { "food" }),
            new ClassificationFilterRule(Full, new[] { "weapons" }, new[] { Filter("rarity", "magic") })
        }));
        Assert.Equal(1, Loaded(target, Full).HiddenParts);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full, new ChestRuleEdit(new[] { "materials" }, null, null), out var source));
        var rules = ContainerPolicyEnvelope.Read(Paste(RuleCopy.Of(Loaded(source!, Full), null)!, target, Full), Full).Rules!;
        Assert.Equal(2, rules.FullRules.Count);
        Assert.Contains(rules.FullRules, x => x.Categories.SequenceEqual(new[] { "materials" }) && x.Filters.Count == 0);
        Assert.Contains(rules.FullRules, x => x.Categories.SequenceEqual(new[] { "weapons" }) && x.Filters.ContainsKey("rarity"));
    }

    [Fact]
    public void Copy_NeedsAStoredRuleWithAShownPart()
    {
        Assert.Null(RuleCopy.Of(new RuleEditState(), "x"));
        Assert.False(RuleCopy.CanCopy(new RuleEditState()));
        var state = Loaded(ContainerPolicyEnvelope.Serialize(1, new ChestRules(Full, ChestRuleState.Configured, includes: new[] { "Wood" })), Full);
        Assert.True(RuleCopy.CanCopy(state));
        // The copy is a snapshot: later edits of the source don't change it.
        var copy = RuleCopy.Of(state, null)!;
        state.Choose("Stone", always: true);
        Assert.Equal(new[] { "Wood" }, copy.Includes); Assert.Equal("", copy.Label);
    }

    [Fact]
    public void Tooltips_ExplainEachState()
    {
        Assert.Equal(("Copy", "This chest has no rule to copy."), EnglishText.Of(RuleCopy.CopyTooltip(false, 0)));
        Assert.DoesNotContain("can't show", EnglishText.Of(RuleCopy.CopyTooltip(true, 0)).Text);
        Assert.EndsWith("Paste it on another chest. Parts this YAVSOS version can't show stay here.", EnglishText.Of(RuleCopy.CopyTooltip(true, 2)).Text);
        Assert.Equal(("Paste", "Copy a rule from another chest first, then paste it here."), EnglishText.Of(RuleCopy.PasteTooltip(null, true, true)));
        var state = Loaded(ContainerPolicyEnvelope.Serialize(1, new ChestRules(Full, ChestRuleState.Configured, includes: new[] { "Wood" })), Full);
        var copy = RuleCopy.Of(state, "Wood")!;
        Assert.Equal("Gives this chest the copied rule:\nWood", Text(RuleCopy.PasteTooltip(copy, true, false)));
        // Inventory case 7: the label is a tinted value, not markup in the text.
        Assert.Equal(Message.Of("yavsos_copy_paste_tip_label").With("label", "Wood", Tone.Label), RuleCopy.PasteTooltip(copy, true, false).Body);
        Assert.Equal("Gives this chest the copied rule:\nWood\nIt replaces this chest's rule.", Text(RuleCopy.PasteTooltip(copy, true, true)));
        Assert.Equal("Gives this chest the copied rule:\nWood\nAnother player is using this chest. Paste once they close it.", Text(RuleCopy.PasteTooltip(copy, false, true)));
        Assert.Equal("Gives this chest the copied rule.", Text(RuleCopy.PasteTooltip(RuleCopy.Of(state, null), true, false)));
        static string Text(Tooltip tip) => EnglishText.Of(tip).Text;
    }

    [Fact]
    public void Folded_ShowsTheHeaderAndStripOnly()
    {
        foreach (var part in new[] { PanelPart.Header, PanelPart.Strip })
        { Assert.True(PanelFold.Shows(part, true)); Assert.True(PanelFold.Shows(part, false)); }
        foreach (var part in new[] { PanelPart.Rule, PanelPart.Edit, PanelPart.Filters, PanelPart.Chest, PanelPart.Cleanup })
        { Assert.False(PanelFold.Shows(part, true)); Assert.True(PanelFold.Shows(part, false)); }

        // The fold button's tooltip names what a click does.
        Assert.Equal(("Fold", "Shows only the title, the status marks and the chest label. Every chest opens like this until you unfold the panel."),
            EnglishText.Of(PanelFold.ButtonTooltip(false)));
        Assert.Equal(("Unfold", "Shows the whole panel again."), EnglishText.Of(PanelFold.ButtonTooltip(true)));
        // The strip's label tooltip: the label, tinted, over what the line is; folded, how to see the rule.
        var line = PanelFold.LineTooltip("Ores", true);
        Assert.Equal(("Chest label", "Ores\nWhat players see when they point at this chest. Unfold the panel to see or change its rule."), EnglishText.Of(line));
        Assert.Equal(MessageList.Of(ListKind.Lines, new object[] { Tinted.Of("Ores", Tone.Label), Message.Of("yavsos_panel_label_line_tip") }), line.List);
        Assert.Equal("What players see when they point at this chest. Unfold the panel to see or change its rule.", EnglishText.Of(PanelFold.LineTooltip(null, true)).Text);
        Assert.Equal(("Chest label", "Ores\nWhat players see when they point at this chest. Messages show here until your next action."),
            EnglishText.Of(PanelFold.LineTooltip("Ores", false)));
    }

    [Fact]
    public void Folded_IsPlacedByTheUnfoldedHeight()
    {
        Assert.Equal(700, PanelFold.PlacedHeight(true, 90, 700));
        Assert.Equal(90, PanelFold.PlacedHeight(true, 90, 0)); // Never unfolded this session.
        Assert.Equal(650, PanelFold.PlacedHeight(false, 650, 700));
        // A panel pushed up from the screen's bottom keeps its top when folded.
        var screen = new PanelRect(0, 0, 1920, 1080); var chest = new PanelRect(500, 100, 400, 500);
        var unfolded = PanelPlacement.Beside(chest, 430, 700, screen, 0, 0);
        var folded = PanelPlacement.Beside(chest, 430, PanelFold.PlacedHeight(true, 90, 700), screen, 0, 0);
        Assert.Equal(unfolded.Top, folded.Top);
    }

    [Fact]
    public void LabelTooltip_ShowsTheLabel_OrSaysThereIsNone()
    {
        Assert.Equal(("Chest label", "What players see when they point at this chest:\nOres"), EnglishText.Of(ChestLabelPreview.Of("Ores", true)));
        // Inventory case 7: the label is tinted by the game side, never wrapped in markup by Core.
        Assert.Equal(Message.Of("yavsos_panel_label_tip").With("label", "Ores", Tone.Label), ChestLabelPreview.Of("Ores", true).Body);
        Assert.Equal("This chest has no rule, so it has no chest label.", EnglishText.Of(ChestLabelPreview.Of(null, true)).Text);
        Assert.Equal("What players see when they point at this chest:\nOres\nYour ShowChestLabels setting is off, so you don't see chest labels.",
            EnglishText.Of(ChestLabelPreview.Of("Ores", false)).Text);
    }
}
