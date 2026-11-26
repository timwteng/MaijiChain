namespace Maijichain.Domain;

public sealed record Member(MemberId Id, string Nickname, string IdentityRef, MemberRole Role, bool Active);

public enum DraftStatus
{
    Open,
    Posted,
    Discarded,
}

public sealed record Draft(
    string Id,
    MemberId CreatedBy,
    MemberId Payer,
    long Amount,
    string Description,
    DateOnly OccurredOn,
    IReadOnlyList<Share> Shares,
    string SplitSummary,
    DateTimeOffset ExpiresAt,
    DraftStatus Status,
    string? ExpenseId);

public enum ExpenseStatus
{
    Posted,
    Reversed,
}

public sealed record Expense(
    string Id,
    string DraftId,
    MemberId RecordedBy,
    MemberId Payer,
    long Amount,
    string Description,
    DateOnly OccurredOn,
    IReadOnlyList<Share> Shares,
    IReadOnlyList<PostingLine> Lines,
    long PostedSequence,
    ExpenseStatus Status,
    string? ReversalReason);

public enum PaymentStatus
{
    Pending,
    Posted,
    Rejected,
}

public sealed record Payment(
    string Id,
    MemberId From,
    MemberId To,
    long Amount,
    string? PlanId,
    int? TransferIndex,
    MemberId RecordedBy,
    PaymentStatus Status,
    long? PostedSequence,
    string? RejectionReason);

public sealed record SettlementPlan(string Id, long BaseSequence, NettingMethod Method, IReadOnlyList<Transfer> Transfers);

/// <summary>One line of a member statement, explaining how a balance was reached (UR-06).</summary>
public sealed record StatementLine(long Sequence, string ReferenceId, EntryKind Kind, long Amount, long RunningBalance);

/// <summary>
/// Event-sourced aggregate for one group ledger. Commands validate and return a <see cref="Decision"/>; only
/// <see cref="Apply"/> changes state, so the same rules run for live commands and for journal replay.
/// </summary>
public sealed class GroupLedger
{
    public static readonly TimeSpan DraftLifetime = TimeSpan.FromHours(24);
    public const long MaxAmount = 1_000_000_000_000;
    public const int MaxTextLength = 80;

    private static readonly HashSet<string> ReservedNicknames = new(StringComparer.OrdinalIgnoreCase) { "all", "me", "by", "date" };

    private readonly List<LedgerEvent> _events = [];
    private readonly Dictionary<MemberId, Member> _members = [];
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expense> _expenses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Payment> _payments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettlementPlan> _plans = new(StringComparer.Ordinal);
    private readonly List<PeriodClosed> _periods = [];
    private readonly List<(long Sequence, string ReferenceId, PostingLine Line)> _entries = [];
    private readonly Dictionary<string, long> _sourceKeys = new(StringComparer.Ordinal);
    private readonly List<(long Sequence, string? PlanId)> _balanceChanges = [];

    private GroupLedger()
    {
    }

    public string GroupId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public Currency Currency { get; private set; } = Currency.Twd;

    public RemainderPolicy RemainderPolicy { get; private set; }

    public long LastSequence => _events.Count;

    public BalanceSheet Balances { get; private set; } = BalanceSheet.Empty;

    public IReadOnlyList<LedgerEvent> Events => _events;

    public IReadOnlyList<Member> Members => _members.Values.OrderBy(m => m.Id).ToList();

    public IReadOnlyList<Member> ActiveMembers => Members.Where(m => m.Active).ToList();

    public IReadOnlyList<PeriodClosed> Periods => _periods;

    public static GroupOpened Open(string groupId, string name, Currency currency, RemainderPolicy policy, CommandContext ctx)
    {
        RequireText(name, ErrorCodes.InvalidDescription, "Group name");
        return new GroupOpened(groupId, name.Trim(), currency.Code, currency.Scale, policy) { Sequence = 1, At = ctx.Now, SourceKey = ctx.SourceKey };
    }

