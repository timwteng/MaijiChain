using System.Net;
using System.Text;
using Maijichain.Application;
using Maijichain.Line;
using Maijichain.Persistence;

namespace Maijichain.Integration.Tests;

public sealed class OutboxDispatcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLineClient _line = new();
    private readonly FileOperationsStore _ops;
    private readonly LineOptions _options = new() { MonthlyPushBudget = 30 };
    private readonly OutboxDispatcher _dispatcher;

    public OutboxDispatcherTests()
    {
        _ops = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        _ops.RememberRoute("g1", "Cgroup");
        _dispatcher = new OutboxDispatcher(_ops, _line, _options, _clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private void Queue(string id, bool important, string? replyToken = "rt", int audience = 11) =>
        _ops.EnqueueOutbound(new OutboxItem(id, "g1", replyToken, _clock.UtcNow, LineMessages.Text(id), important, audience));

    [Fact]
    public async Task Fresh_reply_tokens_use_the_free_reply_api()
    {
        Queue("m1", important: true);
        await _dispatcher.DispatchPendingAsync(Ct);
        Assert.Equal("reply", Assert.Single(_line.Calls).Kind);
        Assert.Empty(_ops.PendingOutbound());
        Assert.Equal(0, _ops.PushRecipientsInMonth(_clock.UtcNow));
    }

    [Fact]
    public async Task Expired_token_pushes_only_important_messages()
    {
        Queue("important", important: true);
        Queue("chatty", important: false);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _dispatcher.DispatchPendingAsync(Ct);

        var call = Assert.Single(_line.Calls);
        Assert.Equal(("push", "Cgroup"), (call.Kind, call.Target));
        Assert.Equal(OutboxDispatcher.RetryKeyFor("important"), call.RetryKey);
        Assert.Equal(11, _ops.PushRecipientsInMonth(_clock.UtcNow));
        Assert.Empty(_ops.PendingOutbound());
    }

    [Fact]
    public async Task Invalid_reply_token_falls_back_to_push()
    {
        Queue("m1", important: true);
        _line.ReplyResults.Enqueue(LineSendStatus.InvalidReplyToken);
        await _dispatcher.DispatchPendingAsync(Ct);
        Assert.Equal(new[] { "reply", "push" }, _line.Calls.Select(c => c.Kind).ToArray());
    }

    [Fact]
    public async Task Push_budget_protects_the_monthly_free_quota()
    {
        Queue("a", important: true, replyToken: null, audience: 20);
        Queue("b", important: true, replyToken: null, audience: 20);
        await _dispatcher.DispatchPendingAsync(Ct);
        Assert.Single(_line.Calls); // 20 + 20 would exceed the budget of 30
        Assert.Equal(20, _ops.PushRecipientsInMonth(_clock.UtcNow));
    }

    [Fact]
    public async Task Transient_failures_retry_with_the_same_push_key()
    {
        Queue("m1", important: true, replyToken: null);
        _line.PushResults.Enqueue(LineSendStatus.Transient);
        await _dispatcher.DispatchPendingAsync(Ct);
        Assert.Single(_ops.PendingOutbound());
        await _dispatcher.DispatchPendingAsync(Ct);
        Assert.Empty(_ops.PendingOutbound());
        Assert.Equal(2, _line.Calls.Count(c => c.RetryKey == OutboxDispatcher.RetryKeyFor("m1")));
    }

    [Fact]
    public void Retry_keys_are_stable_version_4_uuids()
    {
        var key = OutboxDispatcher.RetryKeyFor("evt:reply");
        Assert.Equal(key, OutboxDispatcher.RetryKeyFor("evt:reply"));
        Assert.Equal('4', key.ToString()[14]);
        Assert.NotEqual(key, OutboxDispatcher.RetryKeyFor("other"));
    }
}

public sealed class LineMessagingClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (LineMessagingClient Client, StubHandler Handler) Create(HttpStatusCode status, string body = "{}")
    {
        var handler = new StubHandler(status, body);
        return (new LineMessagingClient(new HttpClient(handler), new LineOptions { ChannelAccessToken = "tok" }), handler);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reply_sends_token_messages_and_bearer_auth()
    {
        var (client, handler) = Create(HttpStatusCode.OK);
        Assert.Equal(LineSendStatus.Sent, await client.ReplyAsync("rt-1", LineMessages.Text("hi"), Ct));
        Assert.Equal("https://api.line.me/v2/bot/message/reply", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("{\"replyToken\":\"rt-1\",\"messages\":[{\"type\":\"text\",\"text\":\"hi\"}]}", handler.Body);
    }

    [Fact]
    public async Task Push_carries_retry_key_and_conflict_means_already_sent()
    {
        var (client, handler) = Create(HttpStatusCode.Conflict);
        var key = Guid.NewGuid();
        Assert.Equal(LineSendStatus.Sent, await client.PushAsync("Cgroup", LineMessages.Text("hi"), key, Ct));
        Assert.Equal(key.ToString(), handler.Request!.Headers.GetValues("X-Line-Retry-Key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":\"Invalid reply token\"}", LineSendStatus.InvalidReplyToken)]
    [InlineData(HttpStatusCode.BadRequest, "{\"message\":\"The request body has 1 error(s)\"}", LineSendStatus.Rejected)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", LineSendStatus.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", LineSendStatus.Transient)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", LineSendStatus.Rejected)]
    public async Task Maps_api_responses(HttpStatusCode status, string body, LineSendStatus expected)
    {
        var (client, _) = Create(status, body);
        Assert.Equal(expected, await client.ReplyAsync("rt", LineMessages.Text("x"), Ct));
    }

    [Fact]
    public void Message_builders_respect_line_limits()
    {
        string json = LineMessages.TextWithChoice(new string('x', 6000), new string('q', 500), new("A very long label that is too long", "a=1", "d"), new("No", "a=2", "d"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(5000, doc.RootElement[0].GetProperty("text").GetString()!.Length);
        Assert.Equal(240, doc.RootElement[1].GetProperty("template").GetProperty("text").GetString()!.Length);
        Assert.Equal(20, doc.RootElement[1].GetProperty("template").GetProperty("actions")[0].GetProperty("label").GetString()!.Length);
    }
}
