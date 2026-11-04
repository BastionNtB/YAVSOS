using System;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Transfers;

internal enum RuleEligibility { Eligible, Missing, Tombstone, ItemDrawer, NotPlayerBuilt, OnCharacter }

/// <summary>
/// A rule read or edit: the refusal, if any, and the message the YAVSOS panel shows for it (null on success: D101,
/// successes say nothing). The panel formats the message when it draws (P10-04).
/// </summary>
internal sealed class ChestRuleResult
{
    internal TransferRefusal Reason { get; }
    internal Message? Message { get; }
    internal ContainerPolicyRead? Policy { get; }
    internal bool Succeeded => Reason == TransferRefusal.None;
    private ChestRuleResult(TransferRefusal reason, Message? message, ContainerPolicyRead? policy)
    { Reason = reason; Message = message; Policy = policy; }
    internal static ChestRuleResult Done(ContainerPolicyRead? policy = null) => new(TransferRefusal.None, null, policy);
    internal static ChestRuleResult Refused(TransferRefusal reason, string key, ContainerPolicyRead? policy = null) =>
        new(reason, YAVSOS.Core.Messages.Message.Of(key), policy);
}

/// <summary>
/// P3-01 chest rule persistence. A rule is an owner-written string field on the container ZDO (D045: any object
/// AzuAutoStore stores into through its ordinary chest path). Every peer reads the replicated field; only the
/// current ZDO owner writes, and only when the stored payload still equals the one the editor saw. The write
/// touches no inventory, so the retired writer model's inventory reload and 5x2 bound do not apply (D043/D045).
/// Unity game thread only.
/// </summary>
internal sealed class ChestRuleStore
{
    private const string RossDrawerType = "DrawerComponent";
    // Private game members, bound once. Container.m_nview may belong to a root object (carts).
    // A failed binding (changed game) leaves every container ineligible instead of throwing.
    private static readonly AccessTools.FieldRef<Container, ZNetView>? ViewField;
    private static readonly Func<Container, long, bool>? CheckAccess;
    internal static string BindingFailure { get; } = "";
    static ChestRuleStore()
    {
        try
        {
            ViewField = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
            var access = AccessTools.DeclaredMethod(typeof(Container), "CheckAccess", new[] { typeof(long) }) ??
                throw new MissingMethodException(nameof(Container), "CheckAccess");
            CheckAccess = AccessTools.MethodDelegate<Func<Container, long, bool>>(access);
        }
        // SF2 (D087): Plugin logs this at load whether or not AzuAutoStore is installed.
        catch (Exception ex)
        {
            ViewField = null; CheckAccess = null;
            BindingFailure = "Chest rules, stashing and cleanup are off: the game's Container.m_nview or Container.CheckAccess(long) could not be bound (" +
                ex.GetType().Name + ": " + ex.Message + "). Chests work as without YAVSOS.";
        }
    }
#if YAVSOS_LAB
    /// <summary>D088: lab-only, every chest reads as if the bindings had failed (SF2).</summary>
    internal static bool LabUnbound;
#endif
    /// <summary>SF2: the game's chest members are bound; false leaves every container ineligible.</summary>
    internal static bool Bound => ViewField != null && CheckAccess != null
#if YAVSOS_LAB
        && !LabUnbound
#endif
        ;
    internal static ZNetView? View(Container container) => container && Bound ? ViewField!(container) : null;
    private readonly Func<ClassificationTaxonomy> taxonomy;
    private readonly Func<bool> policyReady;

    internal ChestRuleStore(Func<ClassificationTaxonomy> taxonomy, Func<bool> policyReady)
    {
        this.taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        this.policyReady = policyReady ?? throw new ArgumentNullException(nameof(policyReady));
    }

