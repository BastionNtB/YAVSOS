using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Transfers;

/// <summary>
/// Session-owned, game-thread snapshot/selection binding. This class neither refreshes
/// a chest nor establishes authority. Its caller supplies fresh owner/access and
/// capability checks before capture and repeats them at the write boundary.
/// </summary>
internal sealed class GameInventorySnapshotAdapter : IDisposable
{
    internal const string NativeAdapter = "valheim-item-110-exact-1";
    private readonly Dictionary<Inventory, InventoryBinding> bindings = new();
    // D061 Q11: one representative per stacking class, compared with the game's own ItemData.IsSameType (and any
    // mod's patch of it), the check MoveItemToThis applies at a chosen cell. Classes hold for this adapter's run.
    private readonly List<ItemDrop.ItemData> mergeClasses = new();
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private bool disposed;

    /// <param name="keepUncapturable">
    /// D086 D16 (U3): a stack that can't be captured exactly (unverified registration, a native save that changes it, state
    /// the format can't hold) stays in the snapshot as a stack that is never moved or merged into, holding its cell, and is
    /// listed in <see cref="GameInventoryCapture.Uncapturable"/>; the rest of the inventory is captured. Otherwise any such
    /// stack fails the whole capture.
    /// </param>
    internal GameInventoryCapture Capture(Inventory inventory, string inventoryId, long owner,
        ushort ownerRevision, Func<ItemDrop.ItemData, string?> registration, Func<ItemDrop.ItemData, bool> protectedItem,
        IEnumerable<int> protectedCells, Func<bool> capabilityCurrent, bool keepUncapturable = false)
    {
        CheckThread();
        // The startup round-trip probe decides support, never the game version number (D058).
        if (!capabilityCurrent() || !NativeInventoryProbe.Supported ||
            inventory == null || inventory.GetType() != typeof(Inventory))
            throw new NotSupportedException("The native inventory capability is unavailable.");
        var width = inventory.GetWidth(); var height = inventory.GetHeight();
        // Native item saves encode grid coordinates as bytes and stack counts as ushorts.
        if (width < 1 || height < 1 || width > 256 || height > 256)
            throw new NotSupportedException("Inventory layout cannot round-trip through the native format.");
        var items = inventory.GetAllItems();
        if (items.Count > ushort.MaxValue || items.Count > width * height)
            throw new InvalidDataException("Inventory occupancy is unsupported.");
        var blocked = new HashSet<int>();
        foreach (var cell in protectedCells)
        {
            if (cell < 0 || cell >= width * height || blocked.Count >= width * height && !blocked.Contains(cell))
                throw new InvalidDataException("Protected cell is outside the native grid.");
            blocked.Add(cell);
        }
        if (!bindings.TryGetValue(inventory, out var binding))
        {
            if (bindings.Count >= TransferLimits.MaximumRunInventories)
                throw new InvalidOperationException("Run inventory binding limit reached.");
            binding = new InventoryBinding(); bindings.Add(inventory, binding);
        }
        var seen = new HashSet<ItemDrop.ItemData>(); var stacks = new List<TransferStack>(items.Count);
        var uncapturable = new List<string>();
        long estimatedBytes = 6;
        foreach (var item in items)
        {
            if (item == null || !seen.Add(item)) throw new InvalidDataException("Duplicate live item reference.");
            if (!binding.Handles.TryGetValue(item, out var handle)) binding.Handles.Add(item, handle = Guid.NewGuid());
            TransferItemIdentity identity;
            try
            {
                // D10: one preflight per item; the round trip compares the loaded copy field by field against this one.
                estimatedBytes += PreflightBytes(item);
                if (estimatedBytes > TransferLimits.MaximumInventoryPayloadBytes)
                    throw new InvalidDataException("Native inventory preimage exceeds its bound.");
                identity = Preflighted(item, registration(item));
                VerifyNativeRoundtrip(item, identity);
            }
            catch (Exception error) when (keepUncapturable && error is NotSupportedException or InvalidDataException or ArgumentException &&
                !(estimatedBytes > TransferLimits.MaximumInventoryPayloadBytes))
            {
                stacks.Add(Uncapturable(item, handle, width, height, item.m_equipped || item.m_shared?.m_questItem == true || protectedItem(item)));
                uncapturable.Add((item.m_dropPrefab ? item.m_dropPrefab.name : item.m_shared?.m_name ?? "unknown item") + " at (" +
                    item.m_gridPos.x + "," + item.m_gridPos.y + ") (" + error.Message.TrimEnd('.') + ")");
                continue;
            }
            stacks.Add(new TransferStack(handle, item.m_gridPos.x, item.m_gridPos.y, item.m_stack,
                item.m_shared.m_maxStackSize, identity, item.m_equipped || item.m_shared.m_questItem || protectedItem(item),
                mergeClass: MergeClass(item)));
        }
        foreach (var old in binding.Handles.Keys.Where(item => !seen.Contains(item)).ToArray()) binding.Handles.Remove(old);
        var package = new ZPackage(); inventory.Save(package); var bytes = package.GetArray();
        if (bytes.Length > TransferLimits.MaximumInventoryPayloadBytes)
            throw new InvalidDataException("Native inventory preimage exceeds its bound.");
        var fingerprint = Hash(bytes);
        if (binding.LastPayload != fingerprint)
        {
            if (binding.Epoch == ulong.MaxValue) throw new InvalidOperationException("Inventory epoch exhausted.");
            binding.Epoch++; binding.LastPayload = fingerprint;
        }
        var stamp = new TransferInventoryStamp(inventoryId, binding.Generation, binding.Epoch, 0,
            owner, ownerRevision, fingerprint);
        var snapshot = new TransferInventorySnapshot(stamp, width, height, stacks, blocked);
        // Fingerprint includes current item and slot protections, not only native bytes.
        if (!capabilityCurrent()) throw new NotSupportedException("Inventory capability changed during capture.");
        return new GameInventoryCapture(snapshot, uncapturable);
    }

