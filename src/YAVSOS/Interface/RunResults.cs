using System;
using System.Collections.Generic;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// P6-04: the latest stash or cleanup result, published once per run when it finishes. The detail
/// (<see cref="RunReport.Detail"/>) is built then, never per frame. Read only. Unity game thread only.
/// </summary>
internal sealed class RunResults
{
    internal ResultSummary? Latest { get; private set; }
    /// <summary>Bumped on every publish, so the panel rebuilds its lines only then.</summary>
    internal int Revision { get; private set; }
    private readonly ResultCountdown countdown = new ResultCountdown(); // P9-23

    /// <param name="chests">X55: the run's chests by the IDs its moves named, so the result lists what each chest or
    /// drawer received (<see cref="ResultPlaces"/>). Null keeps the single moved line.</param>
    internal void Publish(RunReport report, IReadOnlyDictionary<string, Container>? chests = null)
    {
        // Names from other mods go into rich text, so they can't open a tag.
        Func<string, string> itemName = prefab => Texts.Escape(GameText.ItemName(prefab));
        // Display only: if naming a place fails, the result keeps its single moved line.
        try { Latest = report.Detail(itemName, placeName: chests == null ? null : ResultPlaces.Namer(chests)); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Latest = report.Detail(itemName); }
        Revision++;
        // An empty run has nothing to add to its one-line message. A published result is final (a stash or cleanup run
        // ends only after MultiUserChest answers or the run gives up; AzuAutoStore's after nothing is still on its way),
        // so the countdown starts with every line the result will have (D117).
        if (Latest.Empty) countdown.Stop(); else countdown.Restart();
    }

    internal void Dismiss() => countdown.Stop();

    /// <summary>
    /// P9-23: up for the player's <paramref name="seconds"/>; with <paramref name="holdInInventory"/> on, the countdown
    /// waits while the inventory is open. The Close button stays for closing it sooner.
    /// </summary>
    internal bool Visible(float elapsed, float seconds, bool inventoryOpen, bool holdInInventory) =>
        countdown.Tick(elapsed, seconds, inventoryOpen, holdInInventory) && Latest != null;
}
