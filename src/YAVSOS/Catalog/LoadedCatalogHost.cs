using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Catalog;

/// <summary>Read-only, main-thread adapter. Never loads world/location/room assets.</summary>
internal sealed class LoadedCatalogHost : IDisposable
{
    private readonly CatalogCache cache = new CatalogCache(retries: 3, retrySeconds: 3); // D086 B5.36: as loaded sources.
    private int reportedRetries;
    /// <summary>The catalog gave up for its current inputs (D086 B5.36): the reason, else null.</summary>
    internal string? GaveUp => active && cache.GaveUp ? cache.Diagnostic : null;
    private readonly ManualLogSource log;
    private readonly Func<IEnumerable<CatalogFact?>?> open;
    private ObjectDB? database;
    private List<GameObject>? itemList;
    private List<Recipe>? recipeList;
    private int itemCount, recipeCount;
    private bool active;
    private CatalogState reported;
    internal CatalogSnapshot? Current => cache.Current;

    internal LoadedCatalogHost(ManualLogSource log) { this.log = log; open = OpenCapture; }
    internal void BeginSession(bool isActive)
    {
        active = isActive; database = null; itemList = null; recipeList = null;
        cache.BeginSession(isActive); reported = cache.State;
    }

    internal void Invalidate() => cache.Invalidate();

    internal void Update()
    {
        if (!active) { cache.Tick(open); return; } // Also drains cancelled detached work.
        var db = ObjectDB.instance;
        var items = db ? db.m_items : null; var recipes = db ? db.m_recipes : null;
        if (!ReferenceEquals(database, db) || !ReferenceEquals(itemList, items) || !ReferenceEquals(recipeList, recipes) ||
            itemCount != (items?.Count ?? 0) || recipeCount != (recipes?.Count ?? 0))
        {
            database = db; itemList = items; recipeList = recipes;
            itemCount = items?.Count ?? 0; recipeCount = recipes?.Count ?? 0;
            cache.Invalidate();
        }
        cache.Tick(open);
        if (cache.Retries > reportedRetries) log.LogWarning("Loaded catalog rejected: " + cache.Diagnostic);
        reportedRetries = cache.Retries;
        if (reported == cache.State) return;
        reported = cache.State;
        if (reported == CatalogState.FactsReady)
            log.LogInfo($"Loaded catalog: {cache.Current!.Items.Count} items, {cache.Current.Recipes.Count} recipes, " +
                $"{cache.Current.ScalarBytes} UTF-8 scalar bytes, SHA-256 {cache.Current.Fingerprint}. Classification pending.");
        else if (reported == CatalogState.Failed) log.LogWarning("Loaded catalog rejected: " + cache.Diagnostic);
    }

    private IEnumerable<CatalogFact?>? OpenCapture() => database && itemList != null && recipeList != null && itemCount > 0
        ? Capture(database, itemList, recipeList) : null;

