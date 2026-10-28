using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace YAVSOS.Integrations;

/// <summary>
/// Process-lifetime compatibility registration, separate from read-only discovery. Uses the upstream
/// batch and recipe application, never BatchSetup/SetupOnChange, inventory callbacks or a server-role patch.
/// Reflection keeps optional assemblies out of eagerly resolved signatures and permits detached lifecycle checks.
/// </summary>
internal sealed class OptionalEpicLootRegistration : IDisposable
{
    private readonly MethodInfo apply;
    private readonly EventInfo registered;
    private readonly Action invalidate;
    private readonly Action<string> warning;
    private readonly Action<IReadOnlyList<string>> verify;
    private readonly List<(EventInfo Event, object Config)> configs = new();
    private readonly Action onRegistered;
    private readonly EventHandler onChanged;
    private readonly IReadOnlyList<string> names;
    private bool dirty, disposed, registrationObserved;
    internal bool Faulted { get; private set; }
    /// <summary>
    /// Catalog capture waits while a recipe change is being applied. A faulted adapter is off, not pending: only its own
    /// items can lack evidence, and every other category still becomes ready (D086 D1).
    /// </summary>
    internal bool Pending => dirty && !Faulted;

    private OptionalEpicLootRegistration(MethodInfo apply, EventInfo registered, IReadOnlyList<string> names,
        Action invalidate, Action<string> warning, Action<IReadOnlyList<string>> verify)
    {
        this.apply = apply; this.registered = registered; this.names = names;
        this.invalidate = invalidate; this.warning = warning; this.verify = verify;
        onRegistered = () => { registrationObserved = true; Apply(); };
        onChanged = (_, _) => { dirty = true; invalidate(); };
    }

