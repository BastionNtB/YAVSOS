using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>P6-18: the Items tab's icon grid.</summary>
public sealed class ItemGridTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static CatalogFact With(CatalogFact fact, params (string Key, string Value)[] extra) =>
        new(fact.Kind, fact.Identity, fact.Values.Concat(extra.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))));
    private static readonly ClassificationIndex Index = Build(new[]
        {
            With(Item("Copper"), ("teleportable", "False")), With(Item("Tin"), ("teleportable", "False")), With(Item("Iron"), ("teleportable", "False")),
            With(Item("Wood"), ("teleportable", "True")), Item("Bread"), Item("SwordBronze"), Item("RawMeat")
        },
        new ClassificationRuleSet(Taxonomy, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal" }),
            new ClassificationOverride("exact", "Tin", OverrideOperation.Replace, new[] { "materials.metal" }),
            new ClassificationOverride("exact", "Iron", OverrideOperation.Replace, new[] { "materials.metal" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "RawMeat", OverrideOperation.Replace, new[] { "food.stamina" })
        }));
    private static readonly Dictionary<string, string> Names = new()
        { ["Copper"] = "Copper ore", ["Tin"] = "Tin ore", ["Iron"] = "Iron", ["Wood"] = "Wood", ["Bread"] = "Bread", ["SwordBronze"] = "Bronze sword", ["RawMeat"] = "Boar meat" };
    private static IEnumerable<GridSource> Catalog() => Index.Items.Values.Select(x => new GridSource(x.Prefab, Names[x.Prefab], x));
    private static readonly Dictionary<string, IReadOnlyList<string>> None = new();
    private static IReadOnlyList<GridEntry> Grid(string[] categories, string[]? always = null, string[]? never = null, string[]? here = null,
        string? query = null, Dictionary<string, IReadOnlyList<string>>? filters = null) =>
        ItemGrid.Build(Catalog(), categories, filters ?? None, always ?? Array.Empty<string>(), never ?? Array.Empty<string>(), here ?? Array.Empty<string>(), query);
    private static string Show(IEnumerable<GridEntry> rows) =>
        string.Join(" ", rows.Select(x => $"{x.Section}:{x.Prefab}{(x.Mark == GridMark.Always ? "+" : x.Mark == GridMark.Never ? "-" : "")}{(x.Taken ? "" : "~")}"));

    [Fact]
    public void HereFirst_ThenMarked_ThenTheRest_ByName()
    {
        var rows = Grid(new[] { "materials" }, always: new[] { "RawMeat" }, never: new[] { "Tin" }, here: new[] { "Iron", "Bread" });
        // Bread is here but not taken (dimmed); Boar meat is Always without a category; Tin is a category item marked Never.
        Assert.Equal("Here:Bread~ Here:Iron Marked:RawMeat+ Rule:Copper Rule:Tin-~ Rule:Wood", Show(rows));
    }

    [Fact]
    public void AlwaysOnly_RuleShowsItsItems_AndEmptyRuleShowsNothingButHere()
    {
        // "Always Wolf meat here" with no category (maintainer, 2026-10-23).
        Assert.Equal("Marked:RawMeat+", Show(Grid(Array.Empty<string>(), always: new[] { "RawMeat" })));
        Assert.Equal("Here:Copper~", Show(Grid(Array.Empty<string>(), here: new[] { "Copper" })));
    }

    [Fact]
    public void Search_CoversTheWholeCatalog_AndKeepsSections()
    {
        var rows = Grid(new[] { "materials.metal" }, here: new[] { "Wood" }, query: "o");
        // Every name or prefab with "o" (not Bread): Wood is here, metals are the rule's, the rest are found (not taken).
        Assert.Equal("Here:Wood~ Rule:Copper Rule:Iron Rule:Tin Found:RawMeat~ Found:SwordBronze~", Show(rows));
        Assert.All(Grid(new[] { "materials.metal" }, query: "sword"), x => Assert.Equal(GridSection.Found, x.Section));
        Assert.Empty(Grid(new[] { "materials.metal" }, query: "zzz"));
    }

    [Fact]
    public void DefinitionFilters_NarrowTheGrid_InstanceFiltersAreNotApplied()
    {
        var portable = new Dictionary<string, IReadOnlyList<string>> { ["teleportable"] = new[] { "yes" } };
        Assert.Equal("Rule:Wood", Show(Grid(new[] { "materials" }, filters: portable)));
        var magic = new Dictionary<string, IReadOnlyList<string>> { ["rarity"] = new[] { "magic", "rare" } };
        // Without a reader saying the items can have a rarity, none of these can.
        Assert.Empty(Grid(new[] { "materials" }, filters: magic));
        var canHave = ItemGrid.Build(Catalog(), new[] { "weapons" }, magic, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            canHave: (item, dimension) => item.Prefab == "SwordBronze");
        Assert.Equal("Rule:SwordBronze", Show(canHave));
        // D110 (F11): "damaged = no" passes only items that can be damaged; ticking "unknown" also passes the ones that can't, as sorting does.
        bool Durable(ClassificationResult item, string dimension) => item.Prefab == "SwordBronze";
        var undamaged = new Dictionary<string, IReadOnlyList<string>> { ["damaged"] = new[] { "no" } };
        Assert.Equal("Rule:SwordBronze", Show(ItemGrid.Build(Catalog(), new[] { "weapons", "materials" }, undamaged, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), canHave: Durable)));
        var unknown = new Dictionary<string, IReadOnlyList<string>> { ["damaged"] = new[] { "unknown" } };
        Assert.Contains("Rule:Wood", Show(ItemGrid.Build(Catalog(), new[] { "weapons", "materials" }, unknown, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), canHave: Durable)));
    }

    [Fact]
    public void RemovedItems_StillShowSoTheirMarkCanBeCleared()
    {
        var rows = Grid(new[] { "materials.metal" }, always: new[] { "GoneModItem" }, never: new[] { "OtherGone" });
        Assert.Equal("Marked:GoneModItem+ Marked:OtherGone-~ Rule:Copper Rule:Iron Rule:Tin", Show(rows));
    }

    [Fact]
    public void Unlisted_ShowOnlyWhenHereOrMarked()
    {
        var item = Index.Items["Copper"];
        var catalog = new[] { new GridSource("Copper", "Copper ore", item), new GridSource("Wolf_Attack1", "Wolf attack", item, listed: false) };
        Assert.Equal("Rule:Copper", Show(ItemGrid.Build(catalog, new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())));
        Assert.Empty(ItemGrid.Build(catalog, Array.Empty<string>(), None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "attack"));
        Assert.Equal("Here:Wolf_Attack1", Show(ItemGrid.Build(catalog, new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(), new[] { "Wolf_Attack1" }, "attack")));
    }

    [Fact]
    public void Click_CyclesNoneAlwaysNever()
    {
        Assert.Equal(GridMark.Always, ItemGrid.Next(GridMark.None));
        Assert.Equal(GridMark.Never, ItemGrid.Next(GridMark.Always));
        Assert.Equal(GridMark.None, ItemGrid.Next(GridMark.Never));
        Assert.Contains("wins over chests that take it only by category", ItemGrid.MarkText(GridMark.Always, true));
        Assert.Contains("no ticked category", ItemGrid.MarkText(GridMark.Always, false));
    }

    [Fact]
    public void ProductionSizedCatalog_BuildsQuickly()
    {
        var item = Index.Items["Copper"];
        var many = Enumerable.Range(0, 4000).Select(i => new GridSource("Item" + i, "Item " + i, item)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rows = ItemGrid.Build(many, new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(), new[] { "Item7" });
        Assert.Equal(4000, rows.Count);
        Assert.Equal("Item7", rows[0].Prefab);
        Assert.True(clock.ElapsedMilliseconds < 500, clock.ElapsedMilliseconds + " ms");
    }

    // P6-24 (maintainer, 2026-10-23): the game's Fallen Warrior / Shadow Person NPC copies of a cape share its name and
    // icon; they are one icon, the rule still takes every copy, and a mark covers all of them.
    [Fact]
    public void LookAlikes_AreOneIcon_ShowingTheCraftedOneOrTheOneHere()
    {
        var copper = Index.Items["Copper"];
        GridSource[] Sources() => new[]
        {
            new GridSource("FW_Copper", "Copper ore", copper, look: "icon1"), new GridSource("SP_Copper", "Copper ore", copper, look: "icon1"),
            new GridSource("Copper", "Copper ore", copper, look: "icon1", preferred: true),
            new GridSource("CopperScrap", "Copper ore", copper, look: "icon2"), // Same name, another icon: its own entry.
            new GridSource("Tin", "Tin ore", Index.Items["Tin"])
        };
        IReadOnlyList<GridEntry> Build(string[] here, string[]? always = null) =>
            ItemGrid.Build(Sources(), new[] { "materials.metal" }, None, always ?? Array.Empty<string>(), Array.Empty<string>(), here);
        var plain = Build(Array.Empty<string>());
        Assert.Equal("Rule:Copper Rule:CopperScrap Rule:Tin", Show(plain));
        Assert.Equal(new[] { "Copper", "FW_Copper", "SP_Copper" }, plain[0].Copies);
        Assert.Single(plain[1].Copies);
        // A copy that is here is the one shown, in the Here section.
        var held = Build(new[] { "SP_Copper" });
        Assert.Equal(("SP_Copper", GridSection.Here), (held[0].Prefab, held[0].Section));
        Assert.Equal(3, held[0].Copies.Count);
        // One copy marked Always (an older rule): the icon shows the mark.
        Assert.Equal(GridMark.Always, Build(Array.Empty<string>(), new[] { "FW_Copper" })[0].Mark);
        // Without a look (no icon), nothing merges.
        Assert.Equal(3, ItemGrid.Build(new[] { new GridSource("A", "Same", copper), new GridSource("B", "Same", copper), new GridSource("C", "Same", copper) },
            new[] { "materials.metal" }, None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()).Count);
    }
}
