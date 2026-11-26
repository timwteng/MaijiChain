namespace Maijichain.Domain;

/// <summary>Stable internal member identifier. Ledger records never key on display names or LINE IDs.</summary>
public readonly record struct MemberId : IComparable<MemberId>
{
    public MemberId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new DomainException(ErrorCodes.InvalidIdentifier, $"Invalid member id '{value}'.");
        }

        Value = value;
    }

    public string Value { get; }

    public int CompareTo(MemberId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator <(MemberId left, MemberId right) => left.CompareTo(right) < 0;

    public static bool operator >(MemberId left, MemberId right) => left.CompareTo(right) > 0;

    public static bool operator <=(MemberId left, MemberId right) => left.CompareTo(right) <= 0;

    public static bool operator >=(MemberId left, MemberId right) => left.CompareTo(right) >= 0;
}

/// <summary>A member's allocated amount of one expense, in minor units.</summary>
public readonly record struct Share(MemberId Member, long Amount);

/// <summary>A member's signed position: positive = the group owes the member; negative = the member owes the group.</summary>
public sealed record MemberBalance(MemberId Member, long Balance);