    public static GroupLedger Replay(IEnumerable<LedgerEvent> events)
    {
        var ledger = new GroupLedger();
        foreach (var e in events)
        {
            ledger.Apply(e);
        }

        if (ledger._events.Count == 0)
        {
            throw new DomainException(ErrorCodes.GroupNotOpen, "A group journal must start with GroupOpened.");
        }

        return ledger;
    }

    // ---------------------------------------------------------------- queries

    public Member? FindByIdentity(string identityRef) => _members.Values.FirstOrDefault(m => m.Active && m.IdentityRef == identityRef);

    public Member? FindByNickname(string nickname) =>
        _members.Values.FirstOrDefault(m => m.Active && string.Equals(m.Nickname, nickname, StringComparison.OrdinalIgnoreCase));

    public Member GetMember(MemberId id) => _members.TryGetValue(id, out var m) ? m : throw NotFound("member", id.Value);

    public Draft GetDraft(string id) => _drafts.TryGetValue(id, out var d) ? d : throw NotFound("draft", id);

    public Expense GetExpense(string id) => _expenses.TryGetValue(id, out var x) ? x : throw NotFound("expense", id);

    public Payment GetPayment(string id) => _payments.TryGetValue(id, out var p) ? p : throw NotFound("payment", id);

    public SettlementPlan GetPlan(string id) => _plans.TryGetValue(id, out var p) ? p : throw NotFound("plan", id);

    public IReadOnlyList<Payment> PendingPayments => _payments.Values.Where(p => p.Status == PaymentStatus.Pending).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>A plan is stale once any balance change happened after it was computed, other than its own payments (INV-09).</summary>
    public bool IsPlanStale(string planId)
    {
        var plan = GetPlan(planId);
        return _balanceChanges.Any(c => c.Sequence > plan.BaseSequence && c.PlanId != planId);
    }

    public bool IsTransferClaimed(string planId, int index) =>
        _payments.Values.Any(p => p.PlanId == planId && p.TransferIndex == index && p.Status != PaymentStatus.Rejected);

    public IReadOnlyList<StatementLine> StatementFor(MemberId member)
    {
        long running = 0;
        var lines = new List<StatementLine>();
        foreach (var (sequence, reference, line) in _entries.Where(e => e.Line.Member == member))
        {
            running = checked(running + line.Amount);
            lines.Add(new StatementLine(sequence, reference, line.Kind, line.Amount, running));
        }

        return lines;
    }

    public BalanceSheet BalancesThrough(long sequence) =>
        BalanceSheet.Empty.Apply(_entries.Where(e => e.Sequence <= sequence).Select(e => e.Line));

    // --------------------------------------------------------------- commands

    public Decision Join(string identityRef, string nickname, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        if (string.IsNullOrWhiteSpace(identityRef))
        {
            throw new DomainException(ErrorCodes.InvalidIdentifier, "Missing identity reference.");
        }

        if (FindByIdentity(identityRef) is { } existing)
        {
            throw new DomainException(ErrorCodes.AlreadyMember, $"Already joined as @{existing.Nickname}.");
        }

        nickname = nickname.Trim().TrimStart('@');
        if (nickname.Length is < 1 or > 16 || !nickname.All(c => char.IsLetterOrDigit(c) || c == '_') || ReservedNicknames.Contains(nickname))
        {
            throw new DomainException(ErrorCodes.InvalidNickname, "Nickname must be 1–16 letters, digits or underscores.");
        }

        if (FindByNickname(nickname) is not null)
        {
            throw new DomainException(ErrorCodes.NicknameTaken, $"@{nickname} is already taken.");
        }

        var returning = _members.Values.FirstOrDefault(m => m.IdentityRef == identityRef);
        var id = returning?.Id ?? new MemberId($"m{_members.Count + 1:D3}");
        var role = _members.Values.Any(m => m.Active && m.Role == MemberRole.Coordinator) ? MemberRole.Member : MemberRole.Coordinator;
        return Decision.New(Stamp(new MemberJoined(id, nickname, identityRef, role), id, ctx));
    }

