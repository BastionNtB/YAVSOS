using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;

namespace YAVSOS.Core.Tests;

/// <summary>The declared rarity list is process-wide, so tests that change it run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EpicLootRarityDeclarationCollection { public const string Name = "EpicLoot rarity declaration"; }

/// <summary>#104 (D171): the rarity filter's values are the loaded EpicLoot's own <c>ItemRarity</c> declaration.</summary>
[Collection(EpicLootRarityDeclarationCollection.Name)]
public sealed class EpicLootRarityDeclarationTests
{
    private static readonly OptionalCapability Supported = new("epicloot", OptionalCapabilityState.Supported, "0.16.0", "test probe");
    private const string Key = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
    private static IReadOnlyDictionary<string, string> Data(string payload) =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { [Key] = payload });
    private static KeyValuePair<int, string>[] Known(params (int, string)[] extra) =>
        OptionalEpicLootRarity.KnownRarityNames.Select((name, value) => new KeyValuePair<int, string>(value, name))
            .Concat(extra.Select(x => new KeyValuePair<int, string>(x.Item1, x.Item2))).ToArray();
    private static void Restore() => OptionalEpicLootRarity.Declare(Known());

    private static ClassificationResult Sword()
    {
        var catalog = CatalogSnapshot.Build(new[] { ClassificationContractFixture.Item("MagicSword", type: "OneHandedWeapon") });
        return ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(),
            LoadedSourceReceipt.Build(catalog, OptionalEpicLootRarity.MaterialFacts(catalog, Supported)).Snapshot).Index.Items["MagicSword"];
    }

    [Fact]
    public void ANewRarity_IsOfferedReadAndMatched()
    {
        try
        {
            OptionalEpicLootRarity.Declare(Known((6, "Celestial")));
            Assert.Equal(new[] { "magic", "rare", "epic", "legendary", "mythic", "ancient", "celestial" }, ClassificationFilters.Rarities);
            Assert.Equal(ClassificationFilters.Rarities, ClassificationFilters.Values("rarity"));
            // Stored as its number (Newtonsoft's default), at any data version.
            var row = Assert.Single(OptionalEpicLootRarity.ReadInstanceForFilters(Supported, Sword(), Data("{\"Version\":4,\"Rarity\":6}")));
            Assert.Equal(("rarity", "celestial"), (row.Dimension, row.Value));
            Assert.True(ClassificationFilters.IsKnownRuleValue("rarity", "celestial"));
            // Display: without a message key, the lower-cased enum name.
            Assert.Equal("celestial", RuleText.ValueName("rarity", "celestial"));
        }
        finally { Restore(); }
    }

    [Fact]
    public void ARarityNoLongerDeclared_StaysInTheRuleAndMatchesNothing()
    {
        try
        {
            OptionalEpicLootRarity.Declare(Known().Where(x => x.Value != "Mythic"));
            Assert.DoesNotContain("mythic", ClassificationFilters.Rarities);
            // A saved rule naming it still reads (D086 D6) and is shown as stale until the player unticks it (D065).
            Assert.False(ClassificationFilters.IsKnownRuleValue("rarity", "mythic"));
            var rule = new ClassificationFilterRule(DefaultClassificationTaxonomy.Create(), Array.Empty<string>(),
                new[] { new KeyValuePair<string, IEnumerable<string>>("rarity", new[] { "mythic", "epic" }) });
            Assert.Equal(new[] { "epic", "mythic" }, rule.Filters["rarity"].OrderBy(x => x, StringComparer.Ordinal));
            var choice = FilterChoices.Compute(new[] { Sword() }, Array.Empty<string>(), (_, _) => true,
                new Dictionary<string, IReadOnlyList<string>> { ["rarity"] = new[] { "mythic", "epic" } }).Single(x => x.Dimension == "rarity");
            Assert.Contains("mythic", choice.Stale);
            Assert.DoesNotContain("epic", choice.Stale);
            // A stored 4 is now undeclared: read as "could not be read", which no rarity filter accepts.
            Assert.Equal(ClassificationFilters.Unreadable,
                Assert.Single(OptionalEpicLootRarity.ReadInstanceForFilters(Supported, Sword(), Data("{\"Rarity\":4}"))).Value);
        }
        finally { Restore(); }
    }

    [Fact]
    public void TheProbeReadsTheDeclaration_InValueOrder()
    {
        try
        {
            var declared = OptionalEpicLootApi.Rarities(typeof(SevenRarities));
            Assert.Equal(new[] { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient", "Celestial" }, declared.OrderBy(x => x.Key).Select(x => x.Value));
            OptionalEpicLootRarity.Declare(declared);
            Assert.Equal("celestial", ClassificationFilters.Rarities.Last());
        }
        finally { Restore(); }
    }

    public enum SevenRarities { Magic, Rare, Epic, Legendary, Mythic, Ancient, Celestial }

    [Fact]
    public void AnUnusableDeclaration_IsRefused_AndKeepsTheCurrentOne()
    {
        var before = ClassificationFilters.Rarities.ToArray();
        foreach (var bad in new[]
        {
            Array.Empty<KeyValuePair<int, string>>(),
            new[] { new KeyValuePair<int, string>(0, "Magic"), new KeyValuePair<int, string>(0, "Rare") },   // a value twice
            new[] { new KeyValuePair<int, string>(0, "Magic"), new KeyValuePair<int, string>(1, "MAGIC") },  // a name twice, ignoring case
            new[] { new KeyValuePair<int, string>(0, "Ma gic") },
            new[] { new KeyValuePair<int, string>(0, "") },
            Enumerable.Range(0, OptionalEpicLootRarity.MaximumRarities + 1).Select(i => new KeyValuePair<int, string>(i, "R" + i)).ToArray()
        })
            Assert.Throws<ArgumentException>(() => OptionalEpicLootRarity.Declare(bad));
        Assert.Equal(before, ClassificationFilters.Rarities);
    }
}
