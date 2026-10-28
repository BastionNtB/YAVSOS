using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Transfers;

namespace YAVSOS.Integrations;

internal enum AzuIntegrationState { Absent, Active, Disabled }

/// <summary>
/// P3-03: AzuAutoStore stashes; chest rules filter where it may put items (D042). Startup checks 1–2 decide
/// whether to patch at all; patching is all-or-nothing; a failed check disables only this integration and
/// Azu keeps working on its own rules. The runtime backstop refuses a single unapproved write and keeps the
/// integration running (D046). Unity game thread only.
/// </summary>
internal static class AzuAutoStoreIntegration
{
    internal const string AzuGuid = "Azumatt.AzuAutoStore";
    private const string HarmonyId = "com.bastionntb.yavsos.azuautostore";
    private static readonly ConditionalWeakTable<Inventory, Container> InventoryOwners = new ConditionalWeakTable<Inventory, Container>();
    private static readonly ChestRuleDecision[] NoDecisions = Array.Empty<ChestRuleDecision>();
    private static readonly HashSet<string> ReportedBackstops = new HashSet<string>(StringComparer.Ordinal);
    private static ManualLogSource? log;
    private static Harmony? harmony;
    private static int depth;
    private static string entry = "";
    private static float lastPendingNotice = float.NegativeInfinity;
    private static int haveItemSites, canStoreSites;

    internal static AzuIntegrationState State { get; private set; } = AzuIntegrationState.Absent;
    internal static string Status { get; private set; } = "Isolated YAVSOS stashing rules active.";
    private static FieldInfo? existingItemField;
    private static bool existingItemFailed;

    /// <summary>
    /// D101: AzuAutoStore's "Must Have Existing Item To Pull" (3.1.7: a static <c>ConfigEntry&lt;Toggle&gt;</c>, Off/On),
    /// which gates its store key, single-item key and ground pickup for a chest without a rule. Read each time, since the
    /// player can change it in Azu's config. Null when Azu is absent or the setting can't be read (the panel then makes
    /// no claim); a failed read is logged once and not tried again.
    /// </summary>
    internal static bool? ExistingItemOnly
    {
        get
        {
            if (existingItemField == null || existingItemFailed) return null;
            try
            {
                var value = (existingItemField.GetValue(null) as BepInEx.Configuration.ConfigEntryBase)?.BoxedValue;
                if (value is Enum toggle && toggle.GetType().Name == "Toggle")
                    switch (toggle.ToString()) { case "On": return true; case "Off": return false; }
                return null;
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                existingItemFailed = true;
                log?.LogWarning($"Reading AzuAutoStore's \"Must Have Existing Item To Pull\" failed ({error.GetType().Name}: {error.Message}); the rule panel no longer says what it does with a chest without a rule.");
                return null;
            }
        }
    }

    /// <summary>D047: set by the RossItemDrawers adapter; answers whether a reserved empty drawer wants the item.</summary>
    internal static Func<Container, ItemDrop.ItemData, bool>? ReservedDrawerWants { get; set; }

