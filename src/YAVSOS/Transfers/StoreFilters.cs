using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;
using YAVSOS.Interface;

namespace YAVSOS.Transfers;

/// <summary>
/// X60 (D155, D160; YAVSOS-dev#9): reads each container type's built-in filter the way the spike did
/// (docs/design/X60-STOREHOUSE-SPIKE.md): for every piece prefab with a <see cref="Container"/>, an empty
/// <see cref="Inventory"/> with that container's name and size is asked through <see cref="GameTakes"/> about one unit
/// of every item. No Storehouse or other mod types (D058): a type that refuses some item has a filter. The read starts
/// once categories are ready, spends at most <see cref="FrameBudgetMs"/> a frame (no hitch), and runs again when another
/// mod's settings arrive from the server (Jotunn's config sync, D160 Q2), when the item catalog changes and each session.
/// Until it finishes, stores act unruled as before; the previous set stays in use while a new read runs. If the game's
/// check fails, the categories and the automatic rules turn off together for the session. Client and single player only:
/// a dedicated server plans nothing. Unity game thread only; changes nothing in the world.
/// </summary>
internal sealed class StoreFilters
{
    internal const double FrameBudgetMs = 2.0;
    private readonly ManualLogSource log;
    private ContainerFilterSet current = ContainerFilterSet.Empty;
    private bool ready, off, dirty = true, loggedOff;
    private object? catalogRead;
    private Work? work;

    private sealed class Work
    {
        internal int Stage, Index;
        internal readonly List<ContainerTypeInfo> Types = new();
        internal readonly List<(string Name, int Width, int Height)> Shapes = new();
        internal readonly List<ItemDrop.ItemData> Items = new();
        internal readonly List<string> Names = new();
        internal Inventory?[] Inventories = Array.Empty<Inventory?>();
        internal ContainerFilterBuild? Build;
        internal readonly Stopwatch Clock = new();
        internal int Frames;
        internal object? Catalog;
    }

    internal StoreFilters(ManualLogSource log) => this.log = log;

    /// <summary>The filters read this session; empty until ready, and while the check is off.</summary>
    internal ContainerFilterSet Current => current;
    /// <summary>
    /// The filter categories aren't known (not read yet this session, or the check is off): rule parts naming one wait
    /// (<see cref="IFilterCategoryState"/>).
    /// </summary>
    internal bool Pending => !ready;
    internal bool Ready => ready;
    /// <summary>The game's check failed: no filters, no automatic rules, this session.</summary>
    internal bool Off => off;
    internal string Status => off ? "off (" + (GameTakes.Fault ?? "check failed") + ")" : ready ? "ready" : work != null ? "reading" : "waiting";
    /// <summary>The last read's cost, for the Lab: milliseconds of work, frames and asks.</summary>
    internal (double Ms, int Frames, long Asks, int Groups) LastRead { get; private set; }

    /// <summary>A new world or connection: read again once categories are ready; stores act unruled until then.</summary>
    internal void BeginSession()
    {
        current = ContainerFilterSet.Empty; ready = false; work = null; dirty = true; catalogRead = null;
        if (off && GameTakes.Usable) { off = false; loggedOff = false; }
    }

    /// <summary>Another mod's settings changed (the server's arrived): read again, keeping the current set meanwhile.</summary>
    internal void Invalidate() => dirty = true;

    internal void Update(bool categoriesReady, object? catalog)
    {
        if (off || !categoriesReady || !ZNet.instance || ZNet.instance.IsDedicated() || !ObjectDB.instance || !ZNetScene.instance) return;
        if (!ReferenceEquals(catalog, catalogRead) && work == null) dirty = true;
        if (dirty) // A read in progress starts over: its answers may be from before the change.
        {
            dirty = false;
            work = new Work { Catalog = catalog };
        }
        if (work == null) return;
        var started = UiCost.Start();
        try { Step(work); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A changed piece or item shape (D058): only this feature turns off.
            TurnOff(error.GetType().Name + ": " + error.Message);
        }
        UiCost.Stop("store filters", started);
    }

    private void Step(Work run)
    {
        var budget = Stopwatch.StartNew();
        bool More() => budget.Elapsed.TotalMilliseconds < FrameBudgetMs;
        run.Frames++;
        run.Clock.Start();
        try
        {
            if (!GameTakes.Usable) { TurnOff(GameTakes.Fault ?? "the game's check is off"); return; }
            if (run.Stage == 0) Types(run, More);
            if (run.Stage == 1) Items(run, More);
            if (run.Stage == 2) Ask(run, More);
        }
        finally { run.Clock.Stop(); }
    }

