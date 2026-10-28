using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

// P4-00a (D051): taxonomy structure only; item memberships are unchanged by these rows.
public sealed class TaxonomyHierarchyTests
{
    private static readonly ClassificationCategory Misc = new ClassificationCategory("misc", "Misc", 999);
    private static ClassificationTaxonomy Taxonomy(params ClassificationCategory[] rows) => new ClassificationTaxonomy(rows.Append(Misc));
    private static ClassificationCategory Row(string id, string? parent = null) => new ClassificationCategory(id, id, 100, parent);

    [Fact]
    public void Parent_IsExplicitElseNearestExistingPrefixElseRoot()
    {
        var taxonomy = Taxonomy(Row("a"), Row("a.b"), Row("a.b.c"), Row("a.x.y"), Row("q.r"), Row("bait", "a"));
        Assert.Equal("a.b", taxonomy.ParentOf("a.b.c"));
        Assert.Equal("a", taxonomy.ParentOf("a.x.y"));
        Assert.Null(taxonomy.ParentOf("q.r"));
        Assert.Equal("a", taxonomy.ParentOf("bait"));
        Assert.Null(taxonomy.ParentOf("a"));
        Assert.Null(taxonomy.ParentOf("unknown"));
        Assert.Equal(new[] { 0, 1, 2, 1, 0, -1 }, new[] { "a", "a.b", "a.b.c", "a.x.y", "q.r", "unknown" }.Select(taxonomy.DepthOf));
        Assert.True(taxonomy.IsAncestorOrSelf("a", "a.b.c"));
        Assert.True(taxonomy.IsAncestorOrSelf("a.b.c", "a.b.c"));
        Assert.False(taxonomy.IsAncestorOrSelf("a.b", "a.x.y"));
        Assert.False(taxonomy.IsAncestorOrSelf("a", "unknown"));
    }

    [Fact]
    public void Parent_MissingSelfOrCyclicRejects()
    {
        Assert.Throws<ArgumentException>(() => Taxonomy(Row("a", "missing")));
        Assert.Throws<ArgumentException>(() => Row("a", "a"));
        Assert.Throws<ArgumentException>(() => Taxonomy(Row("a", "b"), Row("b", "a")));
        Assert.Throws<ArgumentException>(() => Taxonomy(Row("a", "c"), Row("b", "a"), Row("c", "b")));
        Assert.Throws<ArgumentException>(() => Row("a", "Bad Parent"));
    }

    [Fact]
    public void Other_IsGeneratedPerUmbrellaAsALeafChild()
    {
        var taxonomy = Taxonomy(new ClassificationCategory("a", "Alpha", 10), Row("a.b"), Row("a.b.c"), Row("leaf"));
        Assert.Equal("a.other", taxonomy.OtherOf("a"));
        Assert.Equal("a.b.other", taxonomy.OtherOf("a.b"));
        Assert.Null(taxonomy.OtherOf("leaf"));
        Assert.Null(taxonomy.OtherOf("a.b.c"));
        var other = taxonomy.Categories["a.other"];
        Assert.True(other.Generated);
        Assert.Equal("Alpha · Other", other.Name);
        Assert.Equal(998, other.DisplayRank);
        Assert.Equal(new[] { "a.b", "a.other" }, taxonomy.ChildrenOf("a"));
        Assert.Empty(taxonomy.ChildrenOf("a.other"));
        Assert.Null(taxonomy.OtherOf("a.other"));
    }

    [Fact]
    public void Other_NamedRowUnderItsUmbrellaIsAdopted()
    {
        var taxonomy = Taxonomy(Row("a"), Row("a.b"), new ClassificationCategory("a.other", "Leftovers", 7));
        Assert.Equal("a.other", taxonomy.OtherOf("a"));
        Assert.False(taxonomy.Categories["a.other"].Generated);
        Assert.Equal("Leftovers", taxonomy.Categories["a.other"].Name);
        // A row with that ID placed elsewhere is not the umbrella's Other, and none is generated over it.
        var elsewhere = Taxonomy(Row("a"), Row("a.b"), Row("z"), Row("a.other", "z"));
        Assert.Null(elsewhere.OtherOf("a"));
        Assert.Equal("z", elsewhere.ParentOf("a.other"));
    }

