using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-07 (D062 Q3): a chest with a rule shows an automatic label from it in its hover text, such as
/// "Ores &amp; metals · priority 1" (no prefix, Q8). Appended, never rebuilt, after BetterUI's postfix (Harmony ID
/// <c>MK_BetterUI</c>; 2.5.13 replaces the text of a non-empty chest), so its "( 12/40 )" and the vanilla hints stay.
/// The label is built once per chest and rule (payload) and category set; per hover frame it costs one payload read
/// and a comparison. Read only. Ross ItemDrawers' drawers have their own hover text and get no label. With
/// ZenHoverItem 1.3.0 (postfix, priority 300, edits and appends) the label comes last, below its summary line;
/// Low priority (200) must stay below 300, or ZenHoverItem's no-access cut would remove it.
/// </summary>
[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class ChestLabels
{
    internal const string BetterUi = "MK_BetterUI";
    private sealed class Cached { internal string Payload = "\0"; internal object? Taxonomy; internal string Line = ""; }
    private static readonly ConditionalWeakTable<Container, Cached> Cache = new ConditionalWeakTable<Container, Cached>();
    private static bool failed;
    internal static ManualLogSource? Log;

    // The game's own private access check (Container.CheckAccess: public, or private and built by you), bound once by
    // name; ZenHoverItem cuts the text on the same check. Missing: the label shows as before (D058).
    private static readonly Func<Container, long, bool>? CheckAccess = Bind();
    private static Func<Container, long, bool>? Bind()
    {
        try
        {
            var method = AccessTools.DeclaredMethod(typeof(Container), "CheckAccess", new[] { typeof(long) });
            return method?.ReturnType == typeof(bool) ? AccessTools.MethodDelegate<Func<Container, long, bool>>(method) : null;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { return null; }
    }
    private static bool Accessible(Container chest, long player) =>
        (!chest.m_checkGuardStone || PrivateArea.CheckAccess(chest.transform.position, 0f, false)) && (CheckAccess == null || CheckAccess(chest, player));

    [HarmonyPostfix, HarmonyAfter(BetterUi), HarmonyPriority(Priority.Low)]
    private static void Append(Container __instance, ref string __result)
    {
        if (failed || string.IsNullOrEmpty(__result)) return;
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.Settings.ShowChestLabels.Value) return;
        try
        {
            var payload = ChestRuleStore.Payload(__instance);
            if (payload.Length == 0) return;
            // A chest you cannot open shows no rule. ZenHoverItem (priority 300, before this) cuts that chest's text
            // to its name and "no access"; the label must not reappear under it (D065: defer to its display).
            var player = Player.m_localPlayer;
            if (!player || !Accessible(__instance, player.GetPlayerID())) return;
            var taxonomy = plugin.RuleTaxonomy;
            var cached = Cache.GetOrCreateValue(__instance);
            if (!ReferenceEquals(cached.Payload, payload) && !StringComparer.Ordinal.Equals(cached.Payload, payload) || !ReferenceEquals(cached.Taxonomy, taxonomy))
            {
                cached.Payload = payload; cached.Taxonomy = taxonomy;
                var read = plugin.RuleEvaluator.Read(payload, taxonomy);
                var label = read.HasRule ? RuleText.Label(read.Rules, taxonomy, RuleControls.ItemName, RuleControls.OpenValueName) : null;
                // Q8: no "YAVSOS:" prefix; the colour sets the line apart.
                cached.Line = label == null ? "" : "\n<color=#E8B04A>" + Plain(label) + "</color>";
            }
            if (cached.Line.Length != 0) __result += cached.Line;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            failed = true; DisplayFaults.Add(DisplayFaults.Labels); // Hover text runs every frame; one warning, then the game's text alone.
            Log?.LogWarning("Chest labels turned off: " + error.GetType().Name + ": " + error.Message);
        }
    }

    // Names come from configuration and other mods; keep them from opening their own rich-text tags.
    private static string Plain(string text) => text.Replace("<", "‹").Replace(">", "›");
}
