using Maijichain.Domain;

namespace Maijichain.Domain.Tests;

/// <summary>Small helper that drives a <see cref="GroupLedger"/> the way the application layer does: decide, then apply.</summary>
internal sealed class TestGroup
{
    private int _keys;

    public TestGroup(RemainderPolicy policy = RemainderPolicy.RotateByExpenseKey, params string[] nicknames)
    {
        Now = new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
        Ledger = GroupLedger.Replay([GroupLedger.Open("g-test", "Test group", Currency.Twd, policy, Ctx())]);
        foreach (string nick in nicknames)
        {
            Run(g => g.Join($"identity-{nick}", nick, Ctx()));
        }
    }

    public GroupLedger Ledger { get; }

    public DateTimeOffset Now { get; set; }

    public static TestGroup WithMembers(params string[] nicknames) => new(RemainderPolicy.RotateByExpenseKey, nicknames);

    public CommandContext Ctx(string? key = null) => new(Now, key ?? $"key-{++_keys}");

    public MemberId Id(string nickname) => Ledger.FindByNickname(nickname)?.Id ?? throw new InvalidOperationException(nickname);

    public long Balance(string nickname) => Ledger.Balances[Id(nickname)];

    public Decision Run(Func<GroupLedger, Decision> command)
    {
        var decision = command(Ledger);
        if (!decision.IsDuplicate)
        {
            Ledger.Apply(decision.Event);
        }

        return decision;
    }

    public T Run<T>(Func<GroupLedger, Decision> command)
        where T : LedgerEvent => (T)Run(command).Event;

    public ExpenseDrafted Draft(string by, long amount, SplitRule split, string? payer = null, string description = "test") =>
        Run<ExpenseDrafted>(g => g.ProposeExpense(Id(by), Id(payer ?? by), amount, description, new DateOnly(2026, 11, 20), split, Ctx()));

    public ExpensePosted Expense(string by, long amount, SplitRule split, string? payer = null, string description = "test")
    {
        var draft = Draft(by, amount, split, payer, description);
        return Run<ExpensePosted>(g => g.ConfirmDraft(Id(by), draft.DraftId, Ctx()));
    }

    public EqualSplit Equal(params string[] nicknames) => new(nicknames.Select(Id).ToList());

    public PaymentPosted Receipt(string creditor, string debtor, long amount) =>
        Run<PaymentPosted>(g => g.RecordReceipt(Id(creditor), Id(debtor), amount, Ctx()));
}
