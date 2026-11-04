using System;
using System.Collections.Generic;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>
/// X26 (D130): one chest's last rule change, as stored payloads: <see cref="Before"/> is what an undo (or redo) puts
/// back, <see cref="After"/> what the chest must still hold for it, so the write is a compare-and-set like any edit.
/// Immutable.
/// </summary>
public sealed class RuleUndo
{
    public string Before { get; }
    public string After { get; }
    /// <summary>The change was undone, so the next click makes it again.</summary>
    public bool Redo { get; }

    internal RuleUndo(string before, string after, bool redo) { Before = before; After = after; Redo = redo; }
}

/// <summary>
/// X26 (D130, variant A): each chest keeps its own last rule change, in this game only, until the world ends. A change
/// replaces the chest's record; an undo turns it into a redo and a redo back into an undo (one record, no history). A
/// refusal because the rule changed since drops it. The cleanup opt-out isn't a rule change and has no record.
/// Not thread safe; the game's UI thread owns it.
/// </summary>
/// <typeparam name="TKey">The chest's identity in this world (the game's object ID).</typeparam>
public sealed class RuleHistory<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, RuleUndo> records = new Dictionary<TKey, RuleUndo>();

    public int Count => records.Count;

    /// <summary>The chest's record, or null.</summary>
    public RuleUndo? For(TKey chest) => records.TryGetValue(chest, out var record) ? record : null;

    /// <summary>A saved change from <paramref name="before"/> to <paramref name="after"/>; nothing when it changed nothing.</summary>
    public void Changed(TKey chest, string before, string after)
    {
        if (before == null) throw new ArgumentNullException(nameof(before));
        if (after == null) throw new ArgumentNullException(nameof(after));
        if (StringComparer.Ordinal.Equals(before, after)) return;
        records[chest] = new RuleUndo(before, after, false);
    }

    /// <summary>The record's undo (or redo) was written and the chest now holds <paramref name="written"/>: the record turns around.</summary>
    public void Restored(TKey chest, string written)
    {
        if (written == null) throw new ArgumentNullException(nameof(written));
        if (!records.TryGetValue(chest, out var record)) return;
        records[chest] = new RuleUndo(record.After, written, !record.Redo);
    }

    /// <summary>The rule changed since the record, so it can no longer be undone.</summary>
    public void Drop(TKey chest) => records.Remove(chest);

    /// <summary>The world ended.</summary>
    public void Clear() => records.Clear();
}

/// <summary>X26 (D130): the ↶ button's text, beside Copy.</summary>
public static class UndoButton
{
    /// <summary>
    /// The ↶ button's tooltip (D130): what a click puts back, by its chest label (escaped for rich text by the caller), or
    /// that there is nothing to undo, or that another player is using the chest. The heading names the action and its key.
    /// </summary>
    /// <param name="key">Ctrl + Z, as <see cref="KeyNames"/> names it.</param>
    /// <param name="label">The chest label of <see cref="RuleUndo.Before"/>; null or "" for no rule.</param>
    public static Tooltip Tip(RuleUndo? record, bool editable, string? label, MessageList? key)
    {
        var action = Message.Of(record?.Redo == true ? "yavsos_redo_heading" : "yavsos_undo_heading");
        var heading = key == null ? action : Message.Of("yavsos_undo_heading_key").With("button", action).With("key", key);
        if (record == null) return Tooltip.Of(action, Message.Of("yavsos_undo_tip_none"));
        if (!editable) return Tooltip.Of(heading, Message.Of(record.Redo ? "yavsos_redo_tip_in_use" : "yavsos_undo_tip_in_use"));
        var tip = Message.Of(record.Redo ? "yavsos_redo_tip" : "yavsos_undo_tip");
        return Tooltip.Of(heading, label is { Length: > 0 } ? tip.With("rule", label, Tone.Label) : tip.With("rule", Message.Of("yavsos_panel_norule"), Tone.Faint));
    }
}
