using System;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Transfers;

internal enum RuleEligibility { Eligible, Missing, Tombstone, ItemDrawer, NotPlayerBuilt, OnCharacter }

internal sealed class ChestRuleResult
{
    internal TransferRefusal Reason { get; }
    internal string Message { get; }
    internal ContainerPolicyRead? Policy { get; }
    internal bool Succeeded => Reason == TransferRefusal.None;
    internal ChestRuleResult(TransferRefusal reason, string message, ContainerPolicyRead? policy = null)
    { Reason = reason; Message = message; Policy = policy; }
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
        catch (Exception ex) { ViewField = null; CheckAccess = null; BindingFailure = "Chest rules unavailable: " + ex.GetType().Name; }
    }
    internal static ZNetView? View(Container container) => container && ViewField != null ? ViewField(container) : null;
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

    internal static string Describe(RuleEligibility eligibility) => eligibility switch
    {
        RuleEligibility.Eligible => "",
        RuleEligibility.Tombstone => "Tombstones cannot have YAVSOS rules.",
        RuleEligibility.ItemDrawer => "Item drawers use their own item assignment instead of a YAVSOS rule.",
        RuleEligibility.NotPlayerBuilt => "Only player-built containers can have YAVSOS rules.",
        RuleEligibility.OnCharacter => "Containers carried by a creature cannot have YAVSOS rules.",
        _ => "This container is not available."
    };

    /// <summary>Any peer: the replicated stored payload, parsed against the current taxonomy.</summary>
    internal ChestRuleResult Read(Container container)
    {
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return new ChestRuleResult(TransferRefusal.UnsupportedCapability, Describe(eligibility));
        var payload = Payload(container);
        return new ChestRuleResult(TransferRefusal.None, "", ContainerPolicyEnvelope.Read(payload, taxonomy()));
    }

    internal static string Payload(Container container) =>
        View(container)?.GetZDO()?.GetString(ContainerPolicyEnvelope.ZdoKey, "") ?? "";

    /// <summary>"Don't clean up this container" (D057): an owner-written ZDO flag, separate from the rule, so it also covers unruled containers.</summary>
    internal const string CleanupOptOutKey = "com.bastionntb.yavsos.cleanup-optout";
    internal static bool CleanupOptOut(Container container) => View(container)?.GetZDO()?.GetBool(CleanupOptOutKey, false) == true;

    internal static bool HasAccess(Container container, Player player) => CheckAccess != null && CheckAccess(container, player.GetPlayerID()) &&
        (!container.m_checkGuardStone || PrivateArea.CheckAccess(container.transform.position, 0f, false, false));

    internal ChestRuleResult TrySetCleanupOptOut(Container container, bool optOut)
    {
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return new ChestRuleResult(TransferRefusal.UnsupportedCapability, Describe(eligibility));
        var player = Player.m_localPlayer;
        if (!player || player.IsDead()) return new ChestRuleResult(TransferRefusal.Disconnected, "No local player.");
        var view = View(container)!;
        if (!view.IsOwner()) return new ChestRuleResult(TransferRefusal.UnsupportedAuthority, "Another player is using this chest. Change it after they close it.");
        if (!HasAccess(container, player)) return new ChestRuleResult(TransferRefusal.AccessDenied, "You do not have access to this chest.");
        view.GetZDO().Set(CleanupOptOutKey, optOut);
        return CleanupOptOut(container) == optOut
            ? new ChestRuleResult(TransferRefusal.None, optOut ? "Cleanup leaves this container alone." : "Cleanup may take misplaced items out of this container.")
            : new ChestRuleResult(TransferRefusal.Unconfirmed, "The setting could not be confirmed. Reopen the chest to check it.");
    }

    /// <param name="edit">Categories and exact items (D048); null clears the rule. An edit that leaves nothing clears it too.</param>
    internal ChestRuleResult TryEdit(Container container, string expectedPayload, ChestRuleEdit? edit)
    {
        if (expectedPayload == null) throw new ArgumentNullException(nameof(expectedPayload));
        var eligibility = Eligibility(container);
        if (eligibility != RuleEligibility.Eligible) return new ChestRuleResult(TransferRefusal.UnsupportedCapability, Describe(eligibility));
        if (!policyReady()) return new ChestRuleResult(TransferRefusal.UnsupportedCapability, "YAVSOS is still loading its settings. Try again shortly.");
        var player = Player.m_localPlayer;
        if (!player || player.IsDead()) return new ChestRuleResult(TransferRefusal.Disconnected, "No local player.");
        var view = View(container)!; var zdo = view.GetZDO();
        // With MultiUserChest, other viewers see the chest without owning it. Only the owner writes.
        if (!view.IsOwner()) return new ChestRuleResult(TransferRefusal.UnsupportedAuthority,
            "Another player is using this chest. Edit its rule after they close it.");
        if (!CheckAccess!(container, player.GetPlayerID()) ||
            container.m_checkGuardStone && !PrivateArea.CheckAccess(container.transform.position, 0f, true, false))
            return new ChestRuleResult(TransferRefusal.AccessDenied, "You do not have access to this chest.");
        var current = zdo.GetString(ContainerPolicyEnvelope.ZdoKey, "");
        var currentTaxonomy = taxonomy();
        var refusal = ContainerPolicyEnvelope.TryEdit(current, expectedPayload, currentTaxonomy, edit, out var updated);
        if (refusal == TransferRefusal.PolicyChanged)
            return new ChestRuleResult(refusal, "The rule changed since you opened it. Reopen it and try again.", ContainerPolicyEnvelope.Read(current, currentTaxonomy));
        if (refusal != TransferRefusal.None)
            return new ChestRuleResult(refusal, "This chest has a rule from a different YAVSOS version or category set; it was left unchanged.",
                ContainerPolicyEnvelope.Read(current, currentTaxonomy));
        zdo.Set(ContainerPolicyEnvelope.ZdoKey, updated!);
        var written = zdo.GetString(ContainerPolicyEnvelope.ZdoKey, "");
        if (!view.IsOwner() || !StringComparer.Ordinal.Equals(written, updated))
            return new ChestRuleResult(TransferRefusal.Unconfirmed, "The rule could not be confirmed. Reopen the chest to check it.",
                ContainerPolicyEnvelope.Read(written, currentTaxonomy));
        return new ChestRuleResult(TransferRefusal.None, updated!.Length == 0 ? "Rule cleared." : "Rule saved.", ContainerPolicyEnvelope.Read(written, currentTaxonomy));
    }
}
