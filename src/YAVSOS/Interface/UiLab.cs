using System.Linq;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// Lab entry points for automated Phase 6 runtime tests (called through the test bridge's debug/call_method), in
/// disposable lab worlds only; never used by gameplay code. Actions are queued like the panel's own clicks, so read
/// results with a later call. Open the chest first with CleanupLab.Open. Unity game thread only.
/// </summary>
internal static class UiLab
{
    private static RuleControls? Rules => Plugin.Instance?.RuleControlsForLab;

    public static string Panel() => Rules is { } r
        ? $"chest={(r.Chest ? r.Chest!.name : "none")}; mode={r.Mode}; categories={r.PickingCategories}; items={r.PickingItems}; filters={r.PickingFilters}; priority={r.Priority}; message={Said(r)}"
        : "no panel";
    // D101: the one message, at its spot (Tab, Clear, Chest, Cleanup), until the next action.
    private static string Said(RuleControls r) => r.Messages.Text.Length == 0 ? "" : $"{r.Messages.Spot}/{r.Messages.Kind}: {r.Messages.Text}";
    /// <summary>D101: point at a category tree row while searching, as the pointer would ("" leaves it).</summary>
    public static string PointRow(string id) { if (Rules is { } r) r.RowPointer(id.Length != 0 ? id : r.RowHovered, id.Length != 0); return "pointed"; }
    public static string ShowCategories() { Rules?.ShowCategories(); return "queued"; }
    public static string ShowFilters() { Rules?.ShowFilters(); return "queued"; }
    public static string ShowItems() { Rules?.ShowItems(); return "queued"; }
    // P6-18 item grid.
    public static string GridSearch(string text) { Rules?.SearchItems(text); return "typed"; }
    public static string GridClick(string prefab)
    {
        var entry = Rules?.GridEntries().FirstOrDefault(x => x.Copies.Contains(prefab));
        if (entry == null) return "not in grid";
        Rules!.CycleMark(entry); return "queued for " + string.Join(",", entry.Copies);
    }
    public static string Grid(int show) => Rules is { } r
        ? string.Join(" ", r.GridEntries().GroupBy(x => x.Section).Select(g => $"{g.Key}={g.Count()}")) + $"; view={ItemGridView.Last?.Count ?? -1}/{ItemGridView.Last?.Pool ?? -1} cells | " +
          string.Join(" ", r.GridEntries().Take(show).Select(x => $"{x.Section}:{x.Prefab}{(x.Copies.Count > 1 ? "×" + x.Copies.Count : "")}{(x.Mark == Core.Interface.GridMark.Always ? "+" : x.Mark == Core.Interface.GridMark.Never ? "-" : "")}{(x.Taken ? "" : "~")}")) +
          $" | always={string.Join(",", r.Includes)} never={string.Join(",", r.Excludes)}"
        : "no panel";
    public static string GridScroll(float fraction) { ItemGridView.Last?.ScrollTo(fraction); return ItemGridView.Last == null ? "no grid" : "scrolled " + fraction; }
    public static string GridTooltip(string prefab) => Rules is { } r && r.GridEntries().FirstOrDefault(x => x.Prefab == prefab) is { } e
        ? r.GridTooltip(e).Topic + " || " + r.GridTooltip(e).Text.Replace("\n", " / ")
        : "not in grid";
    public static string Search(string text) { Rules?.SearchFor(text); return "typed"; }
    public static string SearchResult() => Rules is { } r
        ? $"query={r.Search.Query}; categories={string.Join(",", r.Search.Categories)}; items={string.Join(",", r.Search.Items.Select(x => x.Prefab + "→" + x.Category))}; highlight={r.HighlightStatus()}; lit={r.LitSubject}; chests={string.Join(",", r.HighlightTooltip()?.Text.Split('\n') ?? new string[0])}"
        : "no panel";
    public static string Filter(string dimension, string value) { Rules?.ToggleFilter(dimension, value); return "queued"; }
    public static string Explain(string prefab) { Rules?.ExplainItem(prefab); return "queued"; }
    public static string Explanation() => Rules?.Explanation is { } e
        ? e + (Rules.ExplanationContext.Length == 0 ? "" : " | " + Rules.ExplanationContext)
        : "none";
    // P6-24/P6-23 (D071): the header's marks, the Rule section's chips and note, and the tooltip line for an explained stack.
    public static string Marks() => Rules is { } r
        ? string.Join(" | ", r.Marks().Select(x => x.Name + "=" + x.State + ": " + x.Tooltip.Replace("\n", " / ")))
        : "no panel";
    public static string Chips() => Rules is { } r && Plugin.Instance is { } p
        ? string.Join(", ", r.Chips(p.RuleTaxonomy).Select(x => x.Kind + ":" + x.Text)) + " | message=" + Said(r)
        : "no panel";
    public static string TooltipLine(string prefab)
    {
        var player = Player.m_localPlayer;
        bool Is(ItemDrop.ItemData? x) => x?.m_dropPrefab && x!.m_dropPrefab.name == prefab;
        var item = (player ? player.GetInventory().GetAllItems().FirstOrDefault(Is) : null) ?? Rules?.Chest?.GetInventory()?.GetAllItems().FirstOrDefault(Is);
        return item == null || Rules == null ? "no item" : Rules.TooltipLine(item) is { Length: > 0 } line ? line : "none";
    }
    /// <summary>The game's own slot tooltip for the stack, built as the inventory screen builds it (P6-23 path).</summary>
    public static string SlotTooltip(string prefab)
    {
        var gui = InventoryGui.instance; var player = Player.m_localPlayer;
        if (!gui || !player) return "no inventory";
        bool Is(ItemDrop.ItemData? x) => x?.m_dropPrefab && x!.m_dropPrefab.name == prefab;
        var mine = player.GetInventory().GetAllItems().FirstOrDefault(Is);
        var grid = mine != null ? gui.m_playerGrid : gui.ContainerGrid;
        var item = mine ?? Rules?.Chest?.GetInventory()?.GetAllItems().FirstOrDefault(Is);
        if (item == null || !grid) return "no item";
        var tip = new UnityEngine.GameObject("YAVSOS lab tooltip").AddComponent<UITooltip>();
        try
        {
            HarmonyLib.AccessTools.Method(typeof(InventoryGrid), "CreateItemTooltip").Invoke(grid, new object[] { item, tip });
            var text = tip.m_text;
            var at = text.LastIndexOf("\n\n", System.StringComparison.Ordinal);
            return at < 0 ? "(no where line) " + text.Length + " chars" : text.Substring(at + 2);
        }
        finally { UnityEngine.Object.Destroy(tip.gameObject); }
    }
    public static string WherePatches()
    {
        var method = HarmonyLib.AccessTools.Method(typeof(InventoryGrid), "CreateItemTooltip");
        var info = method == null ? null : HarmonyLib.Harmony.GetPatchInfo(method);
        return info == null ? "not patched" : "prefixes=" + string.Join(",", info.Prefixes.Select(x => x.owner + "/" + x.PatchMethod.Name + "@" + x.priority)) +
            " postfixes=" + string.Join(",", info.Postfixes.Select(x => x.owner + "/" + x.PatchMethod.Name)) + " finalizers=" + string.Join(",", info.Finalizers.Select(x => x.owner)) +
            " transpilers=" + string.Join(",", info.Transpilers.Select(x => x.owner));
    }
    /// <summary>
    /// Lab diagnosis (2026-10-23 lag report): world objects whose prefab this client cannot create (the game's
    /// "Missing prefab hash" warning, logged every frame for each), with position, distance, owner and creator.
    /// </summary>
    public static string MissingPrefabs(float radius)
    {
        var zdos = ZDOMan.instance == null ? null
            : HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsByID")?.GetValue(ZDOMan.instance) as System.Collections.Generic.Dictionary<ZDOID, ZDO>;
        var scene = ZNetScene.instance; var player = Player.m_localPlayer;
        if (zdos == null || !scene || !player) return "not in a world";
        var here = player.transform.position;
        var missing = zdos.Values.Where(x => x.GetPrefab() != 0 && !scene.GetPrefab(x.GetPrefab())).ToList();
        return $"{missing.Count} of {zdos.Count} loaded objects have no prefab here | " + string.Join(" | ", missing
            .Select(x => (Zdo: x, Distance: UnityEngine.Vector3.Distance(here, x.GetPosition())))
            .Where(x => x.Distance <= radius).OrderBy(x => x.Distance).Take(40)
            .Select(x => $"hash={x.Zdo.GetPrefab()} at {x.Zdo.GetPosition()} {x.Distance:0} m owner={x.Zdo.GetOwner()} creator={x.Zdo.GetLong(ZDOVars.s_creator)} " +
                $"items={(x.Zdo.GetString(ZDOVars.s_items).Length > 0)} health={x.Zdo.GetFloat(ZDOVars.s_health, -1)} tamed={x.Zdo.GetBool(ZDOVars.s_tamed)} level={x.Zdo.GetInt(ZDOVars.s_level, -1)} " +
                $"name={x.Zdo.GetString(ZDOVars.s_tamedName)}{x.Zdo.GetString(ZDOVars.s_text)}"));
    }
    /// <summary>
    /// Lab cleanup: destroy the world objects <see cref="MissingPrefabs"/> lists that this lab character built (P6-09's
    /// modded test chests outlived their mods, 2026-10-23). Takes ownership first so the server accepts the removal.
    /// </summary>
    public static string RemoveMissing(float radius)
    {
        var zdos = ZDOMan.instance == null ? null
            : HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsByID")?.GetValue(ZDOMan.instance) as System.Collections.Generic.Dictionary<ZDOID, ZDO>;
        var scene = ZNetScene.instance; var player = Player.m_localPlayer;
        if (zdos == null || !scene || !player) return "not in a world";
        var here = player.transform.position; var me = player.GetPlayerID();
        var doomed = zdos.Values.Where(x => x.GetPrefab() != 0 && !scene.GetPrefab(x.GetPrefab()) && x.GetLong(ZDOVars.s_creator) == me &&
            UnityEngine.Vector3.Distance(here, x.GetPosition()) <= radius).ToList();
        foreach (var zdo in doomed) { zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance!.DestroyZDO(zdo); }
        return "removed " + doomed.Count + ": " + string.Join(", ", doomed.Select(x => x.GetPrefab() + "@" + x.GetPosition()));
    }
    /// <summary>The filters and values the Filters tab offers for the open chest's rule, as the panel computes them.</summary>
    public static string FilterOptions() => Rules is { } r
        ? string.Join(" | ", r.FilterOptions().Select(x => Core.Interface.RuleText.FilterName(x.Dimension) + (x.Applies ? "" : " (n/a)") + ": " +
            string.Join(", ", x.Values.Select(v => v != "unknown" && Core.Classification.ClassificationFilters.Values(x.Dimension) == null
                ? RuleControls.OpenValueName(x.Dimension, v) : Core.Interface.RuleText.ValueName(x.Dimension, v)))))
        : "no panel";
    /// <summary>Scrolls every visible list in the rule panel to a fraction (0 top, 1 bottom), for screenshots.</summary>
    public static string PanelScroll(float fraction)
    {
        var panel = UnityEngine.GameObject.Find("YAVSOS rule panel");
        if (!panel) return "no panel";
        var lists = panel.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(false);
        foreach (var list in lists) list.verticalNormalizedPosition = 1 - UnityEngine.Mathf.Clamp01(fraction);
        return "scrolled " + lists.Length;
    }
    public static string Level(int level) { Rules?.SetLevel(level); return "queued"; }
    public static string Results() => Plugin.Instance?.Results.Latest is { } s
        ? s.Title + " | " + s.Headline + " | " + string.Join(" / ", s.Lines.Select(x => $"{x.Kind} {x.Count} {x.Label}")) + (s.MoreLines > 0 ? $" (+{s.MoreLines})" : "")
        : "none";
    public static string CostStart() { UiCost.Reset(); UiCost.On = true; return "measuring"; }
    public static string CostReport() => UiCost.Report();
    public static string CostStop() { UiCost.On = false; return UiCost.Report(); }
    /// <summary>
    /// Phase 7 Q5: the host's synchronous cost of a join, as the pinned ServerSync does it in its login coroutine's first
    /// step: build YAVSOS's full settings package, then deflate it when over 10,000 bytes. Times the same calls.
    /// </summary>
    public static string JoinSendCost()
    {
        var sync = Plugin.Instance?.SyncForLab;
        var build = HarmonyLib.AccessTools.DeclaredMethod(typeof(ServerSync.ConfigSync), "ConfigsToPackage");
        var configs = HarmonyLib.AccessTools.DeclaredField(typeof(ServerSync.ConfigSync), "allConfigs")?.GetValue(sync) as System.Collections.IEnumerable;
        var values = HarmonyLib.AccessTools.DeclaredField(typeof(ServerSync.ConfigSync), "allCustomValues")?.GetValue(sync);
        if (sync == null || build == null || configs == null || values == null) return "unavailable";
        var bases = configs.Cast<ServerSync.OwnConfigEntryBase>().Select(x => x.BaseConfig).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var package = (ZPackage)build.Invoke(null, new object?[] { bases, values, null, false });
        var built = clock.Elapsed.TotalMilliseconds;
        var raw = package.GetArray();
        clock.Restart();
        var compressed = 0L;
        if (raw.LongLength > 10000)
        {
            var output = new System.IO.MemoryStream();
            using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal)) deflate.Write(raw, 0, raw.Length);
            compressed = output.ToArray().LongLength;
        }
        return $"build {built:0.00} ms, deflate {clock.Elapsed.TotalMilliseconds:0.00} ms; {raw.LongLength} bytes -> {compressed} bytes";
    }

    public static string Mode() => Plugin.Instance?.PolicyStatus ?? "not loaded";
    public static string Hover() { var chest = RuleLab.Target(); return chest ? chest!.GetHoverText() : "no target"; }
}