    internal static void Initialize(ManualLogSource logger)
    {
        log = logger;
        if (!Chainloader.PluginInfos.TryGetValue(AzuGuid, out var info) || info?.Instance == null) return;
        var version = info.Metadata.Version.ToString();
        var clock = Stopwatch.StartNew();
        try
        {
            var assembly = info.Instance.GetType().Assembly;
            AzuExclusions.Initialize(assembly, logger);
            YAVSOS.Cleanup.CleanupTriggers.BindAzuStoreKey(assembly);
            var members = Resolve(assembly, out var missing);
            if (!string.IsNullOrEmpty(ChestRuleStore.BindingFailure)) missing.Add(ChestRuleStore.BindingFailure);
            var shape = Gather(assembly, members, missing);
            var problems = AzuStoreCompatibility.Check(shape);
            if (problems.Count != 0) { Disable(version, problems); return; }
            harmony = new Harmony(HarmonyId);
            Apply(harmony, members);
            if (haveItemSites != 2 || canStoreSites != 1)
            { Disable(version, new[] { $"Transpilers replaced {haveItemSites} HaveItem and {canStoreSites} CanItemBeStored sites; expected 2 and 1." }); return; }
            State = AzuIntegrationState.Active;
            // Pay the one-time JIT and parser cost off the game thread, not on the first store key press.
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Warm());
            Status = $"YAVSOS/AzuAutoStore {version} integrated chest rules active.";
            logger.LogInfo($"{Status} Compatibility checks passed in {clock.ElapsedMilliseconds} ms.");
            ReportOtherPatchers(members, logger);
            AzuStoreOrder.Initialize(assembly, logger, method => Shape(method as MethodInfo));
        }
        catch (Exception ex) { Disable(version, new[] { "Integration failed: " + ex.GetType().Name + ": " + ex.Message }); }
    }

    private static void Disable(string version, IEnumerable<string> problems)
    {
        AzuStoreOrder.Disable();
        harmony?.UnpatchSelf(); harmony = null; depth = 0;
        State = AzuIntegrationState.Disabled;
        Status = $"AzuAutoStore {version} changed in ways YAVSOS does not recognise; chest rules have no stash effect. AzuAutoStore keeps working normally.";
        log?.LogWarning(Status + " " + string.Join(" ", problems.Take(12)));
    }

    private sealed class Members
    {
        internal MethodInfo? TryStore, AnyContainerWants, StoreHotkey, StoreThisItem, StoreDrop, ContainerAwake;
    }

    // Check 1: resolve by signature, never by version.
    private static Members Resolve(Assembly assembly, out List<string> missing)
    {
        var found = new List<string>(); missing = found;
        Type? Type(string name) { var type = assembly.GetType(name, false); if (type == null) found.Add(name); return type; }
        MethodInfo? Method(Type? type, string name, Type returns, params Type[] parameters)
        {
            var method = type == null ? null : AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null || method.ReturnType != returns) { found.Add($"{type?.FullName ?? "?"}::{name}"); return null; }
            return method;
        }
        var functions = Type("AzuAutoStore.Util.Functions");
        var boxes = Type("AzuAutoStore.Util.Boxes");
        var plugin = Type("AzuAutoStore.AzuAutoStorePlugin");
        existingItemField = plugin == null ? null : AccessTools.DeclaredField(plugin, "MustHaveExistingItemToPull");
        var muc = Type("AzuAutoStore.APIs.MUC.MUCCompat");
        Type("AzuAutoStore.Interfaces.IContainer");
        Method(boxes, "CanItemBeStored", typeof(bool), typeof(string), typeof(string));
        if (plugin != null && AccessTools.DeclaredField(plugin, "MustHaveExistingItemToPull") == null) found.Add("AzuAutoStorePlugin.MustHaveExistingItemToPull");
        if (muc != null && AccessTools.DeclaredProperty(muc, "CanRouteToOtherOwner")?.GetGetMethod(true) == null) found.Add("MUCCompat.CanRouteToOtherOwner");
        return new Members
        {
            TryStore = Method(functions, "TryStore", typeof(bool), typeof(Container), typeof(ItemDrop.ItemData).MakeByRefType(), typeof(bool), typeof(bool)),
            AnyContainerWants = Method(functions, "AnyContainerWants", typeof(bool), typeof(ItemDrop)),
            StoreHotkey = Method(functions, "TryStore", typeof(void)),
            StoreThisItem = Method(functions, "TryStoreThisItem", typeof(void), typeof(ItemDrop.ItemData), typeof(Inventory)),
            StoreDrop = Method(functions, "CheckItemDropInstanceAndStore", typeof(void), typeof(ItemDrop)),
            ContainerAwake = AccessTools.DeclaredMethod(typeof(Container), "Awake")
        };
    }

    // Check 2: original IL call graph facts, compared by the detached checker.
    private static AzuAssemblyShape Gather(Assembly assembly, Members members, List<string> missing)
    {
        if (members.ContainerAwake == null) missing.Add("Container::Awake");
        var contract = assembly.GetType("AzuAutoStore.Interfaces.IContainer", false);
        var implementers = contract == null ? Array.Empty<string>() : AccessTools.GetTypesFromAssembly(assembly)
            .Where(x => x != contract && contract.IsAssignableFrom(x) && !x.IsInterface).Select(x => x.FullName).ToArray();
        var callers = new List<string>();
        foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("AzuAutoStore", StringComparison.Ordinal)) continue;
            foreach (var method in type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AccessTools.allDeclared)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
                if (Calls(method).Any(AzuStoreCompatibility.IsWriteSink)) callers.Add(Name(method));
            }
        }
        return new AzuAssemblyShape(missing, implementers, Shape(members.TryStore), Shape(members.AnyContainerWants), callers);
    }

    private static AzuMethodShape? Shape(MethodInfo? method) => method == null ? null :
        new AzuMethodShape(Name(method), Calls(method), method.GetMethodBody()?.LocalVariables.Select(x => x.LocalType.Name) ?? Enumerable.Empty<string>());

    private static IEnumerable<string> Calls(MethodBase method)
    {
        List<KeyValuePair<OpCode, object>> code;
        try { code = PatchProcessor.ReadMethodBody(method).ToList(); }
        catch { yield break; }
        foreach (var instruction in code)
            if (instruction.Value is MethodBase callee && (instruction.Key == OpCodes.Call || instruction.Key == OpCodes.Callvirt || instruction.Key == OpCodes.Newobj))
                yield return Name(callee);
    }

    internal static string Name(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "::" + method.Name;

    private static void Apply(Harmony patcher, Members members)
    {
        haveItemSites = 0; canStoreSites = 0;
        var self = typeof(AzuAutoStoreIntegration);
        HarmonyMethod Hook(string name, int priority = Priority.Normal) => new HarmonyMethod(AccessTools.DeclaredMethod(self, name)) { priority = priority };
        patcher.Patch(members.ContainerAwake, postfix: Hook(nameof(RememberInventory)));
        patcher.Patch(members.TryStore, prefix: Hook(nameof(TryStorePrefix)), transpiler: Hook(nameof(TryStoreTranspiler)));
        patcher.Patch(members.AnyContainerWants, transpiler: Hook(nameof(AnyContainerWantsTranspiler)));
        foreach (var method in new[] { members.StoreHotkey, members.StoreThisItem, members.StoreDrop })
            patcher.Patch(method, prefix: Hook(nameof(EnterAzu), Priority.First), finalizer: Hook(nameof(LeaveAzu)));
        foreach (var add in new[]
        {
            AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) }),
            AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })
        })
            // Runs before the bundled MultiUserChest intercept, which respects __runOriginal (Azu 3.1.7).
            patcher.Patch(add ?? throw new MissingMethodException("Inventory", "AddItem"), prefix: Hook(nameof(Backstop), Priority.First));
    }

    private static void ReportOtherPatchers(Members members, ManualLogSource logger)
    {
        foreach (var method in new[] { members.TryStore, members.AnyContainerWants })
        {
            var info = method == null ? null : Harmony.GetPatchInfo(method);
            var owners = info?.Owners.Where(x => x != HarmonyId && x != AzuGuid).ToArray() ?? Array.Empty<string>();
            if (owners.Length != 0) logger.LogInfo($"Other patches on {Name(method!)}: {string.Join(", ", owners)}.");
        }
    }

    // ---- decisions ----

    // Never throw into Azu's loops: an unexpected failure reads as an unreadable rule (rejects), logged once.
    internal static ChestRuleDecision Decide(Container? chest, ItemDrop.ItemData? item)
    {
        var started = Stopwatch.GetTimestamp();
        try { return DecideUnguarded(chest, item); }
        catch (Exception ex)
        {
            if (ReportedBackstops.Add("decide:" + ex.GetType().Name)) log?.LogWarning("Chest rule evaluation failed (" + ex.GetType().Name + "); the chest is treated as accepting nothing from AzuAutoStore.");
            return ChestRuleDecision.RejectUnreadable;
        }
        finally { Decisions++; DecisionTicks += Stopwatch.GetTimestamp() - started; }
    }

    // P3-08 cost counters (cheap; read through RuleLab.Stats).
    internal static long Decisions, DecisionTicks, BackstopRefusals;
