using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

// S1 (audit D01): the panel's rule edit state, out of RuleControls, so every way of loading a rule (the chest, a pasted
// rule) converts and saves it the same way.
public sealed class RuleEditStateTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    private static KeyValuePair<string, IEnumerable<string>> Filter(string dimension, params string[] values) =>
        new KeyValuePair<string, IEnumerable<string>>(dimension, values);

    private static ChestRules Stored(ClassificationFilterRule[] rules, string[]? includes = null, string[]? excludes = null, int priority = 0, bool overflow = false) =>
        new ChestRules(Taxonomy, ChestRuleState.Configured, rules, includes, excludes, priority: priority, overflow: overflow);

    [Fact]
    public void Load_ShowsTheEditableAlternative_AndConvertsUpgradedToLevels()
    {
        var legacy = new ClassificationFilterRule(Taxonomy, new[] { "weapons" }, new[] { Filter("upgraded", "yes"), Filter("rarity", "magic") });
        var state = new RuleEditState();
        state.Load(Stored(new[] { legacy }, new[] { "Wood" }, new[] { "Club" }, priority: 3));

        Assert.True(state.Stored);
        Assert.Equal(new[] { "weapons" }, state.Categories);
        Assert.Equal(new[] { "Wood" }, state.Includes); Assert.Equal(new[] { "Club" }, state.Excludes);
        Assert.Equal(3, state.Priority);
        // P6-21: "upgraded = yes" shows as upgrade levels 2 and up; "upgraded" itself is gone.
        Assert.False(state.Filters.ContainsKey("upgraded"));
        Assert.Equal(new[] { "2", "3", "4", "5+" }, state.Filters["quality"]);
        Assert.Equal(new[] { "magic" }, state.Filters["rarity"]);

        // The save writes the levels and replaces the stored "upgraded" alternative, not a second one beside it.
        var edit = state.Edit();
        Assert.Equal(new[] { "2", "3", "4", "5+" }, edit.Filters["quality"]);
        Assert.True(edit.EditedFilters.ContainsKey("upgraded"));
        var payload = ContainerPolicyEnvelope.Serialize(1, Stored(new[] { legacy }));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, edit, out var updated));
        var saved = ContainerPolicyEnvelope.Read(updated!, Taxonomy).Rules!;
        var only = Assert.Single(saved.FullRules);
        Assert.False(only.Filters.ContainsKey("upgraded"));
        Assert.Equal(new[] { "2", "3", "4", "5+" }, only.Filters["quality"]);
    }

    [Fact]
    public void Load_UpgradedNo_IsLevelOne_AndAnEmptyConversionDropsTheFilter()
    {
        var no = new RuleEditState();
        no.Load(Stored(new[] { new ClassificationFilterRule(Taxonomy, new[] { "weapons" }, new[] { Filter("upgraded", "no") }) }));
        Assert.Equal(new[] { "1" }, no.Filters["quality"]);

        var unknown = new RuleEditState();
        unknown.Load(Stored(new[] { new ClassificationFilterRule(Taxonomy, new[] { "weapons" }, new[] { Filter("upgraded", "unknown") }) }));
        Assert.False(unknown.Filters.ContainsKey("quality")); Assert.False(unknown.Filters.ContainsKey("upgraded"));
    }

    [Fact]
    public void Edits_ToggleAndMove_AndClearEmptiesEverything()
    {
        var state = new RuleEditState();
        state.Load(Stored(new[] { new ClassificationFilterRule(Taxonomy, new[] { "food" }) }, overflow: true));
        Assert.Equal(1, state.HiddenParts); // The overflow flag: kept, not shown (D086 D5).

        state.Choose("Copper", always: true);
        Assert.Contains("Copper", state.Includes);
        state.Choose("Copper", always: false); // Never moves it out of Always.
        Assert.DoesNotContain("Copper", state.Includes); Assert.Contains("Copper", state.Excludes);
        state.Choose("Copper", always: false); // A second Never clears it.
        Assert.Empty(state.Excludes);

        state.ToggleFilter("rarity", "magic"); state.ToggleFilter("rarity", "rare");
        Assert.Equal(new[] { "magic", "rare" }, state.Filters["rarity"]);
        state.RemoveFilterValue("rarity", "magic"); state.ToggleFilter("rarity", "rare");
        Assert.False(state.Filters.ContainsKey("rarity")); // An emptied filter goes.

        state.ToggleCategory(Taxonomy, "materials");
        Assert.Contains("materials", state.Categories);

        state.Clear();
        Assert.False(state.Stored); Assert.Empty(state.Categories); Assert.Empty(state.Filters); Assert.Equal(0, state.HiddenParts);
        Assert.Equal(0, state.Priority);
        Assert.Empty(state.Edit().Categories);
    }
}
