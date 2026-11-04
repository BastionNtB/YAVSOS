using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>X55 (D130 variant A): the result panel lists each chest or drawer that received items, within its line bound.</summary>
public sealed class ResultPlacesTests
{
    private static string[] Lines(ResultSummary detail) => detail.Lines.Select(x => EnglishText.Of(x.Text)).ToArray();

    [Fact]
    public void Moved_CountsPerDestination_StacksSplitAcrossChestsCountAtEach()
    {
        var report = RunReport.ForStash();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        report.Moved(30, item: "Copper", stack: a, destination: "metal");
        report.Moved(10, item: "Copper", stack: a, destination: "spare"); // The rest of stack a went to a second chest.
        report.Moved(20, item: "Tin", stack: b, destination: "metal");
        report.Moved(5, item: "Tin", stack: b, destination: "metal");     // The same stack again: one stack at that chest.
        Assert.Equal((2, 65, 2), (report.MovedStacks, report.MovedUnits, report.Places));
        var detail = report.Detail(placeName: id => ResultPlace.Of(ChestName.Away(id == "metal" ? "Reinforced chest" : "Chest", 4)));
        Assert.Equal(new[]
        {
            "Stashed 2 stacks (65 items) in 2 places:",
            "Reinforced chest 4 m away: Copper ×30, Tin ×25",
            "Chest 4 m away: Copper ×10"
        }, Lines(detail));
        Assert.Equal(new[] { ResultKind.Moved, ResultKind.Place, ResultKind.Place }, detail.Lines.Select(x => x.Kind));
        Assert.Equal((2, 1), (detail.Lines[1].Count, detail.Lines[2].Count));
        Assert.Equal(new[] { new ResultItem("Copper", 30), new ResultItem("Tin", 25) }, detail.Lines[1].Items);
        // The moved line keeps the summary's words as its label; only its text names the places.
        Assert.Equal("Stashed 2 stacks (65 items)", EnglishText.Of(detail.Lines[0].Label));
        Assert.Equal("Stashed 2 stacks (65 items).", EnglishText.Of(detail.Headline));
        Assert.Equal(0, detail.MoreLines);
    }

