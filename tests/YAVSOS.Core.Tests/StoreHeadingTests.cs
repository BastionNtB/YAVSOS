using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

/// <summary>D163 (X60): the stores' categories fold under one heading per mod in the Categories tree.</summary>
public sealed class StoreHeadingTests
{
    private static readonly ClassificationTaxonomy Base = DefaultClassificationTaxonomy.Create();
    private static readonly IReadOnlyCollection<string> Wood = new[] { "Wood", "FineWood" }, Copper = new[] { "Copper" }, Bread = new[] { "Bread" };

    private static (ContainerTypeInfo, IReadOnlyCollection<string>) Store(string prefab, string name, string? mod, string? piece, IReadOnlyCollection<string> takes) =>
        (new ContainerTypeInfo(prefab, name, prefab, mod, piece), takes);

    // Three Storehouse stores, one from another named mod, one whose mod is unknown, and a Shelf that takes everything.
    private static readonly ContainerFilterSet Set = ContainerFilterSet.From(new[]
    {
        Store("piece_storehouse_timbercrib", "Storehouse Timber Crib", "Storehouse", "Timber Crib", Wood),
        Store("piece_storehouse_metalrack", "Storehouse Metal Rack", "Storehouse", "Metal Rack", Copper),
        Store("piece_storehouse_breadbox", "Storehouse Bread Box", "Storehouse", "Bread Box", Bread),
        Store("piece_other_crate", "Morkhalla Ore Crate", "Morkhalla", "Ore Crate", Copper),
        Store("piece_mystery_bin", "Mystery Bin", null, "Mystery Bin", Bread),
        Store("piece_storehouse_shelf", "Storehouse Emergency Shelf", "Storehouse", "Emergency Shelf", new[] { "Wood", "FineWood", "Copper", "Bread", "SwordBronze" }),
    }, 5);
    private static readonly ClassificationTaxonomy Taxonomy = Set.Extend(Base);
    private const string Crib = "store.piece_storehouse_timbercrib", Rack = "store.piece_storehouse_metalrack", Box = "store.piece_storehouse_breadbox",
        Crate = "store.piece_other_crate", Bin = "store.piece_mystery_bin";
    private static readonly string[] Stores = { Crib, Rack, Box, Crate, Bin };
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    private static IReadOnlyList<CategoryTreeRow> Rows(IReadOnlyCollection<string>? stored = null, ICollection<string>? expanded = null,
        ICollection<string>? collapsed = null, Func<string, bool>? has = null) =>
        CategorySelection.Rows(EnglishText.Names(Taxonomy), stored ?? Array.Empty<string>(), has ?? (_ => true), expanded ?? None, collapsed ?? None, Set.Grouping);

    [Fact]
    public void Grouping_OneHeadingPerMod_UnknownModLast()
    {
        var groups = Set.Grouping.Groups;
        Assert.Equal(new[] { "heading:Morkhalla", "heading:Storehouse", "heading:" }, groups.Select(x => x.Id));
        Assert.Equal(new[] { Box, Rack, Crib }, Set.Grouping.Of(Crib)!.Categories);
        Assert.Null(Set.Grouping.Of(Bin)!.Mod);
        Assert.Null(Set.Grouping.Of("materials"));
        Assert.Null(Set.Grouping.Of("store.piece_storehouse_shelf")); // Takes everything: no filter, no category.
        Assert.Equal("Storehouse", Set.ForCategory(Crib)!.Mod);
        Assert.Equal("Timber Crib", Set.ForCategory(Crib)!.Piece);
        Assert.Same(CategoryGrouping.None, ContainerFilterSet.Empty.Grouping);
    }