    public Decision ProposeExpense(MemberId actor, MemberId payer, long amount, string description, DateOnly occurredOn, SplitRule split, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequireActive(actor);
        RequireActive(payer);
        foreach (var participant in split.Participants)
        {
            RequireActive(participant);
        }

        RequireAmount(amount);
        RequireText(description, ErrorCodes.InvalidDescription, "Description");
        string draftId = $"D{_drafts.Count + 1}";
        var shares = split.Allocate(amount, $"{GroupId}/{draftId}", RemainderPolicy);
        var drafted = new ExpenseDrafted(draftId, payer, amount, description.Trim(), occurredOn, shares, split.Summary, ctx.Now + DraftLifetime);
        return Decision.New(Stamp(drafted, actor, ctx));
    }

    public Decision ConfirmDraft(MemberId actor, string draftId, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var draft = GetDraft(draftId);
        if (draft.Status == DraftStatus.Posted)
        {
            return Decision.Repeat(EventAt(GetExpense(draft.ExpenseId!).PostedSequence)); // double tap: no second effect (INV-04)
        }

        if (draft.Status == DraftStatus.Discarded)
        {
            throw new DomainException(ErrorCodes.DraftClosed, $"{draftId} was cancelled.");
        }

        if (ctx.Now > draft.ExpiresAt)
        {
            throw new DomainException(ErrorCodes.DraftExpired, $"{draftId} expired; please enter it again.");
        }

        if (actor != draft.CreatedBy)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the member who entered the expense can confirm it.");
        }

        RequireActive(draft.Payer);
        foreach (var share in draft.Shares)
        {
            RequireActive(share.Member);
        }

