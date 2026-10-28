using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// Q8/Q10: an item's tooltip ends with its smallest YAVSOS categories and facts worth knowing, such as
/// "Raw food, Animal feed · eaten by Boar, Hen", in orange and without a "YAVSOS:" prefix. A postfix on the game's
/// static <c>ItemData.GetTooltip</c>, which runs after BetterUI's replacing prefix too; ZenUI's detail view and VNEI's
/// item view call the same method (ZenUI's short summary does not). Not added to an appended tooltip (another item's
/// text inside this one). One line per item and answers generation, cached. Read only; one warning and off on failure.
/// </summary>
[HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
    new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) })]
internal static class ItemTooltips
{
    private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal);
    private static long generation = long.MinValue;
    private static object? taxonomy;
    private static bool failed, portalsMatter = true;
    internal static ManualLogSource? Log;

    [HarmonyPostfix, HarmonyPriority(Priority.Low)]
    private static void Append(ItemDrop.ItemData item, bool appending, ref string __result)
    {
        if (appending || item == null || __result == null) return;
        var plugin = Plugin.Instance;
        if (plugin == null) return;
        if (!failed && plugin.Settings.ShowItemCategories.Value)
            try { __result += Line(plugin, item); }
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                failed = true;
                Log?.LogWarning("Item category tooltips turned off: " + error.GetType().Name + ": " + error.Message);
            }
        // P6-23: where the stack would go, only in the inventory screen's own slot tooltip (see WhereTooltip).
        if (ReferenceEquals(item, WhereTooltip.Item) && plugin.RuleEditor?.TooltipLine(item) is { Length: > 0 } where)
            __result += "\n\n" + where;
    }

    private static string Line(Plugin plugin, ItemDrop.ItemData item)
    {
        var prefab = item.m_dropPrefab ? item.m_dropPrefab.name : null;
        var answers = plugin.CategoryAnswers;
        if (prefab == null || answers == null || !answers.CategoriesReady) return "";
        var rules = plugin.RuleTaxonomy;
        var portals = !RuleControls.TeleportAll();
        if (answers.Generation != generation || !ReferenceEquals(rules, taxonomy) || portals != portalsMatter)
        { Lines.Clear(); generation = answers.Generation; taxonomy = rules; portalsMatter = portals; }
        if (Lines.TryGetValue(prefab, out var line)) return line;
        var text = RuleText.ItemLine(answers.Resolve(prefab), rules, RuleControls.OpenValueName, portals);
        line = text.Length == 0 ? "" : "\n<color=#E8B04A>" + text.Replace("<", "‹").Replace(">", "›") + "</color>";
        if (Lines.Count < 4096) Lines[prefab] = line;
        return line;
    }
}

/// <summary>
/// P6-23 (D071): marks the stack whose tooltip the inventory screen last built, so <see cref="ItemTooltips"/> adds
/// "Stash stack: …" / "Clean up: …" to that tooltip only, not to VNEI's, ZenUI's or the YAVSOS item grid's (those show
/// other <c>ItemData</c> objects; the line also needs the explained stack itself). The game rebuilds the hovered slot's
/// tooltip every frame and redraws it when the text changes, so the line appears once the stepwise answer arrives,
/// with no extra tooltip calls. EpicLoot, ZenUI and AzuExtendedPlayerInventory also patch that method. A prefix on the
/// private <c>InventoryGrid.CreateItemTooltip</c>, bound by name and shape; if a game update changes it, only this
/// line is off (D058).
/// P8-17: the mark lasts until another grid builds a tooltip or the inventory closes, not only while
/// <c>CreateItemTooltip</c> runs. ZenUI 1.x keeps each slot's tooltip data and redraws it later in
/// <c>UITooltip.LateUpdate</c> (hover start, Alt for the full view), outside that method; with a mark scoped to the
/// call, those redraws left the line out, and returning to a stack never showed it again because the game's text
/// had not changed.
/// </summary>
[HarmonyPatch]
internal static class WhereTooltip
{
    private static ItemDrop.ItemData? item;
    internal static ItemDrop.ItemData? Item => item != null && InventoryGui.IsVisible() ? item : null;

    private static System.Reflection.MethodBase? TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(InventoryGrid), "CreateItemTooltip", new[] { typeof(ItemDrop.ItemData), typeof(UITooltip) });

    private static bool Prepare() => TargetMethod() != null;

    // First: EpicLoot's prefix (2026-10-23, priority 400) builds the tooltip text itself, so the stack must be marked
    // before any other prefix runs. Marking changes nothing for the other mods.
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(InventoryGrid __instance, ItemDrop.ItemData item)
    {
        var gui = InventoryGui.instance;
        WhereTooltip.item = gui && (ReferenceEquals(__instance, gui.m_playerGrid) || ReferenceEquals(__instance, gui.ContainerGrid)) ? item : null;
    }
}
