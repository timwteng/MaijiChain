using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Maijichain.Domain;

/// <summary>Who receives the indivisible remainder units of a split.</summary>
public enum RemainderPolicy
{
    /// <summary>
    /// Default. Remainder units go to participants in member-ID order, starting at a position derived from the
    /// expense key. Deterministic for a given expense, but spread evenly across members over many expenses.
    /// </summary>
    RotateByExpenseKey = 0,

    /// <summary>Spec v0.9 rule: the lowest member IDs always receive the extra units. Kept for replaying legacy history.</summary>
    FirstByMemberId = 1,
}

/// <summary>How an expense is divided among its participants.</summary>
public abstract record SplitRule
{
    public abstract IReadOnlyList<MemberId> Participants { get; }

    /// <summary>Allocates <paramref name="amount"/> minor units. Shares are positive and sum exactly to the amount (INV-03).</summary>
    public abstract IReadOnlyList<Share> Allocate(long amount, string expenseKey, RemainderPolicy policy);

    public abstract string Summary { get; }
}

public sealed record EqualSplit(IReadOnlyList<MemberId> Members) : SplitRule
{
    public override IReadOnlyList<MemberId> Participants => Members;

    public override string Summary => $"equal among {Members.Count}";

    public override IReadOnlyList<Share> Allocate(long amount, string expenseKey, RemainderPolicy policy) =>
        Allocator.ByWeights(amount, Members.Select(m => (m, 1L)).ToList(), expenseKey, policy);
}

public sealed record WeightedSplit(IReadOnlyList<(MemberId Member, int Weight)> Weights) : SplitRule
{
    public override IReadOnlyList<MemberId> Participants => Weights.Select(w => w.Member).ToList();

    public override string Summary => $"weighted among {Weights.Count}";

    public override IReadOnlyList<Share> Allocate(long amount, string expenseKey, RemainderPolicy policy)
    {
        if (Weights.Any(w => w.Weight <= 0 || w.Weight > 1000))
        {
            throw new DomainException(ErrorCodes.InvalidSplit, "Weights must be between 1 and 1000.");
        }

        return Allocator.ByWeights(amount, Weights.Select(w => (w.Member, (long)w.Weight)).ToList(), expenseKey, policy);
    }
}

public sealed record ExactSplit(IReadOnlyList<Share> Amounts) : SplitRule
{
    public override IReadOnlyList<MemberId> Participants => Amounts.Select(a => a.Member).ToList();

    public override string Summary => "exact amounts";

    public override IReadOnlyList<Share> Allocate(long amount, string expenseKey, RemainderPolicy policy)
    {
        Allocator.EnsureDistinct(Participants);
        if (Amounts.Any(a => a.Amount <= 0))
        {
            throw new DomainException(ErrorCodes.InvalidSplit, "Every exact share must be positive.");
        }

        long sum = Amounts.Aggregate(0L, (acc, a) => checked(acc + a.Amount));
        if (sum != amount)
        {
            throw new DomainException(ErrorCodes.SplitMismatch, $"Exact shares add up to {sum} but the expense is {amount} (difference {amount - sum}).");
        }

        return Amounts.OrderBy(a => a.Member).ToList();
    }
}

internal static class Allocator
{
    /// <summary>
    /// Largest-remainder (Hamilton) apportionment. Each member first receives floor(amount × weight / totalWeight);
    /// leftover units go to the largest fractional remainders, ties broken by the rotation order of <paramref name="policy"/>.
    /// </summary>
    public static IReadOnlyList<Share> ByWeights(long amount, IReadOnlyList<(MemberId Member, long Weight)> weights, string expenseKey, RemainderPolicy policy)
    {
        if (amount <= 0)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "Amount must be positive.");
        }

        if (weights.Count == 0)
        {
            throw new DomainException(ErrorCodes.InvalidSplit, "An expense needs at least one participant.");
        }

        EnsureDistinct(weights.Select(w => w.Member).ToList());
        var ordered = weights.OrderBy(w => w.Member).ToArray();
        int n = ordered.Length;
        Int128 totalWeight = 0;
        foreach (var w in ordered)
        {
            totalWeight += w.Weight;
        }

        var shares = new long[n];
        var fractions = new Int128[n];
        long allocated = 0;
        for (int i = 0; i < n; i++)
        {
            Int128 product = (Int128)amount * ordered[i].Weight;
            shares[i] = (long)(product / totalWeight);
            fractions[i] = product % totalWeight;
            allocated += shares[i];
        }

        long remainder = amount - allocated; // always < n
        int offset = policy == RemainderPolicy.RotateByExpenseKey ? StableOffset(expenseKey, n) : 0;
        var receivers = Enumerable.Range(0, n)
            .OrderByDescending(i => fractions[i])
            .ThenBy(i => (i - offset + n) % n)
            .Take((int)remainder);
        foreach (int i in receivers)
        {
            shares[i]++;
        }

        if (shares.Any(s => s <= 0))
        {
            throw new DomainException(ErrorCodes.AmountTooSmall, $"{amount} cannot give every participant at least one unit.");
        }

        return ordered.Select((w, i) => new Share(w.Member, shares[i])).ToList();
    }

    public static void EnsureDistinct(IReadOnlyCollection<MemberId> members)
    {
        if (members.Distinct().Count() != members.Count)
        {
            throw new DomainException(ErrorCodes.InvalidSplit, "A participant is listed more than once.");
        }
    }

    /// <summary>Platform-independent rotation offset: first 8 bytes of SHA-256(key), modulo n.</summary>
    internal static int StableOffset(string key, int n)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return (int)(BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)n);
    }
}
