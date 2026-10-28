using System;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// The panel's one sentence for a <see cref="PlacementExplanation"/> (P6-05, simplified in the R4 review): what the
/// stash key or "Clean up nearby" would do with the stack, and a short reason only when it stays. The competing chests
/// are not shown (maintainer, 2026-10-23: the list was too technical for players). Read only.
/// </summary>
public static class ExplanationView
{
    public const int MaximumText = 160;

    /// <param name="chestName">"this chest" for the open chest, else a name such as "Reinforced chest 5 m away".</param>
    /// <param name="azuStores">
    /// AzuAutoStore does the storing (D070): what no ruled chest takes goes by Azu's own settings, so the stack is not
    /// simply kept.
    /// </param>
    public static string Sentence(PlacementExplanation explanation, bool cleanup, Func<string, string>? chestName = null, Func<string, string>? itemName = null,
        bool azuStores = false)
    {
        if (explanation == null) throw new ArgumentNullException(nameof(explanation));
        var item = Shown(itemName?.Invoke(explanation.Prefab), explanation.Prefab);
        if (explanation.Outcome == PlacementOutcome.Goes && explanation.ChosenId != null)
        {
            var chest = Shown(chestName?.Invoke(explanation.ChosenId), "a chest nearby");
            if (chest != "this chest") chest = "the " + chest;
            return Bound(cleanup ? "Clean up nearby would move " + item + " to " + chest + "." : "The store key would put " + item + " in " + chest + ".");
        }
        var reason = explanation.Outcome switch
        {
            PlacementOutcome.NoRoom => "the chests that take it are full",
            PlacementOutcome.NoChest => "no chest nearby takes it",
            PlacementOutcome.Blocked when explanation.Blocked != null => CleanupReport.Describe(explanation.Blocked.Value),
            PlacementOutcome.Stays => "it belongs here",
            PlacementOutcome.Kept => "it is protected or in use",
            PlacementOutcome.NotReady => "categories are still loading",
            PlacementOutcome.OptedOut => "this chest is set to \"Don't clean up\"",
            PlacementOutcome.Skipped => CleanupReport.Describe(CleanupReason.ChestLimit),
            _ => ""
        };
        if (!cleanup && azuStores && explanation.Outcome is PlacementOutcome.NoChest or PlacementOutcome.NoRoom or PlacementOutcome.Blocked)
        {
            var why = explanation.Outcome == PlacementOutcome.NoChest ? "No chest rule nearby takes " + item
                : explanation.Outcome == PlacementOutcome.NoRoom ? "The chests whose rules take " + item + " are full"
                : "No ruled chest can take " + item + (reason.Length == 0 ? "" : " (" + reason + ")");
            return Bound(why + ", so AzuAutoStore's own settings decide where the store key puts it.");
        }
        var result = cleanup ? "Clean up nearby would leave " + item + " in this chest" : "The store key would keep " + item + " in your inventory";
        return Bound(result + (reason.Length == 0 ? "." : ": " + reason + "."));
    }

    /// <summary>
    /// P6-23 (D071): the same answer as a short line for the game's item tooltip, such as "Stash stack: Reinforced chest
    /// 3 m away" or "Clean up: stays here". The label ("Stash stack", "Clean up") comes first, then where, then why in brackets.
    /// </summary>
    /// <param name="stackKey">
    /// P8-17 (Q12, revised 2026-10-28): the single-stack binding that applies (Azu's when Azu stores), as
    /// <see cref="KeyNames"/> shows it, since the tooltip is about this one stack; the label reads
    /// "Stash stack [Middle mouse]: …"; empty or null keeps "Stash stack: …".
    /// </param>
    public static string Tooltip(PlacementExplanation explanation, bool cleanup, Func<string, string>? chestName = null, bool azuStores = false,
        string? stackKey = null)
    {
        if (explanation == null) throw new ArgumentNullException(nameof(explanation));
        var label = cleanup ? "Clean up: " : string.IsNullOrEmpty(stackKey) ? "Stash stack: " : "Stash stack [" + stackKey + "]: ";
        if (explanation.Outcome == PlacementOutcome.Goes && explanation.ChosenId != null)
        {
            var chest = Shown(chestName?.Invoke(explanation.ChosenId), "a chest nearby");
            return Bound(label + (cleanup && chest == "this chest" ? "stays here" : (cleanup ? "moves to " : "") + chest));
        }
        var reason = explanation.Outcome switch
        {
            PlacementOutcome.NoRoom => "the chests that take it are full",
            PlacementOutcome.NoChest => "no chest nearby takes it",
            PlacementOutcome.Blocked when explanation.Blocked != null => CleanupReport.Describe(explanation.Blocked.Value),
            PlacementOutcome.Kept => "protected or in use",
            PlacementOutcome.NotReady => "categories are still loading",
            PlacementOutcome.OptedOut => "this chest is set to \"Don't clean up\"",
            PlacementOutcome.Skipped => CleanupReport.Describe(CleanupReason.ChestLimit),
            _ => ""
        };
        if (!cleanup && azuStores && explanation.Outcome is PlacementOutcome.NoChest or PlacementOutcome.NoRoom or PlacementOutcome.Blocked)
            return Bound(label + "AzuAutoStore's own settings decide (" + (explanation.Outcome == PlacementOutcome.NoChest ? "no chest rule nearby takes it" : reason) + ")");
        return Bound(label + (cleanup ? "stays here" : "stays in your inventory") + (reason.Length == 0 ? "" : " (" + reason + ")"));
    }

    private static string Shown(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text!;

    private static string Bound(string text) => text.Length <= MaximumText ? text : text.Substring(0, MaximumText - 1) + "…";
}
