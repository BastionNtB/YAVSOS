using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>P6-14 (D065): filters and values follow the rule's categories; saved filters are never dropped.</summary>
public sealed class FilterChoicesTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None = new Dictionary<string, IReadOnlyList<string>>();

    private static CatalogFact With(CatalogFact fact, params (string Key, string Value)[] extra) =>
        new(fact.Kind, fact.Identity, fact.Values.Concat(extra.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))));

    private static ClassificationResult[] World() => Build(new[]
    {
        With(Item("Wood"), ("teleportable", "True")),
        With(Item("CopperOre"), ("teleportable", "False")),
        Item("SwordIron", "OneHandedWeapon"),
        Item("Bread", "Consumable", health: 10, stamina: 30, duration: 600),
        Item("Stew", "Consumable", health: 30, stamina: 10, duration: 600),
    }, evidence: new[] { new ItemClassificationEvidence("Bread", facets: new[] { new FacetEvidence("mod", "Therzie.Warfare", "loaded-registration", "Owner.") }) })
        .Items.Values.ToArray();

    // The game says: weapons have durability and quality; nothing else here does. EpicLoot present.
    private static bool Game(ClassificationResult item, string dimension) => dimension switch
    {
        "rarity" => true,
        "upgraded" or "damaged" or "crafter" => item.Prefab == "SwordIron",
        _ => false
    };

    private static FilterChoice? Of(IReadOnlyList<FilterChoice> choices, string dimension) => choices.SingleOrDefault(x => x.Dimension == dimension);

    [Fact]
    public void Materials_OfferNoEquipmentOrFoodFilters()
    {
        var choices = FilterChoices.Compute(World(), new[] { "materials" }, Game, None);
        Assert.Null(Of(choices, "rarity")); Assert.Null(Of(choices, "damaged")); Assert.Null(Of(choices, "upgraded"));
        Assert.Null(Of(choices, "crafter")); Assert.Null(Of(choices, "food-stat"));
        var portal = Of(choices, "teleportable")!;
        Assert.True(portal.Applies);
        Assert.Equal(new[] { "yes", "no" }, portal.Values); // every material has the flag: no "unknown"
    }

    [Fact]
    public void Weapons_OfferInstanceFilters_WithPlainAndNotCrafted()
    {
        var mine = new[] { ClassificationFilters.CrafterValue(true, 7, "Bastion"), ClassificationFilters.CrafterValue(false, 7, "Bastion") };
        var choices = FilterChoices.Compute(World(), new[] { "weapons" }, Game, None, mine);
        Assert.Equal(ClassificationFilters.Rarities.Concat(new[] { "unknown" }), Of(choices, "rarity")!.Values);
        Assert.Equal(mine.Concat(new[] { "unknown" }), Of(choices, "crafter")!.Values);
        // Another player's saved crafter choice is shown as theirs, not marked stale.
        var lins = ClassificationFilters.CrafterValue(true, 9, "Lin");
        var shared = FilterChoices.Compute(World(), new[] { "weapons" }, Game,
            new Dictionary<string, IReadOnlyList<string>> { ["crafter"] = new[] { lins } }, mine);
        Assert.Contains(lins, Of(shared, "crafter")!.Values); Assert.Empty(Of(shared, "crafter")!.Stale);
        Assert.Equal(new[] { "yes", "no" }, Of(choices, "damaged")!.Values);
        Assert.Null(Of(choices, "food-stat"));
    }

    [Fact]
    public void WithoutEpicLoot_RarityIsNeverOffered()
    {
        var choices = FilterChoices.Compute(World(), new[] { "weapons" }, (item, dimension) => dimension != "rarity" && Game(item, dimension), None);
        Assert.Null(Of(choices, "rarity"));
        Assert.NotNull(Of(choices, "damaged"));
    }

    [Fact]
    public void Food_OffersOnlyTheStatsPresent_AndModsSeen()
    {
        var choices = FilterChoices.Compute(World(), new[] { "food" }, Game, None);
        Assert.Equal(new[] { "health", "stamina" }, Of(choices, "food-stat")!.Values);
        Assert.Equal(new[] { "Therzie.Warfare", "unknown" }, Of(choices, "mod")!.Values); // Stew is vanilla
    }

    // D098: "unknown" for food's main stat or damaged would only mean "not food" / "can't be damaged"; not offered, even
    // when the categories mix in such items. A rule that already saved it keeps it, marked for unticking (D065).
    [Fact]
    public void FoodStatAndDamaged_NeverOfferUnknown_ButKeepASavedOne()
    {
        var all = FilterChoices.Compute(World(), Array.Empty<string>(), Game, None);
        Assert.DoesNotContain("unknown", Of(all, "food-stat")!.Values);
        Assert.DoesNotContain("unknown", Of(all, "damaged")!.Values);
        Assert.Contains("unknown", Of(all, "mod")!.Values); // other filters keep theirs
        var before = FilterChoices.Compute(null, new[] { "materials" }, Game, None);
        Assert.DoesNotContain("unknown", Of(before, "food-stat")!.Values);
        Assert.DoesNotContain("unknown", Of(before, "damaged")!.Values);
        var saved = new Dictionary<string, IReadOnlyList<string>> { ["food-stat"] = new[] { "unknown" }, ["damaged"] = new[] { "no", "unknown" } };
        var kept = FilterChoices.Compute(World(), Array.Empty<string>(), Game, saved);
        Assert.Equal(new[] { "unknown" }, Of(kept, "food-stat")!.Stale);
        Assert.Equal(new[] { "unknown" }, Of(kept, "damaged")!.Stale);
        Assert.Contains("unknown", Of(kept, "damaged")!.Values);
    }

    [Fact]
    public void NoCategories_CoverEveryItem_AndNoAnswersOfferEverything()
    {
        var all = FilterChoices.Compute(World(), Array.Empty<string>(), Game, None);
        Assert.NotNull(Of(all, "food-stat")); Assert.NotNull(Of(all, "rarity")); Assert.NotNull(Of(all, "teleportable"));
        var before = FilterChoices.Compute(null, new[] { "materials" }, Game, None);
        Assert.Equal(RuleText.EditableFilters.Count, before.Count);
        Assert.All(before, x => Assert.True(x.Applies));
    }

    [Fact]
    public void SavedFilters_ThatNoLongerApply_StayVisibleAndMarked()
    {
        var saved = new Dictionary<string, IReadOnlyList<string>>
        {
            ["rarity"] = new[] { "legendary" },          // Wood can't be magic
            ["food-stat"] = new[] { "eitr", "health" },  // no eitr food in scope
        };
        var wood = FilterChoices.Compute(World(), new[] { "materials" }, Game, saved);
        var rarity = Of(wood, "rarity")!;
        Assert.False(rarity.Applies);
        Assert.Equal(new[] { "legendary" }, rarity.Values); Assert.Equal(new[] { "legendary" }, rarity.Stale);
        var food = FilterChoices.Compute(World(), new[] { "food" }, Game, saved);
        var stat = Of(food, "food-stat")!;
        Assert.True(stat.Applies);
        Assert.Equal(new[] { "health", "stamina", "eitr" }, stat.Values); // closed order kept
        Assert.Equal(new[] { "eitr" }, stat.Stale);
    }

    [Fact]
    public void FilterWithOneValueOnly_IsNotOffered_ButASavedOneStaysMarked()
    {
        var choices = FilterChoices.Compute(World(), new[] { "materials.wood" }, Game, None);
        Assert.Null(Of(choices, "teleportable")); // every Wood can go through portals
        var saved = new Dictionary<string, IReadOnlyList<string>> { ["teleportable"] = new[] { "yes" } };
        var portal = Of(FilterChoices.Compute(World(), new[] { "materials.wood" }, Game, saved), "teleportable")!;
        Assert.False(portal.Applies);
        Assert.Equal(new[] { "yes" }, portal.Values);
    }

    [Fact]
    public void WorldThatTeleportsEverything_HidesTeleportable_AndTheTooltipFact()
    {
        var moot = new[] { "teleportable" };
        Assert.Null(Of(FilterChoices.Compute(World(), new[] { "materials" }, Game, None, moot: moot), "teleportable"));
        var saved = new Dictionary<string, IReadOnlyList<string>> { ["teleportable"] = new[] { "no" } };
        Assert.False(Of(FilterChoices.Compute(World(), new[] { "materials" }, Game, saved, moot: moot), "teleportable")!.Applies);
        var ore = World().Single(x => x.Prefab == "CopperOre");
        Assert.DoesNotContain("teleportable", RuleText.ItemLine(ore, DefaultClassificationTaxonomy.Create(), portals: false));
    }

    [Fact]
    public void UmbrellaCategory_CoversItsMembers()
    {
        var items = World();
        var umbrella = items.Single(x => x.Prefab == "SwordIron").Memberships.Select(x => x.Category).OrderBy(x => x.Length).First();
        var choices = FilterChoices.Compute(items, new[] { umbrella }, Game, None);
        Assert.NotNull(Of(choices, "damaged"));
    }

    [Fact]
    public void Fit_KeepsTheTail_AndCountsNamesThatDoNotFit()
    {
        var names = new[] { "Stamina foods", "Seeds & planting items", "Brewing ingredients", "Food ingredients", "Animal feed" };
        var line = RuleText.Fit(names, " · eaten by Boar, Razorback", RuleText.MaximumItemLine);
        Assert.True(line.Length <= RuleText.MaximumItemLine);
        Assert.EndsWith(" · eaten by Boar, Razorback", line);
        Assert.StartsWith("Stamina foods, ", line);
        Assert.Matches(@" \+\d+ · ", line);
        Assert.Equal("Wood · x", RuleText.Fit(new[] { "Wood" }, " · x", 10));
        Assert.Equal("A, B", RuleText.Fit(new[] { "A", "B" }, "", 80));
    }

    [Fact]
    public void Short_RarityAndEverythingAbove_ReadsOrBetter()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> F(params string[] values) =>
            new Dictionary<string, IReadOnlyList<string>> { ["rarity"] = values };
        Assert.Equal("magic or better", RuleText.Short(F(ClassificationFilters.Rarities.ToArray())));
        Assert.Equal("legendary or better", RuleText.Short(F("ancient", "mythic", "legendary")));
        Assert.Equal("magic or legendary", RuleText.Short(F("magic", "legendary")));
        Assert.Equal("ancient", RuleText.Short(F("ancient")));
        Assert.Equal("rare or better or plain", RuleText.Short(F("rare", "epic", "legendary", "mythic", "ancient", "unknown")));
    }

    [Fact]
    public void ItemLine_ShowsSmallestCategories_AndNotTeleportable()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create();
        var items = World();
        var ore = items.Single(x => x.Prefab == "CopperOre");
        var line = RuleText.ItemLine(ore, taxonomy);
        Assert.EndsWith(" · not teleportable", line);
        var names = line.Substring(0, line.IndexOf(" · ", StringComparison.Ordinal)).Split(", ");
        var ids = ore.Memberships.Select(x => x.Category).ToArray();
        foreach (var name in names)
        {
            var id = taxonomy.Categories.Single(x => x.Value.Name == name).Key;
            Assert.DoesNotContain(ids, other => other != id && taxonomy.IsAncestorOrSelf(id, other)); // a leaf, never an umbrella
        }
        Assert.DoesNotContain("teleportable", RuleText.ItemLine(items.Single(x => x.Prefab == "Wood"), taxonomy));
    }

    // P6-21 (maintainer, 2026-10-23): "Can be upgraded" is known per item type, so it offers yes and no from real answers;
    // "Upgrade level" only for items a recipe upgrades, without "unknown"; the Asksvin egg case (quality without recipe) is "no".
    [Fact]
    public void Upgrades_CanBeUpgradedFromRecipes_LevelsOnlyForUpgradableItems()
    {
        bool Recipes(ClassificationResult item, string dimension) => dimension is "upgradable" or "quality" ? item.Prefab == "SwordIron" : Game(item, dimension);
        var all = FilterChoices.Compute(World(), Array.Empty<string>(), Recipes, None);
        Assert.Equal(new[] { "yes", "no" }, Of(all, "upgradable")!.Values);
        Assert.Equal(ClassificationFilters.Qualities, Of(all, "quality")!.Values);
        var food = FilterChoices.Compute(World(), new[] { "food" }, Recipes, None);
        Assert.Null(Of(food, "upgradable")); // Only "no": narrows nothing.
        Assert.Null(Of(food, "quality"));
        var weapons = FilterChoices.Compute(World(), new[] { "weapons" }, Recipes, None);
        Assert.Equal(ClassificationFilters.Qualities, Of(weapons, "quality")!.Values);
    }
}
