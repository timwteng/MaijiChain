namespace Maijichain.Domain;

/// <summary>A positive payment instruction from a debtor to a creditor (INV-05).</summary>
public sealed record Transfer(MemberId From, MemberId To, long Amount);

public enum NettingMethod
{
    /// <summary>Everyone is already settled.</summary>
    None,

    /// <summary>Provably minimal number of transfers (exact zero-sum partition search).</summary>
    ExactMinimum,

    /// <summary>Largest-creditor/largest-debtor heuristic: at most k − 1 transfers, not always minimal.</summary>
    Greedy,
}

public sealed record NettingResult(IReadOnlyList<Transfer> Transfers, NettingMethod Method, int NonZeroMembers)
{
    public bool ProvablyMinimal => Method is NettingMethod.ExactMinimum or NettingMethod.None;
}

/// <summary>
/// Converts net balances into settlement transfers without changing any member's economic position (INV-06).
/// <para>
/// The minimum number of transfers for k non-zero members is k − p, where p is the largest number of disjoint
/// zero-sum groups the members can be partitioned into. Finding p is NP-hard in general, but a subset dynamic
/// program over 2^k states is exact and instant for small groups, so groups of up to
/// <see cref="MaxExactMembers"/> unsettled members get a provably minimal plan; larger inputs fall back to greedy.
/// </para>
/// </summary>
public static class DebtSimplifier
{
    public const int MaxExactMembers = 20;

    public static NettingResult Plan(BalanceSheet balances)
    {
        var nonZero = balances.NonZero;
        if (nonZero.Count == 0)
        {
            return new NettingResult([], NettingMethod.None, 0);
        }

        bool exact = nonZero.Count <= MaxExactMembers;
        var transfers = exact ? ExactMinimum(balances) : Greedy(balances);
        return new NettingResult(transfers, exact ? NettingMethod.ExactMinimum : NettingMethod.Greedy, nonZero.Count);
    }

    /// <summary>Spec §8.2 algorithm: repeatedly match the largest creditor with the largest debtor (ties by member ID).</summary>
    public static IReadOnlyList<Transfer> Greedy(BalanceSheet balances)
    {
        var transfers = GreedyCore(balances.NonZero);
        VerifySettles(balances, transfers);
        return transfers;
    }

    /// <summary>Provably minimal transfer count via maximum zero-sum partition, then greedy within each group.</summary>
    public static IReadOnlyList<Transfer> ExactMinimum(BalanceSheet balances)
    {
        var members = balances.NonZero; // sorted by member ID → deterministic
        int k = members.Count;
        if (k > MaxExactMembers)
        {
            throw new ArgumentException($"Exact netting supports at most {MaxExactMembers} unsettled members.", nameof(balances));
        }

        var transfers = new List<Transfer>();
        foreach (var group in PartitionIntoZeroSumGroups(members))
        {
            transfers.AddRange(GreedyCore(group));
        }

        VerifySettles(balances, transfers);
        return transfers;
    }

    /// <summary>FR-NET-05: replays the transfers and requires every member to end at exactly zero.</summary>
    public static void VerifySettles(BalanceSheet balances, IEnumerable<Transfer> transfers)
    {
        var list = transfers.ToList();
        if (list.Any(t => t.Amount <= 0 || t.From == t.To))
        {
            throw new DomainException(ErrorCodes.InvariantViolated, "Netting produced a non-positive or self transfer.");
        }

        if (balances.Apply(list).NonZero.Count != 0)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, "Netting output does not clear every balance.");
        }
    }

    /// <summary>
    /// dp[mask] = maximum number of zero-sum groups along a removal chain of mask; the chain that realises dp[full]
    /// cuts the members into the largest possible number of independently settleable groups.
    /// </summary>
    internal static List<List<MemberBalance>> PartitionIntoZeroSumGroups(IReadOnlyList<MemberBalance> members)
    {
        int k = members.Count;
        int full = (1 << k) - 1;
        var sum = new long[full + 1];
        var dp = new sbyte[full + 1];
        var removed = new sbyte[full + 1];
        for (int mask = 1; mask <= full; mask++)
        {
            int low = System.Numerics.BitOperations.TrailingZeroCount(mask);
            sum[mask] = sum[mask & (mask - 1)] + members[low].Balance;
            int best = -1;
            int arg = -1;
            for (int rest = mask; rest != 0; rest &= rest - 1)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(rest);
                int value = dp[mask ^ (1 << i)];
                if (value > best)
                {
                    best = value;
                    arg = i;
                }
            }

            dp[mask] = (sbyte)(best + (sum[mask] == 0 ? 1 : 0));
            removed[mask] = (sbyte)arg;
        }

        var groups = new List<List<MemberBalance>>();
        var current = new List<MemberBalance>();
        for (int mask = full; mask != 0;)
        {
            int i = removed[mask];
            current.Add(members[i]);
            mask ^= 1 << i;
            if (sum[mask] == 0)
            {
                groups.Add(current.OrderBy(m => m.Member).ToList());
                current = [];
            }
        }

        return groups.OrderBy(g => g[0].Member).ToList();
    }

    private static List<Transfer> GreedyCore(IReadOnlyList<MemberBalance> nonZero)
    {
        var byAmountThenId = Comparer<(MemberId Member, long Amount)>.Create((a, b) =>
        {
            int c = b.Amount.CompareTo(a.Amount);
            return c != 0 ? c : a.Member.CompareTo(b.Member);
        });
        var creditors = new PriorityQueue<(MemberId Member, long Amount), (MemberId Member, long Amount)>(byAmountThenId);
        var debtors = new PriorityQueue<(MemberId Member, long Amount), (MemberId Member, long Amount)>(byAmountThenId);
        foreach (var b in nonZero)
        {
            var item = (b.Member, Math.Abs(b.Balance));
            (b.Balance > 0 ? creditors : debtors).Enqueue(item, item);
        }

        var transfers = new List<Transfer>();
        while (creditors.Count > 0 && debtors.Count > 0)
        {
            var c = creditors.Dequeue();
            var d = debtors.Dequeue();
            long x = Math.Min(c.Amount, d.Amount);
            transfers.Add(new Transfer(d.Member, c.Member, x));
            if (c.Amount > x)
            {
                var rest = (c.Member, c.Amount - x);
                creditors.Enqueue(rest, rest);
            }

            if (d.Amount > x)
            {
                var rest = (d.Member, d.Amount - x);
                debtors.Enqueue(rest, rest);
            }
        }

        if (creditors.Count != 0 || debtors.Count != 0)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, "Balances do not sum to zero.");
        }

        return transfers;
    }
}