    /// <summary>
    /// Mirrors AzuAutoStore 3.1.7's container registration (player-built, not on another character), minus the
    /// objects whose deposits do not pass through its chest path or that carry their own reservation (D045, D047).
    /// </summary>
    internal static RuleEligibility Eligibility(Container? container)
    {
        if (container && IsRossDrawer(container!)) return RuleEligibility.ItemDrawer;
        var view = View(container!);
        if (!view || !view!.IsValid() || view.GetZDO() == null || container!.GetInventory() == null) return RuleEligibility.Missing;
        if (container.GetComponentInParent<TombStone>()) return RuleEligibility.Tombstone;
        var character = container.GetComponentInParent<Character>();
        if (character && character != Player.m_localPlayer) return RuleEligibility.OnCharacter;
        if (view.GetZDO().GetLong(ZDOVars.s_creator, 0L) == 0L) return RuleEligibility.NotPlayerBuilt;
        return RuleEligibility.Eligible;
    }

    internal static bool IsRossDrawer(Container container)
    {
        for (var type = container.GetType(); type != null && type != typeof(Container); type = type.BaseType)
            if (type.Name == RossDrawerType) return true;
        return false;
    }

    /// <summary>Any peer: the replicated stored payload, parsed against the current taxonomy.</summary>
    internal ChestRuleResult Read(Container container)
    {
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return Ineligible();
        var payload = Payload(container);
        return ChestRuleResult.Done(ContainerPolicyEnvelope.Read(payload, taxonomy()));
    }

    // The panel shows only on eligible chests, so this reaches a player only if a chest changed under an open panel.
    // Callers that need the cause read Eligibility (the Lab bridge logs it).
    private static ChestRuleResult Ineligible() => ChestRuleResult.Refused(TransferRefusal.UnsupportedCapability, "yavsos_edit_not_eligible");

    internal static string Payload(Container container) =>
        View(container)?.GetZDO()?.GetString(ContainerPolicyEnvelope.ZdoKey, "") ?? "";

    /// <summary>"Don't clean up this container" (D057): an owner-written ZDO flag, separate from the rule, so it also covers unruled containers.</summary>
    internal const string CleanupOptOutKey = "com.bastionntb.yavsos.cleanup-optout";
    internal static bool CleanupOptOut(Container container) => View(container)?.GetZDO()?.GetBool(CleanupOptOutKey, false) == true;

    internal static bool HasAccess(Container container, Player player) => Bound && CheckAccess!(container, player.GetPlayerID()) &&
        (!container.m_checkGuardStone || PrivateArea.CheckAccess(container.transform.position, 0f, false, false));

    internal ChestRuleResult TrySetCleanupOptOut(Container container, bool optOut)
    {
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return Ineligible();
        var player = Player.m_localPlayer;
        if (!player || player.IsDead()) return ChestRuleResult.Refused(TransferRefusal.Disconnected, "yavsos_edit_no_player");
        var view = View(container)!;
#if YAVSOS_LAB
        if (FaultLab.RefuseEdits) return ChestRuleResult.Refused(TransferRefusal.UnsupportedAuthority, "yavsos_edit_chest_in_use"); // Lab fault "edits".
#endif
        if (!view.IsOwner()) return ChestRuleResult.Refused(TransferRefusal.UnsupportedAuthority, "yavsos_edit_chest_in_use");
        if (!HasAccess(container, player)) return ChestRuleResult.Refused(TransferRefusal.AccessDenied, "yavsos_edit_no_access");
        view.GetZDO().Set(CleanupOptOutKey, optOut);
        return CleanupOptOut(container) == optOut ? ChestRuleResult.Done() : ChestRuleResult.Refused(TransferRefusal.Unconfirmed, "yavsos_edit_unconfirmed");
    }

    /// <param name="edit">Categories and exact items (D048); null clears the rule. An edit that leaves nothing clears it too.</param>
    internal ChestRuleResult TryEdit(Container container, string expectedPayload, ChestRuleEdit? edit)
    {
        if (expectedPayload == null) throw new ArgumentNullException(nameof(expectedPayload));
        if (Writable(container) is { } refused) return refused;
        var view = View(container)!; var zdo = view.GetZDO();
        var current = zdo.GetString(ContainerPolicyEnvelope.ZdoKey, "");
        var currentTaxonomy = taxonomy();
        var refusal = ContainerPolicyEnvelope.TryEdit(current, expectedPayload, currentTaxonomy, edit, out var updated, out var problem);
        if (refusal == TransferRefusal.PolicyChanged)
            return ChestRuleResult.Refused(refusal, "yavsos_edit_rule_changed", ContainerPolicyEnvelope.Read(current, currentTaxonomy));
        if (refusal != TransferRefusal.None)
            return ChestRuleResult.Refused(refusal, problem switch
            {
                // B4.7: each cause in its own words; only an unreadable rule is about another version or category settings.
                ChestRuleEditProblem.UnknownCategory => "yavsos_edit_unknown_category",
                ChestRuleEditProblem.TooLarge => "yavsos_edit_too_large",
                ChestRuleEditProblem.RevisionExhausted => "yavsos_edit_exhausted",
                ChestRuleEditProblem.Invalid => "yavsos_edit_invalid",
                _ => "yavsos_edit_other_version"
            }, ContainerPolicyEnvelope.Read(current, currentTaxonomy));
        return Commit(view, updated!, currentTaxonomy);
    }

