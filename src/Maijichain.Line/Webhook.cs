using System.Text.Json;
using Maijichain.Application;

namespace Maijichain.Line;

/// <summary>The fields Maijichain reads from one LINE webhook event object.</summary>
public sealed record LineEvent(
    string Type,
    string WebhookEventId,
    long Timestamp,
    string? ReplyToken,
    string? SourceType,
    string? UserId,
    string? GroupId,
    string? RoomId,
    string? MessageType,
    string? Text,
    string? PostbackData,
    bool IsRedelivery)
{
    public string? ChatId => GroupId ?? RoomId;

    public static LineEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var source = Child(e, "source");
        var message = Child(e, "message");
        var postback = Child(e, "postback");
        var delivery = Child(e, "deliveryContext");
        return new LineEvent(
            Type: Str(e, "type") ?? "unknown",
            WebhookEventId: Str(e, "webhookEventId") ?? throw new JsonException("webhookEventId is missing."),
            Timestamp: e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out long t) ? t : 0,
            ReplyToken: Str(e, "replyToken"),
            SourceType: Str(source, "type"),
            UserId: Str(source, "userId"),
            GroupId: Str(source, "groupId"),
            RoomId: Str(source, "roomId"),
            MessageType: Str(message, "type"),
            Text: Str(message, "text"),
            PostbackData: Str(postback, "data"),
            IsRedelivery: delivery is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("isRedelivery", out var r) && r.ValueKind == JsonValueKind.True);
    }

    private static JsonElement? Child(JsonElement e, string name) =>
        e.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : null;

    private static string? Str(JsonElement? e, string name) =>
        e is { } el && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public enum WebhookStatus
{
    Accepted,
    Unauthorized,
    BadRequest,
}

public sealed record WebhookReceipt(WebhookStatus Status, int Accepted, int Duplicates);

/// <summary>
/// Webhook ingress (FR-LINE-02..07): verify the signature over the raw body before parsing, then durably record each
/// event in the inbox under its unique <c>webhookEventId</c>. Domain work happens later, asynchronously.
/// </summary>
public sealed class LineWebhookReceiver(LineOptions options, IOperationsStore ops, IClock clock)
{
    public WebhookReceipt Receive(ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (!LineSignature.IsValid(rawBody, signatureHeader, options.ChannelSecret))
        {
            return new WebhookReceipt(WebhookStatus.Unauthorized, 0, 0);
        }

        List<(string Id, long Timestamp, string Raw)> events;
        try
        {
            using var doc = JsonDocument.Parse(rawBody.ToArray());
            var root = doc.RootElement;
            if (options.BotUserId is { Length: > 0 } bot &&
                (!root.TryGetProperty("destination", out var dest) || dest.GetString() != bot))
            {
                return new WebhookReceipt(WebhookStatus.Unauthorized, 0, 0);
            }

            if (!root.TryGetProperty("events", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return new WebhookReceipt(WebhookStatus.BadRequest, 0, 0);
            }

            events = [];
            foreach (var e in array.EnumerateArray())
            {
                if (!e.TryGetProperty("webhookEventId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
                {
                    return new WebhookReceipt(WebhookStatus.BadRequest, 0, 0);
                }

                long timestamp = e.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out long t) ? t : 0;
                events.Add((id.GetString()!, timestamp, e.GetRawText()));
            }
        }
        catch (JsonException)
        {
            return new WebhookReceipt(WebhookStatus.BadRequest, 0, 0);
        }

        int accepted = 0;
        int duplicates = 0;
        foreach (var (id, timestamp, raw) in events)
        {
            if (ops.TryAcceptInbound(new InboxItem(id, timestamp, clock.UtcNow, raw)))
            {
                accepted++;
            }
            else
            {
                duplicates++; // redelivery or retry: idempotent success, no second effect (FR-LINE-05)
            }
        }

        return new WebhookReceipt(WebhookStatus.Accepted, accepted, duplicates);
    }
}
