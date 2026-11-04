using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// P10-28 (D133): plans one cleanup over a small made-up base so the runtime compiles the planner's code before the
/// player's first press. The first cleanup of each game launch otherwise pays that one-time cost in one frame (lab measurement,
/// 2026-11-04: plan 80 ms and gather 39 ms against 15 and 10 ms warm; a second session in the same game was close to
/// warm, so the cost is per launch, not per session). Everything it uses is its own: its taxonomy, index, answers,
/// evaluator and chests, so it can run off the game thread while the game uses the real ones. The base covers each
/// kind of move (a rejected stack, an unruled chest, a better-priority chest), a full chest, an overflow chest, Always
/// and Never items, an instance filter (so the facets callback runs), AzuAutoStore's exclusions, a held stack, merges
/// and another player's chest through MultiUserChest. Nothing it plans is carried out.
/// </summary>
public static class CleanupWarmup
{
    private const string Metal = "materials.metal", Wood = "materials.wood", Swords = "weapons.swords", Food = "food.health";

    /// <summary>The plan for the made-up base. Read only; safe on any thread.</summary>
    public static CleanupPlan Plan()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create();
        var index = ClassificationIndex.Build(CatalogSnapshot.Build(new[]
            {
                Item("WarmupCopper", "Material"), Item("WarmupWood", "Material"), Item("WarmupSword", "OneHandedWeapon"),
                Item("WarmupBread", "Consumable", health: 30)
            }),
            new ClassificationRuleSet(taxonomy, new[]
            {
                new ClassificationOverride("exact", "WarmupCopper", OverrideOperation.Replace, new[] { Metal }),
                new ClassificationOverride("exact", "WarmupWood", OverrideOperation.Replace, new[] { Wood }),
                new ClassificationOverride("exact", "WarmupSword", OverrideOperation.Replace, new[] { Swords }),
                new ClassificationOverride("exact", "WarmupBread", OverrideOperation.Replace, new[] { Food })
            }));
        string Rule(string[] categories, int priority = 0, bool overflow = false, string[]? always = null, string[]? never = null,
            (string Dimension, string[] Values)[]? filters = null) =>
            ContainerPolicyEnvelope.Serialize(1, new ChestRules(taxonomy, ChestRuleState.Configured,
                new[] { new ClassificationFilterRule(taxonomy, categories,
                    (filters ?? Array.Empty<(string, string[])>()).Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Dimension, x.Values))) },
                always, never, priority, overflow));
        var chests = new[]
        {
            Chest("unruled", "", 0, 4, true, ("WarmupCopper", 20, 50), ("WarmupWood", 30, 50), ("WarmupSword", 1, 1), ("WarmupBread", 5, 10)),
            Chest("wood", Rule(new[] { Wood }, never: new[] { "WarmupBread" }), 1, 4, true, ("WarmupCopper", 10, 50), ("WarmupWood", 40, 50), ("WarmupBread", 2, 10)),
            Chest("metal", Rule(new[] { Metal }), 2, 4, true, ("WarmupCopper", 30, 50)),
            Chest("metal-first", Rule(new[] { Metal }, priority: 2), 3, 4, true),
            Chest("metal-full", Rule(new[] { Metal }, priority: 3), 4, 1, true, ("WarmupCopper", 50, 50)),
            Chest("swords", Rule(new[] { Swords }, filters: new[] { ("rarity", new[] { "unknown", "magic" }) }), 5, 4, true),
            Chest("food", Rule(new[] { Food }, always: new[] { "WarmupBread" }), 6, 4, false),
            Chest("overflow", Rule(Array.Empty<string>(), overflow: true), 7, 4, true),
            Chest("held", "", 8, 2, true, ("WarmupWood", 5, 50))
        };
        var input = new CleanupInput(taxonomy, new Answers(index), chests, categorySize: _ => 1, mucAvailable: true,
            azuAllows: (chest, item) => chest != "overflow" || item.Prefab != "WarmupSword",
            held: new[] { chests[8].Inventory.Stacks[0].Handle },
            instanceFacets: _ => Array.Empty<FacetEvidence>());
        return CleanupPlanner.Plan(input, new ChestRuleEvaluator());
    }

    private static CleanupChest Chest(string id, string payload, double distance, int width, bool local,
        params (string Prefab, int Quantity, int Maximum)[] stacks) =>
        new CleanupChest(id, payload,
            new TransferInventorySnapshot(new TransferInventoryStamp("warmup:" + id, Guid.NewGuid(), 0, 0, local ? 1 : 2, 1, "warmup"),
                width, 1, stacks.Select((s, x) => new TransferStack(Guid.NewGuid(), x, 0, s.Quantity, s.Maximum, Identity(s.Prefab),
                    mergeClass: s.Maximum > 1 ? s.Prefab : null)), Array.Empty<int>()),
            local, distance: distance, ownerMuc: OwnerMucState.Usable);

    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());

    private static CatalogFact Item(string id, string type, double health = 0) => new(CatalogFactKind.Item, id, new Dictionary<string, string>
    {
        ["type"] = type, ["skill"] = type == "OneHandedWeapon" ? "Swords" : "None", ["quest"] = "False", ["hasIcon"] = "True", ["value"] = "0",
        ["foodHealth"] = health.ToString(CultureInfo.InvariantCulture), ["foodStamina"] = "0", ["foodEitr"] = "0", ["foodBurnTime"] = "0",
        ["consumeEffect"] = "", ["ammoType"] = "", ["buildTable"] = ""
    });

    private sealed class Answers : ICategoryAnswers
    {
        private readonly ClassificationIndex index;
        internal Answers(ClassificationIndex index) => this.index = index;
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) => index.Items.TryGetValue(prefab, out var row) ? row : null;
    }
}
