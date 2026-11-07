using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;

namespace YAVSOS.Integrations;

/// <summary>
/// #64 (D151): the favorites of other mods, which YAVSOS's own stash leaves in the player's inventory as it does the
/// hotbar. A stack is a favorite if any installed favorites mod marks it, as an item or by the slot it sits in. Each
/// reader binds its mod's public members by shape once (D058); a mod that isn't installed costs nothing. A reader that
/// can't read its mod leaves those favorites unprotected, so stashing keeps working: the log says why once per cause,
/// and the next stash result says so once per mod (<see cref="TakeUnreadable"/>). Read only. Unity game thread only.
/// </summary>
internal static class Favorites
{
    internal const string ItemFavoriteFrameworkGuid = "MidnightsFX.ItemFavoriteFramework";
    internal const string QuickStackStoreGuid = "goldenrevolver.quick_stack_store";
    internal const string AzuEpiGuid = "Azumatt.AzuExtendedPlayerInventory";

    /// <summary>
    /// What the installed favorites mods mark in one inventory: favorited stacks, and favorited cells (y × width + x),
    /// each with the name of the first mod that marks it, for the stack key's refusal and the tooltip's reason.
    /// </summary>
    internal sealed class Found
    {
        internal static readonly Found None = new(new Dictionary<ItemDrop.ItemData, string>(), new Dictionary<int, string>(), 0);
        internal readonly Dictionary<ItemDrop.ItemData, string> Items;
        internal readonly Dictionary<int, string> Cells;
        private readonly int width;
        internal Found(Dictionary<ItemDrop.ItemData, string> items, Dictionary<int, string> cells, int width) { Items = items; Cells = cells; this.width = width; }
        /// <summary>The mod that marks the stack as a favorite, as an item or by its slot; null when none does.</summary>
        internal string? ModOf(ItemDrop.ItemData item) =>
            Items.TryGetValue(item, out var mod) ? mod
            : width > 0 && Cells.TryGetValue(item.m_gridPos.y * width + item.m_gridPos.x, out mod) ? mod : null;
    }

    private abstract class Reader
    {
        internal abstract string Guid { get; }
        /// <summary>The mod's name as players see it, for the stash result's warning.</summary>
        internal abstract string Name { get; }
        /// <summary>Whether this mod's favorites count now; checked after <see cref="Installed"/>.</summary>
        internal virtual bool Counts => true;
        internal bool Installed => Chainloader.PluginInfos.ContainsKey(Guid);
        /// <summary>Null when bound; otherwise why the mod's members don't have the expected shape.</summary>
        internal abstract string? Bind();
        internal abstract void Read(Player player, Inventory inventory, HashSet<ItemDrop.ItemData> items, HashSet<int> cells);
    }

    /// <summary>
    /// ItemFavoriteFramework (read at 1.0.0, API version 3): its public API. <c>IsProtected</c> is a favorited item or
    /// one in a favorited slot; <c>GetFavoritedSlots</c> the favorited slots; <c>IsReady</c> false means its data for
    /// the player couldn't be loaded.
    /// </summary>
    private sealed class ItemFavoriteFramework : Reader
    {
        private Func<bool>? ready; private Func<ItemDrop.ItemData, bool>? isProtected; private Func<List<Vector2i>>? slots;
        internal override string Guid => ItemFavoriteFrameworkGuid;
        internal override string Name => "ItemFavoriteFramework";
        internal override string? Bind()
        {
            var api = AccessTools.TypeByName("ItemFavoriteFramework.API");
            if (api == null) return "class ItemFavoriteFramework.API not found";
            ready = Static<Func<bool>>(api, "IsReady");
            isProtected = Static<Func<ItemDrop.ItemData, bool>>(api, "IsProtected", typeof(ItemDrop.ItemData));
            slots = Static<Func<List<Vector2i>>>(api, "GetFavoritedSlots");
            if (ready == null || isProtected == null || slots == null)
                return "API.IsReady(), IsProtected(ItemData) or GetFavoritedSlots() not found or of another shape";
            // Its own endpoint list, where the API has one: an endpoint it no longer lists is no longer supported.
            if (Static<Func<string, bool>>(api, "HasEndpoint", typeof(string)) is { } has && !(has("IsProtected") && has("GetFavoritedSlots")))
                return "API.HasEndpoint() no longer lists IsProtected or GetFavoritedSlots";
            return null;
        }
        internal override void Read(Player player, Inventory inventory, HashSet<ItemDrop.ItemData> items, HashSet<int> cells)
        {
            if (!ready!()) throw new InvalidOperationException("API.IsReady() is false: it has no favorites loaded for this character");
            foreach (var item in inventory.GetAllItems())
                if (item != null && isProtected!(item)) items.Add(item);
            if (slots!() is { } list)
                foreach (var slot in list) AddCell(inventory, slot.x, slot.y, cells);
        }
    }