        var lines = Postings.ForExpense(draft.Payer, draft.Amount, draft.Shares);
        return Decision.New(Stamp(new ExpensePosted($"E{_expenses.Count + 1}", draftId, lines), actor, ctx));
    }

    public Decision DiscardDraft(MemberId actor, string draftId, string reason, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var draft = GetDraft(draftId);
        if (draft.Status != DraftStatus.Open)
        {
            throw new DomainException(ErrorCodes.DraftClosed, $"{draftId} is no longer open.");
        }

        if (actor != draft.CreatedBy && !IsCoordinator(actor))
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the member who entered the expense or a coordinator can cancel it.");
        }

        RequireText(reason, ErrorCodes.InvalidReason, "Reason");
        return Decision.New(Stamp(new DraftDiscarded(draftId, reason.Trim()), actor, ctx));
    }

    public Decision ReverseExpense(MemberId actor, string expenseId, string reason, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequireActive(actor);
        var expense = GetExpense(expenseId);
        if (expense.Status != ExpenseStatus.Posted)
        {
            throw new DomainException(ErrorCodes.ExpenseNotPosted, $"{expenseId} is already reversed.");
        }

        if (actor != expense.RecordedBy && !IsCoordinator(actor))
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the member who recorded the expense or a coordinator can reverse it.");
        }

        RequireText(reason, ErrorCodes.InvalidReason, "Reason");
        return Decision.New(Stamp(new ExpenseReversed(expenseId, reason.Trim(), Postings.Reverse(expense.Lines)), actor, ctx));
    }

    /// <summary>Debtor says "I paid". Balances do not move until the creditor confirms receipt (UR-09).</summary>
    public Decision ClaimPayment(MemberId actor, MemberId to, long amount, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequirePaymentParties(actor, to, amount);
        return Decision.New(Stamp(new PaymentClaimed($"P{_payments.Count + 1}", actor, to, amount, null, null), actor, ctx));
    }

    /// <summary>Debtor pays one transfer of a settlement plan; rejected if the plan no longer matches the ledger.</summary>
    public Decision ClaimPlanTransfer(MemberId actor, string planId, int transferIndex, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var plan = GetPlan(planId);
        if (transferIndex < 0 || transferIndex >= plan.Transfers.Count)
        {
            throw NotFound("plan transfer", $"{planId}-{transferIndex + 1}");
        }

        var transfer = plan.Transfers[transferIndex];
        if (transfer.From != actor)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the paying member can mark this transfer as paid.");
        }

        if (IsTransferClaimed(planId, transferIndex))
        {
            throw new DomainException(ErrorCodes.PlanTransferClaimed, $"{planId}-{transferIndex + 1} is already recorded.");
        }

        if (IsPlanStale(planId))
        {
            throw new DomainException(ErrorCodes.PlanStale, $"{planId} is out of date because the ledger changed. Create a new plan.");
        }

        RequirePaymentParties(actor, transfer.To, transfer.Amount);
        var claimed = new PaymentClaimed($"P{_payments.Count + 1}", actor, transfer.To, transfer.Amount, planId, transferIndex);
        return Decision.New(Stamp(claimed, actor, ctx));
    }

    /// <summary>Creditor confirms receipt; this is the only way a debtor-initiated payment reaches the ledger.</summary>
    public Decision ConfirmPayment(MemberId actor, string paymentId, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var payment = GetPayment(paymentId);
        if (payment.Status == PaymentStatus.Posted)
        {
            return Decision.Repeat(EventAt(payment.PostedSequence!.Value));
        }

        if (payment.Status == PaymentStatus.Rejected)
        {
            throw new DomainException(ErrorCodes.PaymentNotPending, $"{paymentId} was rejected.");
        }

        if (actor != payment.To)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the receiving member can confirm a payment.");
        }

        var lines = Postings.ForPayment(payment.From, payment.To, payment.Amount);
        var posted = new PaymentPosted(paymentId, payment.From, payment.To, payment.Amount, payment.PlanId, payment.TransferIndex, lines);
        return Decision.New(Stamp(posted, actor, ctx));
    }

    public Decision RejectPayment(MemberId actor, string paymentId, string reason, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var payment = GetPayment(paymentId);
        if (payment.Status != PaymentStatus.Pending)
        {
            throw new DomainException(ErrorCodes.PaymentNotPending, $"{paymentId} is not waiting for confirmation.");
        }

        if (actor != payment.To)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only the receiving member can reject a payment.");
        }

        RequireText(reason, ErrorCodes.InvalidReason, "Reason");
        return Decision.New(Stamp(new PaymentRejected(paymentId, reason.Trim()), actor, ctx));
    }

    /// <summary>Creditor records money received. Posts immediately: a member can always reduce what is owed to them.</summary>
    public Decision RecordReceipt(MemberId actor, MemberId from, long amount, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequirePaymentParties(from, actor, amount);
        string id = $"P{_payments.Count + 1}";
        var posted = new PaymentPosted(id, from, actor, amount, null, null, Postings.ForPayment(from, actor, amount));
        return Decision.New(Stamp(posted, actor, ctx));
    }

    public Decision CreatePlan(MemberId actor, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequireActive(actor);
        var result = DebtSimplifier.Plan(Balances);
        var plan = new PlanCreated($"S{_plans.Count + 1}", LastSequence, result.Method, result.Transfers);
        return Decision.New(Stamp(plan, actor, ctx));
    }

    /// <summary>Snapshots balances at the current sequence. Balances stay cumulative, so the snapshot is the next opening balance.</summary>
    public Decision ClosePeriod(MemberId actor, string label, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequireCoordinator(actor);
        RequireText(label, ErrorCodes.InvalidDescription, "Label");
        var closed = new PeriodClosed($"R{_periods.Count + 1}", label.Trim(), LastSequence, Balances.All);
        return Decision.New(Stamp(closed, actor, ctx));
    }

    public Decision ChangeRole(MemberId actor, MemberId member, MemberRole role, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        RequireCoordinator(actor);
        var target = RequireActive(member);
        if (target.Role == MemberRole.Coordinator && role != MemberRole.Coordinator && CoordinatorCount() == 1)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "The group needs at least one coordinator.");
        }

        return Decision.New(Stamp(new MemberRoleChanged(member, role), actor, ctx));
    }

    public Decision Deactivate(MemberId actor, MemberId member, CommandContext ctx)
    {
        if (Repeated(ctx, out var repeat))
        {
            return repeat;
        }

        var target = RequireActive(member);
        if (actor != member)
        {
            RequireCoordinator(actor);
        }

        bool openDraft = _drafts.Values.Any(d => d.Status == DraftStatus.Open && (d.Payer == member || d.CreatedBy == member || d.Shares.Any(s => s.Member == member)));
        bool pendingPayment = _payments.Values.Any(p => p.Status == PaymentStatus.Pending && (p.From == member || p.To == member));
        if (Balances[member] != 0 || openDraft || pendingPayment)
        {
            throw new DomainException(ErrorCodes.MemberHasOpenItems, $"@{target.Nickname} must be settled to zero with no open drafts or payments first.");
        }

        if (target.Role == MemberRole.Coordinator && CoordinatorCount() == 1 && ActiveMembers.Count > 1)
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Hand the coordinator role to someone else first.");
        }

        return Decision.New(Stamp(new MemberDeactivated(member), actor, ctx));
    }

    // ------------------------------------------------------------------ apply

    public void Apply(LedgerEvent e)
    {
        if (e.Sequence != LastSequence + 1)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, $"Expected sequence {LastSequence + 1} but got {e.Sequence}.");
        }

        if ((_events.Count == 0) != (e is GroupOpened))
        {
            throw new DomainException(ErrorCodes.InvariantViolated, "GroupOpened must be the first and only opening event.");
        }

        switch (e)
        {
            case GroupOpened g:
                GroupId = g.GroupId;
                Name = g.Name;
                Currency = new Currency(g.CurrencyCode, g.CurrencyScale);
                RemainderPolicy = g.RemainderPolicy;
                break;
            case MemberJoined j:
                _members[j.Member] = new Member(j.Member, j.Nickname, j.IdentityRef, j.Role, Active: true);
                break;
            case MemberRoleChanged r:
                _members[r.Member] = GetMember(r.Member) with { Role = r.Role };
                break;
            case MemberDeactivated d:
                _members[d.Member] = GetMember(d.Member) with { Active = false };
                break;
            case ExpenseDrafted d:
                _drafts[d.DraftId] = new Draft(d.DraftId, RequireActor(e), d.Payer, d.Amount, d.Description, d.OccurredOn, d.Shares, d.SplitSummary, d.ExpiresAt, DraftStatus.Open, null);
                break;
            case DraftDiscarded x:
                _drafts[x.DraftId] = GetDraft(x.DraftId) with { Status = DraftStatus.Discarded };
                break;
            case ExpensePosted p:
                ApplyExpensePosted(p);
                break;
            case ExpenseReversed r:
                var reversed = GetExpense(r.ExpenseId);
                RequireSameLines(r.Lines, Postings.Reverse(reversed.Lines));
                _expenses[r.ExpenseId] = reversed with { Status = ExpenseStatus.Reversed, ReversalReason = r.Reason };
                Post(r.Lines, $"{r.ExpenseId}/reversal", e.Sequence, planId: null);
                break;
            case PaymentClaimed c:
                _payments[c.PaymentId] = new Payment(c.PaymentId, c.From, c.To, c.Amount, c.PlanId, c.TransferIndex, RequireActor(e), PaymentStatus.Pending, null, null);
                break;
            case PaymentPosted p:
                RequireSameLines(p.Lines, Postings.ForPayment(p.From, p.To, p.Amount));
                _payments[p.PaymentId] = _payments.TryGetValue(p.PaymentId, out var pending)
                    ? pending with { Status = PaymentStatus.Posted, PostedSequence = e.Sequence }
                    : new Payment(p.PaymentId, p.From, p.To, p.Amount, p.PlanId, p.TransferIndex, RequireActor(e), PaymentStatus.Posted, e.Sequence, null);
                Post(p.Lines, p.PaymentId, e.Sequence, p.PlanId);
                break;
            case PaymentRejected r:
                _payments[r.PaymentId] = GetPayment(r.PaymentId) with { Status = PaymentStatus.Rejected, RejectionReason = r.Reason };
                break;
            case PlanCreated pc:
                _plans[pc.PlanId] = new SettlementPlan(pc.PlanId, pc.BaseSequence, pc.Method, pc.Transfers);
                break;
            case PeriodClosed pc:
                _periods.Add(pc);
                break;
            default:
                throw new DomainException(ErrorCodes.InvariantViolated, $"Unknown event type {e.GetType().Name}.");
        }

        if (e.SourceKey is not null)
        {
            _sourceKeys.TryAdd(e.SourceKey, e.Sequence);
        }

        _events.Add(e);
    }

    private void ApplyExpensePosted(ExpensePosted p)
    {
        var draft = GetDraft(p.DraftId);
        if (draft.Status != DraftStatus.Open)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, $"{p.DraftId} was posted twice.");
        }

        RequireSameLines(p.Lines, Postings.ForExpense(draft.Payer, draft.Amount, draft.Shares));
        _drafts[p.DraftId] = draft with { Status = DraftStatus.Posted, ExpenseId = p.ExpenseId };
        _expenses[p.ExpenseId] = new Expense(p.ExpenseId, p.DraftId, draft.CreatedBy, draft.Payer, draft.Amount, draft.Description, draft.OccurredOn, draft.Shares, p.Lines, p.Sequence, ExpenseStatus.Posted, null);
        Post(p.Lines, p.ExpenseId, p.Sequence, planId: null);
    }

    // ---------------------------------------------------------------- helpers

    private void Post(IReadOnlyList<PostingLine> lines, string referenceId, long sequence, string? planId)
    {
        Balances = Balances.Apply(lines);
        _entries.AddRange(lines.Select(l => (sequence, referenceId, l)));
        _balanceChanges.Add((sequence, planId));
    }

    private bool Repeated(CommandContext ctx, out Decision decision)
    {
        if (_events.Count == 0)
        {
            throw new DomainException(ErrorCodes.GroupNotOpen, "The group is not open.");
        }

        if (_sourceKeys.TryGetValue(ctx.SourceKey, out long sequence))
        {
            decision = Decision.Repeat(EventAt(sequence));
            return true;
        }

        decision = null!;
        return false;
    }

    private T Stamp<T>(T e, MemberId? actor, CommandContext ctx)
        where T : LedgerEvent => e with { Sequence = LastSequence + 1, At = ctx.Now, Actor = actor, SourceKey = ctx.SourceKey };

    private LedgerEvent EventAt(long sequence) => _events[(int)sequence - 1];

    private Member RequireActive(MemberId id)
    {
        if (!_members.TryGetValue(id, out var member))
        {
            throw new DomainException(ErrorCodes.UnknownMember, $"Unknown member {id}.");
        }

        return member.Active ? member : throw new DomainException(ErrorCodes.InactiveMember, $"@{member.Nickname} is no longer active.");
    }

    private void RequireCoordinator(MemberId actor)
    {
        if (!IsCoordinator(actor))
        {
            throw new DomainException(ErrorCodes.NotAuthorized, "Only a coordinator can do this.");
        }
    }

    private bool IsCoordinator(MemberId actor) => RequireActive(actor).Role == MemberRole.Coordinator;

    private int CoordinatorCount() => _members.Values.Count(m => m.Active && m.Role == MemberRole.Coordinator);

    private void RequirePaymentParties(MemberId from, MemberId to, long amount)
    {
        RequireActive(from);
        RequireActive(to);
        if (from == to)
        {
            throw new DomainException(ErrorCodes.SelfPayment, "You cannot pay yourself.");
        }

        RequireAmount(amount);
    }

    private static void RequireAmount(long amount)
    {
        if (amount is <= 0 or > MaxAmount)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "Amount must be positive and within range.");
        }
    }

    private static void RequireText(string text, string code, string field)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxTextLength || trimmed.Any(char.IsControl))
        {
            throw new DomainException(code, $"{field} must be 1–{MaxTextLength} characters.");
        }
    }

    private static MemberId RequireActor(LedgerEvent e) =>
        e.Actor ?? throw new DomainException(ErrorCodes.InvariantViolated, $"{e.GetType().Name} #{e.Sequence} has no actor.");

    private static void RequireSameLines(IReadOnlyList<PostingLine> actual, IReadOnlyList<PostingLine> expected)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new DomainException(ErrorCodes.InvariantViolated, "Journal posting lines do not match the rule that produced them.");
        }
    }

    private static DomainException NotFound(string kind, string id) => new(ErrorCodes.NotFound, $"No {kind} {id}.");
}
