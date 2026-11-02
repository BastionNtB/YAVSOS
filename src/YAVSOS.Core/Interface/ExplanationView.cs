using System;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// S2 (audit D02): what an explanation found, kept typed until it is shown: the answer with its chosen chest, how to
/// name chests, and a note that says why there is no answer or qualifies one. <see cref="Line"/> is the tooltip's text.
/// </summary>
public sealed class ExplanationAnswer
{
    public PlacementExplanation? Explanation { get; }
    public bool Cleanup { get; }
    /// <summary>Why there is no answer, or a qualifier ("turned off on this server"); empty when there is nothing to add.</summary>
    public string Note { get; }
    private readonly Func<string, string>? chestName;
    private readonly bool azuStores;
    private readonly string? stackKey;

    public ExplanationAnswer(PlacementExplanation explanation, bool cleanup, string note, Func<string, string>? chestName = null, bool azuStores = false,
        string? stackKey = null)
    {
        Explanation = explanation ?? throw new ArgumentNullException(nameof(explanation));
        Cleanup = cleanup; Note = note ?? ""; this.chestName = chestName; this.azuStores = azuStores; this.stackKey = stackKey;
    }

    private ExplanationAnswer(string note) => Note = note ?? "";

    /// <summary>No answer, only <paramref name="note"/> (possibly empty).</summary>
    public static ExplanationAnswer Without(string note) => new ExplanationAnswer(note);

    /// <summary>The tooltip line (<see cref="ExplanationView.Tooltip"/>), or null when there is no answer.</summary>
    public string? Line => Explanation == null ? null : ExplanationView.Tooltip(Explanation, Cleanup, chestName, azuStores, stackKey);
}

/// <summary>
/// The answer as one short line for the game's item tooltip (P6-05, simplified in the R4 review, then P6-23): what the
/// stash key or "Clean up nearby" would do with the stack, and why. The competing chests are not shown (maintainer,
/// 2026-10-23: the list was too technical for players). Read only.
/// </summary>
public static class ExplanationView
{
    public const int MaximumText = 160;

    /// <summary>
    /// P6-23 (D071): the answer as a short line for the game's item tooltip, such as "Stash stack: Reinforced chest
    /// 3 m away (counts as Metals)" or "Clean up: stays here". The label ("Stash stack", "Clean up") comes first, then
    /// where, then why in brackets (X1: for a chosen chest, what made it take the stack).
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
            if (cleanup && chest == "this chest") return Bound(label + "stays here");
            var because = Because(explanation);
            return Bound(label + (cleanup ? "moves to " : "") + chest + (because.Length == 0 ? "" : " (" + because + ")"));
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

    /// <summary>X1: why the chosen chest takes it, "counts as Metals"; empty when there is nothing useful to say.</summary>
    public static string Because(PlacementExplanation explanation) => explanation.Basis switch
    {
        PlacementBasis.Category when !string.IsNullOrWhiteSpace(explanation.Category) => "counts as " + explanation.Category,
        PlacementBasis.Always => "listed under Always",
        PlacementBasis.Holds => "already holds some",
        PlacementBasis.Filters => "matches its filters",
        _ => ""
    };

    private static string Shown(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text!;

    private static string Bound(string text) => text.Length <= MaximumText ? text : text.Substring(0, MaximumText - 1) + "…";
}
