using Maijichain.Domain;

namespace Maijichain.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Append-only, per-group event journal: the authoritative financial record.</summary>
public interface IEventJournal
{
    Task<IReadOnlyList<LedgerEvent>> ReadAllAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>Durably appends one event. Must not return before the event is on stable storage.</summary>
    Task AppendAsync(string groupId, LedgerEvent e, CancellationToken cancellationToken);
}

/// <summary>An accepted inbound webhook event awaiting processing.</summary>
public sealed record InboxItem(string EventId, long EventTimestamp, DateTimeOffset ReceivedAt, string Payload);

/// <summary>
/// A reply waiting to be delivered. Payload is the provider-specific message array. <see cref="AudienceSize"/> is how
/// many recipients a push of this message would be charged for (LINE counts group pushes per member).
/// </summary>
public sealed record OutboxItem(
    string MessageId,
    string GroupId,
    string? ReplyToken,
    DateTimeOffset CreatedAt,
    string Payload,
    bool Important,
    int AudienceSize,
    int Attempts = 0);

public enum DeliveryChannel
{
    Reply,
    Push,
    Dropped,
}

/// <summary>Operational store for webhook de-duplication, the reply outbox and push-quota accounting.</summary>
public interface IOperationsStore
{
    /// <summary>Durably records an inbound event. Returns false if this event ID was already accepted (INV-04).</summary>
    bool TryAcceptInbound(InboxItem item);

    IReadOnlyList<InboxItem> PendingInbound();

    /// <summary>Marks an event processed and discards its raw payload (FR-LINE-09).</summary>
    void CompleteInbound(string eventId, string outcome);

    void EnqueueOutbound(OutboxItem item);

    IReadOnlyList<OutboxItem> PendingOutbound();

    void RecordOutboundAttempt(string messageId);

    void CompleteOutbound(string messageId, DeliveryChannel channel, int recipients);

    /// <summary>Recipients charged by push messages in the calendar month (UTC) containing <paramref name="now"/>.</summary>
    int PushRecipientsInMonth(DateTimeOffset now);

    void RememberRoute(string groupId, string externalChatId);

    string? FindRoute(string groupId);
}
