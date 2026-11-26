using System.Security.Cryptography;
using System.Text;

namespace Maijichain.Line;

/// <summary>Verifies <c>x-line-signature</c>: Base64(HMAC-SHA256(channel secret, raw request body)).</summary>
public static class LineSignature
{
    public static bool IsValid(ReadOnlySpan<byte> rawBody, string? signatureHeader, string channelSecret)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(channelSecret))
        {
            return false;
        }

        Span<byte> received = stackalloc byte[64];
        if (!Convert.TryFromBase64String(signatureHeader, received, out int length) || length != 32)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), rawBody, expected);
        return CryptographicOperations.FixedTimeEquals(expected, received[..length]); // constant-time comparison
    }

    public static string Sign(ReadOnlySpan<byte> rawBody, string channelSecret) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(channelSecret), rawBody));
}

/// <summary>
/// Replaces LINE identifiers with keyed hashes before they reach the ledger, so a leaked journal does not reveal
/// LINE user IDs (NFR-07). Only the chat route needed for push fallback is kept in the operations store.
/// </summary>
public sealed class IdentityHasher(string pepper)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(pepper);

    public string UserRef(string lineUserId) => "u" + Hex("user:" + lineUserId)[..32];

    public string GroupId(string lineChatId) => "g" + Hex("chat:" + lineChatId)[..20];

    private string Hex(string value) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value)));
}
