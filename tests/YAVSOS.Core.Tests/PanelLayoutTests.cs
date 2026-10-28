using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

/// <summary>P8-19 (D101): the rule panel's fixed-height pieces: chip fit, the "No rule" chip, message spots, the short
/// highlight status, the priority heading and tooltips, search notes in the tree and the category highlight's items.</summary>
public sealed class PanelLayoutTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    [Fact]
    public void Fit_AllChipsThatFitInTwoRows_AreShown()
    {
        var shown = RuleChips.Fit(new float[] { 100, 100, 100, 100 }, 210, 4, _ => 60, out var places, out var more);
        Assert.Equal(4, shown);
        Assert.Null(more);
        Assert.Equal(new[] { (0f, 0), (104f, 0), (0f, 1), (104f, 1) }, places);
    }

    [Fact]
    public void Fit_TooMany_FoldsTheRestIntoAMoreChip_WithinTwoRows()
    {
        // Six chips of 100 in rows of 210: four fit, but the "+N more" chip needs room, so three show and "+3 more" follows.
        var shown = RuleChips.Fit(Enumerable.Repeat(100f, 6).ToList(), 210, 4, n => n < 10 ? 60 : 70, out var places, out var more);
        Assert.Equal(3, shown);
        Assert.Equal(3, places.Length);
        Assert.Equal((104f, 1), more);
        Assert.All(places, x => Assert.True(x.Row < RuleChips.Rows));
    }

    [Fact]
    public void Fit_AChipWiderThanARow_IsDrawnAtTheRowsWidth()
    {
        var shown = RuleChips.Fit(new float[] { 500, 50 }, 200, 4, _ => 40, out var places, out _);
        Assert.Equal(2, shown);
        Assert.Equal(new[] { (0f, 0), (0f, 1) }, places);
        Assert.Equal(1, RuleChips.Fit(new float[] { 500, 500, 500 }, 200, 4, _ => 40, out _, out var more));
        Assert.Equal((0f, 1), more);
    }

    [Fact]
    public void More_ListsTheFoldedChips_Bounded()
    {
        var chips = RuleChips.Build(Enumerable.Range(1, 30).Select(x => "Category " + x), "", new[] { "Copper" }, new string[0]);
        var text = RuleChips.More(chips, 5);
        Assert.StartsWith("Also in this rule\nCategory 6\n", text);
        Assert.EndsWith("\n… and 6 more", text); // 26 folded, 20 listed.
        Assert.Contains("Always: Copper", RuleChips.More(chips, 29));
    }

    [Fact]
    public void NoRule_FollowsWhatStashingDoesWithAChestWithoutARule()
    {
        Assert.Equal("No rule: stashing adds only items already in this chest.", RuleChips.NoRule(NoRuleStash.ExistingOnly).Text);
        Assert.Equal("No rule: stashing can put anything here.", RuleChips.NoRule(NoRuleStash.Anything).Text);
        Assert.Contains("Must Have Existing Item To Pull", RuleChips.NoRule(NoRuleStash.Anything).Detail);
        Assert.Equal("No rule: AzuAutoStore treats it normally.", RuleChips.NoRule(NoRuleStash.Unknown).Text); // Setting unreadable: no guess.
        Assert.All(new[] { NoRuleStash.ExistingOnly, NoRuleStash.Anything, NoRuleStash.Unknown }, x => Assert.Equal(ChipKind.Note, RuleChips.NoRule(x).Kind));
    }

    [Fact]
    public void Messages_StayAtTheirSpot_UntilTheNextAction()
    {
        var messages = new PanelMessages();
        Assert.Equal("", messages.At(MessageSpot.Tab));
        messages.Say(MessageSpot.Cleanup, "A stash or cleanup is already running.");
        Assert.Equal("A stash or cleanup is already running.", messages.At(MessageSpot.Cleanup));
        Assert.Equal("", messages.At(MessageSpot.Tab));
        Assert.Equal(MessageKind.Refusal, messages.Kind);
        var revision = messages.Revision;
        messages.Say(MessageSpot.Cleanup, "A stash or cleanup is already running.");
        Assert.Equal(revision, messages.Revision); // The same words again: nothing to redraw.
        messages.Say(MessageSpot.Chest, "The setting could not be confirmed.");
        Assert.Equal("", messages.At(MessageSpot.Cleanup)); // One message at a time.
        messages.Action();
        Assert.Equal("", messages.At(MessageSpot.Chest));
        Assert.True(messages.Revision > revision);
        messages.Say(MessageSpot.Cleanup, "Moved 0 stacks.", MessageKind.Summary);
        Assert.Equal(MessageKind.Summary, messages.Kind);
    }

    [Fact]
    public void HighlightStatus_OneShortLine_NothingWhileLooking()
    {
        Assert.Equal("", HighlightText.Status(false, 3, true, false, 10, false));
        Assert.Equal("", HighlightText.Status(true, 0, false, true, 10, false)); // No "Looking for chests…" flicker.
        Assert.Equal("No chest within 10 m takes this.", HighlightText.Status(true, 0, false, false, 10, false));
        Assert.Equal("No chest within 10 m takes these items.", HighlightText.Status(true, 0, false, false, 10.4f, true));
        Assert.Equal("Lit up 1 chest", HighlightText.Status(true, 1, true, true, 10, false)); // Shown as soon as one is found.
        Assert.Equal("Lit up 3 chests", HighlightText.Status(true, 3, true, false, 10, true));
        Assert.Equal("Found 3 chests", HighlightText.Status(true, 3, false, false, 10, false)); // Colouring unavailable.
    }

    [Fact]
    public void HighlightTooltip_ListsTheChests_Bounded()
    {
        Assert.Null(HighlightText.Tooltip("Copper", Array.Empty<string>()));
        var tip = HighlightText.Tooltip("Copper", new[] { "Chest 6 m", "Chest 8 m" })!.Value;
        Assert.Equal(("Chests that take Copper", "Chest 6 m\nChest 8 m"), tip);
        var many = HighlightText.Tooltip("", Enumerable.Range(1, 15).Select(x => "Chest " + x + " m").ToList())!.Value;
        Assert.Equal("Chests that take it", many.Topic);
        Assert.EndsWith("Chest 12 m\n… and 3 more", many.Text);
    }

    [Fact]
    public void PriorityHeading_NamesTheLevel_OnceTheChestHasARule()
    {
        Assert.Equal("This chest", ChestPriority.Heading(0, false));
        Assert.Equal("This chest · Normal", ChestPriority.Heading(0, true));
        Assert.Equal("This chest · Urgent", ChestPriority.Heading(ChestPriority.Rank(2), true));
        Assert.Equal("Priority 5: Normal (this chest)", ChestPriority.Tooltip(5, 5, true).Topic);
        Assert.Equal("Priority 4: Normal", ChestPriority.Tooltip(4, 5, true).Topic);
        Assert.Contains("Lower numbers fill first", ChestPriority.Tooltip(1, 5, true).Text);
        Assert.Contains("Set a rule first", ChestPriority.Tooltip(5, 5, false).Text);
    }

    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal)
    { ["materials"] = 120, ["materials.ore"] = 9, ["materials.metal"] = 8 };
    private static readonly SearchableItem[] Items =
    {
        new("CopperOre", "Copper ore", new[] { "materials", "materials.ore" }),
        new("CopperScrap", "Copper scrap", new[] { "materials", "materials.ore" }),
        new("CopperChunk", "Copper chunk", new[] { "materials", "materials.ore" }),
        new("Copper", "Copper", new[] { "materials", "materials.metal" }),
        new("Tin", "Tin", new[] { "materials", "materials.metal" }),
    };

    // D101 (C9, Q17): the found items' names go in their smallest category's row, at most 2 and "…".
    [Fact]
    public void SearchNotes_NameTheFoundItems_InTheirCategorysRow()
    {
        var result = CategorySearch.Find(Taxonomy, "copper", Items, x => Counts.TryGetValue(x, out var n) ? n : 0);
        Assert.Equal(new[] { "Copper ore", "Copper chunk", "Copper scrap" }, result.Names("materials.ore"));
        Assert.Equal(new[] { "Copper" }, result.Names("materials.metal"));
        Assert.Empty(result.Names("materials")); // Only the smallest category names an item.
        var names = result.Names("materials.ore");
        Assert.Equal("Copper ore, Copper chunk, …", CategorySearch.Note(names, 2));
        Assert.Equal("Copper ore, Copper chunk, …", CategorySearch.Note(names, 5)); // Never more than two.
        Assert.Equal("Copper ore, …", CategorySearch.Note(names, 1));
        Assert.Equal("…", CategorySearch.Note(names, 0));
        Assert.Equal("Copper", CategorySearch.Note(result.Names("materials.metal"), 2));
        Assert.Equal("", CategorySearch.Note(Array.Empty<string>(), 2));
    }

    // D101 (Q16, B3.20): a category row lights chests through its members, the found ones first, bounded.
    [Fact]
    public void CategoryMembers_FoundItemsFirst_Bounded()
    {
        Assert.Equal(new[] { "CopperScrap", "CopperChunk", "CopperOre" },
            CategorySearch.Members(Items, "materials.ore", new[] { "CopperScrap", "Copper" }, 64));
        Assert.Equal(new[] { "Copper", "CopperChunk" }, CategorySearch.Members(Items, "materials", new[] { "Copper" }, 2));
        Assert.Empty(CategorySearch.Members(Items, "food", Array.Empty<string>(), 64));
    }
}
