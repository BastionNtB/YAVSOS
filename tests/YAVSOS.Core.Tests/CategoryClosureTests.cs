using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

// P4-00b (D051): umbrellas hold their subcategories, and the subcategories (Other included) cover their umbrella.
public sealed class CategoryClosureTests
{
    private static readonly ClassificationTaxonomy Default = DefaultClassificationTaxonomy.Create();
    private static MembershipEvidence Member(string category) => new MembershipEvidence(category, "test", "fixture");
    private static string[] Close(ClassificationTaxonomy taxonomy, params string[] categories) =>
        CategoryClosure.Apply(categories.Select(Member).ToArray(), taxonomy).Select(x => x.Category).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Both D051 properties for one closed membership set.</summary>
    private static void AssertComplete(ClassificationTaxonomy taxonomy, IReadOnlyCollection<string> closed)
    {
        foreach (var category in closed)
        {
            var parent = taxonomy.ParentOf(category);
            if (parent != null) Assert.Contains(parent, closed);
            if (taxonomy.OtherOf(category) != null) Assert.Contains(taxonomy.ChildrenOf(category), closed.Contains);
        }
    }

    [Theory]
    [InlineData("equipment.armor.head", "equipment,equipment.armor,equipment.armor.head")]
    [InlineData("equipment.armor", "equipment,equipment.armor,equipment.armor.other")]
    [InlineData("materials", "materials,materials.other")]
    [InlineData("brewing.bases", "brewing.bases,ingredients")]
    [InlineData("fishing.bait", "fishing.bait")]
    [InlineData("misc", "misc")]
    [InlineData("magic.epicloot", "magic,magic.epicloot,magic.epicloot.other")]
    [InlineData("weapons,equipment.tools", "equipment,equipment.tools,weapons,weapons.other")]
    [InlineData("weapons.swords,weapons.other", "weapons,weapons.other,weapons.swords")]
    public void Apply_AddsAncestorsAndOtherForUmbrellasWithoutANamedChild(string input, string expected)
    {
        var closed = Close(Default, input.Split(','));
        Assert.Equal(expected.Split(',').OrderBy(x => x, StringComparer.Ordinal), closed);
        AssertComplete(Default, closed);
    }

    [Fact]
    public void Apply_RandomSetsSatisfyBothPropertiesAndAreIdempotent()
    {
        var ids = Default.Categories.Keys.ToArray(); var random = new Random(51);
        for (var trial = 0; trial < 500; trial++)
        {
            var input = Enumerable.Range(0, random.Next(1, 6)).Select(_ => ids[random.Next(ids.Length)]).Distinct().ToArray();
            var closed = Close(Default, input);
            Assert.All(input, x => Assert.Contains(x, closed));
            AssertComplete(Default, closed);
            Assert.Equal(closed, Close(Default, closed));
        }
    }

    [Fact]
    public void Apply_PolicyCategoriesAndAdoptedOthersFollowTheSameRules()
    {
        var policy = Default.WithPolicy(PolicyWith(("materials.gems", "Gems"), ("materials.gems.cut", "Cut gems")));
        Assert.Equal(new[] { "materials", "materials.gems", "materials.gems.other" }, Close(policy, "materials.gems"));
        Assert.Equal(new[] { "materials", "materials.gems", "materials.gems.cut" }, Close(policy, "materials.gems.cut"));
        var taxonomy = new ClassificationTaxonomy(new[] { new ClassificationCategory("misc", "Misc", 999), new ClassificationCategory("a", "A", 1),
            new ClassificationCategory("a.b", "B", 1), new ClassificationCategory("a.other", "Named other", 1), new ClassificationCategory("a.other.x", "X", 1) });
        // An adopted Other with its own children receives its own Other in turn.
        Assert.Equal(new[] { "a", "a.other", "a.other.other" }, Close(taxonomy, "a"));
        AssertComplete(taxonomy, Close(taxonomy, "a"));
    }

    [Fact]
    public void Apply_ReturnsInputRowsUnchangedAndRejectsDerivedOrUnknownInput()
    {
        var rows = new[] { Member("misc") };
        Assert.Same(rows, CategoryClosure.Apply(rows, Default));
        var closed = CategoryClosure.Apply(new[] { Member("materials.wood") }, Default);
        Assert.Equal("test", closed[0].Source);
        Assert.Equal(CategoryClosure.Source, Assert.Single(closed.Skip(1)).Source);
        Assert.Throws<ArgumentException>(() => CategoryClosure.Apply(new[] { new MembershipEvidence("materials", CategoryClosure.Source, "forged") }, Default));
        Assert.Throws<ArgumentException>(() => CategoryClosure.Apply(new[] { Member("not.a.category") }, Default));
    }

