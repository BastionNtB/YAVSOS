using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Interface;

namespace YAVSOS.Transfers;

/// <summary>
/// X60 spike (D143, YAVSOS-dev#9; docs/design/X60-STOREHOUSE-SPIKE.md): what each container piece takes when it is
/// empty, asked the way X60 would ask. For every piece prefab with a <see cref="Container"/>, it builds an empty
/// <see cref="Inventory"/> with that container's name and size (Storehouse finds a store's filter by that name), then
/// asks <see cref="GameTakes"/> about one unit of every catalog item. No Storehouse types, no world inventory touched.
/// Lab build only; called through the test bridge's debug/call_method. Unity game thread only.
/// </summary>
internal static class StoreLab
{
    /// <summary>
    /// Asks every container piece whose prefab name contains <paramref name="nameContains"/> ("" for all) about every
    /// catalog item. Logs one "X60 store" line per piece (with <paramref name="sample"/> accepted item names, or "all"),
    /// with <paramref name="full"/> also one "X60 accepts" line listing every accepted item, then one "X60 total" line,
    /// which it also returns. The hash is FNV-1a over the sorted accepted prefab names, so clients can compare lists.
    /// </summary>
    public static string Filters(string nameContains, int sample, bool full)
    {
        if (!ObjectDB.instance || !ZNetScene.instance) return "no ObjectDB or ZNetScene";
        var log = GameTakes.Log;
        var total = Stopwatch.StartNew();

        // The catalog: one unit of each item, carrying its drop prefab as inventory items do (Storehouse reads its name).
        var clock = Stopwatch.StartNew();
        var items = new List<ItemDrop.ItemData>();
        foreach (var prefab in ObjectDB.instance.m_items)
        {
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!drop || drop!.m_itemData?.m_shared == null) continue;
            var item = drop.m_itemData.Clone(); item.m_dropPrefab = prefab; item.m_stack = 1;
            items.Add(item);
        }
        items.Sort((a, b) => string.CompareOrdinal(a.m_dropPrefab.name, b.m_dropPrefab.name));
        var catalogMs = clock.Elapsed.TotalMilliseconds;

        var pieces = ZNetScene.instance.m_prefabs.Where(x => x && x.GetComponent<Piece>() &&
                x.name.IndexOf(nameContains ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => (Prefab: x, Container: x.GetComponentInChildren<Container>(true))).Where(x => x.Container)
            .OrderBy(x => x.Prefab.name, StringComparer.Ordinal).ToList();

        var asks = 0; var filtering = 0; var unanswered = 0; double askMs = 0;
        foreach (var (prefab, container) in pieces)
        {
            int width = container.m_width, height = container.m_height;
            if (width <= 0 || height <= 0)
            {
                log?.LogInfo($"X60 store prefab={prefab.name} name={container.m_name} size={width}x{height} skipped=no cells");
                continue;
            }
            clock.Restart();
            var inventory = new Inventory(container.m_name, null, width, height);
            var accepted = new List<string>(); var refused = 0; var none = 0;
            foreach (var item in items)
            {
                asks++;
                switch (GameTakes.Takes(inventory, item))
                {
                    case true: accepted.Add(item.m_dropPrefab.name); break;
                    case false: refused++; break;
                    default: none++; break;
                }
            }
            var ms = clock.Elapsed.TotalMilliseconds; askMs += ms;
            unanswered += none;
            if (refused != 0) filtering++;
            var shown = refused == 0 && none == 0 ? "all" : string.Join(",", accepted.Take(Math.Max(0, sample)));
            log?.LogInfo($"X60 store prefab={prefab.name} name={container.m_name} size={width}x{height} accepted={accepted.Count} refused={refused} " +
                $"unanswered={none} of={items.Count} hash={Hash(accepted)} ms={ms:0.000} sample={shown}");
            if (full && refused != 0) log?.LogInfo($"X60 accepts prefab={prefab.name} items={string.Join(",", accepted)}");
        }
        var line = $"X60 total containers={pieces.Count} filtering={filtering} items={items.Count} asks={asks} unanswered={unanswered} " +
            $"ms={total.Elapsed.TotalMilliseconds:0.0} catalogMs={catalogMs:0.0} askMs={askMs:0.0} perAskUs={(asks == 0 ? 0 : askMs * 1000.0 / asks):0.00} " +
            $"check={(GameTakes.Fault is { } fault ? "off (" + fault + ")" : "on")}";
        log?.LogInfo(line);
        return line;
    }