    /// <summary>
    /// Quick Stack Store (read at 1.4.16) and AzuExtendedPlayerInventory (read at 2.6.6): a public per-player class
    /// <c>UserConfig</c> of the same shape, <c>GetPlayerConfig(playerID).IsItemNameOrSlotFavorited(item)</c> and
    /// <c>IsSlotFavorited(position)</c>; there is no formal API.
    /// </summary>
    private sealed class UserConfigReader : Reader
    {
        private readonly string guid, name, type; private readonly Func<bool> counts;
        private MethodInfo? config, favorited, slot;
        private object? bound; private Func<ItemDrop.ItemData, bool>? isFavorited; private Func<Vector2i, bool>? isSlot;
        internal UserConfigReader(string guid, string name, string type, Func<bool> counts) { this.guid = guid; this.name = name; this.type = type; this.counts = counts; }
        internal override string Guid => guid;
        internal override string Name => name;
        internal override bool Counts => counts();
        internal override string? Bind()
        {
            var config = AccessTools.TypeByName(type);
            if (config == null) return "class " + type + " not found";
            this.config = AccessTools.DeclaredMethod(config, "GetPlayerConfig", new[] { typeof(long) });
            favorited = AccessTools.DeclaredMethod(config, "IsItemNameOrSlotFavorited", new[] { typeof(ItemDrop.ItemData) });
            slot = AccessTools.DeclaredMethod(config, "IsSlotFavorited", new[] { typeof(Vector2i) });
            if (this.config is not { IsStatic: true } || this.config.ReturnType != config ||
                favorited is not { IsStatic: false } || favorited.ReturnType != typeof(bool) || slot is not { IsStatic: false } || slot.ReturnType != typeof(bool))
                return "UserConfig.GetPlayerConfig(long), IsItemNameOrSlotFavorited(ItemData) or IsSlotFavorited(Vector2i) not found or of another shape";
            return null;
        }
        internal override void Read(Player player, Inventory inventory, HashSet<ItemDrop.ItemData> items, HashSet<int> cells)
        {
            var current = config!.Invoke(null, new object[] { player.GetPlayerID() }) ?? throw new InvalidOperationException("GetPlayerConfig() returned nothing");
            // The mod keeps one object per player; bind its checks to it once.
            if (!ReferenceEquals(current, bound))
            {
                isFavorited = (Func<ItemDrop.ItemData, bool>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData, bool>), current, favorited!);
                isSlot = (Func<Vector2i, bool>)Delegate.CreateDelegate(typeof(Func<Vector2i, bool>), current, slot!);
                bound = current;
            }
            foreach (var item in inventory.GetAllItems())
                if (item != null && isFavorited!(item)) items.Add(item);
            for (var y = 0; y < inventory.GetHeight(); y++)
                for (var x = 0; x < inventory.GetWidth(); x++)
                    if (isSlot!(new Vector2i(x, y))) cells.Add(y * inventory.GetWidth() + x);
        }
    }

    private static readonly Reader[] Readers =
    {
        new ItemFavoriteFramework(),
        new UserConfigReader(QuickStackStoreGuid, "Quick Stack Store", "QuickStackStore.UserConfig", () => true),
        // AzuEPI turns its own favoriting off while Quick Stack Store or AzuAutoStore is loaded (FavoritingMode, 2.6.6);
        // its saved favorites are then not the player's current ones.
        new UserConfigReader(AzuEpiGuid, "AzuExtendedPlayerInventory", "AzuEPI.Game.Favoriting.UserConfig",
            () => !Chainloader.PluginInfos.ContainsKey(QuickStackStoreGuid) && !Chainloader.PluginInfos.ContainsKey(AzuAutoStoreIntegration.AzuGuid))
    };

    private static List<Reader>? installed;
    private static readonly Dictionary<Reader, string> Broken = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly List<string> Unshown = new();
    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal);
    private static ManualLogSource? log;

    /// <summary>Binds the installed favorites mods once, after every plugin has loaded, and says in the log which are read.</summary>
    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        if (installed != null) return;
        installed = new List<Reader>();
        foreach (var reader in Readers.Where(x => x.Installed))
        {
            string? problem;
            try { problem = reader.Bind(); }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { problem = "binding threw " + error.GetType().Name + ": " + error.Message; }
            installed.Add(reader);
            if (problem == null)
            {
                // Say what the binding means: favorites count only for YAVSOS's own stash, and AzuEPI's only while its favoriting is on.
                log?.LogInfo(Chainloader.PluginInfos.ContainsKey(AzuAutoStoreIntegration.AzuGuid)
                    ? reader.Name + " found; AzuAutoStore does the stashing and keeps its own favorites."
                    : reader.Counts ? "Stash keeps " + reader.Name + "'s favorites in your inventory."
                    : reader.Name + " found; its favorites aren't read while Quick Stack Store is loaded, which turns its favoriting off.");
                continue;
            }
            Broken[reader] = problem;
            Fail(reader, problem);
        }
    }

    /// <summary>
    /// The favorites of every installed favorites mod in the player's <paramref name="inventory"/>. A mod that can't be
    /// read adds nothing; its failure is logged once per cause and queued for the next stash result.
    /// </summary>
    internal static Found Read(Player player, Inventory inventory)
    {
        if (installed == null) Initialize(log!);
        if (installed!.Count == 0) return Found.None;
        Dictionary<ItemDrop.ItemData, string>? items = null; Dictionary<int, string>? cells = null;
        foreach (var reader in installed)
        {
            if (!reader.Counts) continue;
            if (Broken.ContainsKey(reader)) { Queue(reader); continue; }
            items ??= new Dictionary<ItemDrop.ItemData, string>(); cells ??= new Dictionary<int, string>();
            var marked = new HashSet<ItemDrop.ItemData>(); var slots = new HashSet<int>();
            try
            {
#if YAVSOS_LAB
                if (LabThrow) throw new InvalidOperationException("Lab fault: favorites reader (D088)");
#endif
                reader.Read(player, inventory, marked, slots);
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                Fail(reader, cause is InvalidOperationException ? cause.Message : cause.GetType().Name + ": " + cause.Message);
            }
            // What a read added before a throw still counts; the first mod that marks a stack or cell names it.
            foreach (var item in marked) if (!items.ContainsKey(item)) items.Add(item, reader.Name);
            foreach (var cell in slots) if (!cells.ContainsKey(cell)) cells.Add(cell, reader.Name);
        }
        return items == null ? Found.None : new Found(items, cells!, inventory.GetWidth());
    }

    /// <summary>
    /// The names of favorites mods that couldn't be read and that no stash result has named yet: each mod once per game
    /// session, so a mod that stays unreadable doesn't repeat the warning on every stash.
    /// </summary>
    internal static IReadOnlyList<string> TakeUnreadable()
    {
        if (Unshown.Count == 0) return Array.Empty<string>();
        var names = Unshown.ToArray();
        Unshown.Clear();
        return names;
    }

    private static void Fail(Reader reader, string reason)
    {
        if (Logged.Add(reader.Name + "|" + reason.Split(':')[0]))
            log?.LogWarning(reader.Name + "'s favorites could not be read (" + reason + "); the stash may take them like any other item.");
        Queue(reader);
    }

    private static void Queue(Reader reader)
    {
        if (Shown.Add(reader.Name)) Unshown.Add(reader.Name);
    }

