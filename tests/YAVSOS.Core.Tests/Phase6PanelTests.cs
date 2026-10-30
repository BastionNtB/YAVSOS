using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

/// <summary>P6-24 (D071): the redesigned panel's status marks, rule chips and priority bar bands.</summary>
public sealed class Phase6PanelTests
{
    [Fact]
    public void Marks_OnlyForInstalledMods_AndBrokenWinsOverWorking()
    {
        Assert.Null(PanelMarks.Azu(null, true, true, true, true, true));
        Assert.Null(PanelMarks.EpicLoot(false, false));
        Assert.Null(PanelMarks.Drawers(false, false, false));
        Assert.Null(PanelMarks.Cleanup(true, true)); // YAVSOS's own and working: no mark.
        Assert.Equal(MarkState.Limited, PanelMarks.Categories(false, 0, true).State); // Loading: amber (D101), no banner.

        var azu = PanelMarks.Azu("3.1.7", true, true, true, true, true)!;
        Assert.Equal(("AzuAutoStore 3.1.7", MarkState.Working), (azu.Name, azu.State));
        var partial = PanelMarks.Azu("3.1.7", true, false, true, true, true)!;
        Assert.Equal(MarkState.Broken, partial.State);
        Assert.Contains("On: chest rules apply to its store key and pickup", partial.Tooltip);
        Assert.Contains("Not working: chest order: its store key keeps its own order", partial.Tooltip);
        var changed = PanelMarks.Azu("", false, false, false, false, false)!;
        Assert.Equal(("AzuAutoStore", MarkState.Broken), (changed.Name, changed.State));
        Assert.Single(changed.Lines);

        var epic = PanelMarks.EpicLoot(true, false)!;
        Assert.Equal(MarkState.Broken, epic.State);
        Assert.Equal("Not working: rarity filter: EpicLoot changed in a way YAVSOS does not recognise. Everything else works.", epic.Tooltip);
        Assert.Equal(MarkState.Working, PanelMarks.Drawers(true, true, true)!.State);
        Assert.Contains("need the AzuAutoStore integration", PanelMarks.Drawers(true, false, false)!.Tooltip);
    }

    // P4-00f (D056): AzuAutoStore's config keeping some of the rule's items out of this chest type is a caution on its
    // mark (maintainer, 2026-10-24), outranking a setting but not a broken part.
    [Fact]
    public void Marks_KeptOutItems_AreACautionOnTheAzuMark()
    {
        Assert.Equal(MarkState.Working, PanelMarks.Azu("3.1.7", true, true, true, true, true, "").State);
        var kept = PanelMarks.Azu("3.1.7", true, true, true, true, true, "its config keeps 3 of this rule's items out of this chest type")!;
        Assert.Equal(MarkState.Caution, kept.State);
        Assert.EndsWith("This chest: its config keeps 3 of this rule's items out of this chest type", kept.Tooltip);
        Assert.Equal(MarkState.Broken, PanelMarks.Azu("3.1.7", true, false, true, true, true, "kept out")!.State);
        Assert.Equal(MarkState.Caution, new IntegrationMark("X", new[] { new MarkLine("a", MarkState.Off), new MarkLine("b", MarkState.Caution) }).State);
    }

    // D086 D2, M7, D088: exclusions or the pause flag read only in part are amber, not broken.
    [Fact]
    public void Marks_AzuExclusionsReadInPart_AreLimited()
    {
        var limited = PanelMarks.Azu("3.1.7", true, true, true, true, true, null, "clean up moves items only into chest types with no exclusions in its config.")!;
        Assert.Equal(MarkState.Limited, limited.State); Assert.Contains("Partly working: clean up moves items only", limited.Tooltip);
        var off = PanelMarks.Azu("3.1.7", true, true, true, false, true)!;
        Assert.Equal(MarkState.Limited, off.State); Assert.Contains("clean up is off", off.Tooltip);
        Assert.Equal(MarkState.Limited, PanelMarks.Azu("3.1.7", true, true, true, true, false)!.State);
        Assert.Equal(MarkState.Caution, PanelMarks.Azu("3.1.7", true, true, true, true, false, "kept out")!.State);
    }

