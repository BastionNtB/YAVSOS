using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

public sealed class ClassificationIndex
{
    public const string Version = "yavsos-classifier-12";
    internal static bool IsInstanceRarityType(string type) => type is "OneHandedWeapon" or "TwoHandedWeapon" or "TwoHandedWeaponLeft" or
        "Bow" or "Shield" or "Helmet" or "Chest" or "Legs" or "Shoulder" or "Hands" or "Utility" or "Trinket" or "Tool" or "Torch";
    public string InputFingerprint { get; }
    public string Fingerprint { get; }
    public ClassificationTaxonomy Taxonomy { get; }
    public IReadOnlyDictionary<string, ClassificationResult> Items { get; }
    public int Work { get; }
    /// <summary>
    /// Rows with malformed facts, each failing only itself (D086 D3): an item that falls back to Misc, or a recipe that gives
    /// no evidence, with the reason. The host logs them (D087).
    /// </summary>
    public IReadOnlyList<string> Skipped { get; }
    private ClassificationIndex(string inputFingerprint, string fingerprint, ClassificationTaxonomy taxonomy,
        SortedDictionary<string, ClassificationResult> items, int work, IReadOnlyList<string> skipped)
    {
        InputFingerprint = inputFingerprint; Fingerprint = fingerprint; Taxonomy = taxonomy;
        Items = new ReadOnlyDictionary<string, ClassificationResult>(items); Work = work; Skipped = skipped;
    }