#if YAVSOS_LAB
    /// <summary>D088: every reader throws, as a mod that changed would; the next stash result warns again.</summary>
    internal static bool LabThrow;
    internal static string LabFault(bool on)
    {
        LabThrow = on; Shown.Clear(); Unshown.Clear(); Logged.Clear();
        return "throw=" + on;
    }
    internal static string LabStatus() => installed == null ? "not initialized"
        : string.Join("; ", installed.Select(x => x.Name + (Broken.TryGetValue(x, out var why) ? " broken: " + why : x.Counts ? " read" : " not counted")));

    /// <summary>
    /// Lab fixture: marks a favorite through the mod's own API, as its Alt+click would: <paramref name="what"/> "item"
    /// favorites the stack at (x, y), "slot" the slot; <paramref name="on"/> false removes the mark.
    /// </summary>
    internal static string LabMark(string mod, string what, int x, int y, bool on)
    {
        var player = Player.m_localPlayer; if (!player) return "no player";
        var inventory = player.GetInventory(); var item = inventory.GetItemAt(x, y);
        if (what == "item" && item == null) return "no stack at " + x + "," + y;
        switch (mod)
        {
            case "iff":
            {
                var api = AccessTools.TypeByName("ItemFavoriteFramework.API"); if (api == null) return "ItemFavoriteFramework not loaded";
                var done = what == "item"
                    ? AccessTools.Method(api, "SetItemFavorited").Invoke(null, new object[] { item!, on })
                    : AccessTools.Method(api, "SetSlotFavorited").Invoke(null, new object[] { x, y, on });
                return "iff " + what + "=" + done;
            }
            case "qss": case "epi":
            {
                var type = AccessTools.TypeByName(mod == "qss" ? "QuickStackStore.UserConfig" : "AzuEPI.Game.Favoriting.UserConfig");
                if (type == null) return mod + " not loaded";
                var config = AccessTools.Method(type, "GetPlayerConfig").Invoke(null, new object[] { player.GetPlayerID() });
                if (what == "item")
                {
                    var marked = (bool)AccessTools.Method(type, "IsItemNameFavorited").Invoke(config, new object[] { item!.m_shared });
                    if (marked != on) AccessTools.Method(type, "ToggleItemNameFavoriting").Invoke(config, new object[] { item.m_shared });
                }
                else
                {
                    var marked = (bool)AccessTools.Method(type, "IsSlotFavorited").Invoke(config, new object[] { new Vector2i(x, y) });
                    if (marked != on) AccessTools.Method(type, "ToggleSlotFavoriting").Invoke(config, new object[] { new Vector2i(x, y) });
                }
                return mod + " " + what + " " + on;
            }
            default: return "unknown mod " + mod + " (iff, qss, epi)";
        }
    }
#endif

    private static void AddCell(Inventory inventory, int x, int y, HashSet<int> cells)
    {
        if (x >= 0 && y >= 0 && x < inventory.GetWidth() && y < inventory.GetHeight()) cells.Add(y * inventory.GetWidth() + x);
    }

    private static T? Static<T>(Type type, string name, params Type[] parameters) where T : Delegate
    {
        var method = AccessTools.DeclaredMethod(type, name, parameters);
        if (method is not { IsStatic: true }) return null;
        try { return (T)Delegate.CreateDelegate(typeof(T), method); }
        catch (ArgumentException) { return null; } // Another return or parameter type.
    }
}
