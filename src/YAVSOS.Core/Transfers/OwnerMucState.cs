namespace YAVSOS.Core.Transfers;

/// <summary>
/// Whether a MultiUserChest request to another player's chest can go to its owner, and if not, why (D061 Q8, D116, F3).
/// Every state but <see cref="Usable"/> skips the move; the cause only picks the result line.
/// </summary>
public enum OwnerMucState
{
    /// <summary>The owner's item format is known, or the copy in use writes its own and the owner's adapts.</summary>
    Usable,
    /// <summary>The owner publishes format 0: their copy has no intercept, so nothing would answer (D086 D13).</summary>
    CantTakeRequests,
    /// <summary>The owner publishes no format and every echo probe went unanswered (D116).</summary>
    NoAnswer,
    /// <summary>The owner publishes no format and their probe is not answered yet; a later run may go through.</summary>
    Checking,
    /// <summary>
    /// The owner publishes a format, or answered the probe in a layout, YAVSOS doesn't recognise; this includes a copy that
    /// takes requests but whose own format its YAVSOS couldn't read (B4.33).
    /// </summary>
    FormatUnknown
}

public static class OwnerMucStates
{
    /// <summary>The skip reason for a state; null for <see cref="OwnerMucState.Usable"/>.</summary>
    public static CleanupReason? Skip(this OwnerMucState state) => state switch
    {
        OwnerMucState.Usable => null,
        OwnerMucState.CantTakeRequests => CleanupReason.OtherOwnerMucUnusable,
        OwnerMucState.NoAnswer => CleanupReason.OtherOwnerMucSilent,
        OwnerMucState.Checking => CleanupReason.OtherOwnerMucChecking,
        _ => CleanupReason.OtherOwnerFormatUnknown
    };
}
