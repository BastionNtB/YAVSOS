using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

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

    // D101: tooltips explain what the button does; they don't warn. The heading is the button's own text.
    public static Tooltip CopyTooltip(bool canCopy, int hiddenParts) => Tooltip.Of(Message.Of("yavsos_copy_button"),
        Message.Of(!canCopy ? "yavsos_copy_tip_none" : hiddenParts == 0 ? "yavsos_copy_tip" : "yavsos_copy_tip_hidden"));

    /// <param name="copied">The copied rule, or null if nothing is copied yet.</param>
    /// <param name="editable">The panel can edit this chest (not read-only).</param>
    /// <param name="hasRule">This chest already has a rule.</param>
    public static Tooltip PasteTooltip(RuleCopy? copied, bool editable, bool hasRule)
    {
        var heading = Message.Of("yavsos_copy_paste_button");
        if (copied == null) return Tooltip.Of(heading, Message.Of("yavsos_copy_paste_tip_empty"));
        // P10-04 (inventory case 7): the label is tinted by the game side, not wrapped in markup here.
        var what = copied.Label.Length == 0 ? Message.Of("yavsos_copy_paste_tip") : Message.Of("yavsos_copy_paste_tip_label").With("label", copied.Label, Tone.Label);
        var note = !editable ? Message.Of("yavsos_copy_paste_tip_in_use") : hasRule ? Message.Of("yavsos_copy_paste_tip_replaces") : null;
        return note == null ? Tooltip.Of(heading, what) : Tooltip.Of(heading, MessageList.Of(ListKind.Lines, new[] { what, note }));
    }
}
