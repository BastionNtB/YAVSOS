using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Transfers;

/// <summary>
/// Startup capability check for the exact native inventory reader (D057 O12, D058), replacing the former game-version
/// pin. Once per world, after ObjectDB has items, it builds a detached inventory from real loaded items with quality,
/// durability, crafter, world level, flags and custom data, saves it, validates the bytes with
/// <see cref="NativeTransferInventoryCodec"/>, loads them into a second inventory and requires every identity, cell and
/// the re-saved bytes to match exactly. Custom data another mod adds on load is allowed when everything of ours survives
/// unchanged and a second round trip is byte-identical. On failure only the features that capture inventories (YAVSOS's
/// own stash and cleanup) are disabled (B4.6).
/// Instance metadata of real items, including EpicLoot data, is still round-tripped per item at every capture.
/// Unity game thread only; touches no world inventory.
/// </summary>
internal static class NativeInventoryProbe
{
    internal const string Registration = "yavsos-native-probe";
    private const string ProbeKey = "com.bastionntb.yavsos.native-probe";
    private static bool ran;
    internal static bool Supported { get; private set; }
    /// <summary>The check has run this session, so <see cref="Supported"/> is its answer rather than "not yet".</summary>
    internal static bool Checked => ran;
    internal static string Status { get; private set; } = "inventory reader not checked yet";

    internal static void BeginSession() { ran = false; Supported = false; Status = "inventory reader not checked yet"; }

    /// <summary>Runs the check once ObjectDB is ready; returns true on the frame it ran.</summary>
    internal static bool Update(Action<string> info, Action<string> warning)
    {
        if (ran || !ObjectDB.instance || ObjectDB.instance.m_items == null || ObjectDB.instance.m_items.Count == 0 || !ZNetScene.instance)
            return false;
        ran = true;
        try
        {
            var detail = Check();
            Supported = true; Status = "inventory reader verified: " + detail;
            info(Status);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            Supported = false;
            Status = "inventory reader unavailable (" + error.GetType().Name + ": " + error.Message + "); YAVSOS's own stash and clean up are disabled, everything else works";
            warning(Status);
        }
        return true;
    }

    private static string Check()
    {
        var prefabs = ObjectDB.instance.m_items.Where(x => x && x.GetComponent<ItemDrop>()).OrderBy(x => x.name, StringComparer.Ordinal).ToList();
        var stackable = prefabs.FirstOrDefault(x => Shared(x).m_maxStackSize > 2)
            ?? throw new InvalidOperationException("no stackable sample item");
        var gear = prefabs.FirstOrDefault(x => Shared(x).m_maxStackSize == 1 && Shared(x).m_useDurability && Shared(x).m_maxQuality > 1)
            ?? prefabs.FirstOrDefault(x => Shared(x).m_maxStackSize == 1)
            ?? throw new InvalidOperationException("no single-stack sample item");
        const int width = 4, height = 2;
        var source = new Inventory("YAVSOS probe", null, width, height);
        Place(source, Sample(stackable, Math.Min(Shared(stackable).m_maxStackSize - 1, 7), false), 0, 0);
        Place(source, Sample(gear, 1, true), 2, 1);

        var saved = new ZPackage(); source.Save(saved); var bytes = saved.GetArray();
        NativeTransferInventoryCodec.Validate(bytes, width, height, Limits);
        var loaded = RoundTrip(bytes, width, height, out var resaved);
        var before = source.GetAllItems();
        var added = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in before)
        {
            GameInventorySnapshotAdapter.VerifyItem(item, Registration);
            var copy = Copy(loaded, item);
            // Another mod may add its own custom data when an inventory loads (EpicLoot's Inventory.Load postfix adds an
            // empty MagicItemComponent entry). Every field and entry of ours must survive unchanged; added keys are named.
            foreach (var key in copy.m_customData.Keys.Where(x => !item.m_customData.ContainsKey(x))) added.Add(key);
            var kept = copy.Clone(); kept.m_customData = copy.m_customData.Where(x => item.m_customData.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            if (!GameInventorySnapshotAdapter.Identity(kept, Registration).Equals(GameInventorySnapshotAdapter.Identity(item, Registration)))
                throw new InvalidOperationException(item.m_dropPrefab.name + " did not round-trip exactly");
        }
        if (added.Count == 0 && !resaved.SequenceEqual(bytes)) throw new InvalidOperationException("re-saved bytes differ");
        // Whatever the load added must be settled: a second round trip changes nothing at all.
        NativeTransferInventoryCodec.Validate(resaved, width, height, Limits);
        var again = RoundTrip(resaved, width, height, out var resavedAgain);
        if (!resavedAgain.SequenceEqual(resaved)) throw new InvalidOperationException("re-saved bytes keep changing");
        foreach (var item in loaded.GetAllItems())
            if (!GameInventorySnapshotAdapter.Identity(Copy(again, item), Registration).Equals(GameInventorySnapshotAdapter.Identity(item, Registration)))
                throw new InvalidOperationException(item.m_dropPrefab.name + " keeps changing on load");
        var note = added.Count == 0 ? "" : "; another mod adds on load: " + string.Join(", ", added);
        return $"{stackable.name} x{before[0].m_stack} and {gear.name} q{before[1].m_quality} round-trip exactly ({bytes.Length} bytes){note}";
    }