    // The D130 check: AzuAutoStore's stash key into 23 item drawers and 3 chests, with leftovers and an unconfirmed store.
    [Fact]
    public void StoreKey_23DrawersAnd3Chests_FitTheBound_AndEndWithMorePlaces()
    {
        var watch = new StoreKeyWatch();
        var counts = new Dictionary<(string, string), int>();
        var places = new Dictionary<string, ResultPlace>();
        void Store(string place, string item, int units)
        {
            counts.TryGetValue((place, item), out var before);
            watch.Stored(place, item, units, before, viaMuc: false, Guid.NewGuid(), now: 0);
            counts[(place, item)] = before + units;
        }
        places["metal"] = ResultPlace.Of(ChestName.Away("Reinforced chest", 6), "Ores & metals · priority 1 (Critical)");
        places["wood"] = ResultPlace.Of(ChestName.Away("Chest", 3), "Wood");
        places["food"] = ResultPlace.Of(ChestName.Away("Chest", 12), "Food");
        for (var i = 0; i < 23; i++) places["drawer" + i] = ResultPlace.Of(ChestName.Away("Item Drawer", 2 + i % 9));
        string[] drawerItems = { "Copper ore", "Tin ore", "Iron scrap", "Silver ore", "Obsidian", "Flint", "Stone", "Resin", "Leather scraps",
            "Deer hide", "Troll hide", "Bone fragments", "Feathers", "Greydwarf eye", "Ancient seed", "Thistle", "Dandelion", "Mushroom",
            "Raspberries", "Blueberries", "Coal", "Surtling core", "Grey mushroom" };
        Store("drawer0", drawerItems[0], 30); Store("drawer1", drawerItems[1], 22); Store("metal", "Copper", 60); Store("wood", "Wood", 50);
        Store("drawer2", drawerItems[2], 18); Store("metal", "Tin", 24); Store("metal", "Bronze", 18); Store("metal", "Iron", 12);
        Store("metal", "Silver", 6); Store("wood", "Fine wood", 20); Store("food", "Cooked meat", 6); Store("food", "Queens jam", 4);
        for (var i = 3; i < 23; i++) Store("drawer" + i, drawerItems[i], 5 + i);
        // One store can't be read (a drawer whose count is unreadable): unconfirmed at once, never retried.
        watch.Stored("drawer9", "Honey", 8, null, viaMuc: false, Guid.NewGuid(), now: 0);
        Assert.True(watch.Observe(0.25, (place, item) => counts.TryGetValue((place, item), out var n) ? n : 0));
        // The watch records nothing as left; a stash's leftovers share these lines, so the check adds them here.
        var report = watch.Report;
        report.Remain(RunReason.NoDestination, "SwordBronze"); report.Remain(RunReason.NoDestination, "ArmorTrollLeatherChest");

        var detail = report.Detail(p => p switch { "SwordBronze" => "Bronze sword", "ArmorTrollLeatherChest" => "Troll leather tunic", _ => p },
            placeName: id => places[id]);
        Assert.Equal(26, report.Places);
        var lines = Lines(detail);
        Assert.Equal(new[]
        {
            "Stashed 32 stacks (620 items) in 26 places:",
            "Item Drawer 2 m away: Copper ore ×30",
            "Item Drawer 3 m away: Tin ore ×22",
            "Reinforced chest 6 m away · Ores & metals · priority 1 (Critical): Copper ×60, Tin ×24, Bronze ×18, Iron ×12 +1 more",
            "Chest 3 m away · Wood: Wood ×50, Fine wood ×20",
            "+22 more places",
            "2 stay in your inventory (no chest accepts it): Bronze sword, Troll leather tunic",
            "1 unconfirmed (not retried): Honey"
        }, lines);
        Assert.Equal(8, detail.Lines.Count); // The panel's default bound: leftovers and the unconfirmed line keep their places.
        Assert.Equal(0, detail.MoreLines);
        Assert.Equal(ResultKind.MorePlaces, detail.Lines[5].Kind); Assert.Equal(22, detail.Lines[5].Count);
        Assert.Equal(new[] { ResultKind.Remaining, ResultKind.Unconfirmed }, detail.Lines.Skip(6).Select(x => x.Kind));
        Assert.All(lines, x => Assert.True(x.Length <= ResultSummary.MaximumText, x.Length + ": " + x));
        Assert.Equal("Stashed 32 stacks (620 items) · 2 stay in your inventory (no chest accepts it) · 1 unconfirmed (not retried).",
            EnglishText.Of(detail.Headline));
    }

    [Fact]
    public void OneDestination_OnePlaceLine()
    {
        var report = RunReport.ForStoreKey();
        report.Moved(40, item: "Copper", stack: Guid.NewGuid(), destination: "metal");
        var detail = report.Detail(placeName: _ => ResultPlace.Of(ChestName.Away("Reinforced chest", 5), "Metals"));
        Assert.Equal(new[] { "Stashed 1 stack (40 items) in 1 place:", "Reinforced chest 5 m away · Metals: Copper ×40" }, Lines(detail));
    }

    [Fact]
    public void PlaceNames_OpenChest_GoneChest_AndCleanupWords()
    {
        var report = RunReport.ForCleanup();
        report.Moved(3, item: "Wood", stack: Guid.NewGuid(), destination: "open");
        report.Moved(1, item: "Resin", stack: Guid.NewGuid(), destination: "gone");
        report.Moved(2, viaMuc: true, item: "Stone", stack: Guid.NewGuid(), destination: "unnamed");
        var detail = report.Detail(placeName: id => id switch
        {
            "open" => ResultPlace.Of(ChestName.Open, "Wood"),
            "unnamed" => ResultPlace.Of(ChestName.Away("", 7)),
            _ => null
        });
        Assert.Equal(new[]
        {
            "Moved 3 stacks (6 items) to 3 places, 1 via MultiUserChest:",
            "This chest · Wood: Wood ×3",
            "A chest nearby: Resin",
            "A chest nearby: Stone ×2"
        }, Lines(detail));
    }