    /// <summary>Detached whole-index build. Caller may run it on a worker; publication/readiness is a separate boundary.</summary>
    public static ClassificationIndex Build(CatalogSnapshot catalog, ClassificationRuleSet rules,
        IEnumerable<ItemClassificationEvidence>? evidence = null, string sourceIdentity = "none", int maxWork = 5000000,
        CancellationToken cancellation = default, ItemProfileScope? profileScope = null)
    {
        if (catalog == null || rules == null) throw new ArgumentNullException(nameof(catalog));
        ClassificationValidation.Text(sourceIdentity, 128);
        profileScope ??= ItemProfileScope.Build(catalog, cancellation: cancellation);
        if (profileScope.CatalogFingerprint != catalog.Fingerprint) throw new InvalidDataException("Profile scope does not match loaded facts.");
        var work = new ClassificationWork(maxWork, cancellation); work.Spend();
        var supplementary = new SortedDictionary<string, ItemClassificationEvidence>(StringComparer.Ordinal);
        var evidenceRows = 0; var scalarBytes = 0;
        void Charge(string value) { scalarBytes = checked(scalarBytes + System.Text.Encoding.UTF8.GetByteCount(value)); if (scalarBytes > 32 * 1024 * 1024) throw new InvalidOperationException("Classification scalar bound exceeded."); }
        void Evidence(params string[] values) { work.Spend(); if (++evidenceRows > 262144) throw new InvalidOperationException("Classification evidence bound exceeded."); foreach (var value in values) Charge(value); }
        var supplementRows = ClassificationValidation.Bounded(evidence ?? Array.Empty<ItemClassificationEvidence>(), 16384);
        cancellation.ThrowIfCancellationRequested();
        foreach (var row in supplementRows)
        {
            work.Spend();
            if (row == null || !catalog.Items.ContainsKey(row.Prefab) || supplementary.ContainsKey(row.Prefab))
                throw new ArgumentException("Supplement must reference a unique actually registered item.");
            foreach (var membership in row.Memberships) { rules.Taxonomy.Require(membership.Category); Evidence(membership.Category, membership.Source, membership.Reason); }
            foreach (var facet in row.Facets) Evidence(facet.Dimension, facet.Value, facet.Source, facet.Reason);
            Charge(row.Prefab); supplementary.Add(row.Prefab, row);
        }
        var inputHash = ClassificationValidation.Hash(writer => {
            writer.Write(Version); writer.Write(profileScope.Fingerprint); writer.Write(DefaultItemSemantics.Version); writer.Write(catalog.Fingerprint); writer.Write(rules.Fingerprint); writer.Write(sourceIdentity); writer.Write(supplementary.Count);
            foreach (var row in supplementary.Values)
            {
                work.Spend(); writer.Write(row.Prefab);
                var memberships = row.Memberships.OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray();
                writer.Write(memberships.Length);
                foreach (var item in memberships) { work.Spend(); writer.Write(item.Category); writer.Write(item.Source); writer.Write(item.Reason); }
                var facets = row.Facets.OrderBy(x => x.Dimension, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
                    .ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray();
                writer.Write(facets.Length);
                foreach (var item in facets) { work.Spend(); writer.Write(item.Dimension); writer.Write(item.Value); writer.Write(item.Source); writer.Write(item.Reason); }
            }
        });
        var facts = new Dictionary<string, ItemFacts>(StringComparer.Ordinal);
        // D086 D3: one item's malformed facts fail only that item (Misc, with the reason), not the index.
        var malformed = new SortedDictionary<string, (CatalogFact Row, string Reason)>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var row in catalog.Items.Values)
        {
            work.Spend();
            try { facts.Add(row.Identity, new ItemFacts(row)); }
            catch (InvalidDataException error) { malformed.Add(row.Identity, (row, error.Message)); }
        }
        var uses = new Dictionary<string, List<CraftingUse>>(StringComparer.Ordinal);
        var upgrades = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var recipe in catalog.Recipes.Values)
        try
        {
            work.Spend();
            var enabled = Boolean(recipe, "enabled"); var upgrade = Boolean(recipe, "upgradeOnly");
            var refiner = Boolean(recipe, "stationUpgrader"); var amount = Integer(recipe, "outputAmount");
            _ = Boolean(recipe, "anyIngredient");
            if (!enabled || amount <= 0 || !facts.TryGetValue(recipe.Value("output"), out var output)) continue;
            if (!catalog.Requirements.TryGetValue(recipe.Identity, out var requirements)) continue;
            // The game's explicit refiner-resource flag distinguishes tokens from ordinary quality costs.
            // Upgrade-only recipes are valid evidence here; no station/ingredient family propagation.
            if (output.MaxQuality > 1)
                foreach (var requirement in requirements)
                {
                    work.Spend();
                    var initial = Integer(requirement, "amount"); var perLevel = Integer(requirement, "upgradeAmount");
                    if (!Boolean(requirement, "upgraderResource") || initial < 0 || perLevel < 0 ||
                        (long)initial + perLevel <= 0 || (float)initial + (float)perLevel >= 2147483648f ||
                        !facts.TryGetValue(requirement.Value("input"), out var input) ||
                        input.Type is not ("Material" or "Misc") || input.Edible || upgrades.ContainsKey(input.Prefab)) continue;
                    Evidence("materials.upgrades", "loaded-recipe:upgrader", recipe.Identity);
                    upgrades.Add(input.Prefab, recipe.Identity);
                }
            if (upgrade || refiner) continue;
            supplementary.TryGetValue(output.Prefab, out var outputEvidence);
            var brewingBase = outputEvidence?.Memberships.Any(x => x.Category == "brewing.bases") == true;
            var foodPreparation = outputEvidence?.Memberships.Any(LoadedItemSemantics.FeastIngredient) == true;
            foreach (var requirement in requirements)
            {
                work.Spend();
                var initial = Integer(requirement, "amount"); var refinement = Boolean(requirement, "upgraderResource");
                _ = Integer(requirement, "upgradeAmount");
                if (initial <= 0 || refinement || !facts.TryGetValue(requirement.Value("input"), out var input)) continue;
                if (!uses.TryGetValue(input.Prefab, out var memberships)) uses.Add(input.Prefab, memberships = new List<CraftingUse>());
                var reason = recipe.Identity;
                Evidence("usage", "crafting", "loaded-recipe", reason);
                memberships.Add(new CraftingUse(recipe.Identity, output.Edible || foodPreparation,
                    output.Type == "Consumable" && output.ConsumeEffect || brewingBase, IsBombOutput(output)));
            }
        }
        // A malformed recipe or requirement gives no further evidence; the rest of the index stands (D086 D3).
        catch (InvalidDataException error) { skipped.Add("recipe " + recipe.Identity + ": " + error.Message); }
        var results = new SortedDictionary<string, ClassificationResult>(StringComparer.Ordinal);
        foreach (var item in facts.Values)
        {
            if (profileScope.Excluded.ContainsKey(item.Prefab)) continue;
            work.Spend(); Charge(item.Prefab);
            var memberships = new List<MembershipEvidence>(); var facets = new List<FacetEvidence>();
            void Add(string category, string reason, string source = "loaded-item")
            {
                rules.Taxonomy.Require(category); Evidence(category, source, reason); memberships.Add(new MembershipEvidence(category, source, reason));
            }
            void Facet(string dimension, string value, string reason, string source)
            {
                Evidence(dimension, value, source, reason); facets.Add(new FacetEvidence(dimension, value, source, reason));
            }
            foreach (var semantic in DefaultItemSemantics.Memberships(item.Prefab, item.Type))
                Add(semantic.Category, semantic.Reason, semantic.Source);
            if (item.Weapon)
            {
                Add("weapons", "Weapon item type: " + item.Type);
                var group = item.Skill switch { "Swords" => "swords", "Axes" => "axes", "Clubs" => "clubs", "Spears" => "spears", "Polearms" => "polearms",
                    "Knives" => "knives", "Bows" => "bows", "Crossbows" => "crossbows", "Unarmed" => "fists", "ElementalMagic" or "BloodMagic" => "magic", _ => null };
                if (group != null) Add("weapons." + group, "Weapon-context skill: " + item.Skill);
            }
            if (item.Armor)
            {
                Add("equipment.armor", "Armor item type: " + item.Type);
                var slot = item.Type switch { "Helmet" => "head", "Chest" => "chest", "Legs" => "legs", "Shoulder" => "capes", _ => null };
                if (slot != null) Add("equipment.armor." + slot, "Armor slot: " + item.Type);
            }
            if (item.Type == "Shield") Add("equipment.shields", "Shield item type.");
            if (item.Type is "Utility" or "Trinket") Add("equipment.accessories", "Accessory item type.");
            if (item.Type is "Tool" or "Torch" || item.BuildTool || item.Weapon && item.Skill is "Pickaxes" or "Farming") Add("equipment.tools", "Tool/build-table or contextual farming/mining facts.");
            if (item.Type is "Ammo" or "AmmoNonEquipable")
            {
                // Fishing bait is its own root, not ammunition (D054).
                if (item.AmmoType == "$item_fishingbait") Add("fishing.bait", "Ammunition-context identifier: " + item.AmmoType);
                else
                {
                    Add("ammunition", "Ammunition item type.");
                    var group = item.AmmoType switch { "$ammo_arrows" or "arrow" => "ammunition.arrows", "$ammo_bolts" => "ammunition.bolts", "$ammo_turretbolt" => "ammunition.turret", _ => null };
                    if (group != null) Add(group, "Ammunition-context identifier: " + item.AmmoType);
                }
            }
            if (item.Edible)
            {
                Add("food", "Positive food duration and stats.");
                var stat = FoodStat.Main(item.Health, item.Stamina, item.Eitr);
                Add("food." + stat, "Main food stat by the game's fork-icon rule (D064).");
                // The same answer as a filter, so it combines with any category (P6-07b).
                Facet("food-stat", stat, "Main food stat by the game's fork-icon rule (D064).", "loaded-item");
            }
            // The game's own portal flag; an older catalog without it leaves the filter Unknown.
            if (item.Teleportable != null) Facet("teleportable", item.Teleportable.Value ? "yes" : "no", "Item teleportable flag.", "loaded-item");
            if (item.Type == "Consumable" && item.ConsumeEffect) Add("meads", "Consumable with a consume status effect.");
            var paymentCurrency = supplementary.TryGetValue(item.Prefab, out var payment) && payment.Memberships.Any(m => m.Category == "currency");
            if (item.Value > 0 && !paymentCurrency) Add("valuables", "Positive base-game value; trade mod compatibility is separate.");
            if (item.Quest) Add("special.quest", "Quest flag; independent of movement eligibility.");
            if (item.Type == "Trophy") Add("trophies", "Trophy item type.");
            if (item.Type == "Fish") Add("fish", "Fish item type.");
            if (item.Type == "Customization") Add("catalog.customization", "Customization registration; catalog-only view.");
            if (upgrades.TryGetValue(item.Prefab, out var upgradeRecipe))
                Add("materials.upgrades", upgradeRecipe, "loaded-recipe:upgrader");
            if (uses.TryGetValue(item.Prefab, out var crafting))
                foreach (var use in crafting)
                {
                    Facet("usage", "crafting", use.Recipe, "loaded-recipe:creation");
                    if (item.Type is "Material" or "Misc" && !item.Edible) Add("materials.crafting", use.Recipe, "loaded-recipe:creation");
                    if (item.Type is "Material" or "Misc" && !item.Edible && use.Bomb) Add("ingredients.bombs", use.Recipe, "loaded-recipe:creation");
                    if (use.Food) Add("ingredients.food", use.Recipe, "loaded-recipe:creation");
                    if (use.Mead) Add("ingredients.brewing", use.Recipe, "loaded-recipe:creation");
                }
            if (supplementary.TryGetValue(item.Prefab, out var extra))
            {
                memberships.AddRange(extra.Memberships); facets.AddRange(extra.Facets);
            }
            var selected = rules.Select(item.Prefab, work);
            if (selected != null)
            {
                var source = "policy:" + selected.Match + ":" + selected.Selector;
                if (selected.Operation == OverrideOperation.Replace) memberships.Clear();
                if (selected.Operation == OverrideOperation.Remove) memberships.RemoveAll(x => selected.Memberships.Contains(x.Category, StringComparer.Ordinal));
                else foreach (var category in selected.Memberships) Add(category, "Explicit " + selected.Operation + " membership.", source);
                foreach (var edit in selected.Facets)
                {
                    if (edit.Dimension == "rarity" && item.InstanceRarity) throw new ArgumentException("Equipment rarity needs current instance evidence, not a prefab override.");
                    if (edit.Operation == OverrideOperation.Replace) facets.RemoveAll(x => x.Dimension == edit.Dimension);
                    if (edit.Operation == OverrideOperation.Remove) facets.RemoveAll(x => x.Dimension == edit.Dimension && edit.Values.Contains(x.Value, StringComparer.Ordinal));
                    else foreach (var value in edit.Values) Facet(edit.Dimension, value, "Explicit " + edit.Operation + " facet.", source);
                }
            }
            if (item.InstanceRarity && facets.Any(x => x.Dimension == "rarity")) throw new ArgumentException("Equipment cannot receive static prefab rarity.");
            if (facets.Any(x => x.Dimension is "upgraded" or "damaged" or "upgradable" or "quality")) throw new ArgumentException("Item condition is current instance evidence, not a prefab fact.");
            if (memberships.Count == 0) Add("misc", "No supported remaining membership.", "fallback");
            var primary = selected?.Primary ?? memberships.Select(x => x.Category).Distinct(StringComparer.Ordinal)
                .OrderBy(x => rules.Taxonomy.Categories[x].DisplayRank).ThenBy(x => x, StringComparer.Ordinal).First();
            if (!memberships.Any(x => x.Category == primary)) throw new ArgumentException("Explicit primary is not a final membership.");
            // Closure follows the display choice, so the primary category is unchanged (D051).
            var closed = CategoryClosure.Apply(memberships, rules.Taxonomy);
            for (var i = memberships.Count; i < closed.Count; i++) Evidence(closed[i].Category, closed[i].Source, closed[i].Reason);
            results.Add(item.Prefab, new ClassificationResult(item.Prefab, primary, item.Quest, item.Icon, item.Type == "Customization", item.InstanceRarity,
                selected == null ? null : selected.Match + ":" + selected.Selector, closed, facets));
        }
        foreach (var bad in malformed)
        {
            if (profileScope.Excluded.ContainsKey(bad.Key)) continue;
            work.Spend(); Charge(bad.Key);
            var reason = "Malformed catalog facts: " + bad.Value.Reason;
            if (reason.Length > 256) reason = reason.Substring(0, 256);
            Evidence("misc", "fallback", reason);
            var memberships = CategoryClosure.Apply(new[] { new MembershipEvidence("misc", "fallback", reason) }, rules.Taxonomy);
            results.Add(bad.Key, new ClassificationResult(bad.Key, "misc", Lenient(bad.Value.Row, "quest"), Lenient(bad.Value.Row, "hasIcon"),
                bad.Value.Row.Value("type") == "Customization", false, null, memberships, Array.Empty<FacetEvidence>()));
            skipped.Add("item " + bad.Key + ": " + bad.Value.Reason);
        }
        var hash = ClassificationValidation.Hash(writer => {
            writer.Write(Version); writer.Write(inputHash); writer.Write(results.Count);
            foreach (var item in results.Values)
            {
                work.Spend(); writer.Write(item.Prefab); writer.Write(item.Primary); writer.Write(item.Quest); writer.Write(item.HasIcon);
                writer.Write(item.CatalogOnly); writer.Write(item.InstanceRarity); writer.Write(item.SelectedOverride ?? "");
                writer.Write(item.Memberships.Count);
                foreach (var membership in item.Memberships) { work.Spend(); writer.Write(membership.Category); writer.Write(membership.Source); writer.Write(membership.Reason); }
                writer.Write(item.Facets.Count);
                foreach (var facet in item.Facets) { work.Spend(); writer.Write(facet.Dimension); writer.Write(facet.Value); writer.Write(facet.Source); writer.Write(facet.Reason); }
            }
        });
        cancellation.ThrowIfCancellationRequested();
        return new ClassificationIndex(inputHash, hash, rules.Taxonomy, results, work.Used, skipped.AsReadOnly());
    }

    private sealed class ItemFacts
    {
        internal string Prefab, Type, Skill, AmmoType;
        internal bool Quest, Icon, BuildTool, ConsumeEffect, Weapon, Armor, InstanceRarity, Edible, AreaThrowable;
        internal bool? Teleportable;
        internal int Value, MaxQuality;
        internal double Health, Stamina, Eitr;
        internal ItemFacts(CatalogFact row)
        {
            Prefab = row.Identity; Type = row.Value("type"); Skill = row.Value("skill"); AmmoType = row.Value("ammoType");
            ClassificationValidation.Text(Type, 128);
            Quest = Boolean(row, "quest"); Icon = Boolean(row, "hasIcon"); Value = Integer(row, "value");
            MaxQuality = row.Value("maxQuality").Length == 0 ? 1 : Integer(row, "maxQuality");
            Health = Number(row, "foodHealth"); Stamina = Number(row, "foodStamina"); Eitr = Number(row, "foodEitr");
            Edible = Number(row, "foodBurnTime") > 0 && (Health > 0 || Stamina > 0 || Eitr > 0);
            BuildTool = row.Value("buildTable").Length > 0; ConsumeEffect = row.Value("consumeEffect").Length > 0;
            Weapon = Type is "OneHandedWeapon" or "TwoHandedWeapon" or "TwoHandedWeaponLeft" or "Bow";
            Armor = Type is "Helmet" or "Chest" or "Legs" or "Shoulder" or "Hands";
            InstanceRarity = IsInstanceRarityType(Type);
            Teleportable = row.Value("teleportable").Length == 0 ? null : Boolean(row, "teleportable");
            AreaThrowable = BombAttack(row, "primary") || BombAttack(row, "secondary");
        }
    }
    private static bool BombAttack(CatalogFact row, string prefix)
    {
        if (row.Value(prefix + "AttackType").Length == 0) return false;
        return row.Value(prefix + "AttackType") == "Projectile" && Boolean(row, prefix + "ConsumesItem") &&
            Integer(row, prefix + "Projectiles") > 0 && Integer(row, prefix + "ProjectileBursts") > 0 &&
            !Boolean(row, prefix + "RecoversItem") && (Number(row, prefix + "ProjectileArea") > 0 ||
                Number(row, prefix + "ImpactChance") > 0 && Integer(row, prefix + "ImpactCount") > 0 &&
                (Number(row, prefix + "ImpactArea") > 0 || ConfiguredSmoke(row, prefix)));
    }
    private static bool ConfiguredSmoke(CatalogFact row, string prefix)
    {
        if (row.Value(prefix + "ImpactSmoke").Length == 0) return false;
        // Zero and negative finite intervals emit each update; only malformed/nonfinite data rejects.
        _ = Number(row, prefix + "SmokeInterval");
        return true;
    }
    // Six creature-summoning throwables still need reviewed semantics: a creature spawn
    // alone does not establish an explosive. Ordinary area/smoke throwables need no identities.
    private static bool IsBombOutput(ItemFacts item) => item.AreaThrowable && item.Type is "OneHandedWeapon" or "Material" or "Misc" ||
        item.Type == "OneHandedWeapon" && item.Prefab is
        "BombBlob_Poison" or "BombBlob_PoisonElite" or "BombBlob_Tar" or "BombBlob_Lava" or
        "BombBlob_Frost" or "BombBlob_Morkhalla";

    private sealed class CraftingUse
    {
        internal string Recipe { get; }
        internal bool Food { get; }
        internal bool Mead { get; }
        internal bool Bomb { get; }
        internal CraftingUse(string recipe, bool food, bool mead, bool bomb) { Recipe = recipe; Food = food; Mead = mead; Bomb = bomb; }
    }
    private static bool Lenient(CatalogFact row, string key) => bool.TryParse(row.Value(key), out var value) && value;
    private static bool Boolean(CatalogFact row, string key) => bool.TryParse(row.Value(key), out var value) ? value : throw new InvalidDataException("Invalid/missing " + key + " in " + row.Identity);
    private static int Integer(CatalogFact row, string key) => int.TryParse(row.Value(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException("Invalid/missing " + key + " in " + row.Identity);
    private static double Number(CatalogFact row, string key) => double.TryParse(row.Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
        ? value : throw new InvalidDataException("Invalid/missing finite " + key + " in " + row.Identity);
}
