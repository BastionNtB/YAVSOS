using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using YAVSOS.Core.Classification;

namespace YAVSOS.Sources;

/// <summary>
/// Loaded producer/acquisition definitions. Acquisition rows are method facets only: merchants, currency, machine sites,
/// build costs, fuel, feed and lures never establish a material origin.
/// </summary>
internal static class LoadedProducerReaders
{
    private const int MaxNestedEntries = 256;

    internal static IEnumerable<LoadedSourceFact?> Read(GameObject prefab, string id, bool active, Action check)
    {
        check();
        if (!prefab) throw new InvalidDataException("Null/destroyed loaded producer definition.");
        var trader = prefab.GetComponent<Trader>(); var incinerator = prefab.GetComponent<Incinerator>();
        var lure = prefab.GetComponent<WispSpawner>(); var collector = prefab.GetComponent<SapCollector>();
        var breeding = prefab.GetComponent<Procreation>();
        if (trader) foreach (var row in Purchases(trader, id, active, check)) yield return row;
        if (incinerator) foreach (var row in Exchanges(incinerator, id, active, check)) yield return row;
        if (lure) foreach (var row in Wisps(lure, id, active, check)) yield return row;
        if (collector) foreach (var row in Sap(collector, id, active, check)) yield return row;
        if (breeding) foreach (var row in Breeding(breeding, id, active, check)) yield return row;
        check();
    }

    private static IEnumerable<LoadedSourceFact?> Purchases(Trader trader, string id, bool active, Action check)
    {
        Alive(trader, check); var entries = trader.m_items;
        if (entries == null) { yield return Coverage(id + ":trade", "gap: missing trader stock list; store faults"); yield break; }
        if (entries.Count > MaxNestedEntries) throw new MalformedDefinitionException("Trader stock bound exceeded.");
        var count = entries.Count;
        void CheckStock()
        {
            check();
            if (!trader || !ReferenceEquals(trader.m_items, entries) || entries.Count != count)
                throw new InvalidDataException("Trader stock changed during capture.");
        }
        for (var i = 0; i < count; i++)
        {
            CheckStock(); var entry = entries[i]; var row = id + ":trade:" + i;
            if (entry == null) throw new MalformedDefinitionException("Null trader stock entry.");
            // World keys are recorded, never treated as current availability or as an origin. StoreGui owns the currency prefab.
            var conditions = "price=" + entry.m_price + "; currency=StoreGui.m_coinPrefab (client store UI; not read, no currency biome)" +
                "; worldKey=" + Clean(entry.m_requiredGlobalKey) + " (recorded, ignored for origin)" +
                "; playerBuyKey=" + Clean(entry.m_buyKey) + "; playerIncrementKey=" + Clean(entry.m_incrementKey);
            if (!entry.m_prefab)
            {
                yield return Coverage(row, (string.IsNullOrEmpty(entry.m_buyKey) ? "gap: missing purchase item; " : "player-key purchase without inventory output; ") + conditions);
                continue;
            }
            var shared = entry.m_prefab.m_itemData?.m_shared;
            var amount = shared == null ? 0 : LoadedProducerSemantics.PurchaseAmount(entry.m_stack, shared.m_maxStackSize, entry.m_price);
            // StoreGui passes this exact name to Inventory.AddItem; clone/configured-prefab fallback cannot prove a purchase.
            var db = ObjectDB.instance;
            var registered = db ? db.GetItemPrefab(entry.m_prefab.name) : null;
            var output = registered && registered.GetComponent<ItemDrop>() ? registered.name : "";
            yield return Acquisition(row, output, id, "purchase",
                conditions + "; stack=" + entry.m_stack + " capped by maxStack=" + (shared?.m_maxStackSize ?? 0), amount,
                active && trader.enabled && amount > 0);
        }
        CheckStock();
    }

