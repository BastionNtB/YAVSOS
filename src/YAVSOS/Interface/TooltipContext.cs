using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using YAVSOS.Cleanup;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;

namespace YAVSOS.Interface;

/// <summary>
/// X16 (P9-13): what the tooltip explanations of one open inventory are answered from, gathered once and spread over
/// frames: the stash side (the player's stacks) and, with a chest open, the cleanup side (that chest's stacks), each with
/// its chest captures, planner input, prepared receivers and snapshot adapter. It is replaced when stale
/// (<see cref="Stale"/>) and disposed when replaced or when the inventory closes. Read only: it feeds explanations,
/// never a run, which gathers its own inputs at the press; a stale context can make a line briefly wrong, never a move.
/// Unity game thread only.
/// </summary>
internal sealed class TooltipContext : IDisposable
{
    /// <summary>Rebuilt at this age (seconds), for what no other trigger sees: another player's deposit, a chest built nearby.</summary>
    internal const float MaximumAge = 5f;
    /// <summary>Rebuilt once the player is this far (metres) from where it was gathered: chests enter or leave range.</summary>
    internal const float MaximumMove = 4f;

    /// <summary>What a context was gathered for, compared at each hover poll.</summary>
    internal readonly struct Key
    {
        internal readonly Player Player; internal readonly Container? Open; internal readonly Vector3 At; internal readonly float Since;
        internal readonly int Results, Rules; internal readonly object? Taxonomy; internal readonly long Generation, Contents;
        internal Key(Player player, Container? open, Vector3 at, float since, int results, int rules, object? taxonomy, long generation, long contents)
        {
            Player = player; Open = open; At = at; Since = since; Results = results; Rules = rules; Taxonomy = taxonomy; Generation = generation;
            Contents = contents;
        }
    }

    internal Key Gathered { get; }
    private StashRunner.Prepared? stashSide;
    private CleanupRunner.Prepared? cleanupSide;
    private bool failed;

    internal TooltipContext(Key key) => Gathered = key;

    internal Container? Open => Gathered.Open;

    /// <summary>
    /// The key for now: a YAVSOS or AzuAutoStore run ended (<paramref name="results"/>), a rule or opt-out changed
    /// (<paramref name="rules"/>), the category answers changed, or the player's or the open chest's contents changed.
    /// </summary>
    internal static Key Stamp(Player player, Container? open, int results, int rules, Plugin plugin, float now)
    {
        var drag = CleanupRunner.DragItem != null && InventoryGui.instance ? CleanupRunner.DragItem(InventoryGui.instance) : null;
        var contents = Contents(player.GetInventory()) * 31 + (open ? Contents(open!.GetInventory()) : 0);
        contents = contents * 31 + (drag == null ? 0 : RuntimeHelpers.GetHashCode(drag));
        return new Key(player, open, player.transform.position, now, results, rules, plugin.RuleTaxonomy, plugin.CategoryAnswers?.Generation ?? long.MinValue,
            contents);
    }

    /// <summary>Whether a context gathered for <see cref="Gathered"/> no longer fits <paramref name="now"/>.</summary>
    internal bool Stale(Key now)
    {
        var then = Gathered;
        return !ReferenceEquals(then.Player, now.Player) || !ReferenceEquals(then.Open, now.Open) || then.Results != now.Results || then.Rules != now.Rules ||
               !ReferenceEquals(then.Taxonomy, now.Taxonomy) || then.Generation != now.Generation || then.Contents != now.Contents ||
               (now.At - then.At).sqrMagnitude >= MaximumMove * MaximumMove || now.Since - then.Since >= MaximumAge;
    }

    // A cheap signature of what an inventory holds and where: stack count, total units, and an order-free sum of each
    // stack's prefab, size, cell, quality and equipped state (equipped stacks and protected cells stay in place).
    internal static long Contents(Inventory? inventory)
    {
        if (inventory == null) return 0;
        long count = 0, units = 0, sum = 0;
        foreach (var item in inventory.GetAllItems())
        {
            if (item == null) continue;
            count++; units += item.m_stack;
            var prefab = item.m_dropPrefab ? item.m_dropPrefab.name : item.m_shared?.m_name ?? "";
            long row = StringComparer.Ordinal.GetHashCode(prefab);
            row = unchecked(row * 397 + item.m_stack);
            row = unchecked(row * 397 + item.m_gridPos.x * 1009 + item.m_gridPos.y);
            row = unchecked(row * 397 + item.m_quality * 2 + (item.m_equipped ? 1 : 0));
            sum = unchecked(sum + row * 0x9E3779B1L);
        }
        return unchecked((sum * 31 + count) * 31 + units);
    }

    /// <summary>The stash side, then the cleanup side when a chest is open, a few chests per step (the runners' Prepare).</summary>
    internal IEnumerator<bool> Gather(StashRunner stash, CleanupRunner cleanup)
    {
        using (var part = stash.Prepare(Open, x => stashSide = x))
            while (part.MoveNext()) yield return true;
        if (!Open) yield break;
        yield return true;
        using (var part = cleanup.Prepare(Open!, x => cleanupSide = x))
            while (part.MoveNext()) yield return true;
    }

    /// <summary>The gather failed: every answer says the stack can't be explained right now, until the next rebuild.</summary>
    internal void Fail() { failed = true; Dispose(); }

    internal ExplanationAnswer Stash(StashRunner runner, ItemDrop.ItemData item) =>
        failed || stashSide == null ? ExplanationAnswer.Without(Failed(item)) : runner.Answer(stashSide, item);

    internal ExplanationAnswer Cleanup(CleanupRunner runner, ItemDrop.ItemData item) =>
        failed || cleanupSide == null ? ExplanationAnswer.Without(Failed(item)) : runner.Answer(cleanupSide, item);

    /// <summary>The note when there is no answer because the gather or the answer failed; the caller logged a warning.</summary>
    internal static Message Failed(ItemDrop.ItemData item) => Message.Of("yavsos_where_note_failed").With("item", RuleControls.ItemName(item));

    public void Dispose()
    {
        var stashing = stashSide; var cleaning = cleanupSide;
        stashSide = null; cleanupSide = null;
        stashing?.Dispose(); cleaning?.Dispose();
    }
}
