using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>Memberships from explicit loaded consumers; a machine never establishes material composition or mod ownership.</summary>
internal static class LoadedItemSemantics
{
    internal static string Category(string method) => method switch
    {
        "building" => "materials.building", "planting" => "farming", "fuel" => "materials.fuel",
        "creature-food" => "animal.feed", "saddle" => "animal.saddles",
        "door-key" or "trader-handover" => "special.keys", "offering" => "special.offerings",
        "feast-ingredient" => "ingredients.food", "siege-ammunition" => "ammunition",
        "fireworks" => "special.fireworks", "payment-currency" => "currency",
        "epicloot-dust" => "magic.epicloot.dust", "epicloot-shards" => "magic.epicloot.shards",
        "epicloot-reagents" => "magic.epicloot.reagents", "epicloot-essence" => "magic.epicloot.essence",
        "epicloot-stones" => "magic.epicloot.stones", "epicloot-socket-materials" => "magic.epicloot.socket-materials",
        "epicloot-unidentified" => "magic.epicloot.unidentified", "epicloot-socket-tools" => "magic.epicloot.socket-tools",
        _ => throw new InvalidDataException("Unsupported loaded item-use method: " + method)
    };

    internal static IReadOnlyList<ItemClassificationEvidence> Build(CatalogSnapshot catalog,
        IReadOnlyList<LoadedSourceFact> facts, IReadOnlyList<MaterialRoute> routes,
        ClassificationSourceSnapshot.ScalarBudget budget, CancellationToken cancellation, ICollection<string>? gaps = null)
    {
        // Keep one ordinal witness per item/category or usage value. All raw consumers remain in the receipt digest.
        var items = new SortedDictionary<string, (SortedDictionary<string, MembershipEvidence> Members, SortedDictionary<string, FacetEvidence> Facets)>(StringComparer.Ordinal);
        (SortedDictionary<string, MembershipEvidence> Members, SortedDictionary<string, FacetEvidence> Facets) Get(string item)
        {
            if (!items.TryGetValue(item, out var evidence))
            {
                budget.Charge(item);
                evidence = (new SortedDictionary<string, MembershipEvidence>(StringComparer.Ordinal), new SortedDictionary<string, FacetEvidence>(StringComparer.Ordinal));
                items.Add(item, evidence);
            }
            return evidence;
        }
        void AddFacet(string item, string dimension, string value, string source, string reason)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!catalog.Items.ContainsKey(item)) return;
            var evidence = Get(item); var key = dimension + ":" + value;
            if (evidence.Facets.ContainsKey(key)) return;
            budget.Charge(item); budget.Charge(dimension); budget.Charge(value); budget.Charge(source); budget.Charge(reason);
            evidence.Facets.Add(key, new FacetEvidence(dimension, value, source, reason));
        }
        void Add(string item, string category, string source, string reason, string? usage = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!catalog.Items.ContainsKey(item)) return;
            var evidence = Get(item);
            if (!evidence.Members.ContainsKey(category))
            {
                budget.Charge(item); budget.Charge(category); budget.Charge(source); budget.Charge(reason);
                evidence.Members.Add(category, new MembershipEvidence(category, source, reason));
            }
            if (usage != null) AddFacet(item, "usage", usage, source, reason);
        }
        bool Material(string item) => catalog.Items.TryGetValue(item, out var fact) &&
            (fact.Value("type") is "Material" or "Misc") && !Edible(fact);
        foreach (var row in facts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (row.Kind is LoadedSourceFactKind.FacetEvidence or LoadedSourceFactKind.Acquisition)
            {
                var acquisition = row.Kind == LoadedSourceFactKind.Acquisition;
                var dimension = acquisition ? "acquisition" : row.Method;
                var value = acquisition ? row.Method : row.Detail;
                if (row.Output.Length == 0 || row.Route.Length == 0 ||
                    !acquisition && dimension is not ("mod" or "related-mod" or "construction-mod" or "rarity" or "eaten-by") ||
                    acquisition && value is not ("purchase" or "production" or "breeding" or "exchange"))
                    throw new InvalidDataException("Unsupported/incomplete loaded facet evidence.");
                if (!row.Eligible || row.Amount <= 0 || !catalog.Items.TryGetValue(row.Output, out var target)) continue;
                // D086 D3: a value the filters can't hold, or static rarity on equipment, skips only this row, named in the
                // receipt's coverage; the item keeps its other evidence.
                if (!ClassificationFilters.IsKnownValue(dimension, value))
                { gaps?.Add(row.Identity + ":gap: " + dimension + " value for " + row.Output + " is not usable; skipped"); continue; }
                if (dimension == "rarity" && ClassificationIndex.IsInstanceRarityType(target.Value("type")))
                { gaps?.Add(row.Identity + ":gap: static rarity on equipment " + row.Output + " needs current instance evidence; skipped"); continue; }
                // All detailed gates remain raw receipt identity. A method facet describes a defined path, not present availability.
                AddFacet(row.Output, dimension, value, Witness(row.Identity), row.Route);
                continue;
            }
            if (row.Kind != LoadedSourceFactKind.ItemUse) continue;
            var category = Category(row.Method); // Reject unsupported methods even on a disabled definition.
            if (row.Output.Length == 0 || row.Route.Length == 0) throw new InvalidDataException("Incomplete loaded item-use evidence.");
            if (!row.Eligible || row.Amount <= 0) continue;
            var usage = row.Method switch { "building" => "building", "planting" => "farming", "fuel" => "fuel",
                "creature-food" => "feed", "saddle" => "equipment", "feast-ingredient" => "food", _ => null };
            var source = Witness(row.Identity);
            var reason = row.Method + (row.Method.StartsWith("epicloot-", StringComparison.Ordinal) ? " definition: " : " consumer: ") + row.Route;
            Add(row.Output, category, source, reason, usage);
            if (row.Method is "epicloot-dust" or "epicloot-shards" or "epicloot-reagents" or "epicloot-essence" or "epicloot-stones" or "epicloot-socket-materials")
                Add(row.Output, "magic.materials", source, reason);
            if (row.Method == "epicloot-socket-tools") Add(row.Output, "equipment.tools", source, reason);
        }
        foreach (var route in routes)
        {
            cancellation.ThrowIfCancellationRequested();
            // The receipt already admitted positive enabled whole alternatives. Missing registrations cannot establish semantic use.
            if (route.Inputs.Any(x => !catalog.Items.ContainsKey(x.Input) || !catalog.Items.ContainsKey(x.Output))) continue;
            foreach (var link in route.Inputs)
            {
                cancellation.ThrowIfCancellationRequested();
                if (link.Kind is not ("smelt" or "cook" or "ferment" or "exchange")) continue;
                var source = Witness(route.Id); var reason = link.Kind + " conversion: " + link.Producer;
                if (Material(link.Input)) Add(link.Input, "materials.processing", source, reason, "processing");
                if (Material(link.Output)) Add(link.Output, "materials.processing", source, reason, "processing");
                var output = catalog.Items[link.Output];
                if (link.Kind == "cook" && Edible(output)) Add(link.Input, "ingredients.food", source, reason, "food");
                if (link.Kind == "ferment" && output.Value("type") == "Consumable")
                {
                    Add(link.Input, "brewing.bases", source, reason, "brewing");
                    Add(link.Output, "meads", source, reason, "brewing");
                }
            }
        }
        return Array.AsReadOnly(items.Select(item => new ItemClassificationEvidence(item.Key,
            item.Value.Members.Values, item.Value.Facets.Values)).ToArray());
    }
    internal static bool FeastIngredient(MembershipEvidence membership) => membership.Category == "ingredients.food" &&
        membership.Source.StartsWith("loaded-use:", StringComparison.Ordinal) &&
        membership.Reason.StartsWith("feast-ingredient consumer: ", StringComparison.Ordinal);

    // Consumer identities may be 1024 characters. Membership source/reason bounds stay smaller without truncating identity.
    private static string Witness(string id) => "loaded-use:" + ClassificationValidation.Hash(writer => writer.Write(id));
    private static bool Edible(CatalogFact fact) => Number(fact, "foodBurnTime") > 0 &&
        (Number(fact, "foodHealth") > 0 || Number(fact, "foodStamina") > 0 || Number(fact, "foodEitr") > 0);
    // D086 D3 (B1.9): a missing or non-finite food stat makes the item not edible here; the index names it.
    private static double Number(CatalogFact fact, string key) => double.TryParse(fact.Value(key), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number) ? number : 0;
}