    private static IEnumerable<LoadedSourceFact?> Exchanges(Incinerator machine, string id, bool active, Action check)
    {
        Alive(machine, check); var root = id + ":incinerate"; var conversions = machine.m_conversions;
        var wired = machine.m_incinerateSwitch && machine.m_container && machine.m_lightingAOEs && machine.m_leverAnim && machine.GetComponent<ZNetView>();
        var usable = active && machine.enabled && wired && conversions != null;
        if (!wired) yield return Coverage(root + ":wiring", "gap: missing switch/container/effect/lever/view reference; incineration faults");
        if (conversions == null) yield return Coverage(root, "gap: missing conversion list; Awake sort faults");
        else if (conversions.Count > LoadedProducerSemantics.MaxIncineratorConversions) throw new MalformedDefinitionException("Incinerator conversion bound exceeded.");
        var count = conversions?.Count ?? 0;
        var tracked = new (Incinerator.IncineratorConversion? Entry, List<Incinerator.Requirement>? Inputs, int Count)[count];
        var shapes = new IncineratorConversionShape?[count];
        var safe = usable; long total = 0;
        void CheckMachine(int read)
        {
            check();
            if (!machine || !ReferenceEquals(machine.m_conversions, conversions) || (conversions?.Count ?? 0) != count)
                throw new InvalidDataException("Incinerator conversions changed during capture.");
            for (var i = 0; i < read; i++)
            {
                var entry = tracked[i];
                if (!ReferenceEquals(conversions![i], entry.Entry) || entry.Entry != null &&
                    (!ReferenceEquals(entry.Entry.m_requirements, entry.Inputs) || (entry.Inputs?.Count ?? 0) != entry.Count))
                    throw new InvalidDataException("Incinerator requirements changed during capture.");
            }
        }
        for (var c = 0; c < count; c++)
        {
            CheckMachine(c); var entry = conversions![c];
            if (entry == null) { tracked[c] = (null, null, 0); safe = false; yield return Coverage(root + ":" + c, "gap: null conversion; Awake sort faults"); continue; }
            var inputs = entry.m_requirements;
            tracked[c] = (entry, inputs, inputs?.Count ?? 0); total += tracked[c].Count;
            if (inputs == null || total > LoadedProducerSemantics.MaxIncineratorRequirements) { safe = false; yield return null; continue; }
            // A null result is added before RemoveAll and faults afterwards, losing the whole inventory.
            var valid = (bool)entry.m_result; var keys = new List<KeyValuePair<string, int>>(inputs.Count);
            foreach (var input in inputs)
            {
                var shared = input != null && input.m_resItem ? input.m_resItem.m_itemData?.m_shared : null;
                if (shared == null || string.IsNullOrEmpty(shared.m_name) || input!.m_amount <= 0) { valid = false; continue; }
                keys.Add(new KeyValuePair<string, int>(shared.m_name, input.m_amount));
            }
            shapes[c] = valid ? new IncineratorConversionShape(entry.m_priority, entry.m_requireOnlyOneIngredient, keys) : null;
            safe &= valid && shapes[c]!.Safe;
            yield return null;
        }
        if (total > LoadedProducerSemantics.MaxIncineratorRequirements)
            yield return Coverage(root + ":bound", "gap: more than " + LoadedProducerSemantics.MaxIncineratorRequirements + " total requirements; reachability unsupported");
        else if (usable && !safe)
            yield return Coverage(root + ":unsafe", "gap: a conversion can fault or discard items (null/non-positive/empty AND); every conversion unsupported");
        var ready = safe ? Array.ConvertAll(shapes, x => x!) : null;
        for (var c = 0; c < count; c++)
        {
            CheckMachine(count); var (entry, inputs, n) = tracked[c];
            if (entry == null) continue;
            var route = root + ":" + c;
            var output = Name(entry.m_result ? entry.m_result.gameObject : null);
            if (output.Length == 0) { yield return Coverage(route, "gap: missing/unresolved incinerator result"); continue; }
            var basis = ready != null && entry.m_resultAmount > 0;
            var detail = "priority=" + entry.m_priority + " (descending; equal order unspecified); mode=" + (entry.m_requireOnlyOneIngredient ? "one-of" : "all") +
                "; resultAmount=" + entry.m_resultAmount + "; inputs match shared item names; aggregate default result separate";
            var any = false;
            if (n > MaxNestedEntries)
            {
                yield return Coverage(route + ":inputs", "gap: single conversion exceeds requirement bound; no input scan");
                yield return Acquisition(route + ":acquire", output, id, "exchange", detail, entry.m_resultAmount, false);
                continue;
            }
            if (inputs == null || n == 0) yield return Coverage(route + ":inputs", "gap: no requirement inputs");
            else if (!entry.m_requireOnlyOneIngredient)
            {
                var reachable = basis && LoadedProducerSemantics.IncineratorAlternativeReachable(ready!, c, -1);
                for (var r = 0; r < n; r++) { CheckMachine(count); yield return Exchange(route + ":" + r, output, inputs[r], route, detail, reachable); }
                if (basis && !reachable) yield return Coverage(route + ":shadow", "gap: an equal/higher-priority conversion may consume the minimal inputs");
                any = reachable;
            }
            else
                for (var r = 0; r < n; r++)
                {
                    CheckMachine(count); var alternative = route + ":alt:" + r;
                    var reachable = basis && LoadedProducerSemantics.IncineratorAlternativeReachable(ready!, c, r);
                    yield return Exchange(alternative, output, inputs[r], alternative, detail, reachable);
                    if (basis && !reachable) yield return Coverage(alternative + ":shadow", "gap: an equal/higher-priority conversion may consume the minimal input");
                    any |= reachable;
                }
            CheckMachine(count);
            yield return Acquisition(route + ":acquire", output, id, "exchange", detail, entry.m_resultAmount, any);
            yield return null;
        }
        CheckMachine(count);
        if (!machine.m_defaultResult) yield break;
        // Residues are counted across the whole remaining inventory. That is not a single-input material recipe.
        yield return Acquisition(root + ":default", Name(machine.m_defaultResult.gameObject), id, "exchange",
            "aggregate residue: one result per " + machine.m_defaultCost + " remaining units after conversions; no input recipe",
            1, usable && safe && machine.m_defaultCost > 0);
        yield return Coverage(root + ":default-domain", "gap: aggregate residue input domain is not a material route");
    }

