using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using YAVSOS.Core.Classification;

namespace YAVSOS.Integrations;

// Reflection-only optional-mod capability checks. No Unity, BepInEx or game types are referenced here, so the
// probes can be exercised against detached fakes. The host resolver supplies the plugin's own types plus the exact
// external types (UnityEngine.GameObject, BepInEx.BepInPlugin, ItemDrop+ItemData) that the pinned API exposes.
internal delegate Type? OptionalTypeResolver(string fullName);

internal static class OptionalFailure
{
    internal static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}

/// <summary>
/// Jotunn public managers (verified on 2.30.2): ItemManager.GetItem/PieceManager.GetPiece are dictionary lookups and
/// CreatureManager.GetCreature is a list search. An owner is reported only when the registered prefab is the
/// exact loaded object; a fault disables the adapter for the rest of the capture. Support is decided by the
/// API shape, never by the version number (D058).
/// </summary>
internal sealed class OptionalJotunnApi
{
    internal const string Adapter = "jotunn";
    internal const string PluginGuid = "com.jotunn.jotunn";
    internal const string VerifiedVersion = "2.30.2";
    private readonly Manager items, pieces, creatures;
    private readonly PropertyInfo sourceMod, guid;
    private readonly object[] argument = new object[1];
    internal bool Faulted { get; private set; }
    internal string Fault { get; private set; } = "";

    private sealed class Manager
    {
        internal PropertyInfo Instance = null!;
        internal MethodInfo Get = null!;
        internal PropertyInfo Prefab = null!;
    }
    private OptionalJotunnApi(Manager items, Manager pieces, Manager creatures, PropertyInfo sourceMod, PropertyInfo guid)
    { this.items = items; this.pieces = pieces; this.creatures = creatures; this.sourceMod = sourceMod; this.guid = guid; }

