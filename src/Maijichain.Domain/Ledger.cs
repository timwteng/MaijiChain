using System.Collections.Immutable;

namespace Maijichain.Domain;

public enum EntryKind
{
    /// <summary>Payer is credited with the full expense amount.</summary>
    Paid,

    /// <summary>Participant is debited with their allocated share.</summary>
    Share,

    /// <summary>Debtor is credited for money they sent.</summary>
    PaymentSent,

    /// <summary>Creditor is debited for money they received.</summary>
    PaymentReceived,

    /// <summary>Exact negation of an earlier line.</summary>
    Reversal,
}

/// <summary>One signed ledger line. Positive = the group owes the member more; negative = the member owes more.</summary>
public sealed record PostingLine(MemberId Member, long Amount, EntryKind Kind);

/// <summary>Constructors for balanced postings. Every posting sums to zero (INV-01, INV-07).</summary>
public static class Postings
{
    public static IReadOnlyList<PostingLine> ForExpense(MemberId payer, long amount, IReadOnlyList<Share> shares)
    {
        var lines = new List<PostingLine>(shares.Count + 1) { new(payer, amount, EntryKind.Paid) };
        lines.AddRange(shares.Select(s => new PostingLine(s.Member, -s.Amount, EntryKind.Share)));
        EnsureBalanced(lines);
        return lines;
    }

    public static IReadOnlyList<PostingLine> ForPayment(MemberId from, MemberId to, long amount)
    {
        if (from == to)
        {
            throw new DomainException(ErrorCodes.SelfPayment, "Sender and receiver must be different members.");
        }

        if (amount <= 0)
        {
            throw new DomainException(ErrorCodes.InvalidAmount, "Payment amount must be positive.");
        }

        var lines = new List<PostingLine> { new(from, amount, EntryKind.PaymentSent), new(to, -amount, EntryKind.PaymentReceived) };
        EnsureBalanced(lines);
        return lines;
    }

    public static IReadOnlyList<PostingLine> Reverse(IReadOnlyList<PostingLine> original)
    {
        var lines = original.Select(l => new PostingLine(l.Member, checked(-l.Amount), EntryKind.Reversal)).ToList();
        EnsureBalanced(lines);
        return lines;
    }

    public static void EnsureBalanced(IReadOnlyList<PostingLine> lines)
    {
        long sum = lines.Aggregate(0L, (acc, l) => checked(acc + l.Amount));
        if (sum != 0 || lines.Count == 0)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, $"Posting does not balance (sum {sum}).");
        }
    }
}

/// <summary>Immutable per-member balances of one ledger. The total is always zero (INV-02).</summary>
public sealed class BalanceSheet
{
    private readonly ImmutableSortedDictionary<MemberId, long> _balances;

    private BalanceSheet(ImmutableSortedDictionary<MemberId, long> balances) => _balances = balances;

    public static BalanceSheet Empty { get; } = new(ImmutableSortedDictionary<MemberId, long>.Empty);

    /// <summary>Builds a sheet from explicit balances, rejecting input that does not sum to zero.</summary>
    public static BalanceSheet From(IEnumerable<MemberBalance> balances)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<MemberId, long>();
        foreach (var b in balances)
        {
            builder[b.Member] = checked(builder.GetValueOrDefault(b.Member) + b.Balance);
        }

        var sheet = new BalanceSheet(builder.ToImmutable());
        sheet.EnsureZeroSum();
        return sheet;
    }

    public long this[MemberId member] => _balances.GetValueOrDefault(member);

    public IReadOnlyList<MemberBalance> All => _balances.Select(kv => new MemberBalance(kv.Key, kv.Value)).ToList();

    public IReadOnlyList<MemberBalance> NonZero => _balances.Where(kv => kv.Value != 0).Select(kv => new MemberBalance(kv.Key, kv.Value)).ToList();

    public BalanceSheet Apply(IEnumerable<PostingLine> lines)
    {
        var builder = _balances.ToBuilder();
        foreach (var line in lines)
        {
            builder[line.Member] = checked(builder.GetValueOrDefault(line.Member) + line.Amount);
        }

        var next = new BalanceSheet(builder.ToImmutable());
        next.EnsureZeroSum();
        return next;
    }

    public BalanceSheet Apply(IEnumerable<Transfer> transfers) =>
        Apply(transfers.SelectMany(t => Postings.ForPayment(t.From, t.To, t.Amount)));

    private void EnsureZeroSum()
    {
        long total = _balances.Values.Aggregate(0L, (acc, v) => checked(acc + v));
        if (total != 0)
        {
            throw new DomainException(ErrorCodes.InvariantViolated, $"Balances sum to {total}, not zero.");
        }
    }
}
