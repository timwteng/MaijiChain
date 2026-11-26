using System.Globalization;

namespace Maijichain.Domain;

/// <summary>
/// Currency of one group ledger. <see cref="Scale"/> is the number of minor-unit digits this deployment uses.
/// ISO 4217 lists TWD with 2 decimals, but everyday TWD settlement uses whole dollars, so the default TWD scale is 0.
/// One ledger holds exactly one currency, which makes cross-currency netting (INV-08) impossible by construction.
/// </summary>
public sealed record Currency
{
    public Currency(string code, int scale)
    {
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper))
        {
            throw new DomainException(ErrorCodes.InvalidCurrency, $"Currency code '{code}' must be three upper-case letters.");
        }

        if (scale is < 0 or > 4)
        {
            throw new DomainException(ErrorCodes.InvalidCurrency, "Currency scale must be between 0 and 4.");
        }

        Code = code;
        Scale = scale;
    }

    public static Currency Twd { get; } = new("TWD", 0);

    public string Code { get; }

    public int Scale { get; }

    public string Format(long minor)
    {
        decimal major = minor / Pow10(Scale);
        string number = major.ToString("N" + Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return $"{number} {Code}";
    }

    /// <summary>Parses a positive amount such as "1200", "1,200", "NT$1200" or, for scale 2, "12.50".</summary>
    public bool TryParse(string text, out long minor)
    {
        minor = 0;
        string s = text.Trim();
        foreach (string prefix in new[] { "NT$", "$" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
            }
        }

        s = s.Replace(",", string.Empty, StringComparison.Ordinal);
        if (s.Length == 0 || s.Length > 18 || !s.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return false;
        }

        string[] parts = s.Split('.');
        if (parts.Length > 2 || parts[0].Length == 0 || (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Length > Scale)))
        {
            return false;
        }

        string fraction = parts.Length == 2 ? parts[1].PadRight(Scale, '0') : new string('0', Scale);
        if (!long.TryParse(parts[0] + fraction, NumberStyles.None, CultureInfo.InvariantCulture, out minor))
        {
            return false;
        }

        return minor > 0;
    }

    private static decimal Pow10(int scale) => scale switch { 0 => 1m, 1 => 10m, 2 => 100m, 3 => 1000m, _ => 10000m };
}