    // D086 B5.36, D087: categories that gave up say so. D101: loading is an amber mark in place of the panel's banner.
    [Fact]
    public void Marks_CategoriesThatGaveUp_AreBroken_LoadingIsAmber()
    {
        var loading = PanelMarks.Categories(false, 0, false);
        Assert.Equal(MarkState.Limited, loading.State); Assert.Contains("Exact items (Always, Never) already work", loading.Tooltip);
        var gave = PanelMarks.Categories(false, 0, false, gaveUp: true)!;
        Assert.Equal(MarkState.Broken, gave.State); Assert.Contains("Exact items (Always, Never) still work", gave.Tooltip);
        // B4.26: with the previous answers kept (D086 D7) a stage that gave up is amber, not hidden.
        var kept = PanelMarks.Categories(true, 5, false, gaveUp: true)!;
        Assert.Equal(MarkState.Limited, kept.State); Assert.Contains("categories from before the latest mod or settings change", kept.Tooltip);
        Assert.Equal(MarkState.Working, PanelMarks.Categories(true, 5, false)!.State);
        // B4.9: a rejected server policy says so instead of "still loading".
        var rejected = PanelMarks.Categories(false, 0, false, serverRejected: true);
        Assert.Equal(MarkState.Broken, rejected.State); Assert.Contains("server's category settings could not be read", rejected.Tooltip);
    }

    // D086 D1: a failed optional part turns only itself off, shown amber ("partly working"), below a broken part.
    [Fact]
    public void Marks_AFailedOptionalPart_IsLimitedNotBroken()
    {
        var limited = new IntegrationMark("X", new[] { new MarkLine("a", MarkState.Working), new MarkLine("crafted items: b", MarkState.Limited) });
        Assert.Equal(MarkState.Limited, limited.State);
        Assert.Contains("Partly working: crafted items:", limited.Tooltip);
        Assert.Equal(MarkState.Working, PanelMarks.EpicLoot(true, true)!.State);
        Assert.Equal(MarkState.Broken, new IntegrationMark("X", new[] { new MarkLine("a", MarkState.Broken), new MarkLine("b", MarkState.Limited) }).State);
        Assert.Equal(MarkState.Caution, new IntegrationMark("X", new[] { new MarkLine("a", MarkState.Limited), new MarkLine("b", MarkState.Caution) }).State);
        Assert.Equal(MarkState.Limited, new IntegrationMark("X", new[] { new MarkLine("a", MarkState.Off), new MarkLine("b", MarkState.Limited) }).State);
    }

    // D071: a setting that turns something off gets the grey dash and names the setting.
    [Fact]
    public void Marks_TurnedOffBySetting_NameTheSetting()
    {
        var stash = PanelMarks.Stashing(false, true, "L", "Shift + L");
        Assert.Equal(MarkState.Off, stash.State);
        Assert.Equal("Off: stashing is turned off by the server setting Server.AllowStashing", stash.Tooltip);
        Assert.Equal("On: L stores nearby, Shift + L on a stack stores it", PanelMarks.Stashing(true, true, "L", "Shift + L").Tooltip);
        Assert.Equal(MarkState.Broken, PanelMarks.Stashing(true, false, "L", "Shift + L").State); // A broken reader outranks the setting.
        var cleanup = PanelMarks.Cleanup(false, true)!;
        Assert.Equal(MarkState.Off, cleanup.State);
        Assert.Contains("Server.AllowCleanup", cleanup.Tooltip);
        Assert.Equal(MarkState.Broken, PanelMarks.Cleanup(false, false)!.State);
        // B4.22: cleanup refused while Azu's exclusions can't be read shows on the Clean up mark itself.
        var azuOff = PanelMarks.Cleanup(true, true, azuUnreadable: true)!;
        Assert.Equal(MarkState.Broken, azuOff.State); Assert.Contains("clean up is off", azuOff.Tooltip);

        var categories = PanelMarks.Categories(true, 2311, true)!;
        Assert.Equal("On: ready: 2,311 items sorted\nOn: the server's category settings apply", categories.Tooltip);
        Assert.Contains("this game's own", PanelMarks.Categories(true, 5, false)!.Tooltip);
        Assert.Equal(MarkState.Working, PanelMarks.MultiUserChest("AzuAutoStore's MultiUserChest", true).State);
        Assert.Equal(MarkState.Broken, PanelMarks.MultiUserChest("", false).State);
        Assert.Null(PanelMarks.Server(false)); // D074 Q4: only a server without YAVSOS gets a mark.
        var server = PanelMarks.Server(true)!;
        Assert.Equal(("Server", MarkState.Working), (server.Name, server.State));
        Assert.Equal("On: this server has no YAVSOS, so your own categories and settings apply", server.Tooltip);
    }

