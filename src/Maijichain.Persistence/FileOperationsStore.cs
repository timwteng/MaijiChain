using System.Text.Json;
using Maijichain.Application;

namespace Maijichain.Persistence;

/// <summary>
/// Webhook inbox, reply outbox, push-quota log and chat routes in one checksummed JSON-lines file.
/// <para>
/// Unlike the ledger journal, this file is compacted: processed events keep only their ID (for de-duplication during
/// <see cref="DedupeRetention"/>) and lose their raw payload, so message text is not retained after processing (FR-LINE-09).
/// </para>
/// </summary>
public sealed class FileOperationsStore : IOperationsStore
{
    public static readonly TimeSpan DedupeRetention = TimeSpan.FromDays(7);
    private const int CompactAfterCompletions = 25;

    private static readonly JsonSerializerOptions Json = JournalJson.Create((typeof(OpsRecord),
    [
        (typeof(InboundAccepted), "in.accepted"),
        (typeof(InboundCompleted), "in.completed"),
        (typeof(OutboundQueued), "out.queued"),
        (typeof(OutboundAttempted), "out.attempted"),
        (typeof(OutboundCompleted), "out.completed"),
        (typeof(RouteLearned), "route.learned"),
    ]));

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IClock _clock;
    private readonly Dictionary<string, InboundState> _inbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboxItem> _outbound = new(StringComparer.Ordinal);
    private readonly List<OutboundCompleted> _pushes = [];
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);
    private long _goodLength;
    private int _completionsSinceCompaction;

    public FileOperationsStore(string path, IClock clock)
    {
        _path = path;
        _clock = clock;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var read = ChecksummedLog.Read(path);
        foreach (string line in read.Lines)
        {
            Replay(JsonSerializer.Deserialize<OpsRecord>(line, Json)!);
        }

        _goodLength = read.GoodLength;
        Compact();
    }

    public bool TryAcceptInbound(InboxItem item)
    {
        lock (_gate)
        {
            if (_inbound.ContainsKey(item.EventId))
            {
                return false;
            }

            Write(new InboundAccepted(item.EventId, item.EventTimestamp, item.ReceivedAt, item.Payload));
            return true;
        }
    }

    public IReadOnlyList<InboxItem> PendingInbound()
    {
        lock (_gate)
        {
            return _inbound.Values.Where(s => s.Pending is not null).Select(s => s.Pending!)
                .OrderBy(i => i.EventTimestamp).ThenBy(i => i.ReceivedAt).ThenBy(i => i.EventId, StringComparer.Ordinal).ToList();
        }
    }

    public void CompleteInbound(string eventId, string outcome)
    {
        lock (_gate)
        {
            if (_inbound.TryGetValue(eventId, out var state) && state.Pending is not null)
            {
                Write(new InboundCompleted(eventId, _clock.UtcNow, outcome));
                CompactIfDue();
            }
        }
    }

    public void EnqueueOutbound(OutboxItem item)
    {
        lock (_gate)
        {
            if (!_outbound.ContainsKey(item.MessageId))
            {
                Write(new OutboundQueued(item));
            }
        }
    }

    public IReadOnlyList<OutboxItem> PendingOutbound()
    {
        lock (_gate)
        {
            return _outbound.Values.OrderBy(o => o.CreatedAt).ThenBy(o => o.MessageId, StringComparer.Ordinal).ToList();
        }
    }

    public void RecordOutboundAttempt(string messageId)
    {
        lock (_gate)
        {
            if (_outbound.ContainsKey(messageId))
            {
                Write(new OutboundAttempted(messageId));
            }
        }
    }

    public void CompleteOutbound(string messageId, DeliveryChannel channel, int recipients)
    {
        lock (_gate)
        {
            if (_outbound.ContainsKey(messageId))
            {
                Write(new OutboundCompleted(messageId, _clock.UtcNow, channel, recipients));
                CompactIfDue();
            }
        }
    }

    public int PushRecipientsInMonth(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _pushes.Where(p => p.At.UtcDateTime.Year == now.UtcDateTime.Year && p.At.UtcDateTime.Month == now.UtcDateTime.Month).Sum(p => p.Recipients);
        }
    }

    public void RememberRoute(string groupId, string externalChatId)
    {
        lock (_gate)
        {
            if (!_routes.TryGetValue(groupId, out string? known) || known != externalChatId)
            {
                Write(new RouteLearned(groupId, externalChatId));
            }
        }
    }

    public string? FindRoute(string groupId)
    {
        lock (_gate)
        {
            return _routes.GetValueOrDefault(groupId);
        }
    }

    /// <summary>Rewrites the file with only what is still needed. Called at start-up and after every few completions.</summary>
    public void Compact()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            var keep = new List<OpsRecord>();
            foreach (var (id, state) in _inbound.ToList())
            {
                if (state.Pending is not null)
                {
                    keep.Add(new InboundAccepted(id, state.Pending.EventTimestamp, state.ReceivedAt, state.Pending.Payload));
                }
                else if (now - state.ReceivedAt <= DedupeRetention)
                {
                    keep.Add(new InboundAccepted(id, 0, state.ReceivedAt, null));
                    keep.Add(new InboundCompleted(id, state.ReceivedAt, "retained-id"));
                }
                else
                {
                    _inbound.Remove(id);
                }
            }

            keep.AddRange(_outbound.Values.Select(o => new OutboundQueued(o)));
            var monthStart = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
            _pushes.RemoveAll(p => p.At < monthStart);
            keep.AddRange(_pushes);
            keep.AddRange(_routes.Select(r => new RouteLearned(r.Key, r.Value)));

            ChecksummedLog.Rewrite(_path, keep.Select(r => JsonSerializer.Serialize(r, Json)));
            _goodLength = new FileInfo(_path).Length;
            _completionsSinceCompaction = 0;
        }
    }

    private void CompactIfDue()
    {
        if (++_completionsSinceCompaction >= CompactAfterCompletions)
        {
            Compact();
        }
    }

    private void Write(OpsRecord record)
    {
        _goodLength = ChecksummedLog.Append(_path, _goodLength, JsonSerializer.Serialize(record, Json));
        Replay(record);
    }

    private void Replay(OpsRecord record)
    {
        switch (record)
        {
            case InboundAccepted a:
                var pending = a.Payload is null ? null : new InboxItem(a.EventId, a.EventTimestamp, a.ReceivedAt, a.Payload);
                _inbound[a.EventId] = new InboundState(a.ReceivedAt, pending);
                break;
            case InboundCompleted c when _inbound.TryGetValue(c.EventId, out var state):
                _inbound[c.EventId] = state with { Pending = null };
                break;
            case OutboundQueued q:
                _outbound[q.Item.MessageId] = q.Item;
                break;
            case OutboundAttempted t when _outbound.TryGetValue(t.MessageId, out var item):
                _outbound[t.MessageId] = item with { Attempts = item.Attempts + 1 };
                break;
            case OutboundCompleted c:
                _outbound.Remove(c.MessageId);
                if (c.Channel == DeliveryChannel.Push)
                {
                    _pushes.Add(c);
                }

                break;
            case RouteLearned r:
                _routes[r.GroupId] = r.ExternalChatId;
                break;
        }
    }

    private sealed record InboundState(DateTimeOffset ReceivedAt, InboxItem? Pending);

    private abstract record OpsRecord;

    private sealed record InboundAccepted(string EventId, long EventTimestamp, DateTimeOffset ReceivedAt, string? Payload) : OpsRecord;

    private sealed record InboundCompleted(string EventId, DateTimeOffset At, string Outcome) : OpsRecord;

    private sealed record OutboundQueued(OutboxItem Item) : OpsRecord;

    private sealed record OutboundAttempted(string MessageId) : OpsRecord;

    private sealed record OutboundCompleted(string MessageId, DateTimeOffset At, DeliveryChannel Channel, int Recipients) : OpsRecord;

    private sealed record RouteLearned(string GroupId, string ExternalChatId) : OpsRecord;
}
