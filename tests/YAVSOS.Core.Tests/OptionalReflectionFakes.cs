using System;
using System.Collections.Generic;

// Detached stand-ins for the pinned optional APIs. Jotunn shapes are resolved by explicit test maps; the EpicLoot
// component/info names must match because the stored custom-data key is derived from the real type name.
namespace YAVSOS.Core.Tests.OptionalFakes
{
    public sealed class FakeGameObject { }
    public sealed class FakePlugin
    {
        public FakePlugin(string guid) { GUID = guid; }
        public string GUID { get; }
    }
    public abstract class FakeEntity
    {
        protected FakeEntity(FakePlugin? owner) { SourceMod = owner; }
        public FakePlugin? SourceMod { get; }
    }
    public sealed class FakeItem : FakeEntity
    {
        public FakeItem(FakeGameObject prefab, FakePlugin? owner) : base(owner) { ItemPrefab = prefab; }
        public FakeGameObject ItemPrefab { get; }
    }
    public sealed class FakePiece : FakeEntity
    {
        public FakePiece(FakeGameObject prefab, FakePlugin? owner) : base(owner) { PiecePrefab = prefab; }
        public FakeGameObject PiecePrefab { get; }
    }
    public sealed class FakeCreature : FakeEntity
    {
        public FakeCreature(FakeGameObject prefab, FakePlugin? owner) : base(owner) { Prefab = prefab; }
        public FakeGameObject Prefab { get; }
    }
    public sealed class FakeItemManager
    {
        public static FakeItemManager Instance { get; } = new FakeItemManager();
        public Dictionary<string, FakeItem> Items { get; } = new Dictionary<string, FakeItem>(StringComparer.Ordinal);
        public FakeItem? GetItem(string name) => Items.TryGetValue(name, out var item) ? item : null;
    }
    public sealed class FakePieceManager
    {
        public static FakePieceManager Instance { get; } = new FakePieceManager();
        public Dictionary<string, FakePiece> Pieces { get; } = new Dictionary<string, FakePiece>(StringComparer.Ordinal);
        public FakePiece? GetPiece(string name) => Pieces.TryGetValue(name, out var piece) ? piece : null;
    }
    public sealed class FakeCreatureManager
    {
        public static FakeCreatureManager Instance { get; } = new FakeCreatureManager();
        public List<(string Name, FakeCreature Creature)> Creatures { get; } = new List<(string, FakeCreature)>();
        public FakeCreature? GetCreature(string name) => Creatures.Find(x => x.Name == name).Creature;
    }
    public sealed class FakeThrowingCreatureManager
    {
        public static FakeThrowingCreatureManager Instance { get; } = new FakeThrowingCreatureManager();
        public FakeCreature? GetCreature(string name) => throw new InvalidOperationException("registry unavailable");
    }
    public sealed class FakeUntypedItemManager
    {
        public static FakeUntypedItemManager Instance { get; } = new FakeUntypedItemManager();
        public object? GetItem(string name) => null;
    }
    public sealed class FakeItemData { }
    public enum FakeOtherRarity { Common, Magic, Rare, Epic, Legendary, Mythic }
}

namespace EpicLoot.Data
{
    public abstract class CustomItemData { }
    public class ItemInfo
    {
        internal const string modGuid = "randyknapp.mods.epicloot";
        internal static string dataKey(string key) => modGuid + "#" + key;
    }
}

namespace EpicLoot
{
    public enum ItemRarity { Magic, Rare, Epic, Legendary, Mythic, Ancient }
    public class MagicItem
    {
        public int Version = 3;
        public ItemRarity Rarity;
        public bool IsUnidentified;
    }
    public class MagicItemComponent : EpicLoot.Data.CustomItemData
    {
        public MagicItem? MagicItem;
    }
}

namespace EpicLoot.ShardStones
{
    public enum ShardType { Red, Custom, None }
    public class ShardDefinition
    {
        public List<EpicLoot.ItemRarity> Rarities = new();
    }
    public static class Shards
    {
        public static class ShardDefinitions
        {
            public static Dictionary<ShardType, ShardDefinition> Current = new();
            public static bool ThrowOnRead;
            public static int Reads;
            public static Dictionary<ShardType, ShardDefinition> ShardEffects
            {
                get { Reads++; return ThrowOnRead ? throw new InvalidOperationException("registry unavailable") : Current; }
            }
        }
    }
}

namespace EpicLoot.Crafting
{
    using YAVSOS.Core.Tests.OptionalFakes;
    public static class CraftingItemExtensions
    {
        public static bool IsMagicCraftingMaterial(FakeItemData item) => false;
        public static bool IsRunestone(FakeItemData item) => false;
        public static EpicLoot.ItemRarity GetCraftingMaterialRarity(FakeItemData item) => EpicLoot.ItemRarity.Magic;
    }
}

namespace YAVSOS.Core.Tests.OptionalFakes
{
    // P8-20: EpicLoot 0.15.4's shape: a standalone component and the stored key published as a constant.
    public sealed class FakeStandaloneMagicItemComponent { public EpicLoot.MagicItem? MagicItem; }
    public static class FakeMagicItemData { public const string Key = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent"; }
    public static class FakeOtherMagicItemData { public const string Key = "randyknapp.mods.epicloot#EpicLoot.Other"; }
}