    [Fact]
    public void Other_IsSkippedWhenItsIdWouldExceedTheLimit()
    {
        var umbrella = new string('u', 60);
        var taxonomy = Taxonomy(Row(umbrella), Row(umbrella + ".x"));
        Assert.Null(taxonomy.OtherOf(umbrella));
        Assert.Equal(new[] { umbrella + ".x" }, taxonomy.ChildrenOf(umbrella));
    }

    [Fact]
    public void GeneratedRows_AreRederivedSoRebuildingAndWithPolicyAreIdempotent()
    {
        var defaults = DefaultClassificationTaxonomy.Create();
        var rebuilt = new ClassificationTaxonomy(defaults.Categories.Values);
        Assert.Equal(defaults.Fingerprint, rebuilt.Fingerprint);
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodecTests.Wrap(
            "<category id=\"materials.gems\" displayName=\"Gems\"/><category id=\"fish.salted\" displayName=\"Salted fish\"/>" +
            "<category id=\"weapons.other\" displayName=\"Odd weapons\"/>", ""), out var policy, out var error), error);
        var changed = defaults.WithPolicy(policy!);
        Assert.Equal(changed.Fingerprint, changed.WithPolicy(policy!).Fingerprint);
        Assert.Equal("materials", changed.ParentOf("materials.gems"));
        Assert.Equal("fish.other", changed.OtherOf("fish"));
        Assert.Equal("weapons.other", changed.OtherOf("weapons"));
        Assert.False(changed.Categories["weapons.other"].Generated);
        Assert.Equal(998, changed.Categories["weapons.other"].DisplayRank);
        Assert.Equal("ingredients", changed.ParentOf("brewing.bases"));
    }

    [Fact]
    public void Fingerprint_CoversParentsButNotRowOrder()
    {
        var flat = Taxonomy(Row("a"), Row("b"));
        var nested = Taxonomy(Row("a"), Row("b", "a"));
        Assert.NotEqual(flat.Fingerprint, nested.Fingerprint);
        Assert.Equal(nested.Fingerprint, new ClassificationTaxonomy(new[] { Misc, Row("b", "a"), Row("a") }).Fingerprint);
    }

    [Fact]
    public void Bounds_LimitNamedRowsAndAllowTheirGeneratedOthers()
    {
        var umbrellas = Enumerable.Range(0, 255).SelectMany(i => new[] { Row("u" + i), Row("u" + i + ".x") }).Append(Row("extra")).ToArray();
        var taxonomy = Taxonomy(umbrellas);
        Assert.Equal(512 + 255, taxonomy.Categories.Count);
        Assert.Throws<ArgumentException>(() => Taxonomy(umbrellas.Append(Row("one-too-many")).ToArray()));
    }

    [Fact]
    public void DefaultTree_GivesEveryFamilyAnUmbrellaAndOther()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create();
        foreach (var family in new[] { "weapons", "equipment", "equipment.armor", "ammunition", "food", "materials",
            "ingredients", "animal", "special", "magic", "magic.epicloot", "magic.wizardry" })
            Assert.Equal(family + ".other", taxonomy.OtherOf(family));
        Assert.Equal("equipment.armor", taxonomy.ParentOf("equipment.armor.head"));
        Assert.Equal("equipment", taxonomy.ParentOf("equipment.armor"));
        Assert.Equal("magic.epicloot", taxonomy.ParentOf("magic.epicloot.dust"));
        Assert.Equal("magic", taxonomy.ParentOf("magic.materials"));
        Assert.Equal("ingredients", taxonomy.ParentOf("brewing.bases"));
        foreach (var root in new[] { "meads", "fish", "trophies", "valuables", "currency", "farming", "fishing.bait",
            "mod.monstrum", "catalog.customization", "misc" })
            Assert.Null(taxonomy.ParentOf(root));
        // Each umbrella ranks after its named children, so a child stays the preferred display category.
        foreach (var row in taxonomy.Categories.Values.Where(x => !x.Generated && taxonomy.ParentOf(x.Id) != null))
            Assert.True(taxonomy.Categories[taxonomy.ParentOf(row.Id)!].DisplayRank > row.DisplayRank, row.Id);
    }
}