    [Fact]
    public void Marks_ChestsUnbound_BreakStashingAndCleanup()
    {
        // SF2 (D087): without the game's chest members no chest can be read; that outranks the reader and the settings.
        var stash = PanelMarks.Stashing(false, false, "L", "Shift + L", chests: false);
        Assert.Equal(MarkState.Broken, stash.State);
        Assert.Equal("Not working: stashing is off: YAVSOS can't read chests in this game version (see the log)", stash.Tooltip);
        var cleanup = PanelMarks.Cleanup(true, true, chests: false)!;
        Assert.Equal(("Clean up", MarkState.Broken), (cleanup.Name, cleanup.State));
        Assert.Equal("Not working: clean up is off: YAVSOS can't read chests in this game version (see the log)", cleanup.Tooltip);
        Assert.Equal(MarkState.Working, PanelMarks.Stashing(true, true, "L", "Shift + L", chests: true).State);
        Assert.Null(PanelMarks.Cleanup(true, true, chests: true));
    }

    [Fact]
    public void Marks_Display_NamesThePartsTurnedOff()
    {
        // SF4 (D087): no mark while every display part works.
        Assert.Null(PanelMarks.Display(Array.Empty<string>()));
        var one = PanelMarks.Display(new[] { "the panel's search" })!;
        Assert.Equal(("Display", MarkState.Limited), (one.Name, one.State));
        Assert.Equal("Partly working: the panel's search is off after an error (see the log); a game restart turns it back on. " +
            "Chest rules, stashing and clean up still work.", one.Tooltip);
        // In the order they failed, each once.
        var three = PanelMarks.Display(new[] { "chest hover labels", "the panel's search", "chest hover labels", "categories in item tooltips" })!;
        Assert.StartsWith("Partly working: chest hover labels, the panel's search and categories in item tooltips are off after an error", three.Tooltip);
        Assert.Contains("turns them back on", three.Tooltip);
        Assert.Single(three.Lines);
        // A new part changes the key, so the panel redraws the marks.
        Assert.NotEqual(PanelMarks.Key(PanelMarks.Shown(one)), PanelMarks.Key(PanelMarks.Shown(three)));
    }

    [Fact]
    public void Marks_ShownInOrder_AndKeyChangesWithAnyState()
    {
        var shown = PanelMarks.Shown(PanelMarks.Azu(null, true, true, true, true, true), PanelMarks.Stashing(true, true, "L", "Shift + L"),
            PanelMarks.Cleanup(true, true), PanelMarks.MultiUserChest("YAVSOS's MultiUserChest", true));
        Assert.Equal(new[] { "Stashing", "MultiUserChest" }, shown.Select(x => x.Name));
        var off = PanelMarks.Shown(PanelMarks.Stashing(false, true, "L", "Shift + L"), PanelMarks.MultiUserChest("YAVSOS's MultiUserChest", true));
        Assert.NotEqual(PanelMarks.Key(shown), PanelMarks.Key(off));
        Assert.Equal(PanelMarks.Key(off), PanelMarks.Key(PanelMarks.Shown(PanelMarks.Stashing(false, true, "L", "Shift + L"), PanelMarks.MultiUserChest("YAVSOS's MultiUserChest", true))));
    }