    private static LoadedSourceFact Exchange(string id, string output, Incinerator.Requirement? input, string route, string detail, bool eligible)
    {
        var name = input != null && input.m_resItem ? Item(input.m_resItem.gameObject) : "";
        return new LoadedSourceFact(LoadedSourceFactKind.RouteInput, id, output, name.Length == 0 ? Missing(id) : name,
            route, "exchange", detail, amount: input?.m_amount ?? 1, eligible: eligible);
    }

    private static IEnumerable<LoadedSourceFact?> Wisps(WispSpawner lure, string id, bool active, Action check)
    {
        Alive(lure, check); var row = id + ":wisp"; var wisp = lure.m_wispPrefab;
        var conditions = "night=" + lure.m_onlySpawnAtNight + "; maxCover=" + F(lure.m_maxCover) + "; chance=" + F(lure.m_spawnChance) +
            "; maxSpawned=" + lure.m_maxSpawned + "; interval=" + F(lure.m_spawnInterval) + "; lured wisp must be picked; no biome gate in definition";
        GameObject? output = null; var amount = 0; var available = false;
        if (wisp)
        {
            // Root components only; child branches remain the declared capture gap.
            var pick = wisp.GetComponent<Pickable>(); var direct = wisp.GetComponent<ItemDrop>();
            if (pick && pick.m_itemPrefab)
            {
                output = pick.m_itemPrefab; amount = pick.m_amount;
                available = !pick.m_defaultPicked || pick.enabled && pick.m_respawnTimeMinutes > 0 && pick.m_spawnCheck == null;
            }
            else if (direct && !wisp.GetComponent<Character>()) { output = wisp; amount = direct.m_itemData?.m_stack ?? 0; available = true; }
        }
        if (!output) { yield return Coverage(row, "gap: lure output is not a root Pickable/ItemDrop; " + conditions); yield break; }
        yield return Acquisition(row, Name(output), id, "production", conditions, amount, active && lure.enabled && available && amount > 0 &&
            FinitePositive(lure.m_spawnChance) && lure.m_maxSpawned > 0 && lure.m_spawnPoint && lure.m_coverPoint);
        yield return Coverage(row + ":origin", "gap: no environment source; lure site, build cost and night are conditions, not natural origin");
    }