    internal static OptionalCapability Probe(string? version, OptionalTypeResolver? resolve, out OptionalJotunnApi? api)
    {
        api = null;
        if (version == null || resolve == null) return OptionalCapability.Absent(Adapter);
        OptionalTypeResolver types = resolve;
        try
        {
            var gameObject = Require(types, "UnityEngine.GameObject");
            var plugin = Require(types, "BepInEx.BepInPlugin");
            var entity = Require(types, "Jotunn.Entities.CustomEntity");
            var sourceMod = Property(entity, "SourceMod", plugin);
            var guid = Property(plugin, "GUID", typeof(string));
            Manager Bind(string managerName, string getter, string entityName, string prefab)
            {
                var manager = Require(types, managerName); var custom = Require(types, entityName);
                if (!custom.IsSubclassOf(entity)) throw new MissingMemberException(entityName + " is not a CustomEntity");
                var instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var get = manager.GetMethod(getter, BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (instance == null || instance.PropertyType != manager || instance.GetGetMethod() == null)
                    throw new MissingMemberException(managerName + ".Instance");
                if (get == null || get.ReturnType != custom) throw new MissingMemberException(managerName + "." + getter);
                return new Manager { Instance = instance, Get = get, Prefab = Property(custom, prefab, gameObject) };
            }
            api = new OptionalJotunnApi(Bind("Jotunn.Managers.ItemManager", "GetItem", "Jotunn.Entities.CustomItem", "ItemPrefab"),
                Bind("Jotunn.Managers.PieceManager", "GetPiece", "Jotunn.Entities.CustomPiece", "PiecePrefab"),
                Bind("Jotunn.Managers.CreatureManager", "GetCreature", "Jotunn.Entities.CustomCreature", "Prefab"), sourceMod, guid);
            return new OptionalCapability(Adapter, OptionalCapabilityState.Supported, version,
                "public ItemManager/PieceManager/CreatureManager lookup; exact loaded prefab identity; " + OptionalDefinitionEvidence.Version);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            return OptionalCapability.Incompatible(Adapter, version, "API shape: " + error.Message);
        }
    }

    internal string? ItemOwner(string name, object loaded) => Owner(items, name, loaded);
    internal string? PieceOwner(string name, object loaded) => Owner(pieces, name, loaded);
    internal string? CreatureOwner(string name, object loaded) => Owner(creatures, name, loaded);

    // The host runs its freshness guard outside this boundary: stale capture must still fail.
    internal void ReadDefinition(Action check, Action read)
    {
        check();
        if (Faulted) return;
        try { read(); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Disable(error); }
    }

    internal OptionalCapability CaptureCapability(OptionalCapability initial) => Faulted
        ? OptionalCapability.Incompatible(Adapter, initial.ObservedVersion, "faulted during capture: " + Fault)
        : initial;

    private void Disable(Exception error)
    {
        Faulted = true;
        Fault = (error is TargetInvocationException { InnerException: { } inner } ? inner : error).GetType().Name;
    }

    private string? Owner(Manager manager, string name, object loaded)
    {
        if (Faulted || name == null || loaded == null) return null;
        try
        {
            var instance = manager.Instance.GetValue(null);
            if (instance == null) return null;
            argument[0] = name;
            var entity = manager.Get.Invoke(instance, argument);
            argument[0] = null!;
            // Reference identity only: a same-named clone, renamed prefab or replaced registration is not ownership.
            if (entity == null || !ReferenceEquals(manager.Prefab.GetValue(entity), loaded)) return null;
            var metadata = sourceMod.GetValue(entity);
            return metadata == null ? null : guid.GetValue(metadata) as string;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            argument[0] = null!;
            Disable(error);
            return null;
        }
    }

    internal static Type Require(OptionalTypeResolver resolve, string name) =>
        resolve(name) ?? throw new MissingMemberException("type " + name);
    internal static PropertyInfo Property(Type owner, string name, Type type)
    {
        var property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property != null && property.PropertyType == type && property.GetGetMethod() != null && property.GetIndexParameters().Length == 0
            ? property : throw new MissingMemberException(owner.FullName + "." + name);
    }
}

/// <summary>
/// EpicLoot storage shape (verified on 0.14.13) used by <see cref="OptionalEpicLootRarity"/>. The probe only inspects
/// metadata; it never calls ItemData.Data(), GetMagicItem() or any EpicLoot method, which create cached components and
/// can save. Support is decided by the shape, never by the version number (D058).
/// </summary>
internal static class OptionalEpicLootApi
{
    internal static OptionalCapability Probe(string? version, OptionalTypeResolver? resolve)
    {
        const string adapter = OptionalEpicLootRarity.Adapter;
        if (version == null || resolve == null) return OptionalCapability.Absent(adapter);
        try
        {
            var component = OptionalJotunnApi.Require(resolve, "EpicLoot.MagicItemComponent");
            var magic = OptionalJotunnApi.Require(resolve, "EpicLoot.MagicItem");
            var rarity = OptionalJotunnApi.Require(resolve, "EpicLoot.ItemRarity");
            var crafting = OptionalJotunnApi.Require(resolve, "EpicLoot.Crafting.CraftingItemExtensions");
            var itemData = OptionalJotunnApi.Require(resolve, "ItemDrop+ItemData");
            // The stored key, in either of EpicLoot's shapes (D058: judged by what is there, not the version; P8-20).
            // 0.15.4 publishes it as MagicItemData.Key beside a standalone MagicItemComponent.
            if (resolve("EpicLoot.MagicItemData") is { } published)
            {
                var key = published.GetField("Key", BindingFlags.Public | BindingFlags.Static);
                if (key == null || !key.IsLiteral || key.GetRawConstantValue() as string != OptionalEpicLootRarity.InstanceDataKey || published.Assembly != component.Assembly)
                    throw new MissingMemberException("MagicItemData.Key");
            }
            else
            {
                // Up to 0.15.3: ItemInfo builds it, omitting an assembly suffix only for component types in its own assembly.
                var baseData = OptionalJotunnApi.Require(resolve, "EpicLoot.Data.CustomItemData");
                var info = OptionalJotunnApi.Require(resolve, "EpicLoot.Data.ItemInfo");
                if (component.FullName != "EpicLoot.MagicItemComponent" || component.BaseType != baseData || component.Assembly != info.Assembly)
                    throw new MissingMemberException("MagicItemComponent key shape");
                var modGuid = info.GetField("modGuid", BindingFlags.NonPublic | BindingFlags.Static);
                if (modGuid == null || !modGuid.IsLiteral || modGuid.GetRawConstantValue() as string != OptionalEpicLootRarity.PluginGuid)
                    throw new MissingMemberException("ItemInfo.modGuid");
                if (info.GetMethod("dataKey", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null)?.ReturnType != typeof(string))
                    throw new MissingMemberException("ItemInfo.dataKey");
            }
            Field(component, "MagicItem", magic);
            Field(magic, "Version", typeof(int));
            var rarityField = Field(magic, "Rarity", rarity);
            // Newtonsoft's default numeric enum form is the only supported stored shape.
            if (magic.GetCustomAttributesData().Concat(rarityField.GetCustomAttributesData()).Concat(rarity.GetCustomAttributesData())
                .Any(x => x.AttributeType.Name.Contains("JsonConverter") || x.AttributeType.Name.Contains("JsonProperty")))
                throw new MissingMemberException("custom MagicItem JSON contract");
            if (!rarity.IsEnum || Enum.GetUnderlyingType(rarity) != typeof(int))
                throw new MissingMemberException("ItemRarity underlying type");
            var names = OptionalEpicLootRarity.RarityNames;
            var values = Enum.GetValues(rarity).Cast<object>().Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture)).OrderBy(x => x).ToArray();
            if (!values.SequenceEqual(Enumerable.Range(0, names.Count)) ||
                names.Select((name, index) => Enum.GetName(rarity, index) == name).Any(x => !x))
                throw new MissingMemberException("ItemRarity declaration");
            foreach (var (method, result) in new[] { ("IsMagicCraftingMaterial", typeof(bool)), ("IsRunestone", typeof(bool)), ("GetCraftingMaterialRarity", rarity) })
                if (crafting.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { itemData }, null)?.ReturnType != result)
                    throw new MissingMemberException("CraftingItemExtensions." + method);
            return new OptionalCapability(adapter, OptionalCapabilityState.Supported, version,
                "read-only custom data " + OptionalEpicLootRarity.InstanceDataKey + "; MagicItem version " +
                OptionalEpicLootRarity.SupportedMagicItemVersion + "; " + OptionalEpicLootRarity.AdapterVersion);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            return OptionalCapability.Incompatible(adapter, version, "API shape: " + error.Message);
        }
    }

    private static FieldInfo Field(Type owner, string name, Type type)
    {
        var field = owner.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field != null && field.FieldType == type && !field.IsInitOnly ? field : throw new MissingMemberException(owner.FullName + "." + name);
    }
}
