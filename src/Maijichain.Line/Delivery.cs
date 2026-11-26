using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maijichain.Application;

namespace Maijichain.Line;

public enum LineSendStatus
{
    Sent,
    InvalidReplyToken,
    Rejected,
    Transient,
}

public interface ILineMessagingClient
{
    Task<LineSendStatus> ReplyAsync(string replyToken, string messagesJson, CancellationToken ct);

    /// <summary>Push with an idempotency key so a retried push is never delivered twice.</summary>
    Task<LineSendStatus> PushAsync(string to, string messagesJson, Guid retryKey, CancellationToken ct);
}

/// <summary>Minimal HTTP client for the reply and push endpoints of the LINE Messaging API.</summary>
public sealed class LineMessagingClient(HttpClient http, LineOptions options) : ILineMessagingClient
{
    private static readonly Uri ReplyUri = new("https://api.line.me/v2/bot/message/reply");
    private static readonly Uri PushUri = new("https://api.line.me/v2/bot/message/push");

    public Task<LineSendStatus> ReplyAsync(string replyToken, string messagesJson, CancellationToken ct) =>
        SendAsync(ReplyUri, $"{{\"replyToken\":{JsonSerializer.Serialize(replyToken)},\"messages\":{messagesJson}}}", null, ct);

    public Task<LineSendStatus> PushAsync(string to, string messagesJson, Guid retryKey, CancellationToken ct) =>
        SendAsync(PushUri, $"{{\"to\":{JsonSerializer.Serialize(to)},\"messages\":{messagesJson}}}", retryKey, ct);

    private async Task<LineSendStatus> SendAsync(Uri uri, string body, Guid? retryKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ChannelAccessToken);
        if (retryKey is { } key)
        {
            request.Headers.Add("X-Line-Retry-Key", key.ToString());
        }

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return LineSendStatus.Sent;
            }

            if (response.StatusCode == HttpStatusCode.Conflict && retryKey is not null)
            {
                return LineSendStatus.Sent; // same retry key already accepted
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                return LineSendStatus.Transient;
            }

            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Contains("reply token", StringComparison.OrdinalIgnoreCase) ? LineSendStatus.InvalidReplyToken : LineSendStatus.Rejected;
        }
        catch (HttpRequestException)
        {
            return LineSendStatus.Transient;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return LineSendStatus.Transient; // HTTP timeout
        }
    }
}

/// <summary>
/// Delivers queued replies. Reply API first (free); if the reply token has expired or was rejected, only messages
/// marked important are pushed, and only while the monthly push budget allows, because group pushes are charged
/// per member. A failed delivery never touches the ledger.
/// </summary>
public sealed class OutboxDispatcher(IOperationsStore ops, ILineMessagingClient client, LineOptions options, IClock clock)
{
    public const int MaxAttempts = 6;

    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        int handled = 0;
        foreach (var item in ops.PendingOutbound())
        {
            ct.ThrowIfCancellationRequested();
            await DispatchAsync(item, ct).ConfigureAwait(false);
            handled++;
        }

        return handled;
    }

    /// <summary>Deterministic UUID per message so every retry of the same push reuses the same X-Line-Retry-Key.</summary>
    public static Guid RetryKeyFor(string messageId)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40); // version 4 layout
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }

    private async Task DispatchAsync(OutboxItem item, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (item.ReplyToken is not null && now - item.CreatedAt <= options.ReplyWindow)
        {
            var status = await client.ReplyAsync(item.ReplyToken, item.Payload, ct).ConfigureAwait(false);
            switch (status)
            {
                case LineSendStatus.Sent:
                    ops.CompleteOutbound(item.MessageId, DeliveryChannel.Reply, 0);
                    return;
                case LineSendStatus.Transient when item.Attempts + 1 < MaxAttempts:
                    ops.RecordOutboundAttempt(item.MessageId);
                    return;
                case LineSendStatus.Rejected:
                    ops.CompleteOutbound(item.MessageId, DeliveryChannel.Dropped, 0); // malformed: pushing would fail too
                    return;
            }
        }

        string? to = ops.FindRoute(item.GroupId);
        bool affordable = ops.PushRecipientsInMonth(now) + item.AudienceSize <= options.MonthlyPushBudget;
        if (!item.Important || to is null || !affordable)
        {
            ops.CompleteOutbound(item.MessageId, DeliveryChannel.Dropped, 0);
            return;
        }

        var pushed = await client.PushAsync(to, item.Payload, RetryKeyFor(item.MessageId), ct).ConfigureAwait(false);
        if (pushed == LineSendStatus.Sent)
        {
            ops.CompleteOutbound(item.MessageId, DeliveryChannel.Push, item.AudienceSize);
        }
        else if (pushed == LineSendStatus.Transient && item.Attempts + 1 < MaxAttempts)
        {
            ops.RecordOutboundAttempt(item.MessageId);
        }
        else
        {
            ops.CompleteOutbound(item.MessageId, DeliveryChannel.Dropped, 0);
        }
    }
}