    private static IEnumerable<LoadedSourceFact?> Sap(SapCollector collector, string id, bool active, Action check)
    {
        Alive(collector, check); var row = id + ":sap";
        // UpdateTick accepts any overlapping ResourceRoot; m_mustConnectTo is only a non-null gate, never the matched source.
        var conditions = "overlapping ResourceRoot with level>1 (any root; mustConnectTo=" +
            Clean(collector.m_mustConnectTo ? collector.m_mustConnectTo.name : null) + " is a non-null gate)" +
            "; secPerUnit=" + F(collector.m_secPerUnit) + "; maxLevel=" + collector.m_maxLevel + "; one scaled item per stored level";
        if (!collector.m_spawnItem) { yield return Coverage(row, "gap: missing sap output; " + conditions); yield break; }
        yield return Acquisition(row, Name(collector.m_spawnItem.gameObject), id, "production", conditions, 1,
            active && collector.enabled && collector.m_mustConnectTo && collector.m_spawnPoint && collector.m_maxLevel > 0 && FinitePositive(collector.m_secPerUnit));
        yield return Coverage(row + ":origin", "natural ResourceRoot origin must be established independently by rooted world traversal; any overlapping root, not mustConnectTo identity; collector site/cost is not origin");
    }

    private static IEnumerable<LoadedSourceFact?> Breeding(Procreation breeding, string id, bool active, Action check)
    {
        Alive(breeding, check); var parent = breeding.gameObject; var row = id + ":breed";
        var tameable = parent.GetComponent<Tameable>(); var character = parent.GetComponent<Character>(); var ai = parent.GetComponent<MonsterAI>();
        var foods = ai && ai.m_consumeItems != null ? ai.m_consumeItems.Count : 0;
        // A character parent is hungry unless MonsterAI consumes food; taming also needs MonsterAI and Character unless it starts tamed.
        var fed = !character || foods > 0;
        var tamable = tameable && (tameable.m_startsTamed || ai && character && foods > 0);
        var scene = ZNetScene.instance;
        // Procreate resolves the offspring registration before either output. A missing registration faults every attempt.
        var offspring = breeding.m_offspring && scene ? scene.GetPrefab(global::Utils.GetPrefabName(breeding.m_offspring.name)) : null;
        var partner = breeding.m_seperatePartner; var alone = breeding.m_noPartnerOffspring;
        var separate = (bool)partner;
        var registeredPartner = partner && scene ? scene.GetPrefab(global::Utils.GetPrefabName(partner.name)) : null;
        var partnerReady = !separate || registeredPartner && registeredPartner.name == partner!.name && CanBeReadyPartner(registeredPartner);
        var offspringIsParent = offspring && offspring.name == parent.name;
        var offspringIsPartner = offspring && separate && offspring.name == partner!.name;
        var basis = active && breeding.enabled && tamable && fed && offspring && LoadedProducerSemantics.ProcreationInitiates(breeding.m_pregnancyChance) &&
            LoadedProducerSemantics.ProcreationCompletes(breeding.m_pregnancyDuration);
        var conditions = "parent=" + Clean(parent.name) + "; tamed (startsTamed=" + (tameable && tameable.m_startsTamed) + "); fed by " + foods +
            " MonsterAI foods; not alerted; initiation needs Random.value>" + F(breeding.m_pregnancyChance) +
            "; population<" + breeding.m_maxCreatures + " within " + F(breeding.m_totalCheckRange) +
            "; partner=" + (separate ? Clean(partner!.name) + " x1" : "same prefab x2 including parent") + " ready within " + F(breeding.m_partnerCheckRange) +
            "; lovePoints=" + breeding.m_requiredLovePoints + "; duration=" + F(breeding.m_pregnancyDuration) +
            "s; no biome/environment gate; origin anchor is the parent's natural source";
        // Resolve every definition reference before the first yield.
        var rows = new List<LoadedSourceFact>(4);
        if (!breeding.m_offspring) rows.Add(Coverage(row + ":registration", "gap: missing offspring; Procreate faults"));
        else
        {
            if (!offspring) rows.Add(Coverage(row + ":registration", "gap: offspring is not a registered prefab; Procreate faults"));
            // Without a no-partner output, the partner must be counted at initiation; otherwise it only selects the birth output.
            rows.Add(Offspring(row, offspring ? offspring : breeding.m_offspring, id,
                conditions + (alone ? "; partner present at birth" : "; partner present at initiation"),
                basis && partnerReady && LoadedProducerSemantics.ProcreationCapacity(breeding.m_maxCreatures, !alone, separate, offspringIsParent, offspringIsPartner)));
        }
        if (alone)
            rows.Add(Offspring(row + ":alone", alone, id, conditions + "; no partner present at birth",
                basis && LoadedProducerSemantics.ProcreationCapacity(breeding.m_maxCreatures, false, separate, offspringIsParent, offspringIsPartner)));
        rows.Add(Coverage(row + ":origin", "gap: breeding needs an external parent anchor; parent natural-source join unsupported; no biome"));
        foreach (var fact in rows) yield return fact;
    }

