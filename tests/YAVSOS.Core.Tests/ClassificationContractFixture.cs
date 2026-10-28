using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

internal static class ClassificationContractFixture
{
    internal static CatalogFact Fact(CatalogFactKind kind, string id, params string[] pairs) => new CatalogFact(kind, id,
        Enumerable.Range(0, pairs.Length / 2).Select(i => new KeyValuePair<string, string>(pairs[2 * i], pairs[2 * i + 1])));
    internal static CatalogFact Item(string id = "Wood", string type = "Material", string skill = "Swords", bool quest = false,
        bool icon = true, int value = 0, double health = 0, double stamina = 0, double eitr = 0, double duration = 0,
        string effect = "", string ammo = "", string table = "") => Fact(CatalogFactKind.Item, id,
        "type", type, "skill", skill, "quest", quest.ToString(), "hasIcon", icon.ToString(), "value", value.ToString(CultureInfo.InvariantCulture),
        "foodHealth", health.ToString(CultureInfo.InvariantCulture), "foodStamina", stamina.ToString(CultureInfo.InvariantCulture),
        "foodEitr", eitr.ToString(CultureInfo.InvariantCulture), "foodBurnTime", duration.ToString(CultureInfo.InvariantCulture),
        "consumeEffect", effect, "ammoType", ammo, "buildTable", table);
    internal static CatalogFact Recipe(string id = "r", string output = "Bread", bool enabled = true, bool upgrade = false,
        bool refiner = false, int amount = 1, bool any = false) => Fact(CatalogFactKind.Recipe, id,
        "output", output, "enabled", enabled.ToString(), "upgradeOnly", upgrade.ToString(), "stationUpgrader", refiner.ToString(),
        "outputAmount", amount.ToString(CultureInfo.InvariantCulture), "anyIngredient", any.ToString());
    internal static CatalogFact Requirement(string id = "slot0", string recipe = "r", string input = "Wood", int amount = 2,
        bool refinement = false, int upgrade = 0) => Fact(CatalogFactKind.Requirement, id,
        "recipe", recipe, "input", input, "amount", amount.ToString(CultureInfo.InvariantCulture),
        "upgradeAmount", upgrade.ToString(CultureInfo.InvariantCulture), "upgraderResource", refinement.ToString());
    internal static ClassificationIndex Build(IEnumerable<CatalogFact> facts, ClassificationRuleSet? rules = null,
        IEnumerable<ItemClassificationEvidence>? evidence = null, string sourceIdentity = "none", int maxWork = 5000000) =>
        ClassificationIndex.Build(CatalogSnapshot.Build(facts), rules ?? new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), evidence, sourceIdentity, maxWork);
    /// <summary>The classifier's own memberships, without the taxonomy closure rows (covered in CategoryClosureTests).</summary>
    internal static string[] Categories(ClassificationResult result) => CategoryClosure.Underived(result.Memberships).Select(x => x.Category).Distinct(StringComparer.Ordinal).ToArray();
    internal static string[] AllCategories(ClassificationResult result) => result.Memberships.Select(x => x.Category).Distinct(StringComparer.Ordinal).ToArray();
    internal static ClassificationFilterRule Rule(string[]? categories = null, params (string Dimension, string[] Values)[] filters) =>
        new ClassificationFilterRule(DefaultClassificationTaxonomy.Create(), categories ?? Array.Empty<string>(),
            filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Dimension, x.Values)));
}