    [Fact]
    public void MorePlaces_SingularAndPlural()
    {
        var plural = RunReport.ForStash();
        for (var i = 0; i < 5; i++) plural.Moved(1, item: "Item" + i, destination: "chest" + i);
        var cut = plural.Detail(maximumLines: 3, placeName: id => ResultPlace.Of(ChestName.Away("Chest", 1)));
        Assert.Equal(new[] { "Stashed 5 stacks (5 items) in 5 places:", "Chest 1 m away: Item0", "+4 more places" }, Lines(cut));
        // Exactly as many places as lines: all shown, no "+N more places".
        var exact = plural.Detail(maximumLines: 6, placeName: _ => ResultPlace.Of(ChestName.Away("Chest", 1)));
        Assert.Equal(6, exact.Lines.Count); Assert.DoesNotContain(exact.Lines, x => x.Kind == ResultKind.MorePlaces);

        // Past the places a report keeps, the rest only count: "+1 more place".
        var many = RunReport.ForStash();
        for (var i = 0; i <= RunReport.MaximumPlaces; i++) many.Moved(1, item: "Wood", destination: "chest" + i);
        Assert.Equal(RunReport.MaximumPlaces + 1, many.Places);
        var all = many.Detail(maximumLines: 100, placeName: _ => ResultPlace.Of(ChestName.Away("Chest", 1)));
        Assert.Equal($"Stashed {RunReport.MaximumPlaces + 1} stacks ({RunReport.MaximumPlaces + 1} items) in {RunReport.MaximumPlaces + 1} places:", Lines(all)[0]);
        Assert.Equal("+1 more place", Lines(all).Last());
        Assert.Equal(RunReport.MaximumPlaces + 2, all.Lines.Count);
    }

    [Fact]
    public void WithoutPlaces_TheMovedLineListsItemsAsBefore()
    {
        static RunReport Placed()
        {
            var report = RunReport.ForStash();
            report.Moved(40, item: "Copper", destination: "metal"); report.Moved(20, item: "Wood", destination: "wood");
            return report;
        }
        const string before = "Stashed 2 stacks (60 items): Copper ×40, Wood ×20";
        // No namer (an older caller, or no player): today's line.
        Assert.Equal(before, Assert.Single(Lines(Placed().Detail())));
        // A move without a destination: place lines wouldn't add up, so today's line.
        var partly = Placed(); partly.Moved(5, item: "Stone");
        Assert.Equal("Stashed 3 stacks (65 items): Copper ×40, Wood ×20, Stone ×5", Lines(partly.Detail(placeName: _ => ResultPlace.Gone))[0]);
        // No room for a place line and "+N more places" beside the other lines: today's lines and bound.
        var full = Placed();
        foreach (var reason in new[] { RunReason.NoCapacity, RunReason.Held, RunReason.Budget }) full.Remain(reason, "X");
        var tight = full.Detail(maximumLines: 4, placeName: _ => ResultPlace.Gone);
        Assert.Equal(before, Lines(tight)[0]);
        Assert.Equal((4, 0), (tight.Lines.Count, tight.MoreLines));
        Assert.DoesNotContain(tight.Lines, x => x.Kind is ResultKind.Place or ResultKind.MorePlaces);
        // Room for exactly one place, when there is only one: shown.
        var single = RunReport.ForStash(); single.Moved(4, item: "Copper", destination: "metal"); single.Remain(RunReason.Held, "X");
        Assert.Equal(new[] { "Stashed 1 stack (4 items) in 1 place:", "A chest nearby: Copper ×4", "1 stays in your inventory (held): X" },
            Lines(single.Detail(maximumLines: 3, placeName: _ => null)));
    }

    [Fact]
    public void PlaceLine_ItemsAreBoundedAndCountedInStacks()
    {
        var report = RunReport.ForCleanup();
        for (var i = 0; i < 6; i++) report.Moved(10, item: "Item" + i, stack: Guid.NewGuid(), destination: "chest");
        var line = report.Detail(placeName: _ => ResultPlace.Of(ChestName.Away("Chest", 2))).Lines[1];
        Assert.Equal((6, 4, 2), (line.Count, line.Items.Count, line.More));
        Assert.Equal("Chest 2 m away: Item0 ×10, Item1 ×10, Item2 ×10, Item3 ×10 +2 more", EnglishText.Of(line.Text));
    }
}
