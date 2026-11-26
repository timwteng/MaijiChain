namespace Maijichain.Domain.Tests;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("1200", 1200L)]
    [InlineData("1,200", 1200L)]
    [InlineData("NT$1200", 1200L)]
    [InlineData("$35", 35L)]
    public void Parses_whole_dollar_twd(string text, long expected)
    {
        Assert.True(Currency.Twd.TryParse(text, out long minor));
        Assert.Equal(expected, minor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    public void Rejects_invalid_or_fractional_twd(string text) => Assert.False(Currency.Twd.TryParse(text, out _));

    [Theory]
    [InlineData("12.50", 1250L)]
    [InlineData("12.5", 1250L)]
    [InlineData("12", 1200L)]
    public void Scale_two_currency_parses_cents(string text, long expected)
    {
        Assert.True(new Currency("USD", 2).TryParse(text, out long minor));
        Assert.Equal(expected, minor);
    }

    [Fact]
    public void Formats_with_thousands_separator() => Assert.Equal("1,100 TWD", Currency.Twd.Format(1100));

    [Fact]
    public void Rejects_bad_codes() => Assert.Equal(ErrorCodes.InvalidCurrency, Assert.Throws<DomainException>(() => new Currency("twd", 0)).Code);
}
