using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Integrations;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-07 (D062 Q3): a chest with a rule shows an automatic label from it in its hover text, such as
/// "Ores &amp; metals · priority 1" (no prefix, Q8). D122: a chest the search lit also says why ("Holds Copper ×57",
/// "Accepts Copper"), ruled or not. Appended, never rebuilt, after BetterUI's postfix (Harmony ID
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
    private sealed class Cached { internal string Payload = "\0"; internal object? Taxonomy; internal int Language = -1; internal string Line = ""; }
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
        if (plugin == null) return;
        try
        {
            // ShowChestLabels turns off the rule's label only; the lit line below is the non-colour cue (D122).
            var payload = plugin.Settings.ShowChestLabels.Value ? ChestRuleStore.Payload(__instance) : "";
            // D122: a chest the search lit says why, ruled or not, in its kind's colour. D123's stays mark first: while it
            // glows it owns the chest's colour, so its line matches what the player sees.
            var lit = plugin.Arrivals.Reason(__instance) ?? plugin.Highlight.Reason(__instance);
            if (payload.Length == 0 && lit == null) return;
            // A chest you cannot open shows no rule. ZenHoverItem (priority 300, before this) cuts that chest's text
            // to its name and "no access"; the label must not reappear under it (D065: defer to its display).
            var player = Player.m_localPlayer;
            if (!player || !Accessible(__instance, player.GetPlayerID())) return;
            var why = lit is { } reason ? WhyLine(reason.Kind, reason.Why) : "";
            if (payload.Length == 0) { __result += why; return; }
            var taxonomy = plugin.RuleTaxonomy;
            var cached = Cache.GetOrCreateValue(__instance);
            if (!ReferenceEquals(cached.Payload, payload) && !StringComparer.Ordinal.Equals(cached.Payload, payload) || !ReferenceEquals(cached.Taxonomy, taxonomy)
                || cached.Language != Texts.Generation)
            {
                cached.Payload = payload; cached.Taxonomy = taxonomy; cached.Language = Texts.Generation;
                var label = Label(plugin, payload, taxonomy);
                // Q8: no "YAVSOS:" prefix; the colour sets the line apart.
                cached.Line = label == null ? "" : "\n<color=" + ChestLabelPreview.Color + ">" + label + "</color>";
            }
            if (cached.Line.Length != 0) __result += cached.Line;
            __result += why;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            failed = true; DisplayFaults.Add(DisplayFaults.Labels); // Hover text runs every frame; one warning, then the game's text alone.
            Log?.LogWarning("Chest labels turned off: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// S3 (audit D04): the label a rule payload shows in the chest's hover text, plain text without the colour; null
    /// without a rule. Out of the Harmony postfix so the panel can preview it (X2).
    /// </summary>
    internal static string? Label(Plugin plugin, string payload, ClassificationTaxonomy taxonomy)
    {
        var read = plugin.RuleEvaluator.Read(payload, taxonomy);
        // P10-04: the label's parts are messages, measured and cut in the player's language (case 9).
        return read.HasRule && RuleText.Label(read.Rules, CategoryText.For(taxonomy), RuleControls.ItemName, Texts.Get, RuleControls.OpenValueName) is { } label
            ? Plain(RuleText.Cut(Texts.Get(label), RuleText.MaximumLabel)) : null;
    }

    // D122/D123: the lit chest's "Holds …"/"Accepts …"/"… stays here" line, formatted again only when it, its colour or
    // the language changes (hover text is asked for every frame). B2-06: the highlight keeps a message, not English, so
    // a language change redraws the line; the names in it are already escaped for rich text.
    private static (ChestHighlight.Kind Kind, Message? Why, Color Color, int Language) whyKey;
    private static string whyLine = "";
    private static string WhyLine(ChestHighlight.Kind kind, Message why)
    {
        var color = ChestHighlight.ColorOf(kind);
        if (whyKey.Kind != kind || !ReferenceEquals(whyKey.Why, why) || whyKey.Color != color || whyKey.Language != Texts.Generation)
        {
            whyKey = (kind, why, color, Texts.Generation);
            whyLine = "\n<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + Texts.Get(why) + "</color>";
        }
        return whyLine;
    }

    // Names come from configuration and other mods; keep them from opening their own rich-text tags.
    private static string Plain(string text) => text.Replace("<", "‹").Replace(">", "›");
}