    private IEnumerable<CatalogFact?> Capture(ObjectDB db, List<GameObject> items, List<Recipe> recipes)
    {
        if (itemCount > 16384 || recipeCount > 32768) throw new InvalidDataException("Loaded registry exceeds catalog caps.");
        var expectedItems = items.Count; var expectedRecipes = recipes.Count;
        void Check()
        {
            if (!db || !ReferenceEquals(ObjectDB.instance, db) || !ReferenceEquals(db.m_items, items) ||
                !ReferenceEquals(db.m_recipes, recipes) || items.Count != expectedItems || recipes.Count != expectedRecipes)
                throw new InvalidDataException("Loaded registry changed during capture.");
        }
        yield return Fact(CatalogFactKind.Context, "game", "version", global::Version.GetVersionString());
        for (var i = 0; i < expectedItems; i++)
        {
            Check();
            var prefab = items[i];
            if (!prefab) throw new InvalidDataException("Null/destroyed item registration.");
            var drop = prefab.GetComponent<ItemDrop>();
            if (!drop)
            {
                yield return Fact(CatalogFactKind.Warning, prefab.name, "reason", "registered-without-itemdrop");
                continue;
            }
            var shared = drop.m_itemData?.m_shared;
            if (shared == null) throw new InvalidDataException("Registered ItemDrop lacks shared facts.");
            var icons = shared.m_icons; var hasIcon = false;
            if (icons != null)
            {
                if (icons.Length > 64) throw new InvalidDataException("Item icon array exceeds bounded adapter work.");
                for (var icon = 0; icon < icons.Length; icon++) if (icons[icon]) { hasIcon = true; break; }
            }
            var itemFact = Fact(CatalogFactKind.Item, prefab.name,
                "nameKey", shared.m_name, "type", shared.m_itemType, "skill", shared.m_skillType,
                "hasIcon", hasIcon, "maxStack", shared.m_maxStackSize, "maxQuality", shared.m_maxQuality,
                "quest", shared.m_questItem, "teleportable", shared.m_teleportable, "value", shared.m_value,
                "weight", shared.m_weight, "ammoType", shared.m_ammoType, "foodHealth", shared.m_food,
                "foodStamina", shared.m_foodStamina, "foodEitr", shared.m_foodEitr, "foodBurnTime", shared.m_foodBurnTime,
                "isDrink", shared.m_isDrink, "consumeEffect", shared.m_consumeStatusEffect ? shared.m_consumeStatusEffect.name : "",
                "buildTable", shared.m_buildPieces ? shared.m_buildPieces.name : "");
            var values = new List<KeyValuePair<string, string>>(itemFact.Values);
            CaptureAttack(values, "primary", shared.m_attack);
            CaptureAttack(values, "secondary", shared.m_secondaryAttack);
            yield return new CatalogFact(CatalogFactKind.Item, prefab.name, values);
        }
        var identities = new CatalogRecipeIdentity();
        for (var i = 0; i < expectedRecipes; i++)
        {
            Check();
            var recipe = recipes[i];
            if (!recipe) throw new InvalidDataException("Null/destroyed recipe registration.");
            var name = recipe.name;
            var resources = recipe.m_resources;
            if (resources == null || resources.Length > 256) throw new InvalidDataException("Missing/bounded recipe resources.");
            var definition = Fact(CatalogFactKind.Recipe, name, "output", ItemName(recipe.m_item),
                "enabled", recipe.m_enabled, "outputAmount", recipe.m_amount, "upgradeOnly", recipe.m_noCraftOnlyUpgrade,
                "station", recipe.m_craftingStation ? recipe.m_craftingStation.gameObject.name : "",
                "stationUpgrader", recipe.m_craftingStation && recipe.m_craftingStation.m_upgrader,
                "anyIngredient", recipe.m_requireOnlyOneIngredient);
            var slots = new List<CatalogFact>(resources.Length);
            yield return null; // Keep large recipe definitions cooperative while gathering detached slots.
            for (var resource = 0; resource < resources.Length; resource++)
            {
                Check();
                if (!recipe || !ReferenceEquals(recipe.m_resources, resources)) throw new InvalidDataException("Recipe changed during capture.");
                var requirement = resources[resource];
                if (requirement == null) throw new InvalidDataException("Null recipe requirement; complete recipe cannot be established.");
                slots.Add(Fact(CatalogFactKind.Requirement, resource.ToString(CultureInfo.InvariantCulture),
                    "recipe", name, "input", ItemName(requirement.m_resItem), "amount", requirement.m_amount,
                    "upgradeAmount", requirement.m_amountPerLevel, "upgraderResource", requirement.m_upgraderResource));
                yield return null;
            }
            Check();
            foreach (var fact in identities.Add(definition, slots)) yield return fact;
        }
        Check(); // Includes the final exhaustion step before detached publication.
    }
    private static void CaptureAttack(List<KeyValuePair<string, string>> values, string prefix, Attack? attack)
    {
        // Only consumed projectile definitions need the additional detached impact scalars.
        // No asset loading or recursive prefab traversal; Core decides their semantic meaning.
        if (attack == null || attack.m_attackType != Attack.AttackType.Projectile || !attack.m_consumeItem) return;
        var projectile = attack.m_attackProjectile ? attack.m_attackProjectile.GetComponent<Projectile>() : null;
        if (!projectile) return;
        var impact = projectile.m_spawnOnHit;
        var area = impact ? impact.GetComponent<Aoe>() : null;
        var smoke = impact ? impact.GetComponent<SmokeSpawner>() : null;
        void Add(string key, object value) => values.Add(new KeyValuePair<string, string>(prefix + key,
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        Add("AttackType", attack.m_attackType); Add("ConsumesItem", attack.m_consumeItem);
        Add("Projectiles", attack.m_projectiles); Add("ProjectileBursts", attack.m_projectileBursts);
        Add("RecoversItem", projectile.m_respawnItemOnHit); Add("ProjectileArea", projectile.m_aoe);
        Add("ImpactChance", projectile.m_spawnOnHitChance); Add("ImpactCount", projectile.m_spawnCount);
        Add("ImpactTerrain", projectile.m_spawnOnTerrain); Add("ImpactCharacters", projectile.m_spawnOnCharacters);
        Add("ImpactObjects", projectile.m_spawnOnWearNTear);
        Add("ImpactArea", area && area.enabled && impact!.activeSelf ? area.m_radius : 0f);
        Add("ImpactSmoke", smoke && smoke.enabled && impact!.activeSelf && smoke.m_smokePrefab ? smoke.m_smokePrefab.name : "");
        Add("SmokeInterval", smoke ? smoke.m_interval : 0f);
    }

    private static string ItemName(ItemDrop? item) => item ? item.gameObject.name : "";
    private static CatalogFact Fact(CatalogFactKind kind, string identity, params object[] values)
    {
        var scalars = new List<KeyValuePair<string, string>>(values.Length / 2);
        for (var i = 0; i < values.Length; i += 2)
            scalars.Add(new KeyValuePair<string, string>((string)values[i], Convert.ToString(values[i + 1], CultureInfo.InvariantCulture) ?? ""));
        return new CatalogFact(kind, identity, scalars);
    }

    public void Dispose() { active = false; cache.Dispose(); }
}

internal static class CatalogRegistryPatches
{
    [HarmonyLib.HarmonyPatch(typeof(ObjectDB), "UpdateRegisters"), HarmonyLib.HarmonyPostfix]
    private static void Registered(ObjectDB __instance)
    {
        if (ReferenceEquals(ObjectDB.instance, __instance)) Plugin.Instance?.InvalidateLoadedCatalog();
    }
}