    private static bool CanBeReadyPartner(GameObject partner)
    {
        var reproduction = partner.GetComponent<Procreation>(); var tameable = partner.GetComponent<Tameable>();
        var character = partner.GetComponent<Character>(); var ai = partner.GetComponent<MonsterAI>();
        var food = ai && ai.m_consumeItems != null && ai.m_consumeItems.Count > 0;
        return reproduction && reproduction.enabled && tameable && (!character || food) &&
            (tameable.m_startsTamed || ai && character && food);
    }

    private static LoadedSourceFact Offspring(string id, GameObject output, string producer, string conditions, bool eligible) =>
        output.GetComponent<Character>()
            ? Coverage(id, "creature offspring " + Clean(output.name) + " is not an inventory item; growth/drops unsupported here; " + conditions)
            : Acquisition(id, Name(output), producer, "breeding", conditions, 1, eligible);

    // Callers resolve before yielding; a later destroyed reference must not be dereferenced after resumption.
    private static LoadedSourceFact Acquisition(string id, string name, string producer, string method, string detail, int amount, bool eligible) =>
        name.Length == 0 ? Coverage(id, "gap: unresolved " + method + " inventory output; " + detail) :
            new LoadedSourceFact(LoadedSourceFactKind.Acquisition, id, output: name, route: producer, method: method,
                detail: detail, amount: amount, eligible: eligible && amount > 0);
    private static string Name(GameObject? output) => output ? Item(output!) : "";

    // Same registered inventory identity rule as LoadedSourceHost.
    private static string Item(GameObject prefab)
    {
        var drop = prefab.GetComponent<ItemDrop>(); if (!drop) return "";
        var configured = drop.m_itemData?.m_dropPrefab;
        var db = ObjectDB.instance; if (!db) return "";
        var registered = configured ? db.GetItemPrefab(configured.name) : null;
        if (!registered) registered = db.GetItemPrefab(prefab.name);
        if (!registered)
        {
            var cloneBase = LoadedSourceSemantics.CloneBaseName(prefab.name);
            if (cloneBase != null) registered = db.GetItemPrefab(cloneBase);
        }
        return registered && registered.GetComponent<ItemDrop>() ? registered.name : "";
    }

    // Mod-supplied keys/names are descriptive only; keep details bounded and strictly encodable.
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var text = new StringBuilder(Math.Min(value!.Length, 128));
        foreach (var c in value) { if (text.Length == 128) break; text.Append(char.IsControl(c) || char.IsSurrogate(c) ? '?' : c); }
        return text.ToString();
    }
    private static void Alive(Component component, Action check)
    {
        check();
        if (!component) throw new InvalidDataException("Loaded producer destroyed during capture.");
    }
    private static LoadedSourceFact Coverage(string id, string detail) => new LoadedSourceFact(LoadedSourceFactKind.Coverage, id, detail: detail);
    private static string Missing(string id) => "yavsos:missing-source:" + id;
    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool FinitePositive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
}
