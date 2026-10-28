using System;

namespace YAVSOS.Core.Transfers;

public enum TransferExecutionState
{
    Planned, Applying, Verified,
    Refused, Unconfirmed
}

/// <summary>
/// Elementary live operation state for a move performed by the verified current owner (D042).
/// The state machine cannot grant ownership or access. Once the game move may have been entered,
/// only an observed, conserved result is Verified; anything else is Unconfirmed, which is reported
/// and logged, never retried, replayed or refunded, and does not persist beyond the live operation.
/// </summary>
public sealed class TransferOperation
{
    public DepositPlan Plan { get; }
    public TransferExecutionState State { get; private set; } = TransferExecutionState.Planned;
    public TransferRefusal Reason { get; private set; }
    public bool MayHaveExecuted => State == TransferExecutionState.Applying;
    public int? VerifiedMovedUnits { get; private set; } = 0;
    public int? VerifiedRemainingUnits => VerifiedMovedUnits.HasValue ? Plan.RequestedUnits - VerifiedMovedUnits.Value : (int?)null;
    public bool IsTerminal => State == TransferExecutionState.Verified || State == TransferExecutionState.Refused ||
        State == TransferExecutionState.Unconfirmed;

    public TransferOperation(DepositPlan plan) => Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    /// <summary>The owner's own fresh revalidation; a refusal here proves the game move was never entered.</summary>
    public TransferRefusal BeginApplying(TransferRefusal freshValidation)
    {
        Require(TransferExecutionState.Planned);
        if (freshValidation != TransferRefusal.None)
        {
            if (freshValidation == TransferRefusal.Unconfirmed || !Enum.IsDefined(typeof(TransferRefusal), freshValidation))
                throw new ArgumentException("Invalid refusal reason.");
            Reason = freshValidation; State = TransferExecutionState.Refused; VerifiedMovedUnits = 0;
            return freshValidation;
        }
        State = TransferExecutionState.Applying; VerifiedMovedUnits = null;
        return TransferRefusal.None;
    }
    public TransferRefusal Verify(int sourceDecrease, int destinationIncrease, bool exactIdentityConserved, bool ownerAndSessionCurrent,
        bool finalStateMatchesPlan, int unexpectedGroundUnits = 0)
    {
        Require(TransferExecutionState.Applying);
        if (sourceDecrease <= 0 || sourceDecrease > Plan.PlannedUnits || sourceDecrease != destinationIncrease ||
            !exactIdentityConserved || !ownerAndSessionCurrent || !finalStateMatchesPlan || unexpectedGroundUnits != 0)
            return Refuse(TransferRefusal.InvariantFailed);
        VerifiedMovedUnits = sourceDecrease; State = TransferExecutionState.Verified;
        return TransferRefusal.None;
    }
    public TransferRefusal Refuse(TransferRefusal reason)
    {
        if (reason == TransferRefusal.None || reason == TransferRefusal.Unconfirmed || !Enum.IsDefined(typeof(TransferRefusal), reason))
            throw new ArgumentException("Invalid refusal reason.");
        if (State == TransferExecutionState.Verified || State == TransferExecutionState.Refused)
            throw new InvalidOperationException("Terminal live operation cannot be rewritten.");
        if (State == TransferExecutionState.Unconfirmed) return TransferRefusal.Unconfirmed;
        Reason = reason;
        if (MayHaveExecuted)
        {
            State = TransferExecutionState.Unconfirmed; VerifiedMovedUnits = null;
            return TransferRefusal.Unconfirmed;
        }
        State = TransferExecutionState.Refused; VerifiedMovedUnits = 0;
        return reason;
    }
    private void Require(TransferExecutionState expected)
    { if (State != expected) throw new InvalidOperationException("Illegal transfer lifecycle transition."); }
}