    // Every piece prefab with a container, with its inventory's name and size (Storehouse finds a store's filter by that
    // name; its fixed-place stores read the size).
    private static void Types(Work run, Func<bool> more)
    {
        var prefabs = ZNetScene.instance.m_prefabs;
        while (run.Index < prefabs.Count && more())
        {
            var prefab = prefabs[run.Index++];
            if (!prefab || !prefab.GetComponent<Piece>()) continue;
            var container = prefab.GetComponentInChildren<Container>(true);
            if (!container || container.m_width <= 0 || container.m_height <= 0) continue;
            var piece = prefab.GetComponent<Piece>();
            var key = container.m_name + "\u0001" + container.m_width + "x" + container.m_height;
            var (name, mod, shown) = Name(prefab.name, piece.m_name, container.m_name);
            run.Types.Add(new ContainerTypeInfo(prefab.name, name, key, mod, shown));
            run.Shapes.Add((container.m_name, container.m_width, container.m_height));
        }
        if (run.Index >= prefabs.Count) { run.Stage = 1; run.Index = 0; }
    }

    // One unit of each item, carrying its drop prefab as inventory items do (Storehouse reads the prefab name).
    private static void Items(Work run, Func<bool> more)
    {
        var items = ObjectDB.instance.m_items;
        while (run.Index < items.Count && more())
        {
            var prefab = items[run.Index++];
            var drop = prefab ? prefab.GetComponent<ItemDrop>() : null;
            if (!drop || drop!.m_itemData?.m_shared == null) continue;
            var item = drop.m_itemData.Clone(); item.m_dropPrefab = prefab; item.m_stack = 1;
            run.Items.Add(item); run.Names.Add(prefab!.name);
        }
        if (run.Index < items.Count) return;
        run.Stage = 2; run.Index = 0;
        run.Inventories = new Inventory?[run.Types.Count];
        run.Build = new ContainerFilterBuild(run.Types, run.Names, (type, item) =>
        {
            var inventory = run.Inventories[type];
            if (inventory == null)
            {
                var (name, width, height) = run.Shapes[type];
                run.Inventories[type] = inventory = new Inventory(name, null, width, height);
            }
            return GameTakes.Takes(inventory, run.Items[item]);
        });
    }

    private void Ask(Work run, Func<bool> more)
    {
        var build = run.Build!;
        if (!build.Step(more)) return;
        work = null; catalogRead = run.Catalog;
        LastRead = (run.Clock.Elapsed.TotalMilliseconds, run.Frames, build.Asks, build.Groups);
        if (build.Failed || build.Result == null) { TurnOff(GameTakes.Fault ?? "an answer was missing"); return; }
        current = build.Result; ready = true;
        log.LogInfo($"Container filters read: {current.Filters.Count} of {current.Types} container types filter their items " +
            $"({run.Items.Count} items, {build.Groups} name-and-size groups, {build.Asks} asks, {run.Clock.Elapsed.TotalMilliseconds:0.0} ms over {run.Frames} frames)." +
            (current.Dropped != 0 ? $" {current.Dropped} filters got no category: the category list is full." : ""));
    }

    private void TurnOff(string why)
    {
        off = true; ready = false; work = null; current = ContainerFilterSet.Empty;
        if (loggedOff) return;
        loggedOff = true;
        log.LogWarning($"Container filters turned off ({why}): Storehouse-style stores get no categories or automatic rules this session, " +
            "and rule parts naming one wait. Everything else works as before.");
    }

    /// <summary>The filter of a placed container's type (its piece prefab, from its object's prefab); null for none.</summary>
    internal ContainerFilter? For(Container? container)
    {
        if (!ready || current.Filters.Count == 0 || !container) return null;
        var zdo = ChestRuleStore.View(container!)?.GetZDO();
        if (zdo == null || !ZNetScene.instance) return null;
        var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        return prefab ? current.ForPrefab(prefab.name) : null;
    }

    // "Storehouse Timber Crib": the piece's own name in the player's language, after the name of the mod that added it
    // when Jotunn knows it (its public piece list). Without a piece name, the container's; without either, the prefab.
    // D163: also the mod (the tree's heading, null when unknown) and the piece's own name (its row under that heading).
    private static (string Name, string? Mod, string Shown) Name(string prefab, string pieceToken, string containerToken)
    {
        var shown = GameText.Localize(string.IsNullOrEmpty(pieceToken) ? containerToken : pieceToken);
        if (string.IsNullOrWhiteSpace(shown) || shown.StartsWith("$", StringComparison.Ordinal)) shown = prefab;
        string? mod = null;
        try { mod = Jotunn.Managers.PieceManager.Instance.GetPiece(prefab)?.SourceMod?.Name; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (string.IsNullOrWhiteSpace(mod)) return (shown, null, shown);
        if (shown.StartsWith(mod!, StringComparison.OrdinalIgnoreCase)) return (shown, mod, shown);
        return (Texts.Get(Message.Of("yavsos_category_container").With("mod", mod!).With("container", shown)), mod, shown);
    }
}
