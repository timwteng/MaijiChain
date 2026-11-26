using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Maijichain.Application;
using Maijichain.Line;
using Maijichain.Persistence;

namespace Maijichain.Integration.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maijichain-tests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeLineClient : ILineMessagingClient
{
    public ConcurrentQueue<(string Kind, string Target, string Messages, Guid? RetryKey)> Calls { get; } = new();

    public Queue<LineSendStatus> ReplyResults { get; } = new();

    public Queue<LineSendStatus> PushResults { get; } = new();

    public IEnumerable<string> Texts => Calls.SelectMany(c => JsonDocument.Parse(c.Messages).RootElement.EnumerateArray())
        .Where(m => m.GetProperty("type").GetString() == "text").Select(m => m.GetProperty("text").GetString()!);

    public Task<LineSendStatus> ReplyAsync(string replyToken, string messagesJson, CancellationToken ct)
    {
        Calls.Enqueue(("reply", replyToken, messagesJson, null));
        return Task.FromResult(ReplyResults.Count > 0 ? ReplyResults.Dequeue() : LineSendStatus.Sent);
    }

    public Task<LineSendStatus> PushAsync(string to, string messagesJson, Guid retryKey, CancellationToken ct)
    {
        Calls.Enqueue(("push", to, messagesJson, retryKey));
        return Task.FromResult(PushResults.Count > 0 ? PushResults.Dequeue() : LineSendStatus.Sent);
    }
}

/// <summary>Builds webhook bodies shaped like the LINE Messaging API's.</summary>
internal static class Webhooks
{
    public const string Secret = "test-channel-secret-0123456789abcdef";
    public const string Pepper = "test-pepper-0123456789abcdef0123456789";
    public const string GroupId = "C4af4980629c0e3b8f2f0a4c1b2d3e4f5";

    private static int _counter;

    public static string NextId() => $"01JTEST{Interlocked.Increment(ref _counter):D19}";

    public static string Text(string userId, string text, string? id = null, string? group = GroupId, long timestamp = 1_763_600_000_000, bool redelivery = false) =>
        Event(id, timestamp, redelivery, userId, group, "message", $"\"message\":{{\"type\":\"text\",\"id\":\"m1\",\"text\":{JsonSerializer.Serialize(text)}}}");

    public static string Postback(string userId, string data, string? id = null, long timestamp = 1_763_600_000_000) =>
        Event(id, timestamp, false, userId, GroupId, "postback", $"\"postback\":{{\"data\":{JsonSerializer.Serialize(data)}}}");

    public static string Join(string? id = null) => Event(id, 1_763_600_000_000, false, null, GroupId, "join", null);

    public static string Body(params string[] events) => $"{{\"destination\":\"Ubot\",\"events\":[{string.Join(',', events)}]}}";

    public static byte[] Bytes(string body) => Encoding.UTF8.GetBytes(body);

    private static string Event(string? id, long timestamp, bool redelivery, string? userId, string? group, string type, string? extra)
    {
        var source = group is null
            ? $"{{\"type\":\"user\",\"userId\":\"{userId}\"}}"
            : userId is null ? $"{{\"type\":\"group\",\"groupId\":\"{group}\"}}" : $"{{\"type\":\"group\",\"groupId\":\"{group}\",\"userId\":\"{userId}\"}}";
        string extraPart = extra is null ? string.Empty : "," + extra;
        return $"{{\"type\":\"{type}\",\"mode\":\"active\",\"timestamp\":{timestamp},\"source\":{source},\"webhookEventId\":\"{id ?? NextId()}\"," +
               $"\"deliveryContext\":{{\"isRedelivery\":{(redelivery ? "true" : "false")}}},\"replyToken\":\"rt-{Guid.NewGuid():N}\"{extraPart}}}";
    }
}

/// <summary>The real pipeline (receiver → inbox → processor → ledger → outbox → dispatcher) on temp files.</summary>
internal sealed class Pipeline : IDisposable
{
    private readonly TempDir _dir = new();

    public Pipeline()
    {
        Options = new LineOptions { ChannelSecret = Webhooks.Secret, ChannelAccessToken = "token", IdentityPepper = Webhooks.Pepper };
        Ops = new FileOperationsStore(_dir.File("ops.jsonl"), Clock);
        Journal = new FileEventJournal(_dir.File("ledgers"));
        Ledger = new LedgerService(Journal, Clock);
        Hasher = new IdentityHasher(Options.IdentityPepper);
        Receiver = new LineWebhookReceiver(Options, Ops, Clock);
        Processor = new LineEventProcessor(Ledger, Ops, Hasher, Options);
        Dispatcher = new OutboxDispatcher(Ops, Line, Options, Clock);
    }

    public FakeClock Clock { get; } = new();

    public FakeLineClient Line { get; } = new();

    public LineOptions Options { get; }

    public FileOperationsStore Ops { get; }

    public FileEventJournal Journal { get; }

    public LedgerService Ledger { get; }

    public IdentityHasher Hasher { get; }

    public LineWebhookReceiver Receiver { get; }

    public LineEventProcessor Processor { get; }

    public OutboxDispatcher Dispatcher { get; }

    public string GroupKey => Hasher.GroupId(Webhooks.GroupId);

    public string DataPath => _dir.Path;

    public WebhookReceipt Post(params string[] events)
    {
        string body = Webhooks.Body(events);
        byte[] bytes = Webhooks.Bytes(body);
        return Receiver.Receive(bytes, LineSignature.Sign(bytes, Webhooks.Secret));
    }

    /// <summary>Posts events, processes the inbox, dispatches replies, and returns the reply texts produced.</summary>
    public async Task<List<string>> SayAsync(params string[] events)
    {
        int before = Line.Texts.Count();
        Post(events);
        await DrainAsync();
        return Line.Texts.Skip(before).ToList();
    }

    public async Task DrainAsync()
    {
        foreach (var item in Ops.PendingInbound())
        {
            Ops.CompleteInbound(item.EventId, await Processor.ProcessAsync(item, CancellationToken.None));
        }

        await Dispatcher.DispatchPendingAsync(CancellationToken.None);
    }

    public Task<long> BalanceAsync(string nickname) =>
        Ledger.QueryAsync(GroupKey, g => g.Balances[g.FindByNickname(nickname)!.Id], CancellationToken.None);

    public void Dispose() => _dir.Dispose();
}