    // A stack kept in place (D086 D16): it holds its cell, nothing merges into it, and no plan moves it. Its identity is a
    // stand-in unique to the stack, since its real state couldn't be captured; it never matches a real item.
    private static TransferStack Uncapturable(ItemDrop.ItemData item, Guid handle, int width, int height, bool isProtected)
    {
        var x = item.m_gridPos.x; var y = item.m_gridPos.y;
        if (x < 0 || y < 0 || x >= width || y >= height) throw new NotSupportedException("An unreadable item is outside the inventory grid.");
        var quantity = Math.Max(1, Math.Min(item.m_stack, ushort.MaxValue));
        var maximum = Math.Max(quantity, Math.Min(item.m_shared?.m_maxStackSize ?? 1, ushort.MaxValue));
        var prefab = item.m_dropPrefab ? item.m_dropPrefab.name : "unknown";
        var identity = new TransferItemIdentity(prefab.Length is > 0 and <= 256 ? prefab : "unknown", "uncapturable:" + handle.ToString("N"), 1, 0, 0, 0, 0, "",
            false, false, Array.Empty<KeyValuePair<string, string>>(), NativeAdapter, Array.Empty<byte>());
        // Equipped, quest and quick-slot items stay protected (kept silently), as when they can be captured.
        return new TransferStack(handle, x, y, quantity, maximum, identity, isProtected, transferSupported: false);
    }

    // Stackables only; null keeps the strict full-identity rule. A class needs IsSameType both ways, so a mod's
    // one-sided patch never lets YAVSOS merge what the game would refuse at the cell.
    private string? MergeClass(ItemDrop.ItemData item)
    {
        if (item.m_shared.m_maxStackSize <= 1) return null;
        for (var index = 0; index < mergeClasses.Count; index++)
            if (mergeClasses[index].IsSameType(item) && item.IsSameType(mergeClasses[index])) return "c" + index;
        if (mergeClasses.Count >= TransferLimits.MaximumTerminalEntries) return null;
        mergeClasses.Add(item.Clone());
        return "c" + (mergeClasses.Count - 1);
    }

    internal bool TryResolve(Inventory inventory, Guid instanceGeneration, Guid handle, out ItemDrop.ItemData? item)
    {
        CheckThread(); item = null;
        if (!bindings.TryGetValue(inventory, out var binding) || binding.Generation != instanceGeneration) return false;
        foreach (var pair in binding.Handles)
            if (pair.Value == handle && inventory.ContainsItem(pair.Key)) { item = pair.Key; return true; }
        return false;
    }

    /// <summary>The live item a handle from this inventory's captures stands for (P6-07b instance evidence); read only.</summary>
    internal bool TryItem(Inventory inventory, Guid handle, out ItemDrop.ItemData? item)
    {
        CheckThread(); item = null;
        return bindings.TryGetValue(inventory, out var binding) && TryResolve(inventory, binding.Generation, handle, out item);
    }

    /// <summary>The handle of a live item from this inventory's latest capture.</summary>
    internal bool TryHandle(Inventory inventory, ItemDrop.ItemData item, out Guid handle)
    {
        CheckThread(); handle = Guid.Empty;
        return item != null && bindings.TryGetValue(inventory, out var binding) && binding.Handles.TryGetValue(item, out handle);
    }

    internal static TransferItemIdentity Identity(ItemDrop.ItemData item, string? registration)
    {
        PreflightBytes(item);
        return Preflighted(item, registration);
    }
    // The identity of an item whose state the preflight (or an equal round-tripped original) has already bounded.
    private static TransferItemIdentity Preflighted(ItemDrop.ItemData item, string? registration)
    {
        if (string.IsNullOrEmpty(registration) || !item.m_dropPrefab)
            throw new NotSupportedException("Item registration is not currently verified.");
        return new TransferItemIdentity(item.m_dropPrefab.name, registration!, item.m_quality, item.m_variant,
            item.m_worldLevel, item.m_durability, item.m_crafterID, item.m_crafterName, item.m_pickedUp,
            item.m_cheated, item.m_customData, NativeAdapter, Array.Empty<byte>());
    }

