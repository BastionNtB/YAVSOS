using System;
using System.Collections.Generic;
using HarmonyLib;
using YAVSOS.Cleanup;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// X55 (D130 variant A): names the chests and drawers a run's result lists, the way the where-line and the chest label
/// already name them: the open chest, otherwise the game's name and distance (<see cref="CleanupRunner.ChestNamer"/>),
/// then the chest label when the chest has a rule and ShowChestLabels is on. A RossItemDrawers drawer is named by its
/// own hover name ("Item Drawer"), since its chest name may be the base chest's. Asked once per shown line when a
/// result is published (<see cref="RunReport.Detail"/>), so the distance is from where the player is then. Read only.
/// Unity game thread only.
/// </summary>
internal static class ResultPlaces
{
    private static readonly AccessTools.FieldRef<InventoryGui, Container>? CurrentContainer = Field();
    private static AccessTools.FieldRef<InventoryGui, Container>? Field()
    {
        try { return AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer"); }
        catch { return null; } // Without it the open chest is named like any other, by its name and distance.
    }

    /// <summary>A namer over the run's chests by ID; null without a player (the result keeps its single moved line).</summary>
    internal static Func<string, ResultPlace?>? Namer(IReadOnlyDictionary<string, Container> chests)
    {
        var player = Player.m_localPlayer;
        var plugin = Plugin.Instance;
        if (!player || plugin == null) return null;
        var gui = InventoryGui.instance;
        var open = CurrentContainer != null && gui && InventoryGui.IsVisible() ? CurrentContainer(gui) : null;
        var chestName = CleanupRunner.ChestNamer(player, open ? open : null, chests);
        return id =>
        {
            if (!chests.TryGetValue(id, out var chest) || !chest) return null;
            var name = chestName(id);
            if (RossItemDrawersIntegration.IsDrawer(chest))
                return ResultPlace.Of(name == null || name.IsOpen ? name : ChestName.Away(Texts.Escape(DrawerName(chest, name.Name)), name.Meters));
            if (name != null && !name.IsOpen) name = ChestName.Away(Texts.Escape(name.Name), name.Meters);
            return ResultPlace.Of(name, Label(plugin, chest));
        };
    }

    // The chest label as its hover text shows it; none when labels are off or the rule can't be read (the name still shows).
    private static string? Label(Plugin plugin, Container chest)
    {
        try
        {
            var payload = plugin.Settings.ShowChestLabels.Value ? ChestRuleStore.Payload(chest) : "";
            return payload.Length == 0 ? null : ChestLabels.Label(plugin, payload, plugin.RuleTaxonomy);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { return null; }
    }

    // The drawer's own hover name (DrawerComponent implements Hoverable again); the chest's name when it gives none.
    private static string DrawerName(Container drawer, string fallback)
    {
        try
        {
            var hover = ((Hoverable)drawer).GetHoverName();
            return string.IsNullOrWhiteSpace(hover) ? fallback : GameText.Localize(hover);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { return fallback; }
    }
}
