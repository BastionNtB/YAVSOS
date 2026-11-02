using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
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
        Assert.Equal("This chest has no rule to copy.", RuleCopy.CopyTooltip(false, 0));
        Assert.DoesNotContain("can't show", RuleCopy.CopyTooltip(true, 0));
        Assert.Contains("Parts this version can't show stay here.", RuleCopy.CopyTooltip(true, 2));
        Assert.StartsWith("Copy a rule", RuleCopy.PasteTooltip(null, true, true));
        var state = Loaded(ContainerPolicyEnvelope.Serialize(1, new ChestRules(Full, ChestRuleState.Configured, includes: new[] { "Wood" })), Full);
        var copy = RuleCopy.Of(state, "Wood")!;
        Assert.Contains("Wood", RuleCopy.PasteTooltip(copy, true, false));
        Assert.EndsWith("It replaces this chest's rule.", RuleCopy.PasteTooltip(copy, true, true));
        Assert.DoesNotContain("replaces", RuleCopy.PasteTooltip(copy, true, false));
        Assert.Contains("no one else is using this chest", RuleCopy.PasteTooltip(copy, false, true));
        Assert.Equal("Gives this chest the copied rule.", RuleCopy.PasteTooltip(RuleCopy.Of(state, null), true, false));
    }

    [Fact]
    public void Folded_ShowsTheHeaderBannersAndLabelLineOnly()
    {
        foreach (var part in new[] { PanelPart.Header, PanelPart.Banners })
        { Assert.True(PanelFold.Shows(part, true)); Assert.True(PanelFold.Shows(part, false)); }
        foreach (var part in new[] { PanelPart.Rule, PanelPart.Edit, PanelPart.Filters, PanelPart.Chest, PanelPart.Cleanup })
        { Assert.False(PanelFold.Shows(part, true)); Assert.True(PanelFold.Shows(part, false)); }
        Assert.True(PanelFold.Shows(PanelPart.Label, true)); Assert.False(PanelFold.Shows(PanelPart.Label, false));

        Assert.Equal(("Ores · priority 2 (Urgent)", true), PanelFold.Line("Ores · priority 2 (Urgent)", false));
        Assert.Equal((PanelFold.NoRule, false), PanelFold.Line(null, false));
        Assert.Equal((PanelFold.NoRule, false), PanelFold.Line("", false));
        Assert.Null(PanelFold.Line(null, true)); // An unreadable rule: its banner says so.
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
        Assert.Contains("<color=" + ChestLabelPreview.Color + ">Ores</color>", ChestLabelPreview.Tooltip("Ores", true));
        Assert.Equal("This chest has no rule, so it shows no label.", ChestLabelPreview.Tooltip(null, true));
        Assert.Contains("ShowChestLabels", ChestLabelPreview.Tooltip("Ores", false));
    }
}
