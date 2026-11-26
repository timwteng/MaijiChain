using System.Text.Json;

namespace Maijichain.Domain.Tests;

/// <summary>
/// Replays every case file in <c>tests/fixtures/transaction-cases</c>. Drop anonymised historical cases there (same
/// schema, see the folder README) to turn the "150 transaction cases" claim into a runnable, auditable suite.
/// </summary>
public sealed class CaseCorpusTests
{
    public static IEnumerable<object[]> Cases()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "fixtures", "transaction-cases");
        foreach (string file in Directory.GetFiles(folder, "*.cases.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                yield return new object[] { Path.GetFileName(file), c.GetProperty("id").GetString()!, c.GetRawText() };
            }
        }
    }

    [Fact]
    public void Corpus_is_present() => Assert.NotEmpty(Cases());

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_replays_with_expected_outcome(string file, string id, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement;
        var policy = c.TryGetProperty("remainderPolicy", out var p) ? Enum.Parse<RemainderPolicy>(p.GetString()!) : RemainderPolicy.RotateByExpenseKey;
        var nicknames = c.GetProperty("members").EnumerateArray().Select(m => m.GetString()!).ToArray();
        var t = new TestGroup(policy, nicknames);

        int stepNo = 0;
        foreach (var step in c.GetProperty("steps").EnumerateArray())
        {
            stepNo++;
            string? expectedError = step.TryGetProperty("error", out var e) ? e.GetString() : null;
            try
            {
                Apply(t, step);
                Assert.True(expectedError is null, $"{file} {id} step {stepNo}: expected error {expectedError}");
            }
            catch (DomainException ex) when (expectedError is not null)
            {
                Assert.Equal(expectedError, ex.Code);
            }

            Assert.Equal(0, t.Ledger.Balances.All.Sum(b => b.Balance));
        }

        var expect = c.GetProperty("expect");
        if (expect.TryGetProperty("balances", out var balances))
        {
            foreach (var b in balances.EnumerateObject())
            {
                Assert.Equal(b.Value.GetInt64(), t.Balance(b.Name));
            }

            if (!balances.EnumerateObject().Any())
            {
                Assert.Empty(t.Ledger.Balances.NonZero);
            }
        }

        var plan = DebtSimplifier.Plan(t.Ledger.Balances);
        if (expect.TryGetProperty("transfers", out var transfers))
        {
            Assert.Equal(transfers.GetInt32(), plan.Transfers.Count);
        }

        if (expect.TryGetProperty("maxTransfers", out var max))
        {
            Assert.InRange(plan.Transfers.Count, 0, max.GetInt32());
        }

        Assert.Empty(t.Ledger.Balances.Apply(plan.Transfers).NonZero);
        Assert.Equal(t.Ledger.Balances.All, GroupLedger.Replay(t.Ledger.Events).Balances.All);
    }

    private static void Apply(TestGroup t, JsonElement step)
    {
        string op = step.GetProperty("op").GetString()!;
        string by = step.TryGetProperty("by", out var b) ? b.GetString()! : string.Empty;
        switch (op)
        {
            case "expense":
                t.Expense(by, step.GetProperty("amount").GetInt64(), Split(t, step), step.TryGetProperty("payer", out var payer) ? payer.GetString() : null);
                break;
            case "reverse":
                t.Run(g => g.ReverseExpense(t.Id(by), step.GetProperty("expense").GetString()!, step.GetProperty("reason").GetString()!, t.Ctx()));
                break;
            case "receipt":
                t.Receipt(by, step.GetProperty("from").GetString()!, step.GetProperty("amount").GetInt64());
                break;
            case "claim":
                t.Run(g => g.ClaimPayment(t.Id(by), t.Id(step.GetProperty("to").GetString()!), step.GetProperty("amount").GetInt64(), t.Ctx()));
                break;
            case "confirm-payment":
                t.Run(g => g.ConfirmPayment(t.Id(by), step.GetProperty("payment").GetString()!, t.Ctx()));
                break;
            case "reject":
                t.Run(g => g.RejectPayment(t.Id(by), step.GetProperty("payment").GetString()!, "not received", t.Ctx()));
                break;
            case "plan":
                t.Run(g => g.CreatePlan(t.Id(by), t.Ctx()));
                break;
            case "pay-plan":
                var plan = t.Ledger.GetPlan(step.GetProperty("plan").GetString()!);
                var indexes = step.TryGetProperty("all", out var all) && all.GetBoolean()
                    ? Enumerable.Range(0, plan.Transfers.Count)
                    : [step.GetProperty("transfer").GetInt32() - 1];
                foreach (int i in indexes)
                {
                    var tr = plan.Transfers[i];
                    var claim = (PaymentClaimed)t.Run(g => g.ClaimPlanTransfer(tr.From, plan.Id, i, t.Ctx())).Event;
                    t.Run(g => g.ConfirmPayment(tr.To, claim.PaymentId, t.Ctx()));
                }

                break;
            default:
                throw new InvalidOperationException($"Unknown op '{op}'.");
        }
    }

    private static SplitRule Split(TestGroup t, JsonElement step) => step.GetProperty("split").GetString() switch
    {
        "equal" => new EqualSplit(step.GetProperty("participants").EnumerateArray().Select(x => t.Id(x.GetString()!)).ToList()),
        "exact" => new ExactSplit(step.GetProperty("shares").EnumerateObject().Select(x => new Share(t.Id(x.Name), x.Value.GetInt64())).ToList()),
        "weighted" => new WeightedSplit(step.GetProperty("weights").EnumerateObject().Select(x => (t.Id(x.Name), x.Value.GetInt32())).ToList()),
        var other => throw new InvalidOperationException($"Unknown split '{other}'."),
    };
}