    internal static OptionalEpicLootRegistration? TryInstall(bool headless, string? epicVersion, string? jotunnVersion,
        OptionalTypeResolver resolve, Action invalidate, Action<string> warning, out string diagnostic,
        Action<Type, IReadOnlyList<string>>? preflight = null, Action<IReadOnlyList<string>>? verify = null)
    {
        diagnostic = "not required: EpicLoot absent";
        if (epicVersion == null) return null;
        diagnostic = "Jotunn absent; actual catalog admission remains strict";
        // Support is decided by the loader/manager shape below, never by the version number (D058).
        if (jotunnVersion == null) return null;
        OptionalEpicLootRegistration? adapter = null;
        try
        {
            var loader = resolve("Common.ItemBatchLoader") ?? throw new MissingMemberException();
            var definition = resolve("Common.ItemDefinition") ?? throw new MissingMemberException();
            var manager = resolve("Jotunn.Managers.ItemManager") ?? throw new MissingMemberException();
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var batch = loader.GetMethod("BatchAddItems", statics, null, Type.EmptyTypes, null);
            var apply = loader.GetMethod("ReapplyAllRecipeConfig", statics, null, Type.EmptyTypes, null);
            var registered = manager.GetEvent("OnItemsRegistered", BindingFlags.Static | BindingFlags.Public);
            var instance = manager.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var get = manager.GetMethod("GetItem", new[] { typeof(string) });
            if (batch?.ReturnType != typeof(bool) || apply?.ReturnType != typeof(void) ||
                registered?.EventHandlerType != typeof(Action) || instance == null || get == null || get.ReturnType == typeof(void))
                throw new MissingMemberException();
            if (loader.GetField("resourceDefinitions", statics)?.GetValue(null) is not IList definitions ||
                definitions.Count is < 1 or > 64) throw new InvalidDataException("definition bound");
            var names = new List<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<object>();
            object Value(object owner, string property) => owner.GetType().GetProperty(property)?.GetValue(owner)
                ?? throw new InvalidDataException("missing definition/config value");
            foreach (var item in definitions)
            {
                if (item == null || item.GetType() != definition) throw new InvalidDataException("definition shape");
                var name = Value(item, "Prefab") as string;
                if (string.IsNullOrWhiteSpace(name) || name!.Length > 128 || !unique.Add(name))
                    throw new InvalidDataException("invalid/duplicate definition identity");
                // New stat/damage semantics require a separate reviewed adapter, never SetupOnChange's world scans.
                if (Value(item, "ModifableStats") is not IDictionary stats || stats.Count != 0 ||
                    item.GetType().GetProperty("DamageMods")?.GetValue(item) is IDictionary { Count: > 0 })
                    throw new InvalidDataException("unsupported stat/damage definitions");
                names.Add(name);
                foreach (var key in new[] { "CraftableCfg", "StationLVLCfg", "CraftAmountCfg", "CraftedAtCfg" }) entries.Add(Value(item, key));
                entries.Add(Value(Value(item, "Recipe"), "RecipeConfig"));
            }
            adapter = new OptionalEpicLootRegistration(apply!, registered!, names.AsReadOnly(), invalidate, warning, verify ?? (_ => { }));
            foreach (var config in entries)
            {
                var changed = config.GetType().GetEvent("SettingChanged");
                if (changed?.EventHandlerType != typeof(EventHandler)) throw new MissingMemberException("config event");
                adapter.configs.Add((changed, config));
            }
            var existing = 0;
            foreach (var name in names) if (get.Invoke(instance, new object[] { name }) != null) existing++;
            // D086 D1 (b): EpicLoot registering its items itself on a dedicated server is the shim's retirement case.
            if (headless && existing == names.Count)
            {
                adapter.Dispose();
                diagnostic = "not needed: EpicLoot registered its " + names.Count + " upstream recipe definitions itself (" + string.Join(", ", names) + ")";
                return null;
            }
            if (headless && existing != 0 || !headless && existing != names.Count)
                throw new InvalidDataException("unexpected partial registration; existing owner retained");
            if (headless)
            {
                preflight?.Invoke(loader, adapter.names);
                // Upstream returns true even when AddItem failed. Observe every postcondition independently.
                if (!Equals(batch!.Invoke(null, null), true)) throw new InvalidDataException("upstream batch refused");
                foreach (var name in names)
                    if (get.Invoke(instance, new object[] { name }) == null) throw new InvalidDataException("incomplete registration");
            }
            registered!.AddEventHandler(null, adapter.onRegistered);
            foreach (var entry in adapter.configs) entry.Event.AddEventHandler(entry.Config, adapter.onChanged);
            diagnostic = (headless ? "registered" : "observing") + " " + names.Count + " upstream recipe definitions; strict catalog admission unchanged";
            return adapter;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            adapter?.Dispose();
            // No automatic retry or live unregister after a partial upstream failure. Actual catalog checks still apply.
            diagnostic = "unavailable: " + (error is TargetInvocationException { InnerException: { } inner } ? inner : error).GetType().Name +
                "; actual catalog admission remains strict" + (adapter == null ? "" : " (" + string.Join(", ", adapter.names) + ")");
            return null;
        }
    }

    internal void Update(bool registryReady)
    {
        if (!disposed && !Faulted && dirty && registrationObserved && registryReady) Apply();
    }

    private void Apply()
    {
        if (disposed || Faulted) return;
        dirty = true; invalidate();
        try { apply.Invoke(null, null); verify(names); dirty = false; invalidate(); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            Faulted = true;
            warning("EpicLoot recipe compatibility application failed (" + (error is TargetInvocationException { InnerException: { } inner } ? inner : error).GetType().Name +
                "); only this compatibility is off. " + string.Join(", ", names) + " may get no categories on a dedicated server; every other category becomes ready.");
            invalidate();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; dirty = false;
        registered.RemoveEventHandler(null, onRegistered);
        foreach (var entry in configs) entry.Event.RemoveEventHandler(entry.Config, onChanged);
        configs.Clear();
        // Registered prefabs/recipes belong to the process lifetime; disposal never removes live item definitions.
    }
}
