namespace Maijichain.Domain.Tests;

public sealed class GroupLedgerTests
{
    private static void AssertZeroSum(GroupLedger g) => Assert.Equal(0, g.Balances.All.Sum(b => b.Balance)); // INV-02

    [Fact]
    public void First_member_becomes_coordinator_and_nicknames_are_unique()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        Assert.Equal(MemberRole.Coordinator, t.Ledger.GetMember(t.Id("amy")).Role);
        Assert.Equal(MemberRole.Member, t.Ledger.GetMember(t.Id("bob")).Role);
        Assert.Equal(ErrorCodes.NicknameTaken, Assert.Throws<DomainException>(() => t.Ledger.Join("identity-x", "AMY", t.Ctx())).Code);
        Assert.Equal(ErrorCodes.AlreadyMember, Assert.Throws<DomainException>(() => t.Ledger.Join("identity-amy", "amy2", t.Ctx())).Code);
        Assert.Equal(ErrorCodes.InvalidNickname, Assert.Throws<DomainException>(() => t.Ledger.Join("identity-y", "all", t.Ctx())).Code);
        Assert.Equal(ErrorCodes.InvalidNickname, Assert.Throws<DomainException>(() => t.Ledger.Join("identity-z", "two words", t.Ctx())).Code);
    }

    [Fact]
    public void Draft_does_not_move_balances_until_confirmed()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        var draft = t.Draft("amy", 300, t.Equal("amy", "bob", "cat"));
        Assert.Empty(t.Ledger.Balances.NonZero); // UR-03

        t.Run(g => g.ConfirmDraft(t.Id("amy"), draft.DraftId, t.Ctx()));
        Assert.Equal(200, t.Balance("amy"));
        Assert.Equal(-100, t.Balance("bob"));
        AssertZeroSum(t.Ledger);
    }

    [Fact]
    public void TC002_payer_inside_split_nets_own_share()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        var posted = t.Expense("amy", 300, t.Equal("amy", "bob", "cat"));
        Assert.Equal(0, posted.Lines.Sum(l => l.Amount)); // INV-01
        Assert.Equal(200, t.Balance("amy"));
    }

    [Fact]
    public void TC003_payer_outside_split_is_owed_everything()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        t.Expense("amy", 300, t.Equal("bob", "cat"));
        Assert.Equal(300, t.Balance("amy"));
        Assert.Equal(-150, t.Balance("cat"));
    }

    [Fact]
    public void Someone_else_can_record_an_expense_paid_by_another_member()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("bob", 500, t.Equal("amy", "bob"), payer: "amy");
        Assert.Equal(250, t.Balance("amy"));
        Assert.Equal(t.Id("bob"), t.Ledger.GetExpense("E1").RecordedBy);
    }

    [Fact]
    public void TC006_second_confirmation_has_no_effect()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        var draft = t.Draft("amy", 100, t.Equal("amy", "bob"));
        var first = t.Run(g => g.ConfirmDraft(t.Id("amy"), draft.DraftId, t.Ctx("tap-1")));
        var second = t.Run(g => g.ConfirmDraft(t.Id("amy"), draft.DraftId, t.Ctx("tap-2")));

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Same(first.Event, second.Event);
        Assert.Equal(50, t.Balance("amy"));
    }

    [Fact]
    public void TC005_repeated_source_key_returns_original_result()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        var first = t.Run(g => g.RecordReceipt(t.Id("amy"), t.Id("bob"), 40, t.Ctx("webhook-1")));
        var again = t.Run(g => g.RecordReceipt(t.Id("amy"), t.Id("bob"), 40, t.Ctx("webhook-1")));
        Assert.True(again.IsDuplicate);
        Assert.Equal(first.Event, again.Event);
        Assert.Equal(-40, t.Balance("amy"));
    }

    [Fact]
    public void Only_the_creator_can_confirm_and_expired_or_cancelled_drafts_stay_closed()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        var draft = t.Draft("amy", 100, t.Equal("amy", "bob"));
        Assert.Equal(ErrorCodes.NotAuthorized, Assert.Throws<DomainException>(() => t.Ledger.ConfirmDraft(t.Id("bob"), draft.DraftId, t.Ctx())).Code);

        t.Now += GroupLedger.DraftLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(ErrorCodes.DraftExpired, Assert.Throws<DomainException>(() => t.Ledger.ConfirmDraft(t.Id("amy"), draft.DraftId, t.Ctx())).Code);

        var other = t.Draft("amy", 100, t.Equal("amy", "bob"));
        t.Run(g => g.DiscardDraft(t.Id("amy"), other.DraftId, "typo", t.Ctx()));
        Assert.Equal(ErrorCodes.DraftClosed, Assert.Throws<DomainException>(() => t.Ledger.ConfirmDraft(t.Id("amy"), other.DraftId, t.Ctx())).Code);
        Assert.Empty(t.Ledger.Balances.NonZero);
    }

    [Fact]
    public void TC007_reversal_keeps_history_and_replacement_gets_new_id()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        var original = t.Expense("amy", 900, t.Equal("amy", "bob", "cat"));
        var reversed = t.Run<ExpenseReversed>(g => g.ReverseExpense(t.Id("amy"), original.ExpenseId, "wrong amount", t.Ctx()));

        Assert.Equal(original.Lines.Select(l => -l.Amount), reversed.Lines.Select(l => l.Amount)); // INV-07
        Assert.Empty(t.Ledger.Balances.NonZero);
        Assert.Equal(ExpenseStatus.Reversed, t.Ledger.GetExpense(original.ExpenseId).Status);
        Assert.Contains(t.Ledger.Events, e => e is ExpensePosted p && p.ExpenseId == original.ExpenseId); // still visible

        var replacement = t.Expense("amy", 600, t.Equal("amy", "bob", "cat"));
        Assert.NotEqual(original.ExpenseId, replacement.ExpenseId);
        Assert.Equal(400, t.Balance("amy"));
        Assert.Equal(ErrorCodes.ExpenseNotPosted, Assert.Throws<DomainException>(() => t.Ledger.ReverseExpense(t.Id("amy"), original.ExpenseId, "again", t.Ctx())).Code);
    }

    [Fact]
    public void Only_recorder_or_coordinator_may_reverse()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        var posted = t.Expense("bob", 300, t.Equal("amy", "bob", "cat"));
        Assert.Equal(ErrorCodes.NotAuthorized, Assert.Throws<DomainException>(() => t.Ledger.ReverseExpense(t.Id("cat"), posted.ExpenseId, "no", t.Ctx())).Code);
        t.Run(g => g.ReverseExpense(t.Id("amy"), posted.ExpenseId, "coordinator fix", t.Ctx())); // amy is coordinator
        Assert.Empty(t.Ledger.Balances.NonZero);
    }

    [Fact]
    public void Debtor_claim_moves_nothing_until_creditor_confirms()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        var claim = t.Run<PaymentClaimed>(g => g.ClaimPayment(t.Id("bob"), t.Id("amy"), 100, t.Ctx()));
        Assert.Equal(-100, t.Balance("bob"));

        Assert.Equal(ErrorCodes.NotAuthorized, Assert.Throws<DomainException>(() => t.Ledger.ConfirmPayment(t.Id("bob"), claim.PaymentId, t.Ctx())).Code);
        t.Run(g => g.ConfirmPayment(t.Id("amy"), claim.PaymentId, t.Ctx("received-1")));
        Assert.Empty(t.Ledger.Balances.NonZero);

        var again = t.Run(g => g.ConfirmPayment(t.Id("amy"), claim.PaymentId, t.Ctx("received-2")));
        Assert.True(again.IsDuplicate); // FR-SET-04
        Assert.Empty(t.Ledger.Balances.NonZero);
    }

    [Fact]
    public void Creditor_can_reject_a_claim_and_record_receipts_directly()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        var claim = t.Run<PaymentClaimed>(g => g.ClaimPayment(t.Id("bob"), t.Id("amy"), 100, t.Ctx()));
        t.Run(g => g.RejectPayment(t.Id("amy"), claim.PaymentId, "not received", t.Ctx()));
        Assert.Equal(-100, t.Balance("bob"));
        Assert.Equal(ErrorCodes.PaymentNotPending, Assert.Throws<DomainException>(() => t.Ledger.ConfirmPayment(t.Id("amy"), claim.PaymentId, t.Ctx())).Code);

        t.Receipt("amy", "bob", 100);
        Assert.Empty(t.Ledger.Balances.NonZero);
        Assert.Equal(ErrorCodes.SelfPayment, Assert.Throws<DomainException>(() => t.Ledger.RecordReceipt(t.Id("amy"), t.Id("amy"), 5, t.Ctx())).Code);
    }

    [Fact]
    public void Plan_survives_its_own_payments_and_settles_everyone()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat", "dan");
        t.Expense("amy", 1200, t.Equal("amy", "bob", "cat", "dan"));
        t.Expense("bob", 400, t.Equal("bob", "cat", "dan", "amy"));
        var plan = t.Run<PlanCreated>(g => g.CreatePlan(t.Id("cat"), t.Ctx()));
        Assert.Equal(NettingMethod.ExactMinimum, plan.Method);

        for (int i = 0; i < plan.Transfers.Count; i++)
        {
            var transfer = plan.Transfers[i];
            int index = i;
            var claim = t.Run<PaymentClaimed>(g => g.ClaimPlanTransfer(transfer.From, plan.PlanId, index, t.Ctx()));
            t.Run(g => g.ConfirmPayment(transfer.To, claim.PaymentId, t.Ctx()));
            Assert.False(t.Ledger.IsPlanStale(plan.PlanId)); // a plan's own payments never make it stale
        }

        Assert.Empty(t.Ledger.Balances.NonZero);
    }

    [Fact]
    public void TC011_plan_becomes_stale_when_ledger_changes_for_another_reason()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        var plan = t.Run<PlanCreated>(g => g.CreatePlan(t.Id("amy"), t.Ctx()));
        t.Expense("amy", 50, t.Equal("bob"));

        Assert.True(t.Ledger.IsPlanStale(plan.PlanId));
        Assert.Equal(ErrorCodes.PlanStale, Assert.Throws<DomainException>(() => t.Ledger.ClaimPlanTransfer(t.Id("bob"), plan.PlanId, 0, t.Ctx())).Code);
    }

    [Fact]
    public void A_plan_transfer_is_claimed_once_unless_rejected()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        var plan = t.Run<PlanCreated>(g => g.CreatePlan(t.Id("amy"), t.Ctx()));
        var claim = t.Run<PaymentClaimed>(g => g.ClaimPlanTransfer(t.Id("bob"), plan.PlanId, 0, t.Ctx()));
        Assert.Equal(ErrorCodes.PlanTransferClaimed, Assert.Throws<DomainException>(() => t.Ledger.ClaimPlanTransfer(t.Id("bob"), plan.PlanId, 0, t.Ctx())).Code);
        Assert.Equal(ErrorCodes.NotAuthorized, Assert.Throws<DomainException>(() => t.Ledger.ClaimPlanTransfer(t.Id("amy"), plan.PlanId, 0, t.Ctx())).Code);

        t.Run(g => g.RejectPayment(t.Id("amy"), claim.PaymentId, "not yet", t.Ctx()));
        t.Run(g => g.ClaimPlanTransfer(t.Id("bob"), plan.PlanId, 0, t.Ctx()));
    }

    [Fact]
    public void Closing_a_period_snapshots_balances_that_later_activity_does_not_change()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        Assert.Equal(ErrorCodes.NotAuthorized, Assert.Throws<DomainException>(() => t.Ledger.ClosePeriod(t.Id("bob"), "2026-Q4", t.Ctx())).Code);
        var closed = t.Run<PeriodClosed>(g => g.ClosePeriod(t.Id("amy"), "2026-Q4", t.Ctx()));
        t.Expense("bob", 80, t.Equal("amy", "bob"));

        Assert.Equal(closed.Balances, t.Ledger.BalancesThrough(closed.ThroughSequence).All);
        Assert.Equal(100, closed.Balances.Single(b => b.Member == t.Id("amy")).Balance);
        Assert.Equal(60, t.Balance("amy")); // balances carry forward across periods
    }

    [Fact]
    public void Members_leave_only_when_settled_and_inactive_members_cannot_be_charged()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        t.Expense("amy", 300, t.Equal("amy", "bob", "cat"));
        Assert.Equal(ErrorCodes.MemberHasOpenItems, Assert.Throws<DomainException>(() => t.Ledger.Deactivate(t.Id("cat"), t.Id("cat"), t.Ctx())).Code);

        t.Receipt("amy", "cat", 100);
        var cat = t.Id("cat");
        t.Run(g => g.Deactivate(cat, cat, t.Ctx()));
        Assert.Null(t.Ledger.FindByNickname("cat"));
        Assert.Equal(ErrorCodes.InactiveMember, Assert.Throws<DomainException>(() => t.Ledger.ProposeExpense(t.Id("amy"), t.Id("amy"), 10, "x", default, new EqualSplit([cat]), t.Ctx())).Code);

        var back = t.Run<MemberJoined>(g => g.Join("identity-cat", "kitty", t.Ctx()));
        Assert.Equal(cat, back.Member); // returning member keeps their ID and history
    }

    [Fact]
    public void Statement_explains_the_balance()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        t.Expense("bob", 60, t.Equal("amy", "bob"));
        var lines = t.Ledger.StatementFor(t.Id("amy"));
        Assert.Equal(t.Balance("amy"), lines[^1].RunningBalance);
        Assert.Equal(new[] { "E1", "E1", "E2" }, lines.Select(l => l.ReferenceId).ToArray());
    }

    [Fact]
    public void Replay_rebuilds_identical_state()
    {
        var t = TestGroup.WithMembers("amy", "bob", "cat");
        t.Expense("amy", 999, t.Equal("amy", "bob", "cat"));
        var e2 = t.Expense("bob", 120, t.Equal("amy", "cat"));
        t.Run(g => g.ReverseExpense(t.Id("bob"), e2.ExpenseId, "dup", t.Ctx()));
        t.Receipt("amy", "cat", 50);

        var copy = GroupLedger.Replay(t.Ledger.Events);
        Assert.Equal(t.Ledger.Balances.All, copy.Balances.All);
        Assert.Equal(t.Ledger.LastSequence, copy.LastSequence);
    }

    [Fact]
    public void Replay_rejects_tampered_posting_lines()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        t.Expense("amy", 200, t.Equal("amy", "bob"));
        var events = t.Ledger.Events.ToList();
        var posted = (ExpensePosted)events[^1];
        events[^1] = posted with { Lines = posted.Lines.Select(l => l with { Amount = l.Amount * 2 }).ToList() };
        Assert.Equal(ErrorCodes.InvariantViolated, Assert.Throws<DomainException>(() => GroupLedger.Replay(events)).Code);
    }

    [Fact]
    public void Replay_rejects_gaps_in_sequence()
    {
        var t = TestGroup.WithMembers("amy", "bob");
        var events = t.Ledger.Events.Where(e => e.Sequence != 2).ToList();
        Assert.Throws<DomainException>(() => GroupLedger.Replay(events));
    }

    [Fact]
    public void Property_random_activity_never_breaks_invariants()
    {
        var random = new Random(42);
        string[] nicks = Enumerable.Range(1, 11).Select(i => $"p{i}").ToArray();
        for (int scenario = 0; scenario < 120; scenario++)
        {
            var t = new TestGroup((RemainderPolicy)(scenario % 2), nicks);
            for (int step = 0; step < 40; step++)
            {
                string actor = nicks[random.Next(nicks.Length)];
                try
                {
                    switch (random.Next(6))
                    {
                        case 0 or 1 or 2:
                            var participants = nicks.Where(_ => random.Next(3) > 0).DefaultIfEmpty(actor).ToArray();
                            t.Expense(actor, random.Next(1, 20_000), t.Equal(participants), payer: nicks[random.Next(nicks.Length)]);
                            break;
                        case 3:
                            t.Receipt(actor, nicks[random.Next(nicks.Length)], random.Next(1, 3000));
                            break;
                        case 4 when t.Ledger.Events.OfType<ExpensePosted>().LastOrDefault() is { } last:
                            t.Run(g => g.ReverseExpense(g.GetExpense(last.ExpenseId).RecordedBy, last.ExpenseId, "random", t.Ctx()));
                            break;
                        default:
                            var plan = t.Run<PlanCreated>(g => g.CreatePlan(t.Id(actor), t.Ctx()));
                            Assert.Empty(t.Ledger.Balances.Apply(plan.Transfers).NonZero);
                            break;
                    }
                }
                catch (DomainException ex) when (ex.Code is ErrorCodes.SelfPayment or ErrorCodes.AmountTooSmall or ErrorCodes.ExpenseNotPosted)
                {
                    // legitimate rejections of random input
                }

                AssertZeroSum(t.Ledger);
            }

            Assert.Equal(t.Ledger.Balances.All, GroupLedger.Replay(t.Ledger.Events).Balances.All);
        }
    }
}
