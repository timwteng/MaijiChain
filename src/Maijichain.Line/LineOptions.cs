using Maijichain.Domain;

namespace Maijichain.Line;

public sealed class LineOptions
{
    /// <summary>Channel secret used to verify <c>x-line-signature</c>. Secret: never commit or log.</summary>
    public string ChannelSecret { get; set; } = string.Empty;

    /// <summary>Long-lived or v2.1 channel access token for the reply/push APIs. Secret.</summary>
    public string ChannelAccessToken { get; set; } = string.Empty;

    /// <summary>Server-side pepper for hashing LINE user and chat IDs before they reach the ledger. Secret.</summary>
    public string IdentityPepper { get; set; } = string.Empty;

    /// <summary>Optional bot user ID; when set, webhooks for any other <c>destination</c> are rejected.</summary>
    public string? BotUserId { get; set; }

    /// <summary>
    /// Push recipients allowed per calendar month. Reply messages are free; pushes are charged per group member,
    /// and the free Taiwan plan includes 200 messages a month, so the default leaves headroom.
    /// </summary>
    public int MonthlyPushBudget { get; set; } = 150;

    /// <summary>How long after receipt a reply token is still used. LINE documents one minute.</summary>
    public TimeSpan ReplyWindow { get; set; } = TimeSpan.FromSeconds(50);

    public string DefaultCurrencyCode { get; set; } = "TWD";

    public int DefaultCurrencyScale { get; set; }

    public RemainderPolicy RemainderPolicy { get; set; } = RemainderPolicy.RotateByExpenseKey;

    /// <summary>Offset used to decide "today" for expense dates. Taiwan has no daylight saving time.</summary>
    public int UtcOffsetHours { get; set; } = 8;

    public Currency DefaultCurrency => new(DefaultCurrencyCode, DefaultCurrencyScale);

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ChannelSecret))
        {
            yield return "Line:ChannelSecret is required.";
        }

        if (string.IsNullOrWhiteSpace(ChannelAccessToken))
        {
            yield return "Line:ChannelAccessToken is required.";
        }

        if (IdentityPepper.Length < 32)
        {
            yield return "Line:IdentityPepper must be at least 32 characters of random data.";
        }

        if (MonthlyPushBudget < 0)
        {
            yield return "Line:MonthlyPushBudget cannot be negative.";
        }
    }
}
