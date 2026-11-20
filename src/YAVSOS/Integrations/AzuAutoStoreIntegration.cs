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
using YAVSOS.Core.Messages;
using YAVSOS.Cleanup;
using YAVSOS.Interface;

namespace YAVSOS.Integrations;

internal enum AzuIntegrationState { Absent, Active, Disabled }

/// <summary>
/// P3-03: AzuAutoStore stashes; chest rules filter where it may put items (D042). Startup checks 1–2 decide what to
/// patch, one hook at a time (D171, D058): a failed hook check turns off only that hook, and only a missing
/// <c>TryStore</c> or required game member turns the whole integration off, when Azu keeps working on its own rules.
/// The runtime backstop refuses a single unapproved write and keeps the integration running (D046); a write path
/// the reviewed list doesn't know is watched too, and its writes into ruled chests are logged, not refused, since
/// refusing on a path nobody checked could lose the item. Unity game thread only.
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
    private static int depth, logOnlyDepth;
    private static string entry = "", logOnlyEntry = "";
    private static float lastPendingNotice = float.NegativeInfinity;
    private static int tryStoreSites, pullHaveSites, canStoreSites;

    internal static AzuIntegrationState State { get; private set; } = AzuIntegrationState.Absent;
    /// <summary>D171: H2 in TryStore is on, so an empty chest whose rule accepts an item takes it from the stash keys.</summary>
    internal static bool EmptyChestsActive { get; private set; }
    /// <summary>D171: H2 and H3 in AnyContainerWants are on, so ground pickup asks chest rules too.</summary>
    internal static bool PickupRulesActive { get; private set; }
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
            YAVSOS.Cleanup.CleanupTriggers.BindAzuStoreKey(assembly, logger);
            var members = Resolve(assembly, out var missing, out var optional);
            if (!string.IsNullOrEmpty(ChestRuleStore.BindingFailure)) missing.Add(ChestRuleStore.BindingFailure);
            var shape = Gather(assembly, members, missing, out var paths);
            var check = AzuStoreCompatibility.Assess(shape);
            ReportUnread(logger, check.Rules);
            if (!check.Rules) { Disable(version, check.Whole); return; }
            harmony = new Harmony(HarmonyId);
            var problems = new List<string>();
            problems.AddRange(check.EmptyChestProblems); problems.AddRange(check.PickupProblems);
            Apply(harmony, members);
            // Each transpiler on its own, so one that fails turns off only its hook (D171): a throw or a wrong site count.
            EmptyChestsActive = check.EmptyChests && Transpile(harmony, members.TryStore!, nameof(TryStoreTranspiler),
                () => tryStoreSites == 1, () => $"The TryStore transpiler replaced {tryStoreSites} HaveItem sites; expected 1.", problems);
            PickupRulesActive = check.Pickup && Transpile(harmony, members.AnyContainerWants!, nameof(AnyContainerWantsTranspiler),
                () => pullHaveSites == 1 && canStoreSites == 1,
                () => $"The AnyContainerWants transpiler replaced {pullHaveSites} HaveItem and {canStoreSites} CanItemBeStored sites; expected 1 and 1.", problems);
            var guarded = GuardNewPaths(harmony, check.NewWritePaths, paths);
            State = AzuIntegrationState.Active;
            // Pay the one-time JIT and parser cost off the game thread, not on the first store key press.
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Warm());
            Status = $"YAVSOS/AzuAutoStore {version} integrated chest rules active." +
                (EmptyChestsActive ? "" : " Empty chests with a rule don't take items from the stash keys (AzuAutoStore changed).") +
                (PickupRulesActive ? "" : " Ground pickup doesn't ask chest rules (AzuAutoStore changed); stores still follow them.");
            logger.LogInfo($"{Status} Compatibility checks done in {clock.ElapsedMilliseconds} ms" + (problems.Count == 0 ? "." : "; parts are off (see the next warning)."));
            if (problems.Count != 0) logger.LogWarning("AzuAutoStore check, parts turned off: " + string.Join(" ", problems.Take(12)));
            var notes = check.Notes.Concat(optional).Concat(guarded).ToList();
            if (notes.Count != 0)
                logger.LogWarning($"AzuAutoStore {version} differs from the version YAVSOS was checked against; chest rules stay on: " + string.Join(" ", notes.Take(24)) +
                    (notes.Count > 24 ? " …" : ""));
            ReportOtherPatchers(members, logger);
            AzuStoreOrder.Initialize(assembly, logger, method => Shape(method as MethodInfo));
            ReportUnread(logger, true); // The store-order checks read more methods; the integration is on.
        }
        catch (Exception ex) { Disable(version, new[] { "Integration failed: " + ex.GetType().Name + ": " + ex.Message }); }
    }

    private static void Disable(string version, IEnumerable<string> problems)
    {
        AzuStoreOrder.Disable();
        harmony?.UnpatchSelf(); harmony = null; depth = 0; logOnlyDepth = 0; logOnlyEntry = "";
        State = AzuIntegrationState.Disabled; EmptyChestsActive = PickupRulesActive = false;
        Status = $"AzuAutoStore {version} changed in ways YAVSOS does not recognise; chest rules have no stash effect. AzuAutoStore keeps working normally.";
        log?.LogWarning(Status + " " + string.Join(" ", problems.Take(12)));
    }

    private sealed class Members
    {
        internal MethodInfo? TryStore, AnyContainerWants, StoreHotkey, StoreThisItem, StoreDrop, ContainerAwake;
    }

    // Check 1: resolve by signature, never by version. Only what the whole integration needs goes into missing; a member
    // one hook needs shows in that hook's shape, and the rest are notes (D171).
    private static Members Resolve(Assembly assembly, out List<string> missing, out List<string> optional)
    {
        var required = new List<string>(); var notes = new List<string>(); missing = required; optional = notes;
        Type? Type(string name) => assembly.GetType(name, false);
        MethodInfo? Method(Type? type, string name, Type returns, List<string>? list, params Type[] parameters)
        {
            var method = type == null ? null : AccessTools.DeclaredMethod(type, name, parameters);
            if (method != null && method.ReturnType == returns) return method;
            list?.Add($"Missing member: {type?.FullName ?? "AzuAutoStore.Util.Functions"}::{name}.");
            return null;
        }
        var functions = Type("AzuAutoStore.Util.Functions");
        var plugin = Type("AzuAutoStore.AzuAutoStorePlugin");
        // D101: read for the panel's no-rule text when present; nothing depends on it (#103).
        existingItemField = plugin == null ? null : AccessTools.DeclaredField(plugin, "MustHaveExistingItemToPull");
        var members = new Members
        {
            // Null TryStore or AnyContainerWants: the shape check names it and turns off what needs it.
            TryStore = Method(functions, "TryStore", typeof(bool), null, typeof(Container), typeof(ItemDrop.ItemData).MakeByRefType(), typeof(bool), typeof(bool)),
            AnyContainerWants = Method(functions, "AnyContainerWants", typeof(bool), null, typeof(ItemDrop)),
            // The backstop's entry guards; a missing one leaves that entry to H1 and the new-path guard.
            StoreHotkey = Method(functions, "TryStore", typeof(void), notes),
            StoreThisItem = Method(functions, "TryStoreThisItem", typeof(void), notes, typeof(ItemDrop.ItemData), typeof(Inventory)),
            StoreDrop = Method(functions, "CheckItemDropInstanceAndStore", typeof(void), notes, typeof(ItemDrop)),
            ContainerAwake = AccessTools.DeclaredMethod(typeof(Container), "Awake")
        };
        if (members.ContainerAwake == null) required.Add("Container::Awake");
        return members;
    }

    // Check 2: original IL call graph facts, compared by the detached checker.
    private static AzuAssemblyShape Gather(Assembly assembly, Members members, List<string> missing, out Dictionary<string, List<MethodBase>> paths)
    {
        paths = new Dictionary<string, List<MethodBase>>(StringComparer.Ordinal);
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
                if (!Calls(method).Any(AzuStoreCompatibility.IsWriteSink)) continue;
                callers.Add(Name(method));
                if (!paths.TryGetValue(Name(method), out var overloads)) paths.Add(Name(method), overloads = new List<MethodBase>());
                overloads.Add(method);
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
        catch (Exception error)
        {
            if (unread.Count < 64) unread.Add(Name(method) + " (" + error.GetType().Name + ")");
            yield break;
        }
        foreach (var instruction in code)
            if (instruction.Value is MethodBase callee && (instruction.Key == OpCodes.Call || instruction.Key == OpCodes.Callvirt || instruction.Key == OpCodes.Newobj))
                yield return Name(callee);
    }

    // SF13 (D087, maintainer 2026-10-29): a method whose code can't be read counts as calling nothing, so it wasn't checked
    // for other write paths. Named in the log; the integration stays on (no behaviour change).
    private static readonly List<string> unread = new();
    private static int unreadReported;
    private static void ReportUnread(ManualLogSource logger, bool stayingOn)
    {
        if (unread.Count == unreadReported) return;
        var names = unread.Skip(unreadReported).ToList(); unreadReported = unread.Count;
        logger.LogWarning($"AzuAutoStore check: the code of {names.Count} method{(names.Count == 1 ? "" : "s")} could not be read, so " +
            $"{(names.Count == 1 ? "it was" : "they were")} not checked for other ways AzuAutoStore puts items into chests: {string.Join(", ", names.Take(12))}" +
            $"{(names.Count > 12 ? ", …" : "")}. " + (stayingOn ? "The integration stays on" : "The integration is off for the reasons logged next") +
            "; please report this with your AzuAutoStore version.");
    }

    internal static string Name(MethodBase method) => (method.DeclaringType?.FullName ?? "?") + "::" + method.Name;

    private static HarmonyMethod Hook(string name, int priority = Priority.Normal) =>
        new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AzuAutoStoreIntegration), name)) { priority = priority };

    private static void Apply(Harmony patcher, Members members)
    {
        tryStoreSites = 0; pullHaveSites = 0; canStoreSites = 0;
        patcher.Patch(members.ContainerAwake, postfix: Hook(nameof(RememberInventory)));
        patcher.Patch(members.TryStore, prefix: Hook(nameof(TryStorePrefix)));
        foreach (var method in new[] { members.StoreHotkey, members.StoreThisItem, members.StoreDrop })
            if (method != null) patcher.Patch(method, prefix: Hook(nameof(EnterAzu), Priority.First), finalizer: Hook(nameof(LeaveAzu)));
        foreach (var add in new[]
        {
            AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData) }),
            AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })
        })
            // Runs before the bundled MultiUserChest intercept, which respects __runOriginal (Azu 3.1.7).
            patcher.Patch(add ?? throw new MissingMethodException("Inventory", "AddItem"), prefix: Hook(nameof(Backstop), Priority.First));
    }

    private static bool Transpile(Harmony patcher, MethodInfo method, string transpiler, Func<bool> replaced, Func<string> mismatch, List<string> problems)
    {
        try
        {
            patcher.Patch(method, transpiler: Hook(transpiler));
            if (replaced()) return true;
            problems.Add(mismatch());
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            problems.Add($"Patching {Name(method)} failed ({error.GetType().Name}: {error.Message}).");
        }
        try { patcher.Unpatch(method, HarmonyPatchType.Transpiler, HarmonyId); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { problems.Add($"Removing the {Name(method)} transpiler failed ({error.GetType().Name})."); }
        return false;
    }

    // D171 (#102): a write path the reviewed list doesn't know is watched like the three entries, but its writes into a
    // ruled chest are only logged. D046 refuses a write only where refusing was checked not to lose the item; on a path
    // nobody reviewed, Azu might ignore the add's result, or have taken the item already (here, in a caller or on another
    // peer). A misplaced item is the safe failure. azu-release-check reports the path for review.
    private static IEnumerable<string> GuardNewPaths(Harmony patcher, IReadOnlyList<string> names, Dictionary<string, List<MethodBase>> paths)
    {
        var notes = new List<string>();
        foreach (var name in names)
        foreach (var method in paths.TryGetValue(name, out var overloads) ? overloads : new List<MethodBase>())
        {
            try
            {
                patcher.Patch(method, prefix: Hook(nameof(EnterLogOnly), Priority.First), finalizer: Hook(nameof(LeaveLogOnly)));
                notes.Add($"Its writes into chests with a rule that doesn't accept the item are logged ({name}).");
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                notes.Add($"Watching {name} failed ({error.GetType().Name}: {error.Message}); its writes aren't checked.");
            }
        }
        return notes;
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
            // SF10 (D087): the item, the chest and the cause, once per exception type and chest.
            string id = "?", detail = "";
            try
            {
                id = chest ? ChestRuleStore.View(chest!)?.GetZDO()?.m_uid.ToString() ?? "?" : "none";
                detail = " for " + (item?.m_dropPrefab ? item!.m_dropPrefab.name : "an item") + " into " +
                    (chest ? chest!.m_name + " at " + chest.transform.position + " (" + id + ")" : "no chest");
            }
            catch (Exception) { } // Naming is best effort; nothing may reach Azu's loop.
            if (ReportedBackstops.Add("decide:" + ex.GetType().Name + ":" + id))
                log?.LogWarning("Chest rule evaluation failed" + detail + " (" + ex.GetType().Name + ": " + ex.Message + Interface.RuleControls.Where(ex) +
                    "); the chest is treated as accepting nothing from AzuAutoStore.");
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
        var plugin = Plugin.Instance;
        // X60: a store's automatic rule counts as its rule here too, and its type's filter narrows it (D155, D160).
        var (payload, filter) = plugin != null ? plugin.RuleOf(chest!) : (ChestRuleStore.Payload(chest!), (ContainerFilter?)null);
        if (payload.Length == 0) return ChestRuleDecision.NoRule;
        var prefab = item.m_dropPrefab ? item.m_dropPrefab.name : null;
        if (plugin == null || prefab == null) return ChestRuleDecision.RejectUnreadable;
#if YAVSOS_LAB
        var answers = DiagnosticCategoriesPending ? NoAnswers.Instance : plugin.CategoryAnswers ?? NoAnswers.Instance;
#else
        var answers = plugin.CategoryAnswers ?? NoAnswers.Instance;
#endif
        // P6-07b: the item's own rarity and condition, read only when this chest's rule has an instance filter.
        return ContainerFilter.Narrow(plugin.RuleEvaluator.Evaluate(payload, prefab, plugin.RuleTaxonomy, answers, plugin.InstanceFacetsFor(payload, prefab, item, answers)),
            filter, prefab);
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
        GameText.Say(YAVSOS.Core.Interface.CategoryWait.AzuPending(Plugin.Instance?.CategoriesWait ?? CategoryWaitState.Loading)); // X62, X71
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
                tryStoreSites++;
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
            else pullHaveSites++;
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
    private static void EnterLogOnly(MethodBase __originalMethod)
    { EnterAzu(__originalMethod); if (logOnlyDepth++ == 0) logOnlyEntry = Name(__originalMethod); }
    private static Exception? LeaveLogOnly(Exception? __exception) { if (logOnlyDepth > 0) logOnlyDepth--; return LeaveAzu(__exception); }

    // D046 backstop: refuse the one write; the item stays with the player (verified for Azu 3.1.7's paths).
    private static void Backstop(Inventory __instance, ItemDrop.ItemData item, ref bool __runOriginal, ref bool __result)
    {
        if (depth == 0 || !__runOriginal || item == null || !InventoryOwners.TryGetValue(__instance, out var chest)) return;
        if (!AzuStoreCompatibility.RefuseWrite(depth, Decide(chest, item))) return;
        if (logOnlyDepth > 0)
        {
            // D046's required check: refusing here could lose the item, so the write goes through and is reported.
            if (ReportedBackstops.Add("log:" + logOnlyEntry))
                log?.LogWarning($"AzuAutoStore put an item into a chest whose rule doesn't accept it, through a path YAVSOS hasn't checked ({logOnlyEntry}). " +
                    "Refusing could have lost the item, so the write went through. Chest rules remain active; please report this with your AzuAutoStore version.");
            return;
        }
        __runOriginal = false; __result = false; BackstopRefusals++;
        if (ReportedBackstops.Add(entry))
            log?.LogWarning($"AzuAutoStore tried to put an item into a ruled chest outside the checked path ({entry}); the write was refused and the item stayed where it was. Chest rules remain active.");
    }
}