    [Fact]
    public void Heading_IsNeverACategory()
    {
        Assert.All(Set.Grouping.Groups, x => Assert.True(CategoryGroup.IsHeading(x.Id)));
        Assert.All(Set.Grouping.Groups, x => Assert.False(Taxonomy.Categories.ContainsKey(x.Id)));
        Assert.False(CategoryGroup.IsHeading(Crib));
        // A category ID can't take a heading's form, so the two never meet in the tree's fold sets.
        Assert.ThrowsAny<ArgumentException>(() => new ClassificationCategory("heading:storehouse", "Storehouse", 1));
        Assert.ThrowsAny<ArgumentException>(() => CategorySelection.Toggle(Taxonomy, Array.Empty<string>(), "heading:Storehouse"));
    }

    [Fact]
    public void Rows_StoresFoldUnderTheirHeadings_FoldedByDefault()
    {
        var rows = Rows();
        Assert.DoesNotContain(rows, x => Stores.Contains(x.Id));
        var roots = rows.Where(x => x.Depth == 0).ToArray();
        var storehouse = Assert.Single(rows, x => x.Id == "heading:Storehouse");
        Assert.True(storehouse.Heading); Assert.True(storehouse.HasChildren); Assert.False(storehouse.Expanded);
        Assert.Equal(CategorySelectionState.Off, storehouse.State);
        Assert.Equal("Storehouse stores (3)", storehouse.Name);
        Assert.Equal("Morkhalla stores (1)", Assert.Single(rows, x => x.Id == "heading:Morkhalla").Name);
        Assert.Equal("Stores from other mods (1)", Assert.Single(rows, x => x.Id == "heading:").Name);
        Assert.All(rows.Where(x => !CategoryGroup.IsHeading(x.Id)), x => Assert.False(x.Heading));
        // Headings sort among the top-level rows by their text; Misc stays last.
        var names = roots.Where(x => x.Id != "misc").Select(x => x.Name).ToArray();
        Assert.Equal(names.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase), names);
        Assert.Equal("misc", roots[^1].Id);
    }

    [Fact]
    public void Rows_AnOpenHeadingShowsItsStoresByTheirOwnNames()
    {
        var rows = Rows(expanded: new HashSet<string> { "heading:Storehouse" });
        var at = rows.ToList().FindIndex(x => x.Id == "heading:Storehouse");
        Assert.True(rows[at].Expanded);
        var under = rows.Skip(at + 1).Take(3).ToArray();
        Assert.Equal(new[] { Box, Rack, Crib }, under.Select(x => x.Id));
        Assert.Equal(new[] { "Bread Box", "Metal Rack", "Timber Crib" }, under.Select(x => x.Name));
        Assert.All(under, x => { Assert.Equal(1, x.Depth); Assert.False(x.Heading); });
        Assert.Equal(0, rows[at + 4].Depth); // The next top-level row follows the three.
        // The other headings stay folded.
        Assert.DoesNotContain(rows, x => x.Id == Crate || x.Id == Bin);
    }

    [Fact]
    public void Rows_ATickedStoreOpensItsHeading_UnlessThePlayerFoldedIt()
    {
        var rows = Rows(stored: new[] { Crib });
        Assert.True(Assert.Single(rows, x => x.Id == "heading:Storehouse").Expanded);
        Assert.Equal(CategorySelectionState.On, Assert.Single(rows, x => x.Id == Crib).State);
        Assert.False(Assert.Single(rows, x => x.Id == "heading:Morkhalla").Expanded);
        var folded = Rows(stored: new[] { Crib }, collapsed: new HashSet<string> { "heading:Storehouse" });
        Assert.False(Assert.Single(folded, x => x.Id == "heading:Storehouse").Expanded);
        Assert.DoesNotContain(folded, x => x.Id == Crib);
    }

    [Fact]
    public void Rows_TheCountIsTheShownStores_AndAHeadingWithNoneIsLeftOut()
    {
        bool Has(string id) => id != Box && id != Crate;
        var rows = Rows(has: Has);
        Assert.Equal("Storehouse stores (2)", Assert.Single(rows, x => x.Id == "heading:Storehouse").Name);
        Assert.DoesNotContain(rows, x => x.Id == "heading:Morkhalla");
        // A rule naming the empty store still shows it (D054), and so its heading.
        Assert.Contains(Rows(stored: new[] { Crate }, has: Has), x => x.Id == "heading:Morkhalla" && x.Expanded);
    }

    [Fact]
    public void Rows_WithoutGrouping_StoresAreTopLevelRowsAsBefore()
    {
        var rows = CategorySelection.Rows(EnglishText.Names(Taxonomy), Array.Empty<string>(), _ => true, None, None);
        Assert.All(Stores, id => Assert.Contains(rows, x => x.Id == id && x.Depth == 0));
        Assert.DoesNotContain(rows, x => x.Heading);
        Assert.Equal("Storehouse Timber Crib", Assert.Single(rows, x => x.Id == Crib).Name);
    }

    [Fact]
    public void Rows_AServersOwnCategoryOfTheSameIdKeepsItsName()
    {
        var server = new ClassificationTaxonomy(Base.Categories.Values.Where(x => !x.Generated).Append(new ClassificationCategory(Crib, "Wood crib (server)", 5)));
        var rows = CategorySelection.Rows(EnglishText.Names(Set.Extend(server)), Array.Empty<string>(), _ => true,
            new HashSet<string> { "heading:Storehouse" }, None, Set.Grouping);
        Assert.Equal("Wood crib (server)", Assert.Single(rows, x => x.Id == Crib).Name);
        Assert.Equal("Metal Rack", Assert.Single(rows, x => x.Id == Rack).Name);
    }

    [Fact]
    public void Search_FindsAStore_AndOpensItsHeadingToShowOnlyTheHit()
    {
        var names = EnglishText.Names(Taxonomy);
        int Count(string id) => Set.ForCategory(id)?.Count ?? 1;
        var search = CategorySearch.Find(names, "crib", Array.Empty<SearchableItem>(), Count);
        Assert.Equal(new[] { Crib }, search.Categories);
        var collapsed = new HashSet<string> { "heading:Storehouse" }; // Folded by the player: the search still reveals the hit.
        var rows = CategorySearch.Rows(names, Array.Empty<string>(), _ => true, None, collapsed, search, Set.Grouping);
        Assert.Equal(new[] { "heading:Storehouse", Crib }, rows.Select(x => x.Id));
        Assert.True(rows[0].Expanded && rows[0].Heading);
        Assert.Equal("Timber Crib", rows[1].Name);
        // Each store's full name is what a search matches, so the mod's name finds them all.
        var all = CategorySearch.Find(names, "storehouse", Array.Empty<SearchableItem>(), Count);
        var shown = CategorySearch.Rows(names, Array.Empty<string>(), _ => true, None, None, all, Set.Grouping);
        Assert.Equal(new[] { "heading:Storehouse", Box, Rack, Crib }, shown.Select(x => x.Id));
        // An item a store's category holds best leads there too.
        var items = new[] { new SearchableItem("Bread", "Bread", new[] { "food", Box, Bin }) };
        var bread = CategorySearch.Find(names, "bread", items, id => id == "food" ? 40 : Count(id));
        Assert.Contains(CategorySearch.Rows(names, Array.Empty<string>(), _ => true, None, None, bread, Set.Grouping), x => x.Heading && x.Expanded);
    }

    [Fact]
    public void Toggle_AStoreUnderAHeading_TicksOnlyThatStore()
    {
        var stored = CategorySelection.Toggle(Taxonomy, Array.Empty<string>(), Rack);
        Assert.Equal(new[] { Rack }, stored);
        var rows = Rows(stored: stored);
        Assert.Equal(CategorySelectionState.On, Assert.Single(rows, x => x.Id == Rack).State);
        Assert.Equal(CategorySelectionState.Off, Assert.Single(rows, x => x.Id == Crib).State);
        Assert.Equal(new[] { Rack, Crib }, CategorySelection.Toggle(Taxonomy, stored, Crib).ToArray());
    }
}
