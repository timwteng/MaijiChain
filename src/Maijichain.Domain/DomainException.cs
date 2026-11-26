namespace Maijichain.Domain;

/// <summary>A business-rule violation. <see cref="Code"/> is stable and used to choose user-facing wording.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class ErrorCodes
{
    public const string InvalidIdentifier = "id.invalid";
    public const string InvalidCurrency = "currency.invalid";
    public const string InvalidAmount = "amount.invalid";
    public const string AmountTooSmall = "amount.too_small";
    public const string SplitMismatch = "split.mismatch";
    public const string InvalidSplit = "split.invalid";
    public const string UnknownMember = "member.unknown";
    public const string InactiveMember = "member.inactive";
    public const string AlreadyMember = "member.exists";
    public const string NicknameTaken = "member.nickname_taken";
    public const string InvalidNickname = "member.nickname_invalid";
    public const string MemberHasOpenItems = "member.open_items";
    public const string NotAuthorized = "auth.denied";
    public const string NotFound = "item.not_found";
    public const string DraftClosed = "draft.closed";
    public const string DraftExpired = "draft.expired";
    public const string ExpenseNotPosted = "expense.not_posted";
    public const string PaymentNotPending = "payment.not_pending";
    public const string SelfPayment = "payment.self";
    public const string PlanStale = "plan.stale";
    public const string PlanTransferMismatch = "plan.transfer_mismatch";
    public const string PlanTransferClaimed = "plan.transfer_claimed";
    public const string InvalidDescription = "description.invalid";
    public const string InvalidReason = "reason.invalid";
    public const string InvariantViolated = "invariant.violated";
    public const string GroupNotOpen = "group.not_open";
}