    /// <summary>
    /// X26 (A2-09): an undo or redo puts back <paramref name="previousPayload"/>, whole, through the same checks and
    /// owner-only compare-and-set as an edit: refused when the chest no longer holds <paramref name="expectedPayload"/>
    /// (<paramref name="redo"/> picks the words), while another player is using the chest, or without access.
    /// </summary>
    internal ChestRuleResult TryRestore(Container container, string expectedPayload, string previousPayload, bool redo)
    {
        if (expectedPayload == null) throw new ArgumentNullException(nameof(expectedPayload));
        if (previousPayload == null) throw new ArgumentNullException(nameof(previousPayload));
        if (Writable(container) is { } refused) return refused;
        var view = View(container)!;
        var current = view.GetZDO().GetString(ContainerPolicyEnvelope.ZdoKey, "");
        var currentTaxonomy = taxonomy();
        if (ContainerPolicyEnvelope.TryRestore(current, expectedPayload, previousPayload, out var updated) != TransferRefusal.None)
            return ChestRuleResult.Refused(TransferRefusal.PolicyChanged, redo ? "yavsos_edit_redo_changed" : "yavsos_edit_undo_changed",
                ContainerPolicyEnvelope.Read(current, currentTaxonomy));
        return Commit(view, updated!, currentTaxonomy);
    }

    // The checks every rule write makes before comparing payloads; null: this player may write the chest's rule now.
    private ChestRuleResult? Writable(Container container)
    {
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return Ineligible();
        if (!policyReady()) return ChestRuleResult.Refused(TransferRefusal.UnsupportedCapability, "yavsos_edit_loading");
        var player = Player.m_localPlayer;
        if (!player || player.IsDead()) return ChestRuleResult.Refused(TransferRefusal.Disconnected, "yavsos_edit_no_player");
        var view = View(container)!;
#if YAVSOS_LAB
        if (FaultLab.RefuseEdits) return ChestRuleResult.Refused(TransferRefusal.UnsupportedAuthority, "yavsos_edit_chest_in_use"); // Lab fault "edits".
#endif
        // With MultiUserChest, other viewers see the chest without owning it. Only the owner writes.
        if (!view.IsOwner()) return ChestRuleResult.Refused(TransferRefusal.UnsupportedAuthority, "yavsos_edit_chest_in_use");
        if (!CheckAccess!(container, player.GetPlayerID()) ||
            container.m_checkGuardStone && !PrivateArea.CheckAccess(container.transform.position, 0f, true, false))
            return ChestRuleResult.Refused(TransferRefusal.AccessDenied, "yavsos_edit_no_access");
        return null;
    }

    // Writes the payload and confirms it by reading it back while still the owner.
    private static ChestRuleResult Commit(ZNetView view, string updated, ClassificationTaxonomy currentTaxonomy)
    {
        var zdo = view.GetZDO();
        zdo.Set(ContainerPolicyEnvelope.ZdoKey, updated);
        var written = zdo.GetString(ContainerPolicyEnvelope.ZdoKey, "");
        if (!view.IsOwner() || !StringComparer.Ordinal.Equals(written, updated))
            return ChestRuleResult.Refused(TransferRefusal.Unconfirmed, "yavsos_edit_unconfirmed", ContainerPolicyEnvelope.Read(written, currentTaxonomy));
        return ChestRuleResult.Done(ContainerPolicyEnvelope.Read(written, currentTaxonomy));
    }
}
