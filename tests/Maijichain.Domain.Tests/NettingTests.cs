namespace Maijichain.Domain.Tests;

public sealed class NettingTests
{
    private static MemberId M(string id) => new(id);

    private static BalanceSheet Sheet(params (string Id, long Balance)[] balances) =>
        BalanceSheet.From(balances.Select(b => new MemberBalance(M(b.Id), b.Balance)));

    [Fact]
    public void Worked_example_from_spec_section_8_3()
    {
        var sheet = Sheet(("A", 1100), ("B", 200), ("C", -500), ("D", -800));

        var greedy = DebtSimplifier.Greedy(sheet);
        Assert.Equal(new[] { new Transfer(M("D"), M("A"), 800), new Transfer(M("C"), M("A"), 300), new Transfer(M("C"), M("B"), 200) }, greedy);

        var plan = DebtSimplifier.Plan(sheet);
        Assert.Equal(NettingMethod.ExactMinimum, plan.Method);
        Assert.Equal(3, plan.Transfers.Count);
        Assert.Equal(1300, plan.Transfers.Sum(t => t.Amount));
    }

    [Fact]
    public void Greedy_is_not_always_minimal_but_exact_is()
    {
        // Debtors -3,-2,-2 and creditors 3,4 split into {3,-3} and {4,-2,-2}: 3 transfers, while greedy needs 4.
        var sheet = Sheet(("a", -3), ("b", -2), ("c", -2), ("d", 3), ("e", 4));
        Assert.Equal(4, DebtSimplifier.Greedy(sheet).Count);
        Assert.Equal(3, DebtSimplifier.ExactMinimum(sheet).Count);
    }

    [Fact]
    public void Exact_finds_two_independent_groups()
    {
        var sheet = Sheet(("c1", 6), ("c2", 4), ("c3", 3), ("d1", -7), ("d2", -6));
        var exact = DebtSimplifier.ExactMinimum(sheet);
        Assert.Equal(3, exact.Count);
        Assert.Contains(new Transfer(M("d2"), M("c1"), 6), exact);
        Assert.Equal(4, DebtSimplifier.Greedy(sheet).Count);
    }

    [Fact]
    public void TC008_circular_obligations_need_no_transfer()
    {
        var sheet = BalanceSheet.Empty
            .Apply(Postings.ForPayment(M("a"), M("b"), 100))
            .Apply(Postings.ForPayment(M("b"), M("c"), 100))
            .Apply(Postings.ForPayment(M("c"), M("a"), 100));
        var plan = DebtSimplifier.Plan(sheet);
        Assert.Empty(plan.Transfers);
        Assert.Equal(NettingMethod.None, plan.Method);
    }

    [Fact]
    public void TC009_eleven_unsettled_members_clear_in_at_most_ten_transfers()
    {
        var balances = new long[] { 1200, 950, 400, 35, -60, -175, -230, -420, -500, -600, -600 };
        var sheet = Sheet(balances.Select((b, i) => ($"m{i + 1:D3}", b)).ToArray());
        var plan = DebtSimplifier.Plan(sheet);
        Assert.InRange(plan.Transfers.Count, 1, 10);
        Assert.Empty(sheet.Apply(plan.Transfers).NonZero);
    }

    [Fact]
    public void TC010_equal_magnitudes_break_ties_by_member_id()
    {
        var sheet = Sheet(("m002", 5), ("m001", 5), ("m004", -5), ("m003", -5));
        var expected = new[] { new Transfer(M("m003"), M("m001"), 5), new Transfer(M("m004"), M("m002"), 5) };
        Assert.Equal(expected, DebtSimplifier.Greedy(sheet));
        Assert.Equal(expected, DebtSimplifier.ExactMinimum(sheet));
    }

    [Fact]
    public void Rejects_balances_that_do_not_sum_to_zero() =>
        Assert.Equal(ErrorCodes.InvariantViolated, Assert.Throws<DomainException>(() => Sheet(("a", 5), ("b", -4))).Code);

