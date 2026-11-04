using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

/// <summary>P10-04 (S13): built-in category names are messages; admin-set names stay as written.</summary>
public sealed class CategoryNamesTests
{
    private static readonly ClassificationTaxonomy Default = DefaultClassificationTaxonomy.Create();

    // The X52 wording pass changed these English names; the code keeps the source name (fingerprint, logs).
    private static readonly Dictionary<string, string> Reworded = new(StringComparer.Ordinal) {
        ["materials.moulds"] = "Molds",
        // The mod's name as the word list writes it (EpicLoot), not "Epic Loot".
        ["magic.epicloot"] = "EpicLoot", ["magic.epicloot.dust"] = "EpicLoot · Dust", ["magic.epicloot.shards"] = "EpicLoot · Shards",
        ["magic.epicloot.reagents"] = "EpicLoot · Reagents", ["magic.epicloot.stones"] = "EpicLoot · Runestones",
        ["magic.epicloot.essence"] = "EpicLoot · Essence", ["magic.epicloot.socket-materials"] = "EpicLoot · Socket materials",
        ["magic.epicloot.unidentified"] = "EpicLoot · Unidentified gear", ["magic.epicloot.socket-tools"] = "EpicLoot · Socket tools"
    };

    private static ClassificationTaxonomy WithPolicy(params (string Id, string Name)[] categories)
    {
        var rows = string.Concat(categories.Select(x => "<category id=\"" + x.Id + "\" displayName=\"" + x.Name + "\"/>"));
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodecTests.Wrap(rows, ""), out var policy, out var error), error);
        return Default.WithPolicy(policy!);
    }

    [Fact]
    public void EveryBuiltInCategory_HasAKeyedEnglishName()
    {
        var named = Default.Categories.Values.Where(x => !x.Generated).ToArray();
        Assert.Equal(85, named.Length);
        var names = EnglishText.Names(Default);
        foreach (var row in named)
        {
            Assert.NotNull(row.NameKey);
            Assert.True(EnglishText.File.ContainsKey(row.NameKey!), row.NameKey);
            Assert.Equal(Message.Of(row.NameKey!), CategoryNames.Of(Default, row.Id));
            Assert.Equal(Reworded.TryGetValue(row.Id, out var english) ? english : row.Name, names.Shown(row.Id));
        }
        Assert.Equal(named.Length, named.Select(x => x.NameKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryShownName_FitsAChip()
    {
        // A chip is cut at 40 characters (D101); no built-in or derived Other name should need the cut in English.
        var names = EnglishText.Names(Default);
        foreach (var id in Default.Categories.Keys) Assert.True(names.Shown(id).Length <= 40, id + ": " + names.Shown(id));
    }

    [Fact]
    public void OtherSubcategory_IsOneMessageNamingItsUmbrella()
    {
        var names = EnglishText.Names(Default);
        Assert.Equal("weapons.other", Default.OtherOf("weapons"));
        Assert.Equal(Message.Of("yavsos_category_other_name").With("umbrella", Message.Of("yavsos_category_weapons_name")),
            CategoryNames.Of(Default, "weapons.other"));
        Assert.Equal("Weapons · Other", names.Shown("weapons.other"));
        // Under an umbrella an admin renamed, the umbrella's name is shown as written.
        var renamed = WithPolicy(("weapons", "Arms"));
        Assert.Equal("Arms · Other", EnglishText.Names(renamed).Shown("weapons.other"));
        var longName = new string('L', 121);
        Assert.Equal("Other", EnglishText.Names(WithPolicy(("weapons", longName))).Shown("weapons.other"));
    }

    [Fact]
    public void AdminSetNames_StayAsWritten_AndUnknownIdsShowTheirId()
    {
        var taxonomy = WithPolicy(("materials.metal", "Metalle"), ("materials.gems", "Gems"));
        Assert.Null(taxonomy.Categories["materials.metal"].NameKey);
        Assert.Equal("Metalle", CategoryNames.Of(taxonomy, "materials.metal"));
        Assert.Equal("Gems", EnglishText.Names(taxonomy).Shown("materials.gems"));
        Assert.Equal(Message.Of("yavsos_category_materials_ore_name"), CategoryNames.Of(taxonomy, "materials.ore"));
        Assert.Equal("retired.id", EnglishText.Names(taxonomy).Shown("retired.id"));
        // The source names and the fingerprint don't depend on the keys.
        Assert.Equal(Default.Fingerprint, new ClassificationTaxonomy(Default.Categories.Values).Fingerprint);
    }

    [Fact]
    public void BuiltInIdsListedWithTheirBuiltInNames_StayMessages()
    {
        // POLICY.md: an assignment's categories must be listed, built-in IDs included; their names stay translatable.
        var taxonomy = WithPolicy(("materials.metal", "Metals"), ("weapons.other", "Weapons · Other"));
        Assert.Equal(Message.Of("yavsos_category_materials_metal_name"), CategoryNames.Of(taxonomy, "materials.metal"));
        Assert.Same(Default.Categories["materials.metal"], taxonomy.Categories["materials.metal"]);
        Assert.Equal(Message.Of("yavsos_category_other_name").With("umbrella", Message.Of("yavsos_category_weapons_name")),
            CategoryNames.Of(taxonomy, "weapons.other"));
        Assert.Equal("Weapons · Other", EnglishText.Names(taxonomy).Shown("weapons.other"));
        // Another name for the listed Other is the admin's and stays as written.
        Assert.Equal("Other arms", CategoryNames.Of(WithPolicy(("weapons.other", "Other arms")), "weapons.other"));
    }

    [Fact]
    public void SearchTreeAndLabel_UseTheNameThePlayerSees()
    {
        // A translated name, as another language's file would give it.
        var names = new CategoryNames(Default, m => m.Key == "yavsos_category_materials_metal_name" ? "Metalle" : EnglishText.Of(m));
        var items = new[] { new SearchableItem("Copper", "Copper", new[] { "materials", "materials.metal" }) };
        Assert.Equal(new[] { "materials.metal" }, CategorySearch.Find(names, "metalle", items, _ => 1).Categories);
        Assert.DoesNotContain("materials.metal", CategorySearch.Find(names, "metals", items, _ => 1).Categories);
        var rows = CategorySelection.Rows(names, new[] { "materials.metal" }, _ => true, new HashSet<string>(), new HashSet<string>());
        Assert.Equal("Metalle", rows.Single(x => x.Id == "materials.metal").Name);
        var rules = new ChestRules(Default, ChestRuleState.Configured, new[] { new ClassificationFilterRule(Default, new[] { "materials.metal" }) });
        Assert.Equal("Metalle", EnglishText.Of(RuleText.Label(rules, names, x => x, EnglishText.Of)!));
    }
}
