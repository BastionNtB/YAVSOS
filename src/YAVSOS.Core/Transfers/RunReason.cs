namespace YAVSOS.Core.Transfers;

/// <summary>Why a stash or cleanup run left a stack where it was (or, for <see cref="ChestUnreadable"/>, a chest out of the run).</summary>
public enum RunReason
{
    /// <summary>No ruled chest in range accepts it.</summary>
    NoDestination,
    /// <summary>Accepting chests have no room left.</summary>
    NoCapacity,
    /// <summary>The only accepting chests are of a type AzuAutoStore's config excludes it from (D056).</summary>
    AzuExcluded,
    /// <summary>One side is another player's chest and no MultiUserChest is present.</summary>
    OtherOwnerNoMuc,
    /// <summary>Both sides are other players' chests.</summary>
    BothRemote,
    /// <summary>The other player's MultiUserChest item format is unknown, so YAVSOS's copy cannot send it a request (D061 Q8).</summary>
    OtherOwnerFormatUnknown,
    /// <summary>The other player's MultiUserChest can't take requests: they publish format 0 (D086 D13, F3).</summary>
    OtherOwnerMucUnusable,
    /// <summary>The other player publishes no MultiUserChest format and no echo probe was answered (D116, F3).</summary>
    OtherOwnerMucSilent,
    /// <summary>The other player's MultiUserChest is still being checked by echo probe (D116, F3); run again in a moment.</summary>
    OtherOwnerMucChecking,
    /// <summary>The other player's chest already has a MultiUserChest request this run.</summary>
    MucBusy,
    /// <summary>The stack is protected or cannot be moved exactly.</summary>
    Unsupported,
    /// <summary>The stack is on the cursor or in use by an open inventory screen (D057 O8).</summary>
    Held,
    /// <summary>The run's bounds were reached; run again.</summary>
    Budget,
    /// <summary>More chests in range than one run reads; the same ones are left out on every run.</summary>
    ChestLimit,
    /// <summary>A category chest may take it once categories finish loading (D086 D4); try again shortly.</summary>
    CategoriesPending,
    /// <summary>Its chest was set to "cleanup leaves this alone" during the run (B4.5).</summary>
    OptedOut,
    /// <summary>A nearby chest couldn't be read, so it took part in nothing; counted per chest, not per stack (SF3).</summary>
    ChestUnreadable
}