    [Fact]
    public void Build_ConcreteItemsCloseWithoutChangingTheirDisplayCategory()
    {
        var index = Build(new[] {
            Item("Gloves", "Hands"), Item("Pick", "TwoHandedWeapon", "Pickaxes"), Item("Siege", "Ammo", ammo: "$ammo_siege"),
            Item("Bait", "AmmoNonEquipable", ammo: "$item_fishingbait"), Item("Wood"), Item("Helm", "Helmet"), Item("Unknown", "Misc") });
        Assert.Equal(new[] { "equipment", "equipment.armor", "equipment.armor.other" }, AllCategories(index.Items["Gloves"]).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "equipment", "equipment.tools", "weapons", "weapons.other" }, AllCategories(index.Items["Pick"]).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "ammunition", "ammunition.other" }, AllCategories(index.Items["Siege"]).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "fishing.bait" }, AllCategories(index.Items["Bait"]));
        Assert.Equal(new[] { "materials", "materials.wood" }, AllCategories(index.Items["Wood"]).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "misc" }, AllCategories(index.Items["Unknown"]));
        foreach (var item in index.Items.Values)
        {
            AssertComplete(index.Taxonomy, AllCategories(item));
            Assert.DoesNotContain(item.Primary, item.Memberships.Where(CategoryClosure.IsDerived).Select(x => x.Category));
        }
        Assert.Equal("equipment.armor.head", index.Items["Helm"].Primary);
    }

    [Fact]
    public void Rule_NamingAnUmbrellaAcceptsItsSubcategoryMembersAndOldChildRulesStillWork()
    {
        var index = Build(new[] { Item("Copper"), Item("Wood"), Item("Helm", "Helmet") },
            new ClassificationRuleSet(Default, new[] { new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal" }) }));
        Assert.True(ClassificationFilters.Matches(index.Items["Copper"], Rule(new[] { "materials" })));
        Assert.True(ClassificationFilters.Matches(index.Items["Wood"], Rule(new[] { "materials" })));
        Assert.False(ClassificationFilters.Matches(index.Items["Helm"], Rule(new[] { "materials" })));
        Assert.True(ClassificationFilters.Matches(index.Items["Helm"], Rule(new[] { "equipment" })));
        Assert.True(ClassificationFilters.Matches(index.Items["Copper"], Rule(new[] { "materials.metal" })));
        Assert.False(ClassificationFilters.Matches(index.Items["Wood"], Rule(new[] { "materials.metal" })));
    }

    [Fact]
    public void Build_ReplacementWithAChildOnlyStillReceivesTheUmbrella()
    {
        var rules = new ClassificationRuleSet(Default, new[] { new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.metal" }) });
        var item = Build(new[] { Item("Wood") }, rules).Items["Wood"];
        Assert.Equal("materials.metal", item.Primary);
        Assert.Equal(new[] { "materials", "materials.metal" }, AllCategories(item).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Wire_CarriesNoDerivedRowsRebuildsThemAndRoundTripsDeterministically()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("Gloves", "Hands"), Item("Wood"), Item("Pick", "TwoHandedWeapon", "Pickaxes") });
        var policy = EmptyPolicy();
        var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy)), policy);
        var wire = AuthoritativeAssignmentsCodec.ToWire(snapshot);
        Assert.DoesNotContain(CategoryClosure.Source, Decoded(wire));
        Assert.Contains(snapshot.Items["Gloves"].Memberships, CategoryClosure.IsDerived);
        var decoded = AuthoritativeAssignmentsCodec.ParseWire(wire, policy);
        Assert.Equal(snapshot.Fingerprint, decoded.Fingerprint);
        Assert.Equal(wire, AuthoritativeAssignmentsCodec.ToWire(decoded));
        foreach (var pair in snapshot.Items)
            Assert.Equal(pair.Value.Memberships.Select(x => (x.Category, x.Source, x.Reason)), decoded.Items[pair.Key].Memberships.Select(x => (x.Category, x.Source, x.Reason)));
    }

    [Fact]
    public void Resolve_FallbackForAClientOnlyItemIsClosedToo()
    {
        var policy = EmptyPolicy();
        var server = CatalogSnapshot.Build(new[] { Item("Wood") });
        var snapshot = AuthoritativeAssignmentSnapshot.Create(server, ClassificationPipeline.Build(server, ClassificationRuleSet.FromSchema1(policy)), policy);
        var local = CatalogSnapshot.Build(new[] { Item("Wood"), Item("ClientOnly") });
        Assert.Equal(new[] { "misc" }, AllCategories(snapshot.Resolve("ClientOnly", local)!));
        Assert.Equal(new[] { "materials", "materials.wood" }, AllCategories(snapshot.Resolve("Wood", local)!).OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string Decoded(string wire)
    {
        using var input = new MemoryStream(Convert.FromBase64String(wire.Substring("yas3:".Length)));
        using var inflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream(); inflate.CopyTo(output);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
    private static CategoryPolicy EmptyPolicy()
    {
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out var error), error); return policy!;
    }
    private static CategoryPolicy PolicyWith(params (string Id, string Name)[] categories)
    {
        var rows = string.Concat(categories.Select(x => "<category id=\"" + x.Id + "\" displayName=\"" + x.Name + "\"/>"));
        var xml = CategoryPolicyCodec.EmptyDocument.Replace("<categories/>", "<categories>" + rows + "</categories>");
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var error), error); return policy!;
    }
}
