namespace Maijichain.Domain;

public enum MemberRole
{
    Member,
    Coordinator,
}

/// <summary>
/// An immutable fact in a group's append-only journal. Balances, drafts and plans are all derived by replaying
/// these events, so history is never rewritten: corrections are new events (spec principle 3).
/// </summary>
public abstract record LedgerEvent
{
    public long Sequence { get; init; }

    public DateTimeOffset At { get; init; }

    public MemberId? Actor { get; init; }

    /// <summary>Idempotency key of the command that produced this event (e.g. a LINE webhookEventId).</summary>
    public string? SourceKey { get; init; }
}

public sealed record GroupOpened(string GroupId, string Name, string CurrencyCode, int CurrencyScale, RemainderPolicy RemainderPolicy) : LedgerEvent;

public sealed record MemberJoined(MemberId Member, string Nickname, string IdentityRef, MemberRole Role) : LedgerEvent;

public sealed record MemberRoleChanged(MemberId Member, MemberRole Role) : LedgerEvent;

public sealed record MemberDeactivated(MemberId Member) : LedgerEvent;

public sealed record ExpenseDrafted(
    string DraftId,
    MemberId Payer,
    long Amount,
    string Description,
    DateOnly OccurredOn,
    IReadOnlyList<Share> Shares,
    string SplitSummary,
    DateTimeOffset ExpiresAt) : LedgerEvent;

public sealed record DraftDiscarded(string DraftId, string Reason) : LedgerEvent;

public sealed record ExpensePosted(string ExpenseId, string DraftId, IReadOnlyList<PostingLine> Lines) : LedgerEvent;

public sealed record ExpenseReversed(string ExpenseId, string Reason, IReadOnlyList<PostingLine> Lines) : LedgerEvent;

public sealed record PaymentClaimed(string PaymentId, MemberId From, MemberId To, long Amount, string? PlanId, int? TransferIndex) : LedgerEvent;

public sealed record PaymentPosted(
    string PaymentId,
    MemberId From,
    MemberId To,
    long Amount,
    string? PlanId,
    int? TransferIndex,
    IReadOnlyList<PostingLine> Lines) : LedgerEvent;

public sealed record PaymentRejected(string PaymentId, string Reason) : LedgerEvent;

public sealed record PlanCreated(string PlanId, long BaseSequence, NettingMethod Method, IReadOnlyList<Transfer> Transfers) : LedgerEvent;

public sealed record PeriodClosed(string PeriodId, string Label, long ThroughSequence, IReadOnlyList<MemberBalance> Balances) : LedgerEvent;

/// <summary>Result of a command: the event to append, or the earlier event when the command was a repeat.</summary>
public sealed record Decision(LedgerEvent Event, bool IsDuplicate)
{
    public static Decision New(LedgerEvent e) => new(e, false);

    public static Decision Repeat(LedgerEvent e) => new(e, true);
}

/// <summary>Per-command inputs that are not business data.</summary>
public sealed record CommandContext(DateTimeOffset Now, string SourceKey);
