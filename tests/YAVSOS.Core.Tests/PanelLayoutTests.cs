using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Messages;
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
        var chips = RuleChips.Build(Enumerable.Range(1, 30).Select(x => "Category " + x), null, new[] { "Copper" }, new string[0]);
        var (topic, text) = EnglishText.Of(RuleChips.More(chips, 5));
        Assert.Equal("Also in this rule", topic);
        Assert.StartsWith("Category 6\nCategory 7\n", text);
        Assert.EndsWith("Category 25\n… and 6 more", text); // 26 folded, 20 listed.
        Assert.Equal("Category 30\nAlways: Copper", EnglishText.Of(RuleChips.More(chips, 29)).Text);
        // The "+N more" chip: its count picks the plural form, and its tooltip is More's.
        var folded = RuleChips.Folded(chips, 5);
        Assert.Equal(Message.Of("yavsos_chip_folded").Count(26), folded.Said);
        Assert.Equal("+26 more", Phase6PanelTests.Words(folded));
        Assert.Equal(RuleChips.More(chips, 5), folded.Tip);
        Assert.Equal("+1 more", Phase6PanelTests.Words(RuleChips.Folded(1)));
        // A folded filters chip is listed by its words.
        var filtered = RuleChips.Build(new[] { "Food" }, Phase6PanelTests.Fox(), new string[0], new string[0]);
        Assert.Equal("only eaten by Fox", EnglishText.Of(RuleChips.More(filtered, 1)).Text);
    }

    [Fact]
    public void NoRule_FollowsWhatStashingDoesWithAChestWithoutARule()
    {
        Assert.Equal("No rule: stashing adds only what's here.", Phase6PanelTests.Words(RuleChips.NoRule(NoRuleStash.ExistingOnly)));
        Assert.Equal("No rule: stashing can put anything here.", Phase6PanelTests.Words(RuleChips.NoRule(NoRuleStash.Anything)));
        Assert.Equal(("AzuAutoStore's \"Must Have Existing Item To Pull\" setting is off, so it stashes anything into chests without a rule, after the chests whose rules accept the item.",
            "Clean up nearby can move items out of a chest without a rule into a chest whose rule accepts them."), EnglishText.Of(RuleChips.NoRule(NoRuleStash.Anything).Tip!));
        Assert.Equal("No rule: AzuAutoStore works as usual.", Phase6PanelTests.Words(RuleChips.NoRule(NoRuleStash.Unknown))); // Setting unreadable: no guess.
        Assert.All(new[] { NoRuleStash.ExistingOnly, NoRuleStash.Anything, NoRuleStash.Unknown }, x => Assert.Equal(ChipKind.Note, RuleChips.NoRule(x).Kind));
    }

    [Fact]
    public void Messages_StayInTheStrip_UntilTheNextAction_AndFlagTheRefusedControl()
    {
        var messages = new PanelMessages();
        var busy = Message.Of("yavsos_run_busy");
        Assert.Null(messages.Current);
        messages.Say(PanelControl.Cleanup, busy);
        Assert.Equal(busy, messages.Current);
        Assert.Equal("A stash or cleanup is already running.", EnglishText.Of(messages.Current!));
        Assert.True(messages.Flagged(PanelControl.Cleanup)); // D132: the refused control keeps an amber outline.
        Assert.False(messages.Flagged(PanelControl.Edit));
        Assert.Equal(MessageKind.Refusal, messages.Kind);
        var revision = messages.Revision;
        messages.Say(PanelControl.Cleanup, Message.Of("yavsos_run_busy"));
        Assert.Equal(revision, messages.Revision); // The same message again: nothing to redraw.
        messages.Say(PanelControl.Priority, Message.Of("yavsos_edit_unconfirmed"));
        Assert.False(messages.Flagged(PanelControl.Cleanup)); // One message at a time.
        Assert.True(messages.Flagged(PanelControl.Priority));
        messages.Action();
        Assert.Null(messages.Current);
        Assert.False(messages.Flagged(PanelControl.Priority)); // Until the next action.
        Assert.True(messages.Revision > revision);
        messages.Say(PanelControl.Cleanup, Core.Transfers.RunReport.ForCleanup().Summary(), MessageKind.Summary);
        Assert.Equal(MessageKind.Summary, messages.Kind);
        Assert.False(messages.Flagged(PanelControl.Cleanup)); // A summary refused nothing.
        Assert.Equal("Nothing to clean up.", EnglishText.Of(messages.Current!));
    }

    [Fact]
    public void HighlightStatus_CountsHoldersAndTakers_NothingWhileLooking()
    {
        string? Status(int holds, int takes, bool scanning, float range, int subjects, bool withTakes, bool active = true) =>
            HighlightText.Status(active, holds, takes, scanning, range, subjects, withTakes) is { } status ? EnglishText.Of(status) : null;
        Assert.Null(Status(3, 0, false, 10, 1, false, active: false));
        Assert.Null(Status(0, 0, true, 10, 1, true)); // No "Looking for chests…" flicker.
        Assert.Equal("No chest within 10 m holds this.", Status(0, 0, false, 10, 1, false));
        Assert.Equal("No chest within 10 m holds or accepts these.", Status(0, 0, false, 10.4f, 3, true));
        Assert.Equal("No chest within 11 m holds this.", Status(0, 0, false, 10.5f, 1, false)); // Whole meters, as "0" rounded.
        Assert.Equal("1 chest holds this.", Status(1, 0, true, 10, 1, false)); // Shown as soon as one is found.
        Assert.Equal("2 chests hold these.", Status(2, 5, false, 10, 2, false)); // D122: takers only with the toggle.
        Assert.Equal("2 chests hold this · 1 more accepts it.", Status(2, 1, false, 10, 1, true));
        Assert.Equal("No chest holds this · 3 chests accept it.", Status(0, 3, false, 10, 1, true));
        Assert.Equal("1 chest holds these · no other chest accepts them.", Status(1, 0, false, 10, 4, true));
    }

    // Inventory case 2: the verb follows the chest count and the pronoun the number of things searched, each a count of
    // its own message, so a language can inflect both.
    [Fact]
    public void HighlightStatus_CarriesEachCountInItsOwnMessage()
    {
        var status = HighlightText.Status(true, 2, 3, false, 10, 4, true)!;
        Assert.Equal("yavsos_search_status", status.Key);
        Assert.True(status.TryGet("parts", out var value));
        var parts = Assert.IsType<MessageList>(value);
        Assert.Equal(ListKind.Parts, parts.Kind);
        Assert.Equal(Message.Of("yavsos_search_status_holds").With("this", Message.Of("yavsos_search_this").Count(4)).Count(2), parts.Items[0]);
        Assert.Equal(Message.Of("yavsos_search_status_accepts_more").With("it", Message.Of("yavsos_search_it").Count(4)).Count(3), parts.Items[1]);
    }

    [Fact]
    public void HighlightTooltip_ListsTheChestsWithWhy_Bounded()
    {
        var chest = Message.Of("yavsos_search_tip_chest").With("chest", "Chest");
        Assert.Null(HighlightText.Tooltip(MessageList.Comma("Copper"), Array.Empty<Message>(), Array.Empty<Message>()));
        var tip = HighlightText.Tooltip(MessageList.Comma("Copper"), new[] { chest.With("meters", 6) }, new[] { Message.Of("yavsos_search_tip_this_chest") })!;
        Assert.Equal(("Chests lit for Copper", "Chest 6 m · holds\nThis chest · accepts"), EnglishText.Of(tip));
        var many = EnglishText.Of(HighlightText.Tooltip(null, Enumerable.Range(1, 15).Select(x => chest.With("meters", x)).ToList(), Array.Empty<Message>())!);
        Assert.Equal("Chests lit for it", many.Topic);
        Assert.EndsWith("Chest 12 m · holds\n… and 3 more", many.Text);
        Assert.DoesNotContain("Chest 13 m", many.Text);
    }

    // X54: a search of several items names them, not "these items".
    [Fact]
    public void HighlightNames_NameTheItems_ThenSayHowManyMore()
    {
        Assert.Null(HighlightText.Names(Array.Empty<string>()));
        Assert.Null(HighlightText.Names(new[] { "" }));
        Assert.Equal("Copper", EnglishText.Of(HighlightText.Names(new[] { "Copper", "Copper" })!)); // Look-alike copies share a name.
        Assert.Equal("Copper, Tin", EnglishText.Of(HighlightText.Names(new[] { "Copper", "Tin" })!));
        Assert.Equal("Copper, Tin +2 more", EnglishText.Of(HighlightText.Names(new[] { "Copper", "Tin", "Iron", "Silver" })!));
        Assert.Equal("Chests lit for Copper, Tin +2 more",
            EnglishText.Of(HighlightText.Tooltip(HighlightText.Names(new[] { "Copper", "Tin", "Iron", "Silver" }), new[] { Message.Of("yavsos_search_tip_this_chest") }, Array.Empty<Message>())!).Topic);
    }

    [Fact]
    public void HighlightLabel_SaysWhyAChestIsLit()
    {
        Assert.Equal("Holds Copper ×57", EnglishText.Of(HighlightText.Holds("Copper", 57, 0)));
        Assert.Equal("Holds Copper Ore ×105 +1 more", EnglishText.Of(HighlightText.Holds("Copper Ore", 105, 1)));
        Assert.Equal("Accepts Copper, Tin", EnglishText.Of(HighlightText.Accepts(HighlightText.Names(new[] { "Copper", "Tin" })))); // X54.
        Assert.Equal("Accepts Copper, Tin +1 more", EnglishText.Of(HighlightText.Accepts(HighlightText.Names(new[] { "Copper", "Tin", "Iron" }))));
        Assert.Equal("Accepts Ores", EnglishText.Of(HighlightText.Accepts(HighlightText.Names(new[] { "Ores" }))));
        Assert.Equal("Accepts it", EnglishText.Of(HighlightText.Accepts(null)));
    }

    [Fact]
    public void StaysLabel_SaysWhatStaysAfterCleanupAndWhy()
    {
        Assert.Equal("Bronze Sword stays here: no chest accepts it", EnglishText.Of(HighlightText.Stays("Bronze Sword", 0, false)));
        Assert.Equal("The chests that accept these are full, so they stay here: Copper Ore +1 more", EnglishText.Of(HighlightText.Stays("Copper Ore", 1, true)));
        Assert.Equal("Tin stays here: the chests that accept it are full", EnglishText.Of(HighlightText.Stays("Tin", 0, true)));
    }

    // B2-06: the highlight keeps why a chest is lit as data; the same key and values are the same reason, so a recheck
    // repaints only a change, and the label is formatted (in the player's language) only when it is drawn.
    [Fact]
    public void HighlightReasons_AreData_EqualWhenKeyAndValuesAre()
    {
        Assert.Equal(HighlightText.Holds("Copper", 57, 1), HighlightText.Holds("Copper", 57, 1));
        Assert.NotEqual(HighlightText.Holds("Copper", 57, 1), HighlightText.Holds("Copper", 58, 1));
        Assert.Equal(HighlightText.Accepts(HighlightText.Names(new[] { "Copper", "Tin" })), HighlightText.Accepts(HighlightText.Names(new[] { "Copper", "Tin" })));
        Assert.NotEqual(HighlightText.Accepts(HighlightText.Names(new[] { "Copper" })), HighlightText.Accepts(HighlightText.Names(new[] { "Tin" })));
        Assert.NotEqual(HighlightText.Stays("Tin", 0, true), HighlightText.Stays("Tin", 0, false));
    }

    [Fact]
    public void TakersButton_IsNamedAsTheWordListSays()
    {
        var on = EnglishText.Of(HighlightText.TakesToggle(true));
        Assert.Equal("Also show chests that accept it", on.Topic);
        Assert.Contains("the accepts color", on.Text);
        Assert.Contains("the holds color", on.Text);
        Assert.StartsWith("Off: ", EnglishText.Of(HighlightText.TakesToggle(false)).Text);
    }

    [Fact]
    public void PriorityHeading_NamesTheLevel_OnceTheChestHasARule()
    {
        Assert.Equal("This chest", EnglishText.Of(ChestPriority.Heading(0, false)));
        Assert.Equal("This chest · Normal", EnglishText.Of(ChestPriority.Heading(0, true)));
        Assert.Equal("This chest · Urgent", EnglishText.Of(ChestPriority.Heading(ChestPriority.Rank(2), true)));
        Assert.Equal("This chest · Fuhgeddaboudit", EnglishText.Of(ChestPriority.Heading(ChestPriority.Rank(10), true))); // The longest heading.
        Assert.Equal("Priority 5: Normal (this chest)", EnglishText.Of(ChestPriority.Tip(5, 5, true)).Topic);
        Assert.Equal("Priority 4: Normal", EnglishText.Of(ChestPriority.Tip(4, 5, true)).Topic);
        Assert.Equal("Priority 5: Normal", EnglishText.Of(ChestPriority.Tip(5, 5, false)).Topic); // No rule: no level is this chest's.
        Assert.Equal("Lower numbers fill first. At the same priority, the most specific rule fills first.", EnglishText.Of(ChestPriority.Tip(1, 5, true)).Text);
        Assert.EndsWith("\nPriority is part of the rule, so set a rule first.", EnglishText.Of(ChestPriority.Tip(5, 5, false)).Text);
    }

    // P10-04: the priority inside a chest label, as a message for the label's builder; the English bridge says the same.
    [Fact]
    public void PriorityPhrase_NamesEveryLevelButTheDefault()
    {
        Assert.Null(ChestPriority.Phrase(0));
        Assert.Equal(new[] { "priority 1 (Critical)", "priority 2 (Urgent)", "priority 3 (Urgent)", "priority 4 (Normal)", "priority 6 (Moderate)",
                "priority 7 (Moderate)", "priority 8 (Minor)", "priority 9 (Minor)", "priority 10 (Fuhgeddaboudit)" },
            Enumerable.Range(ChestPriority.Highest, ChestPriority.Lowest).Where(x => x != ChestPriority.Default)
                .Select(x => EnglishText.Of(ChestPriority.Phrase(ChestPriority.Rank(x))!)).ToArray());
        Assert.Equal("priority 10 (Fuhgeddaboudit)", EnglishText.Of(ChestPriority.Phrase(-5)!));
        Assert.Equal("priority 2 (Urgent)", EnglishText.Of(ChestPriority.Phrase(ChestPriority.Rank(2))!)); // The guide's example.
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
        var result = CategorySearch.Find(EnglishText.Names(Taxonomy), "copper", Items, x => Counts.TryGetValue(x, out var n) ? n : 0);
        Assert.Equal(new[] { "Copper ore", "Copper chunk", "Copper scrap" }, result.Names("materials.ore"));
        Assert.Equal(new[] { "Copper" }, result.Names("materials.metal"));
        Assert.Empty(result.Names("materials")); // Only the smallest category names an item.
        var names = result.Names("materials.ore");
        Assert.Equal("Ores [Faint:· Copper ore, Copper chunk, …]", EnglishText.Marked(CategorySearch.Note("Ores", names, 2)!));
        Assert.Equal("Ores · Copper ore, Copper chunk, …", EnglishText.Of(CategorySearch.Note("Ores", names, 5)!)); // Never more than two.
        Assert.Equal("Ores · Copper ore, …", EnglishText.Of(CategorySearch.Note("Ores", names, 1)!));
        Assert.Equal("Ores · …", EnglishText.Of(CategorySearch.Note("Ores", names, 0)!));
        Assert.Equal("Metals · Copper", EnglishText.Of(CategorySearch.Note("Metals", result.Names("materials.metal"), 2)!));
        Assert.Null(CategorySearch.Note("Ores", Array.Empty<string>(), 2));
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