#if YAVSOS_LAB
    /// <summary>Lab fixture only (A-Z4): let H1 pass so the backstop can be observed refusing. Default off.</summary>
    internal static bool DiagnosticSkipH1;
    /// <summary>Lab fixture only (A-R2): decisions see categories as not ready. Default off.</summary>
    internal static bool DiagnosticCategoriesPending;
#endif

    private static ChestRuleDecision DecideUnguarded(Container? chest, ItemDrop.ItemData? item)
    {
        if (!chest || item == null) return ChestRuleDecision.NoRule;
        var payload = ChestRuleStore.Payload(chest!);
        if (payload.Length == 0) return ChestRuleDecision.NoRule;
        var plugin = Plugin.Instance;
        var prefab = item.m_dropPrefab ? item.m_dropPrefab.name : null;
        if (plugin == null || prefab == null) return ChestRuleDecision.RejectUnreadable;
#if YAVSOS_LAB
        var answers = DiagnosticCategoriesPending ? NoAnswers.Instance : plugin.CategoryAnswers ?? NoAnswers.Instance;
#else
        var answers = plugin.CategoryAnswers ?? NoAnswers.Instance;
#endif
        // P6-07b: the item's own rarity and condition, read only when this chest's rule has an instance filter.
        return plugin.RuleEvaluator.Evaluate(payload, prefab, plugin.RuleTaxonomy, answers, plugin.InstanceFacetsFor(payload, prefab, item, answers));
    }

    private static void Warm()
    {
        try
        {
            var taxonomy = DefaultClassificationTaxonomy.Create();
            var payload = YAVSOS.Core.Transfers.ContainerPolicyEnvelope.Serialize(1, new ChestRules(taxonomy, ChestRuleState.Configured,
                new[] { new ClassificationFilterRule(taxonomy, new[] { "misc" }) }, new[] { "Wood" }, new[] { "Stone" }));
            var evaluator = new ChestRuleEvaluator(4, 4);
            evaluator.Evaluate(payload, "Wood", taxonomy, NoAnswers.Instance);
            evaluator.Evaluate(payload, "Copper", taxonomy, NoAnswers.Instance);
        }
        catch { /* Warm-up only; real evaluation reports its own failures. */ }
    }

    private sealed class NoAnswers : ICategoryAnswers
    {
        internal static readonly NoAnswers Instance = new NoAnswers();
        public long Generation => long.MinValue;
        public bool CategoriesReady => false;
        public ClassificationResult? Resolve(string prefab) => null;
    }

    private static void NoticePending()
    {
        // D048: one short, rate-limited notice rather than one per item.
        if (Time.unscaledTime - lastPendingNotice < 30f || !Player.m_localPlayer) return;
        lastPendingNotice = Time.unscaledTime;
        Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "YAVSOS: categories are still initializing; category chests were skipped.");
    }

    // ---- patches ----

    private static void RememberInventory(Container __instance)
    {
        var inventory = __instance ? __instance.GetInventory() : null;
        if (inventory != null) InventoryOwners.AddOrUpdate(inventory, __instance!);
    }

    // H1
    private static bool TryStorePrefix(Container nearbyContainer, ref ItemDrop.ItemData item, ref bool __result)
    {
#if YAVSOS_LAB
        if (DiagnosticSkipH1) return true;
#endif
        // D070: YAVSOS's chest order. Not a rule decision, so it never shows the categories notice.
        if (nearbyContainer && item != null && AzuStoreOrder.Refuses(nearbyContainer, item)) { __result = false; return false; }
        var decision = Decide(nearbyContainer, item);
        if (AzuStoreCompatibility.MayTry(decision)) return true;
        if (decision == ChestRuleDecision.RejectCategoriesPending) NoticePending();
        __result = false;
        return false;
    }

    // H2 replacement in TryStore: stack (Inventory, string, bool) + (Container, ref ItemData).
    private static bool HaveItemInTryStore(Inventory inventory, string name, bool matchWorldLevel, Container chest, ref ItemDrop.ItemData item) =>
        HaveItem(inventory, name, matchWorldLevel, chest, item);

    // H2 replacement in AnyContainerWants: stack (Inventory, string, bool) + (Container, ItemDrop).
    private static bool HaveItemInPull(Inventory inventory, string name, bool matchWorldLevel, Container chest, ItemDrop drop) =>
        HaveItem(inventory, name, matchWorldLevel, chest, drop ? drop.m_itemData : null);

    private static bool HaveItem(Inventory inventory, string name, bool matchWorldLevel, Container chest, ItemDrop.ItemData? item)
    {
        var original = inventory.HaveItem(name, matchWorldLevel);
        if (original || item == null) return original;
#if YAVSOS_LAB
        if (DiagnosticSkipH1) return true;
#endif
        var reserved = false;
        try { reserved = ReservedDrawerWants?.Invoke(chest, item) == true; }
        catch (Exception ex) { ReservedDrawerWants = null; log?.LogWarning("Reserved drawer support disabled: " + ex.GetType().Name); }
        return AzuStoreCompatibility.HasItem(original, Decide(chest, item), reserved);
    }

    // H3 replacement in AnyContainerWants: stack (string, string) + (Container, ItemDrop). Calls Azu's own check first.
    private static Func<string, string, bool>? canItemBeStored;
    private static bool CanItemBeStoredInPull(string container, string prefab, Container chest, ItemDrop drop) =>
        AzuStoreCompatibility.CanStore(canItemBeStored!(container, prefab), Decide(chest, drop ? drop.m_itemData : null));

    private static IEnumerable<CodeInstruction> TryStoreTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var replacement = AccessTools.DeclaredMethod(typeof(AzuAutoStoreIntegration), nameof(HaveItemInTryStore));
        foreach (var instruction in instructions)
        {
            if (IsCall(instruction, AzuStoreCompatibility.HaveItem))
            {
                haveItemSites++;
                var first = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                yield return first;
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Call, replacement);
                continue;
            }
            yield return instruction;
        }
    }

    private static IEnumerable<CodeInstruction> AnyContainerWantsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        // The container local is the one loaded for its single GetInventory() call.
        var inventoryCall = code.FindIndex(x => IsCall(x, AzuStoreCompatibility.GetInventory));
        if (inventoryCall < 1 || !IsLoadLocal(code[inventoryCall - 1].opcode)) return code;
        var loadChest = code[inventoryCall - 1];
        var haveItem = AccessTools.DeclaredMethod(typeof(AzuAutoStoreIntegration), nameof(HaveItemInPull));
        var canStore = AccessTools.DeclaredMethod(typeof(AzuAutoStoreIntegration), nameof(CanItemBeStoredInPull));
        var result = new List<CodeInstruction>(code.Count + 8);
        foreach (var instruction in code)
        {
            var isHave = IsCall(instruction, AzuStoreCompatibility.HaveItem);
            var isStore = IsCall(instruction, AzuStoreCompatibility.CanItemBeStored);
            if (!isHave && !isStore) { result.Add(instruction); continue; }
            if (isStore) { canItemBeStored ??= AccessTools.MethodDelegate<Func<string, string, bool>>((MethodInfo)instruction.operand); canStoreSites++; }
            else haveItemSites++;
            result.Add(new CodeInstruction(loadChest.opcode, loadChest.operand).MoveLabelsFrom(instruction));
            result.Add(new CodeInstruction(OpCodes.Ldarg_0));
            result.Add(new CodeInstruction(OpCodes.Call, isHave ? haveItem : canStore));
        }
        return result;
    }

    private static bool IsLoadLocal(OpCode opcode) => opcode == OpCodes.Ldloc || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc_0 ||
        opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3;

    private static bool IsCall(CodeInstruction instruction, string callee) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodBase method && Name(method) == callee;

    private static void EnterAzu(MethodBase __originalMethod) { if (depth++ == 0) entry = __originalMethod.Name; }
    private static Exception? LeaveAzu(Exception? __exception) { if (depth > 0) depth--; return __exception; }

    // D046 backstop: refuse the one write; the item stays with the player (verified for Azu 3.1.7's paths).
    private static void Backstop(Inventory __instance, ItemDrop.ItemData item, ref bool __runOriginal, ref bool __result)
    {
        if (depth == 0 || !__runOriginal || item == null || !InventoryOwners.TryGetValue(__instance, out var chest)) return;
        if (!AzuStoreCompatibility.RefuseWrite(depth, Decide(chest, item))) return;
        __runOriginal = false; __result = false; BackstopRefusals++;
        if (ReportedBackstops.Add(entry))
            log?.LogWarning($"AzuAutoStore tried to put an item into a ruled chest outside the checked path ({entry}); the write was refused and the item stayed where it was. Chest rules remain active.");
    }
}