    private static long PreflightBytes(ItemDrop.ItemData item)
    {
        if (item == null || item.GetType() != typeof(ItemDrop.ItemData) || item.m_shared == null ||
            item.m_stack < 1 || item.m_stack > ushort.MaxValue || item.m_stack > item.m_shared.m_maxStackSize ||
            item.m_shared.m_maxStackSize < 1 || item.m_shared.m_maxStackSize > ushort.MaxValue ||
            item.m_quality < 1 || item.m_quality > ushort.MaxValue || item.m_variant < 0 ||
            item.m_worldLevel < 0 || item.m_worldLevel > byte.MaxValue || item.m_gridPos.x < 0 ||
            item.m_gridPos.x > byte.MaxValue || item.m_gridPos.y < 0 || item.m_gridPos.y > byte.MaxValue ||
            float.IsNaN(item.m_durability) || float.IsInfinity(item.m_durability) || item.m_durability < 0 ||
            (double)item.m_durability * 100 > int.MaxValue || item.m_customData == null || item.m_customData.Count > 2048)
            throw new NotSupportedException("Item state cannot be represented by the native adapter.");
        // Overestimates all fixed fields, string prefixes and dictionary length encodings.
        long bytes = 128 + TextBytes(item.m_crafterName, 16384);
        foreach (var pair in item.m_customData) bytes += 10 + TextBytes(pair.Key, 16384) + TextBytes(pair.Value, 65536);
        if (bytes > TransferLimits.MaximumInventoryPayloadBytes) throw new InvalidDataException("Item metadata exceeds its bound.");
        return bytes;
    }
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static int TextBytes(string value, int maximum)
    {
        if (value == null || value.Length > maximum) throw new InvalidDataException("Invalid native item text.");
        var count = StrictUtf8.GetByteCount(value);
        if (count > maximum) throw new InvalidDataException("Native item text exceeds its bound.");
        return count;
    }
    internal static void VerifyItem(ItemDrop.ItemData item, string registration) =>
        VerifyNativeRoundtrip(item, Identity(item, registration));
    /// <summary>The most a native save may lower durability: one hundredth, plus float rounding.</summary>
    private const float DurabilitySaveStep = 0.011f;
    private static void VerifyNativeRoundtrip(ItemDrop.ItemData item, TransferItemIdentity identity)
    {
        var package = new ZPackage(); item.Save(package); var serialized = package.GetArray();
        if (serialized.Length > TransferLimits.MaximumInventoryPayloadBytes) throw new InvalidDataException("Item payload exceeds its bound.");
        var read = new ZPackage(serialized);
        var (prefabHash, loaded) = ItemDrop.ItemData.Load(read, global::Version.Item.NewCheats);
        if (read.GetPos() != serialized.Length || prefabHash != item.m_dropPrefab.name.GetStableHashCode())
            throw new InvalidDataException("Native item identity did not round-trip.");
        loaded.m_dropPrefab = item.m_dropPrefab; loaded.m_shared = item.m_shared;
        // The game saves durability as (int)(durability * 100), so a save can lower it by under a hundredth (78.5099945,
        // a loaded 78.51, saves as 78.50). That is the game's own rounding on every save, not a change by YAVSOS; refusing
        // it made any chest or inventory holding worn gear unusable (runtime 2026-10-21). Everything else must match.
        if (Math.Abs(loaded.m_durability - item.m_durability) > DurabilitySaveStep)
            throw new NotSupportedException("Native save changes item durability; this exact-state adapter refuses it.");
        loaded.m_durability = item.m_durability;
        if (loaded.m_stack != item.m_stack || loaded.m_gridPos != item.m_gridPos || loaded.m_equipped != item.m_equipped ||
            !identity.Equals(Preflighted(loaded, identity.Registration)))
            throw new NotSupportedException("Native save normalizes item state; this exact-state adapter refuses it.");
    }
    private static string Hash(byte[] bytes)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private void CheckThread()
    {
        if (disposed) throw new ObjectDisposedException(nameof(GameInventorySnapshotAdapter));
        if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("Game-thread capture required.");
    }
    public void Dispose() { CheckThread(); bindings.Clear(); mergeClasses.Clear(); disposed = true; }
    private sealed class InventoryBinding
    {
        internal readonly Guid Generation = Guid.NewGuid();
        internal readonly Dictionary<ItemDrop.ItemData, Guid> Handles = new();
        internal ulong Epoch;
        internal string LastPayload = "";
    }
}

/// <summary>One capture of a live inventory. D10: the native bytes are hashed into the snapshot's stamp and not kept.</summary>
internal sealed class GameInventoryCapture
{
    internal TransferInventorySnapshot Snapshot { get; }
    /// <summary>Stacks kept in place because they couldn't be captured exactly (D086 D16): the item, its cell and why.</summary>
    internal IReadOnlyList<string> Uncapturable { get; }
    internal GameInventoryCapture(TransferInventorySnapshot snapshot, IReadOnlyList<string>? uncapturable = null)
    { Snapshot = snapshot; Uncapturable = uncapturable ?? Array.Empty<string>(); }
}