    [Fact]
    public void Chips_CategoriesFiltersThenItems()
    {
        var chips = RuleChips.Build(new[] { "Food" }, "eaten by Fox", new[] { "Wolf meat" }, new[] { "Raspberries" });
        Assert.Equal(new[] { (ChipKind.Category, "Food"), (ChipKind.Filter, "only eaten by Fox"), (ChipKind.Always, "Wolf meat"), (ChipKind.Never, "Raspberries") },
            chips.Select(x => (x.Kind, x.Text)));
        Assert.All(chips, x => Assert.NotEqual("", x.Detail)); // D101: every chip has a tooltip.
        Assert.StartsWith("Always: Wolf meat\n", chips[2].Detail);
        var many = RuleChips.Build(Enumerable.Range(1, 9).Select(x => "Item " + x), "", new string[0], new string[0], ignored: 2);
        Assert.Equal(10, many.Count); // No per-kind cap any more: the two-row fit folds the rest (D101).
        Assert.Equal((ChipKind.Note, "2 ignored categories"), (many.Last().Kind, many.Last().Text));
        Assert.Single(RuleChips.Build(new string[0], "", new[] { "Wolf cape", "Wolf cape", "Wolf cape" }, new string[0])); // Look-alike copies.
        var longName = RuleChips.Build(new[] { new string('x', 60) }, "", new string[0], new string[0]).Single();
        Assert.Equal(RuleChips.MaximumText, longName.Text.Length);
        Assert.EndsWith("…", longName.Text);
        Assert.StartsWith(new string('x', 60), longName.Detail); // The tooltip has the whole name.
    }

    // D101: what a rule of only filters, or only Never items, means is a chip in the chips area; a rule that takes
    // nothing is a caution, first, so the two-row limit never folds it away.
    [Fact]
    public void ChipNote_ExplainsRulesWithoutCategories()
    {
        Assert.Null(RuleChips.Note(1, 0, 0, false, 0, "AzuAutoStore"));
        Assert.Null(RuleChips.Note(0, 2, 0, false, 0, "AzuAutoStore"));
        Assert.Equal((ChipKind.Note, "Any item that matches the filters."), Pair(RuleChips.Note(0, 0, 0, true, 0, "AzuAutoStore")!));
        Assert.Equal("Any item that matches the filters, and the Always items.", RuleChips.Note(0, 1, 0, true, 0, "AzuAutoStore")!.Text);
        Assert.Equal((ChipKind.Caution, "Takes nothing: Never alone takes nothing."), Pair(RuleChips.Note(0, 0, 3, false, 0, "stashing")!));
        Assert.Contains("(stashing included)", RuleChips.Note(0, 0, 3, false, 0, "stashing")!.Detail);
        Assert.Null(RuleChips.Note(0, 0, 0, false, 1, "stashing")); // Ignored categories: the chip says so.
        var neverOnly = RuleChips.Build(new string[0], "", new string[0], new[] { "Bronze" }, stasher: "AzuAutoStore");
        Assert.Equal(new[] { ChipKind.Caution, ChipKind.Never }, neverOnly.Select(x => x.Kind));
        static (ChipKind, string) Pair(RuleChip chip) => (chip.Kind, chip.Text);
    }

    // D071: one colour band per D069 name along the 1–10 bar.
    [Fact]
    public void PriorityBands_FollowTheNames()
    {
        for (var level = ChestPriority.Highest; level <= ChestPriority.Lowest; level++)
            for (var other = ChestPriority.Highest; other <= ChestPriority.Lowest; other++)
                Assert.Equal(ChestPriority.Name(level) == ChestPriority.Name(other), ChestPriority.Band(level) == ChestPriority.Band(other));
        Assert.Equal(0, ChestPriority.Band(1)); Assert.Equal(5, ChestPriority.Band(10));
    }
}
