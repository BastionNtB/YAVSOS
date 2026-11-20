using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
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

    [Fact]
    public void Grid_TakesAsManyItemsAsTheCatalog()
    {
        // #105 (D171): a pack with more than 8,192 items used to throw here and turn the rule panel off.
        var bread = Index.Items["Bread"];
        var catalog = Enumerable.Range(0, AuthoritativeAssignmentsCodec.MaximumItems).Select(i => new GridSource("Item" + i, "Item " + i, bread));
        Assert.Equal(AuthoritativeAssignmentsCodec.MaximumItems, ItemGrid.MaximumItems);
        var grid = ItemGrid.Build(catalog, new[] { "food.health" }, None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(AuthoritativeAssignmentsCodec.MaximumItems, grid.Count);
    }
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
        Assert.Contains("preferred over chests that accept it only by category", EnglishText.Of(ItemGrid.MarkText(GridMark.Always, true)));
        Assert.Equal("Always here, although no ticked category accepts it.", EnglishText.Of(ItemGrid.MarkText(GridMark.Always, false)));
        Assert.Equal("Never here, whatever the categories say.", EnglishText.Of(ItemGrid.MarkText(GridMark.Never, true)));
        Assert.Equal("Accepted by this rule's categories.", EnglishText.Of(ItemGrid.MarkText(GridMark.None, true)));
        Assert.Equal("Not accepted by this rule.", EnglishText.Of(ItemGrid.MarkText(GridMark.None, false)));
        Assert.Equal("Click: Always → Never → clear.", EnglishText.Of(ItemGrid.ClickHint));
    }

    // X59 (D149): with the ticked categories given, "Accepted by this rule" is one section per category, in the given order.
    private static IReadOnlyList<GridEntry> Grouped(string[] categories, string[] groups, string[]? always = null, string[]? never = null, string[]? here = null, string? query = null) =>
        ItemGrid.Build(Catalog(), categories, None, always ?? Array.Empty<string>(), never ?? Array.Empty<string>(), here ?? Array.Empty<string>(), query, null, groups);
    private static string Sections(IEnumerable<GridEntry> rows) =>
        string.Join(" ", rows.Select(x => $"{x.Section}{(x.Group != null ? "/" + x.Group : "")}:{x.Prefab}"));

    [Fact]
    public void Groups_SplitTheRuleSection_ByTickedCategory_InTheGivenOrder()
    {
        // Food first, then Materials, as the panel's chips would order them; names sort within each section.
        Assert.Equal("Rule/food:RawMeat Rule/food:Bread Rule/materials:Copper Rule/materials:Iron Rule/materials:Tin Rule/materials:Wood",
            Sections(Grouped(new[] { "materials", "food" }, new[] { "food", "materials" })));
        // The other order puts Materials first.
        Assert.StartsWith("Rule/materials:Copper", Sections(Grouped(new[] { "materials", "food" }, new[] { "materials", "food" })));
        // Without groups, one section as before, and no entry carries a group.
        Assert.All(Grid(new[] { "materials", "food" }), x => Assert.Null(x.Group));
    }

    [Fact]
    public void Groups_AnItemInTwoTickedCategories_ShowsUnderTheFirst()
    {
        // Copper is in materials and in materials.metal; with both given, it sits under whichever comes first.
        Assert.Equal("Rule/materials.metal:Copper Rule/materials.metal:Iron Rule/materials.metal:Tin Rule/materials:Wood",
            Sections(Grouped(new[] { "materials" }, new[] { "materials.metal", "materials" })));
        Assert.Equal("Rule/materials:Copper Rule/materials:Iron Rule/materials:Tin Rule/materials:Wood",
            Sections(Grouped(new[] { "materials" }, new[] { "materials", "materials.metal" })));
    }

    [Fact]
    public void Groups_OnlyTheRuleSection_IsSplit()
    {
        // Here and Marked keep one section each and no group.
        var rows = Grouped(new[] { "materials" }, new[] { "materials" }, always: new[] { "Bread" }, here: new[] { "Tin" });
        Assert.Equal("Here:Tin Marked:Bread Rule/materials:Copper Rule/materials:Iron Rule/materials:Wood", Sections(rows));
        // So do a search's other items.
        Assert.Equal("Found:SwordBronze", Sections(Grouped(new[] { "materials" }, new[] { "materials" }, query: "sword")));
        Assert.Equal("Copper ore (1)", EnglishText.Of(ItemGrid.Title(GridSection.Rule, "Copper ore", 1)));
        Assert.Equal("Metals (12)", EnglishText.Of(ItemGrid.Title(GridSection.Rule, "Metals", 12)));
        Assert.Equal("Accepted by this rule (3)", EnglishText.Of(ItemGrid.Title(GridSection.Rule, null, 3)));
        Assert.Equal("In this chest or your inventory (2)", EnglishText.Of(ItemGrid.Title(GridSection.Here, "ignored", 2)));
    }

    [Fact]
    public void Buttons_NeverAllAndClear_ButOnlyClear_OutsideTheCategories()
    {
        Assert.Equal(new[] { GridButton.Clear }, ItemGrid.Buttons(GridSection.Marked));
        foreach (var section in new[] { GridSection.Here, GridSection.Rule, GridSection.Found })
            Assert.Equal(new[] { GridButton.NeverAll, GridButton.Clear }, ItemGrid.Buttons(section));
    }

    [Fact]
    public void MarkSection_NeverAll_MarksEveryIconShown_AndCountsOnlyTheChanged()
    {
        var rows = Grouped(new[] { "materials", "food" }, new[] { "materials", "food" }, always: new[] { "Copper" }, never: new[] { "Tin" });
        var metals = rows.Where(x => x.Group == "materials").ToList();
        var includes = new SortedSet<string>(new[] { "Copper", "Bread" }, StringComparer.Ordinal);
        var excludes = new SortedSet<string>(new[] { "Tin" }, StringComparer.Ordinal);
        // Copper (Always), Iron and Wood change; Tin is already Never.
        Assert.Equal(3, ItemGrid.MarkSection(metals, GridButton.NeverAll, includes, excludes));
        Assert.Equal(new[] { "Bread" }, includes);
        Assert.Equal(new[] { "Copper", "Iron", "Tin", "Wood" }, excludes);
        // Nothing left to change: 0, and the lists stay as they are.
        var again = Grouped(new[] { "materials", "food" }, new[] { "materials", "food" }, always: includes.ToArray(), never: excludes.ToArray()).Where(x => x.Group == "materials");
        Assert.Equal(0, ItemGrid.MarkSection(again, GridButton.NeverAll, includes, excludes));
        Assert.Equal(new[] { "Copper", "Iron", "Tin", "Wood" }, excludes);
    }

    [Fact]
    public void MarkSection_Clear_TakesBothMarksOff_OnlyInThatSection()
    {
        var includes = new SortedSet<string>(new[] { "Copper", "Bread" }, StringComparer.Ordinal);
        var excludes = new SortedSet<string>(new[] { "Tin", "RawMeat" }, StringComparer.Ordinal);
        var rows = Grouped(new[] { "materials", "food" }, new[] { "materials", "food" }, always: includes.ToArray(), never: excludes.ToArray());
        Assert.Equal(2, ItemGrid.MarkSection(rows.Where(x => x.Group == "materials"), GridButton.Clear, includes, excludes));
        Assert.Equal(new[] { "Bread" }, includes);      // Food's marks are untouched.
        Assert.Equal(new[] { "RawMeat" }, excludes);
    }

    [Fact]
    public void MarkSection_AppliesToEveryLookAlikeCopy()
    {
        var item = Index.Items["Copper"];
        var catalog = new[] { new GridSource("Copper", "Copper ore", item, look: "ore"), new GridSource("CopperNpc", "Copper ore", item, look: "ore") };
        var rows = ItemGrid.Build(catalog, new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, new[] { "materials" });
        var entry = Assert.Single(rows);
        Assert.Equal("materials", entry.Group);
        var includes = new SortedSet<string>(StringComparer.Ordinal); var excludes = new SortedSet<string>(StringComparer.Ordinal);
        Assert.Equal(1, ItemGrid.MarkSection(rows, GridButton.NeverAll, includes, excludes));
        Assert.Equal(new[] { "Copper", "CopperNpc" }, excludes);
    }

    [Fact]
    public void SectionButtons_SayWhatTheyDo()
    {
        Assert.Equal("All", EnglishText.Of(ItemGrid.ButtonLabel(GridButton.NeverAll)));
        Assert.Equal("Clear", EnglishText.Of(ItemGrid.ButtonLabel(GridButton.Clear)));
        Assert.Equal("Never all", EnglishText.Of(ItemGrid.ButtonName(GridButton.NeverAll)));
        Assert.Equal("Marked 12 items Never. Undo puts them back.", EnglishText.Of(ItemGrid.ButtonDone(GridButton.NeverAll, 12)));
        Assert.Equal("Marked 1 item Never. Undo puts it back.", EnglishText.Of(ItemGrid.ButtonDone(GridButton.NeverAll, 1)));
        Assert.Equal("Cleared the marks on 3 items. Undo puts them back.", EnglishText.Of(ItemGrid.ButtonDone(GridButton.Clear, 3)));
        Assert.Equal("Nothing to change in this section.", EnglishText.Of(ItemGrid.ButtonDone(GridButton.Clear, 0)));
        var tip = ItemGrid.ButtonTip(GridButton.NeverAll, 5, searching: true);
        Assert.Equal("Never all", EnglishText.Of(tip.Heading!));
        Assert.Equal(new[] { "Marks the 5 items this section shows as Never here.", "With a search, only the items it finds.", "Undo puts the section back in one step." },
            tip.List!.Items.Select(x => EnglishText.Of((Message)x)).ToArray());
        Assert.Equal(2, ItemGrid.ButtonTip(GridButton.Clear, 1, searching: false).List!.Items.Count);
    }

    // #62 (D158): the sections a grid shows, as the view splits them: where the section or its ticked category changes.
    private static List<(GridSection Section, string? Group, List<GridEntry> Rows)> Split(IReadOnlyList<GridEntry> rows)
    {
        var sections = new List<(GridSection, string?, List<GridEntry>)>();
        foreach (var row in rows)
        {
            if (sections.Count == 0 || sections[^1].Item1 != row.Section || sections[^1].Item2 != row.Group) sections.Add((row.Section, row.Group, new List<GridEntry>()));
            sections[^1].Item3.Add(row);
        }
        return sections;
    }

    // #62 (D158): the section view button switches between one section per ticked category and one for the whole rule;
    // the sections, their counts and their buttons follow.
    [Fact]
    public void SectionView_PerCategoryOrOneSection_SectionsCountsAndButtons()
    {
        var ticked = new[] { "food", "materials" };
        var per = Split(ItemGrid.Build(Catalog(), ticked, None, new[] { "SwordBronze" }, Array.Empty<string>(), new[] { "Tin" }, null, null, ItemGrid.Groups(true, ticked)));
        Assert.Equal(new[] { "Here::1", "Marked::1", "Rule:food:2", "Rule:materials:3" }, per.Select(x => $"{x.Section}:{x.Group}:{x.Rows.Count}"));
        var single = Split(ItemGrid.Build(Catalog(), ticked, None, new[] { "SwordBronze" }, Array.Empty<string>(), new[] { "Tin" }, null, null, ItemGrid.Groups(false, ticked)));
        Assert.Equal(new[] { "Here::1", "Marked::1", "Rule::5" }, single.Select(x => $"{x.Section}:{x.Group}:{x.Rows.Count}"));
        Assert.Null(ItemGrid.Groups(false, ticked));
        Assert.Same(ticked, ItemGrid.Groups(true, ticked));
        // The same items in both views, so the switch hides or adds nothing.
        Assert.Equal(per.SelectMany(x => x.Rows).Select(x => x.Prefab).OrderBy(x => x), single.SelectMany(x => x.Rows).Select(x => x.Prefab).OrderBy(x => x));
        // Titles: each category by name, or the one section.
        Assert.Equal("Food (2)", EnglishText.Of(ItemGrid.Title(GridSection.Rule, "Food", per[2].Rows.Count)));
        Assert.Equal("Accepted by this rule (5)", EnglishText.Of(ItemGrid.Title(single[2].Section, single[2].Group, single[2].Rows.Count)));
        // Buttons: each accepted section has Never all and Clear in either view.
        Assert.All(per.Concat(single).Where(x => x.Section == GridSection.Rule), x => Assert.Equal(new[] { GridButton.NeverAll, GridButton.Clear }, ItemGrid.Buttons(x.Section)));
        // Never all on the one section marks every accepted item; on a category section only that category's.
        var includes = new SortedSet<string>(StringComparer.Ordinal); var excludes = new SortedSet<string>(StringComparer.Ordinal);
        Assert.Equal(2, ItemGrid.MarkSection(per[2].Rows, GridButton.NeverAll, includes, excludes));
        Assert.Equal(new[] { "Bread", "RawMeat" }, excludes);
        excludes.Clear();
        Assert.Equal(5, ItemGrid.MarkSection(single[2].Rows, GridButton.NeverAll, includes, excludes));
        // One ticked category: the same one section either way, and the button still shows (D158).
        Assert.Equal(new[] { "Rule:materials:4" }, Split(ItemGrid.Build(Catalog(), new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), null, null, ItemGrid.Groups(true, new[] { "materials" }))).Select(x => $"{x.Section}:{x.Group}:{x.Rows.Count}"));
    }

    [Fact]
    public void SectionView_TooltipSaysWhatAClickSwitchesTo()
    {
        var per = ItemGrid.ViewTip(true);
        Assert.Equal("Section view", EnglishText.Of(per.Heading!));
        Assert.Equal("Each ticked category has its own section. Click to show one section for the whole rule.", EnglishText.Of(per.Body!));
        Assert.Equal("One section shows everything this rule accepts. Click to give each ticked category its own section.", EnglishText.Of(ItemGrid.ViewTip(false).Body!));
    }

    // D158: Never all stops above 512 icons to mark per click, in either view; Clear and single marks aren't limited.
    [Fact]
    public void NeverAll_StopsAbove512ItemsToMark_InEitherView()
    {
        var metal = Index.Items["Copper"]; var food = Index.Items["Bread"];
        var catalog = Enumerable.Range(0, 300).Select(i => new GridSource("Metal" + i, "Metal " + i, metal))
            .Concat(Enumerable.Range(0, 300).Select(i => new GridSource("Food" + i, "Food " + i, food))).ToList();
        var ticked = new[] { "materials", "food" };
        IReadOnlyList<GridEntry> Rows(bool perCategory, string[]? never = null, string? query = null) =>
            ItemGrid.Build(catalog, ticked, None, Array.Empty<string>(), never ?? Array.Empty<string>(), Array.Empty<string>(), query, null, ItemGrid.Groups(perCategory, ticked));
        // Per category: 300 each, both under the limit.
        foreach (var (_, _, rows) in Split(Rows(true)))
        {
            Assert.Equal(300, ItemGrid.ToMark(rows, GridButton.NeverAll));
            Assert.True(ItemGrid.Usable(GridButton.NeverAll, ItemGrid.ToMark(rows, GridButton.NeverAll)));
        }
        // One section: 600 to mark, over the limit; the click changes nothing.
        var one = Split(Rows(false)).Single().Rows;
        Assert.Equal(600, ItemGrid.ToMark(one, GridButton.NeverAll));
        Assert.Equal(600, ItemGrid.ToMark(one, 0, one.Count, GridButton.NeverAll));
        Assert.False(ItemGrid.Usable(GridButton.NeverAll, 600));
        var includes = new SortedSet<string>(StringComparer.Ordinal); var excludes = new SortedSet<string>(StringComparer.Ordinal);
        Assert.Equal(0, ItemGrid.MarkSection(one, GridButton.NeverAll, includes, excludes));
        Assert.Empty(excludes);
        var tip = ItemGrid.ButtonTip(GridButton.NeverAll, one.Count, false, 600);
        Assert.Equal("Never all", EnglishText.Of(tip.Heading!));
        Assert.Equal("This section has 600 items to mark, more than Never all marks at once (512). Search to narrow it, or untick a category.", EnglishText.Of(tip.Body!));
        // Items already Never don't count: 100 of them leave 500, under the limit.
        var marked = Enumerable.Range(0, 100).Select(i => "Metal" + i).ToArray();
        var fewer = Split(Rows(false, never: marked)).Single().Rows;
        Assert.Equal((600, 500), (fewer.Count, ItemGrid.ToMark(fewer, GridButton.NeverAll)));
        Assert.True(ItemGrid.Usable(GridButton.NeverAll, 500));
        Assert.Equal(500, ItemGrid.MarkSection(fewer, GridButton.NeverAll, includes, excludes));
        Assert.Equal(500, excludes.Count); // The 100 already Never sit in the rule, not in this set.
        // A search narrows the section under the limit.
        var found = Split(Rows(false, query: "Metal 1")).Single().Rows;
        Assert.True(ItemGrid.Usable(GridButton.NeverAll, ItemGrid.ToMark(found, GridButton.NeverAll)));
        // The edge: exactly 512 works, 513 doesn't; the count tooltip is unchanged under the limit.
        Assert.True(ItemGrid.Usable(GridButton.NeverAll, ItemGrid.MaximumNeverAll));
        Assert.False(ItemGrid.Usable(GridButton.NeverAll, ItemGrid.MaximumNeverAll + 1));
        Assert.Equal("Marks the 512 items this section shows as Never here.",
            EnglishText.Of((Message)ItemGrid.ButtonTip(GridButton.NeverAll, 512, false, 512).List!.Items[0]));
        var edge = Enumerable.Range(0, 513).Select(i => new GridSource("Edge" + i, "Edge " + i, metal)).ToList();
        var edgeRows = ItemGrid.Build(edge, new[] { "materials" }, None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        excludes.Clear();
        Assert.Equal(0, ItemGrid.MarkSection(edgeRows, GridButton.NeverAll, includes, excludes));
        Assert.Equal(512, ItemGrid.MarkSection(edgeRows.Skip(1), GridButton.NeverAll, includes, excludes));
        // Clear has no limit.
        Assert.True(ItemGrid.Usable(GridButton.Clear, 10_000));
        var cleared = Split(Rows(false, never: catalog.Select(x => x.Prefab).ToArray())).Single().Rows;
        Assert.Equal(600, ItemGrid.MarkSection(cleared, GridButton.Clear, includes, new SortedSet<string>(catalog.Select(x => x.Prefab), StringComparer.Ordinal)));
    }

    // X61 (D143): a right-click steps the other way, from the mark the icon has now.
    [Theory]
    [InlineData(GridMark.None, GridMark.Never)]
    [InlineData(GridMark.Never, GridMark.Always)]
    [InlineData(GridMark.Always, GridMark.None)]
    public void RightClick_CyclesNoneNeverAlways(GridMark from, GridMark to)
    {
        Assert.Equal(to, ItemGrid.Previous(from));
        Assert.Equal(to, ItemGrid.Step(from, backwards: true));
        Assert.Equal(ItemGrid.Next(from), ItemGrid.Step(from, backwards: false));
        // The two directions undo each other, from every mark.
        Assert.Equal(from, ItemGrid.Next(ItemGrid.Previous(from)));
        Assert.Equal(from, ItemGrid.Previous(ItemGrid.Next(from)));
        Assert.Equal("Right-click: Never → Always → clear.", EnglishText.Of(ItemGrid.RightClickHint));
    }

    // X61: switching buttons mid-cycle steps from where the mark is, not from where the last cycle started.
    [Fact]
    public void Clicks_MixedButtons_StepFromTheCurrentMark()
    {
        var mark = GridMark.None;
        mark = ItemGrid.Step(mark, false); Assert.Equal(GridMark.Always, mark); // click
        mark = ItemGrid.Step(mark, true); Assert.Equal(GridMark.None, mark);    // right-click goes back
        mark = ItemGrid.Step(mark, true); Assert.Equal(GridMark.Never, mark);   // right-click again
        mark = ItemGrid.Step(mark, false); Assert.Equal(GridMark.None, mark);   // click: Never → clear
        mark = ItemGrid.Step(mark, true); Assert.Equal(GridMark.Never, mark);
        mark = ItemGrid.Step(mark, true); Assert.Equal(GridMark.Always, mark);
        mark = ItemGrid.Step(mark, false); Assert.Equal(GridMark.Never, mark);
    }

    // P10-04: the group titles are plural messages with the count as a value; the marks' names come from their own keys.
    [Fact]
    public void Titles_NameEachGroupWithItsCount()
    {
        Assert.Equal("In this chest or your inventory (3)", EnglishText.Of(ItemGrid.Title(GridSection.Here, 3)));
        Assert.Equal("Always or Never, outside the categories (1)", EnglishText.Of(ItemGrid.Title(GridSection.Marked, 1)));
        Assert.Equal("Accepted by this rule (12)", EnglishText.Of(ItemGrid.Title(GridSection.Rule, 12)));
        Assert.Equal("Other items (40)", EnglishText.Of(ItemGrid.Title(GridSection.Found, 40)));
        Assert.Equal(Message.Of("yavsos_grid_rule").Count(12), ItemGrid.Title(GridSection.Rule, 12));
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