    // ---- X60 build (D155, D160): the filters YAVSOS reads and uses, for session B.

    /// <summary>
    /// The container filters as YAVSOS holds them now: "X60 filters status=… types=… filters=… dropped=… readMs=… frames=…
    /// asks=… groups=… automatic=on|off priority=… categoryFor=…" (also logged), and with <paramref name="list"/> one
    /// "X60 filter" line per filter (prefab, category, name, members, hash), comparable with the spike's per-store hash.
    /// </summary>
    public static string FilterStatus(bool list)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return "not loaded";
        var filters = plugin.StoreFilters; var set = filters.Current; var (ms, frames, asks, groups) = filters.LastRead;
        var line = $"X60 filters status={filters.Status} types={set.Types} filters={set.Filters.Count} dropped={set.Dropped} generation={set.Generation} " +
            $"readMs={ms:0.0} frames={frames} asks={asks} groups={groups} automatic={(plugin.Settings.StoreAutomaticRules.Value ? "on" : "off")} " +
            $"priority={plugin.Settings.StoreDefaultPriority.Value} taxonomy={plugin.RuleTaxonomy.Categories.Count}";
        var log = GameTakes.Log;
        log?.LogInfo(line);
        if (list)
            foreach (var filter in set.Filters)
                log?.LogInfo($"X60 filter prefab={filter.Prefab} category={filter.Category} name={filter.Name} members={filter.Count} " +
                    $"hash={Hash(filter.Members.OrderBy(x => x, StringComparer.Ordinal))}");
        return line;
    }

    /// <summary>Reads the filters again now, as when the server's settings arrive.</summary>
    public static string Reread() { Plugin.Instance?.StoreFilters.Invalidate(); return "reread queued"; }

    /// <summary>
    /// The open or selected chest (RuleLab's target): "X60 rule chest=… prefab=… filter=… automatic=yes|no stored=… level=…"
    /// and, for each item in <paramref name="items"/> (comma separated prefabs), "item:decision" as every reader sees it
    /// (the AzuAutoStore hook's decision, narrowed by the filter) and "filter=yes|no".
    /// </summary>
    public static string Rule(string items)
    {
        var plugin = Plugin.Instance; var chest = RuleLab.Target();
        if (plugin == null || !chest) return "no target";
        var (payload, filter) = plugin.RuleOf(chest!);
        var stored = ChestRuleStore.Payload(chest!);
        var rules = plugin.RuleEvaluator.Read(payload, plugin.RuleTaxonomy).Rules;
        var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(ChestRuleStore.View(chest!)?.GetZDO()?.GetPrefab() ?? 0) : null;
        var parts = new List<string>();
        foreach (var item in (items ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
        {
            var data = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(item)?.GetComponent<ItemDrop>()?.m_itemData?.Clone() : null;
            if (data != null) data.m_dropPrefab = ObjectDB.instance!.GetItemPrefab(item);
            parts.Add(item + ":" + (data == null ? "unknown" : Integrations.AzuAutoStoreIntegration.Decide(chest, data).ToString()) +
                ":filter=" + (filter == null ? "none" : filter.Takes(item) ? "yes" : "no"));
        }
        return $"X60 rule chest={GameText.Localize(chest!.m_name)} prefab={(prefab ? prefab!.name : "?")} filter={filter?.Category ?? "none"} " +
            $"automatic={(StringComparer.Ordinal.Equals(stored, payload) ? "no" : "yes")} " +
            $"stored={(stored.Length == 0 ? "none" : "yes")} level={(rules == null ? "-" : ChestPriority.Level(rules.Priority).ToString())} " +
            $"categories={(rules == null ? "-" : string.Join(",", rules.FullRules.SelectMany(x => x.Categories)))} | " + string.Join(" ", parts);
    }

    // FNV-1a (32-bit) over the names, each followed by a comma, in their (ordinal) catalog order.
    private static string Hash(IEnumerable<string> names)
    {
        var hash = 2166136261u;
        foreach (var name in names)
            foreach (var b in Encoding.UTF8.GetBytes(name + ","))
                hash = (hash ^ b) * 16777619u;
        return hash.ToString("x8");
    }
}
