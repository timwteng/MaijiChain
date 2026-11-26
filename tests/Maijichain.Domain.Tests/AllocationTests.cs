namespace Maijichain.Domain.Tests;

public sealed class AllocationTests
{
    private static readonly MemberId A = new("m001");
    private static readonly MemberId B = new("m002");
    private static readonly MemberId C = new("m003");

    [Fact]
    public void TC001_hundred_split_three_ways_gives_34_33_33()
    {
        var shares = new EqualSplit([A, B, C]).Allocate(100, "g/D1", RemainderPolicy.RotateByExpenseKey);

        Assert.Equal(100, shares.Sum(s => s.Amount));
        Assert.Equal(new[] { 33L, 33L, 34L }, shares.Select(s => s.Amount).Order().ToArray());
        Assert.Equal(shares, new EqualSplit([C, A, B]).Allocate(100, "g/D1", RemainderPolicy.RotateByExpenseKey)); // order-independent, repeatable
    }

    [Fact]
    public void Legacy_policy_gives_extra_unit_to_lowest_member_id()
    {
        var shares = new EqualSplit([C, B, A]).Allocate(100, "any", RemainderPolicy.FirstByMemberId);
        Assert.Equal(new[] { new Share(A, 34), new Share(B, 33), new Share(C, 33) }, shares);
    }

    [Fact]
    public void Rotation_spreads_rounding_units_evenly_while_legacy_rule_is_biased()
    {
        var members = Enumerable.Range(1, 11).Select(i => new MemberId($"m{i:D3}")).ToList();
        var random = new Random(20261120);
        var rotated = new long[11];
        var legacy = new long[11];
        for (int i = 0; i < 3000; i++)
        {
            long amount = random.Next(11, 10_000);
            long floor = amount / 11;
            var r = new EqualSplit(members).Allocate(amount, $"g/D{i}", RemainderPolicy.RotateByExpenseKey);
            var l = new EqualSplit(members).Allocate(amount, $"g/D{i}", RemainderPolicy.FirstByMemberId);
            for (int m = 0; m < 11; m++)
            {
                rotated[m] += r[m].Amount - floor;
                legacy[m] += l[m].Amount - floor;
            }
        }

        double mean = rotated.Average();
        Assert.All(rotated, extra => Assert.InRange((double)extra, mean * 0.85, mean * 1.15));
        Assert.Equal(0, legacy[10]); // the 11th member never absorbs a rounding unit under the v0.9 rule
        Assert.True(legacy[0] > 2000, $"first member absorbed {legacy[0]} units");
    }

    [Theory]
    [InlineData(900L, 2, 1, 600L, 300L)]
    [InlineData(10L, 1, 2, 3L, 7L)] // 3.33 / 6.67: the larger fractional remainder gets the unit
    [InlineData(1000L, 1, 1, 500L, 500L)]
    public void Weighted_split_uses_largest_remainder(long amount, int wa, int wb, long expectA, long expectB)
    {
        var shares = new WeightedSplit([(A, wa), (B, wb)]).Allocate(amount, "k", RemainderPolicy.RotateByExpenseKey);
        Assert.Equal(new[] { new Share(A, expectA), new Share(B, expectB) }, shares);
    }

    [Fact]
    public void TC004_exact_shares_must_add_up()
    {
        var ex = Assert.Throws<DomainException>(() => new ExactSplit([new Share(A, 500), new Share(B, 499)]).Allocate(1000, "k", default));
        Assert.Equal(ErrorCodes.SplitMismatch, ex.Code);
        Assert.Contains("difference 1", ex.Message);
    }

    [Theory]
    [InlineData(0L, ErrorCodes.InvalidAmount)]
    [InlineData(-10L, ErrorCodes.InvalidAmount)]
    [InlineData(2L, ErrorCodes.AmountTooSmall)]
    public void Rejects_unsplittable_amounts(long amount, string code) =>
        Assert.Equal(code, Assert.Throws<DomainException>(() => new EqualSplit([A, B, C]).Allocate(amount, "k", default)).Code);

    [Fact]
    public void Rejects_duplicate_and_empty_participants()
    {
        Assert.Equal(ErrorCodes.InvalidSplit, Assert.Throws<DomainException>(() => new EqualSplit([A, A]).Allocate(10, "k", default)).Code);
        Assert.Equal(ErrorCodes.InvalidSplit, Assert.Throws<DomainException>(() => new EqualSplit([]).Allocate(10, "k", default)).Code);
        Assert.Equal(ErrorCodes.InvalidSplit, Assert.Throws<DomainException>(() => new ExactSplit([new Share(A, 10), new Share(B, 0)]).Allocate(10, "k", default)).Code);
    }

    [Fact]
    public void Property_equal_shares_are_positive_sum_exactly_and_differ_by_at_most_one()
    {
        var random = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            int n = random.Next(1, 12);
            var members = Enumerable.Range(1, n).Select(x => new MemberId($"m{x:D3}")).OrderBy(_ => random.Next()).ToList();
            long amount = random.NextInt64(n, 5_000_000);
            var shares = new EqualSplit(members).Allocate(amount, $"k{i}", (RemainderPolicy)(i % 2));
            Assert.Equal(amount, shares.Sum(s => s.Amount));
            Assert.True(shares.Max(s => s.Amount) - shares.Min(s => s.Amount) <= 1);
            Assert.All(shares, s => Assert.True(s.Amount > 0));
        }
    }
}
