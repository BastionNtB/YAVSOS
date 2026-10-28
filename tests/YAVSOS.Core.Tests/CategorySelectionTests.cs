using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

// P4-00c (D051/D054): the picker tree's selection logic.
public sealed class CategorySelectionTests
{
    private static readonly ClassificationTaxonomy Default = DefaultClassificationTaxonomy.Create();
    private static string[] Toggle(string id, params string[] stored) => CategorySelection.Toggle(Default, stored, id).ToArray();
    private static CategorySelectionState State(string id, params string[] stored) => CategorySelection.StateOf(Default, stored, id);

    [Fact]
    public void StateOf_DistinguishesStoredImpliedPartialAndOff()
    {
        Assert.Equal(CategorySelectionState.On, State("materials", "materials"));
        Assert.Equal(CategorySelectionState.Implied, State("materials.metal", "materials"));
        Assert.Equal(CategorySelectionState.Implied, State("equipment.armor.head", "equipment"));
        Assert.Equal(CategorySelectionState.Partial, State("equipment", "equipment.armor.head"));
        Assert.Equal(CategorySelectionState.Off, State("weapons", "equipment.armor.head"));
    }

    [Fact]
    public void Toggle_TickingANodeReplacesItsStoredDescendants()
    {
        Assert.Equal(new[] { "materials" }, Toggle("materials", "materials.metal", "materials.wood"));
        Assert.Equal(new[] { "food", "materials" }, Toggle("materials", "materials.metal", "food"));
        Assert.Equal(new[] { "materials.metal" }, Toggle("materials.metal"));
    }

    [Fact]
    public void Toggle_TickingEveryChildCollapsesIntoTheParentRecursively()
    {
        var food = Default.ChildrenOf("food");
        Assert.Equal(new[] { "food" }, Toggle(food.Last(), food.Take(food.Count - 1).ToArray()));
        var armor = Default.ChildrenOf("equipment.armor");
        var others = Default.ChildrenOf("equipment").Where(x => x != "equipment.armor");
        Assert.Equal(new[] { "equipment" }, Toggle(armor.Last(), armor.Take(armor.Count - 1).Concat(others).ToArray()));
    }

    [Fact]
    public void Toggle_UntickingAnImpliedNodeStoresTheRestOfTheStoredAncestor()
    {
        var result = Toggle("materials.metal", "materials");
        Assert.Equal(Default.ChildrenOf("materials").Where(x => x != "materials.metal"), result);
        var deep = Toggle("equipment.armor.head", "equipment", "food");
        Assert.DoesNotContain("equipment", deep); Assert.DoesNotContain("equipment.armor", deep); Assert.DoesNotContain("equipment.armor.head", deep);
        Assert.Contains("food", deep); Assert.Contains("equipment.shields", deep); Assert.Contains("equipment.armor.chest", deep); Assert.Contains("equipment.armor.other", deep);
        Assert.Equal(CategorySelectionState.Off, CategorySelection.StateOf(Default, deep, "equipment.armor.head"));
        Assert.Equal(CategorySelectionState.Partial, CategorySelection.StateOf(Default, deep, "equipment"));
        // Ticking it again restores the single umbrella.
        Assert.Equal(new[] { "equipment", "food" }, CategorySelection.Toggle(Default, deep, "equipment.armor.head").ToArray());
    }

    [Fact]
    public void Toggle_UntickingAStoredNodeRemovesOnlyItAndKeepsUnknownIds()
    {
        Assert.Equal(new[] { "food", "gone.category" }, Toggle("materials", "materials", "food", "gone.category"));
        Assert.Equal(new[] { "gone.category", "materials" }, Toggle("materials", "gone.category"));
        Assert.Throws<ArgumentException>(() => Toggle("gone.category"));
    }

    [Fact]
    public void Toggle_RandomSequencesStayNormalizedAndAcceptTheSameAsTheirSelection()
    {
        var ids = Default.Categories.Keys.ToArray(); var random = new Random(54);
        for (var trial = 0; trial < 200; trial++)
        {
            var stored = new SortedSet<string>(StringComparer.Ordinal);
            for (var step = 0; step < 12; step++)
            {
                var id = ids[random.Next(ids.Length)];
                var before = CategorySelection.StateOf(Default, stored, id);
                stored = CategorySelection.Toggle(Default, stored, id);
                var after = CategorySelection.StateOf(Default, stored, id);
                Assert.Equal(before is CategorySelectionState.On or CategorySelectionState.Implied, after is CategorySelectionState.Off or CategorySelectionState.Partial);
                Assert.Equal(stored, CategorySelection.Normalize(Default, stored));
                foreach (var row in stored) Assert.All(stored.Where(x => x != row), x => Assert.False(Default.IsAncestorOrSelf(row, x)));
            }
        }
    }

    [Fact]
    public void Rows_HideEmptyCategoriesUnlessNamedAndExpandPartialNodes()
    {
        var counts = CategorySelection.MemberCounts(Build(new[] { Item("Wood"), Item("Helm", "Helmet") }).Items.Values);
        Assert.Equal(1, counts["materials"]); Assert.Equal(1, counts["equipment"]); Assert.False(counts.ContainsKey("weapons"));
        bool Has(string id) => counts.ContainsKey(id);
        var none = new HashSet<string>();
        var rows = CategorySelection.Rows(Default, Array.Empty<string>(), Has, none, none);
        Assert.Equal(new[] { "equipment", "materials" }, rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(rows, x => Assert.False(x.Expanded));
        var named = CategorySelection.Rows(Default, new[] { "catalog.customization", "equipment.armor.head" }, Has, none, none);
        Assert.Contains(named, x => x.Id == "catalog.customization" && x.State == CategorySelectionState.On);
        Assert.Contains(named, x => x.Id == "equipment" && x.Expanded && x.State == CategorySelectionState.Partial);
        Assert.Contains(named, x => x.Id == "equipment.armor.head" && x.Depth == 2);
        var collapsed = CategorySelection.Rows(Default, new[] { "equipment.armor.head" }, Has, none, new HashSet<string> { "equipment" });
        Assert.DoesNotContain(collapsed, x => x.Depth > 0);
        var open = CategorySelection.Rows(Default, Array.Empty<string>(), Has, new HashSet<string> { "materials" }, none);
        Assert.Equal(new[] { "materials", "materials.wood" }, open.Where(x => x.Id.StartsWith("materials", StringComparison.Ordinal)).Select(x => x.Id));
    }

    // Maintainer, 2026-10-21: A–Z within each level and Misc last, not by DisplayRank (which picks primary categories).
    [Fact]
    public void Rows_AreAlphabeticalWithinEachLevelAndMiscLast()
    {
        var none = new HashSet<string>();
        var rows = CategorySelection.Rows(Default, Array.Empty<string>(), _ => true, new HashSet<string>(Default.Categories.Keys), none);
        foreach (var level in rows.GroupBy(x => Default.ParentOf(x.Id) ?? ""))
        {
            var names = level.Where(x => x.Id != "misc").Select(x => x.Name).ToArray();
            Assert.Equal(names.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase), names);
        }
        var roots = rows.Where(x => x.Depth == 0).ToArray();
        Assert.Equal("misc", roots[^1].Id);
        Assert.True(Array.FindIndex(roots, x => x.Id == "fish") < Array.FindIndex(roots, x => x.Id == "fishing.bait"));
    }
}