    private static Inventory RoundTrip(byte[] bytes, int width, int height, out byte[] resaved)
    {
        var loaded = new Inventory("YAVSOS probe copy", null, width, height);
        loaded.Load(new ZPackage(bytes));
        var package = new ZPackage(); loaded.Save(package); resaved = package.GetArray();
        return loaded;
    }

    private static ItemDrop.ItemData Copy(Inventory loaded, ItemDrop.ItemData item)
    {
        var copy = loaded.GetItemAt(item.m_gridPos.x, item.m_gridPos.y) ?? throw new InvalidOperationException("item cell changed");
        if (copy.m_stack != item.m_stack || copy.m_equipped != item.m_equipped || !copy.m_dropPrefab || loaded.GetAllItems().Count != 2)
            throw new InvalidOperationException(item.m_dropPrefab.name + " did not round-trip exactly");
        return copy;
    }

    private static ItemDrop.ItemData.SharedData Shared(GameObject prefab) => prefab.GetComponent<ItemDrop>().m_itemData.m_shared;

    private static ItemDrop.ItemData Sample(GameObject prefab, int stack, bool rich)
    {
        var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        item.m_dropPrefab = prefab; item.m_stack = stack; item.m_equipped = false;
        item.m_quality = rich ? Math.Min(2, item.m_shared.m_maxQuality) : 1;
        item.m_variant = rich && item.m_shared.m_variants > 1 ? 1 : 0;
        // Native saves keep durability in hundredths; a whole value round-trips without normalization.
        item.m_durability = item.m_shared.m_useDurability ? Mathf.Floor(item.GetMaxDurability() / 2) : item.m_durability;
        item.m_worldLevel = rich ? 1 : 0;
        item.m_pickedUp = rich;
        item.m_crafterID = rich ? 4242424242L : 0;
        item.m_crafterName = rich ? "YAVSOS probe ✓" : "";
        item.m_customData = rich
            ? new Dictionary<string, string> { [ProbeKey] = "{\"text\":\"ü\\n\",\"n\":3}", [ProbeKey + ".empty"] = "" }
            : new Dictionary<string, string>();
        return item;
    }

    private static void Place(Inventory inventory, ItemDrop.ItemData item, int x, int y)
    {
        if (!inventory.AddItem(item, new Vector2i(x, y)) || inventory.GetItemAt(x, y) != item)
            throw new InvalidOperationException("could not place " + item.m_dropPrefab.name);
    }

    private static NativeTransferPrefabLimits? Limits(int hash)
    {
        var prefab = ObjectDB.instance.GetItemPrefab(hash);
        var data = prefab ? prefab.GetComponent<ItemDrop>()?.m_itemData : null;
        return data?.m_shared == null ? null
            : new NativeTransferPrefabLimits(data.m_shared.m_maxStackSize, data.m_shared.m_maxQuality, data.m_shared.m_variants);
    }
}
