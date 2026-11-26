using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Maijichain.Line;
using Maijichain.WebHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Maijichain.Integration.Tests;

/// <summary>Runs the real ASP.NET Core host on a loopback port, with only the outbound LINE client faked.</summary>
public sealed class HostTests : IAsyncDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeLineClient _line = new();
    private readonly CapturingLogs _logs = new();
    private WebApplication? _app;
    private HttpClient? _http;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _dir.Dispose();
    }

    private async Task<HttpClient> StartAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataDirectory"] = _dir.Path,
            ["Line:ChannelSecret"] = Webhooks.Secret,
            ["Line:ChannelAccessToken"] = "test-token",
            ["Line:IdentityPepper"] = Webhooks.Pepper,
            ["urls"] = "http://127.0.0.1:0",
            ["Logging:LogLevel:Microsoft"] = "Warning",
        };
        _app = MaijichainHost.Build([], settings, services =>
        {
            services.AddSingleton<ILineMessagingClient>(_line);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        await _app.StartAsync(Ct);
        string address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
        return _http;
    }

    private static HttpRequestMessage Signed(string body, string secret = Webhooks.Secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/line") { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("x-line-signature", LineSignature.Sign(bytes, secret));
        request.Content.Headers.ContentType = new("application/json");
        return request;
    }

    private async Task WaitForRepliesAsync(int count)
    {
        for (int i = 0; i < 100 && _line.Calls.Count < count; i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(_line.Calls.Count >= count, $"expected {count} replies, got {_line.Calls.Count}");
    }

    [Fact]
    public async Task Signed_webhook_is_acknowledged_and_processed_asynchronously()
    {
        var http = await StartAsync();
        string body = Webhooks.Body(Webhooks.Text("Uamy", "/join amy"), Webhooks.Text("Ubob", "/join bob", timestamp: 1_763_600_000_001));

        var response = await http.SendAsync(Signed(body), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WaitForRepliesAsync(2);
        Assert.Contains(_line.Texts, t => t.Contains("Welcome @bob", StringComparison.Ordinal));

        var redelivered = await http.SendAsync(Signed(body), Ct); // same webhookEventIds
        Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        await Task.Delay(300, Ct);
        Assert.Equal(2, _line.Calls.Count);
    }

    [Fact]
    public async Task Bad_signature_is_rejected_and_health_endpoint_works()
    {
        var http = await StartAsync();
        var response = await http.SendAsync(Signed(Webhooks.Body(Webhooks.Text("Uamy", "/join amy")), secret: "forged"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz", Ct)).StatusCode);
        Assert.Empty(_line.Calls);
    }

    [Fact]
    public async Task Logs_never_contain_message_text_or_line_ids()
    {
        var http = await StartAsync();
        await http.SendAsync(Signed(Webhooks.Body(Webhooks.Text("Uamy", "/join amy"), Webhooks.Text("Uamy", "/add 450 secret-santa gift"))), Ct);
        await WaitForRepliesAsync(2);
        string all = string.Join('\n', _logs.Lines);
        Assert.DoesNotContain("secret-santa", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Uamy", all, StringComparison.Ordinal);
        Assert.DoesNotContain(Webhooks.GroupId, all, StringComparison.Ordinal);
        Assert.Contains(_logs.Lines, l => l.Contains("processed: drafted", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_secrets_fail_fast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MaijichainHost.Build([], new Dictionary<string, string?> { ["Line:ChannelSecret"] = string.Empty }));
        Assert.Contains("ChannelSecret", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue($"{category}: {formatter(state, exception)}");
        }
    }
}

/// <summary>Dependency rules that keep the ledger independent of LINE and of Bitcoin (spec principle 5, NFR-11).</summary>
public sealed class ArchitectureTests
{
    private static IEnumerable<string> References(Type marker) => marker.Assembly.GetReferencedAssemblies().Select(a => a.Name!);

    [Fact]
    public void Ledger_side_assemblies_never_reference_bitcoin()
    {
        foreach (var marker in new[] { typeof(Maijichain.Domain.GroupLedger), typeof(Maijichain.Application.LedgerService), typeof(Maijichain.Persistence.FileEventJournal), typeof(LineWebhookReceiver), typeof(MaijichainHost) })
        {
            Assert.DoesNotContain(References(marker), n => n.StartsWith("NBitcoin", StringComparison.Ordinal) || n.Contains("BitcoinPoc", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Domain_depends_only_on_the_base_class_library()
    {
        Assert.All(References(typeof(Maijichain.Domain.GroupLedger)), n => Assert.True(n.StartsWith("System", StringComparison.Ordinal) || n == "netstandard", n));
    }

    [Fact]
    public void Application_does_not_know_about_line_or_files()
    {
        Assert.DoesNotContain(References(typeof(Maijichain.Application.LedgerService)), n => n.Contains("Line", StringComparison.Ordinal) || n.Contains("Persistence", StringComparison.Ordinal));
    }
}
