using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>
/// X2 (D116): a copied chest rule, as the panel showed it: categories (unknown ones as stored, D050), Always and Never,
/// filters and priority. "Don't clean up this chest" is the chest's, not the rule's, so it stays behind. Parts the editor
/// can't show (D086 D5) aren't copied. Held by this game only until the next copy or leaving the world; nothing is sent
/// until it is pasted, and a paste saves like any edit. Immutable.
/// </summary>
public sealed class RuleCopy
{
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> Unknown { get; }
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; }
    public int Priority { get; }
    /// <summary>The source chest's hover label (S3), for Paste's tooltip; "" if it had none.</summary>
    public string Label { get; }

    private RuleCopy(RuleEditState state, string label)
    {
        Categories = state.Categories.ToArray(); Unknown = state.Unknown.ToArray();
        Includes = state.Includes.ToArray(); Excludes = state.Excludes.ToArray();
        Filters = state.Filters.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToArray(), StringComparer.Ordinal);
        Priority = state.Priority; Label = label;
    }

    /// <summary>Whether Copy has something to take: a stored rule with a part the editor shows.</summary>
    public static bool CanCopy(RuleEditState state) =>
        state != null && state.Stored && state.Categories.Count + state.Unknown.Count + state.Includes.Count + state.Excludes.Count + state.Filters.Count != 0;

    /// <summary>What Copy takes from the open chest's rule as loaded (so already converted, S1); null when <see cref="CanCopy"/> is false.</summary>
    public static RuleCopy? Of(RuleEditState state, string? label) => CanCopy(state) ? new RuleCopy(state, label ?? "") : null;

    // D101: tooltips explain what the button does; they don't warn.
    public const string CopyTopic = "Copy", PasteTopic = "Paste";

    public static string CopyTooltip(bool canCopy, int hiddenParts) => !canCopy ? "This chest has no rule to copy."
        : "Copies this chest's rule: categories, Always and Never items, filters and priority. Paste it on another chest." +
          (hiddenParts == 0 ? "" : " Parts this version can't show stay here.");

    /// <param name="copied">The copied rule, or null if nothing is copied yet.</param>
    /// <param name="editable">The panel can edit this chest (not read-only).</param>
    /// <param name="hasRule">This chest already has a rule.</param>
    public static string PasteTooltip(RuleCopy? copied, bool editable, bool hasRule)
    {
        if (copied == null) return "Copy a rule from another chest first, then paste it here.";
        var what = copied.Label.Length == 0 ? "Gives this chest the copied rule." : "Gives this chest the copied rule:\n<color=" + ChestLabelPreview.Color + ">" + copied.Label + "</color>";
        if (!editable) return what + "\nYou can paste once no one else is using this chest.";
        return what + (hasRule ? "\nIt replaces this chest's rule." : "");
    }
}