    [Fact]
    public void Large_groups_fall_back_to_greedy_and_say_so()
    {
        var balances = Enumerable.Range(1, 24).Select(i => ($"m{i:D3}", (long)(i % 2 == 0 ? i * 10 : -i * 10))).ToList();
        long fix = -balances.Sum(b => b.Item2);
        balances.Add(("m999", fix));
        var plan = DebtSimplifier.Plan(Sheet(balances.ToArray()));
        Assert.Equal(NettingMethod.Greedy, plan.Method);
        Assert.False(plan.ProvablyMinimal);
        Assert.True(plan.Transfers.Count <= plan.NonZeroMembers - 1);
    }

    [Fact]
    public void Exact_handles_the_largest_supported_group_quickly()
    {
        var random = new Random(3);
        var values = Enumerable.Range(0, DebtSimplifier.MaxExactMembers - 1).Select(_ => (long)random.Next(-5000, 5000)).Where(v => v != 0).ToList();
        values.Add(-values.Sum());
        var sheet = Sheet(values.Select((v, i) => ($"m{i:D3}", v)).ToArray());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var transfers = DebtSimplifier.ExactMinimum(sheet);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Empty(sheet.Apply(transfers).NonZero);
    }

    [Fact]
    public void Property_plans_settle_preserve_positions_and_exact_is_optimal()
    {
        var random = new Random(20261120);
        for (int run = 0; run < 1500; run++)
        {
            int k = random.Next(1, 9);
            var values = new List<long>();
            for (int i = 0; i < k - 1; i++)
            {
                // small magnitudes make zero-sum subsets common, which is where greedy and exact differ
                values.Add(random.Next(1, 7) * (random.Next(2) == 0 ? -1 : 1) * (run % 3 == 0 ? 1 : 100));
            }

            values.Add(-values.Sum());
            var named = values.Select((v, i) => ($"m{i:D3}", v)).OrderBy(_ => random.Next()).ToArray();
            var sheet = Sheet(named);
            int nonZero = sheet.NonZero.Count;

            var exact = DebtSimplifier.ExactMinimum(sheet);
            var greedy = DebtSimplifier.Greedy(sheet);
            long credit = sheet.NonZero.Where(b => b.Balance > 0).Sum(b => b.Balance);

            foreach (var plan in new[] { exact, greedy })
            {
                Assert.Empty(sheet.Apply(plan).NonZero);                            // INV-06 / FR-NET-05
                Assert.All(plan, t => Assert.True(t.Amount > 0 && t.From != t.To)); // INV-05
                Assert.Equal(credit, plan.Sum(t => t.Amount));                        // no money routed through intermediaries
                Assert.True(plan.Count <= Math.Max(0, nonZero - 1));                 // FR-NET-04
            }

            Assert.Equal(BruteForceMinimum(sheet.NonZero.Select(b => b.Balance).ToArray()), exact.Count);
            Assert.True(greedy.Count >= exact.Count);
            Assert.Equal(exact, DebtSimplifier.ExactMinimum(Sheet(Enumerable.Reverse(named).ToArray()))); // NFR-02 determinism
        }
    }

    /// <summary>Independent oracle: enumerate every set partition and keep the one with most zero-sum blocks.</summary>
    private static int BruteForceMinimum(long[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        int best = 0;
        var blocks = new List<long>();

        void Assign(int index)
        {
            if (index == values.Length)
            {
                if (blocks.All(b => b == 0))
                {
                    best = Math.Max(best, blocks.Count);
                }

                return;
            }

            for (int b = 0; b < blocks.Count; b++)
            {
                blocks[b] += values[index];
                Assign(index + 1);
                blocks[b] -= values[index];
            }

            blocks.Add(values[index]);
            Assign(index + 1);
            blocks.RemoveAt(blocks.Count - 1);
        }

        Assign(0);
        return values.Length - best;
    }
}
