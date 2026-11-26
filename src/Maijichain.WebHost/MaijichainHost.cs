using System.Threading.Channels;
using Maijichain.Application;
using Maijichain.Line;
using Maijichain.Persistence;

namespace Maijichain.WebHost;

public static class MaijichainHost
{
    public const int MaxWebhookBytes = 1 << 20;

    /// <summary>Builds the app. Tests pass extra configuration and service overrides.</summary>
    public static WebApplication Build(string[] args, IDictionary<string, string?>? settings = null, Action<IServiceCollection>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        var lineOptions = new LineOptions();
        builder.Configuration.GetSection("Line").Bind(lineOptions);
        var problems = lineOptions.Validate().ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", problems) + " Use user-secrets or environment variables (Line__ChannelSecret, …).");
        }

        string dataDirectory = Path.GetFullPath(builder.Configuration["DataDirectory"] ?? "data", builder.Environment.ContentRootPath);
        var services = builder.Services;
        services.AddSingleton(lineOptions);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IEventJournal>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Maijichain.Journal");
            return new FileEventJournal(Path.Combine(dataDirectory, "ledgers"), message => log.LogWarning("{JournalMessage}", message));
        });
        services.AddSingleton<IOperationsStore>(sp => new FileOperationsStore(Path.Combine(dataDirectory, "ops.jsonl"), sp.GetRequiredService<IClock>()));
        services.AddSingleton<LedgerService>();
        services.AddSingleton(new IdentityHasher(lineOptions.IdentityPepper));
        services.AddSingleton<LineWebhookReceiver>();
        services.AddSingleton<LineEventProcessor>();
        services.AddSingleton<ILineMessagingClient>(sp => new LineMessagingClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(10) },
            lineOptions));
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<ProcessingSignal>();
        services.AddHostedService<InboxWorker>();
        services.AddHostedService<OutboxWorker>();
        overrides?.Invoke(services);

        var app = builder.Build();
        app.MapPost("/webhooks/line", ReceiveWebhookAsync);
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    private static async Task<IResult> ReceiveWebhookAsync(HttpRequest request, LineWebhookReceiver receiver, ProcessingSignal signal, ILoggerFactory loggers, CancellationToken ct)
    {
        if (request.ContentLength > MaxWebhookBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (buffer.Length > MaxWebhookBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // The signature is computed over the exact bytes received, before any parsing (FR-LINE-02).
        var receipt = receiver.Receive(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), request.Headers["x-line-signature"]);
        loggers.CreateLogger("Maijichain.Webhook").LogInformation(
            "Webhook {Status}: {Accepted} accepted, {Duplicates} duplicate", receipt.Status, receipt.Accepted, receipt.Duplicates);
        if (receipt.Accepted > 0)
        {
            signal.Inbound.Writer.TryWrite(true);
        }

        return receipt.Status switch
        {
            WebhookStatus.Accepted => Results.Ok(),
            WebhookStatus.Unauthorized => Results.Unauthorized(),
            _ => Results.BadRequest(),
        };
    }
}

/// <summary>Wakes the workers as soon as there is work, with polling as a fallback.</summary>
public sealed class ProcessingSignal
{
    public Channel<bool> Inbound { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public Channel<bool> Outbound { get; } = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public static async Task WaitAsync(Channel<bool> channel, TimeSpan fallback, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(fallback);
        try
        {
            await channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // fallback poll
        }
    }
}

/// <summary>Processes accepted webhook events in event-timestamp order (FR-LINE-06/07).</summary>
public sealed class InboxWorker(IOperationsStore ops, LineEventProcessor processor, ProcessingSignal signal, ILogger<InboxWorker> log) : BackgroundService
{
    private const int MaxFailures = 5;
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var item in ops.PendingInbound())
            {
                try
                {
                    string outcome = await processor.ProcessAsync(item, stoppingToken).ConfigureAwait(false);
                    ops.CompleteInbound(item.EventId, outcome);
                    log.LogInformation("Event {EventId} processed: {Outcome}", item.EventId, outcome);
                    _failures.Remove(item.EventId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    int failures = _failures[item.EventId] = _failures.GetValueOrDefault(item.EventId) + 1;
                    log.LogError("Event {EventId} failed ({Failures}/{Max}): {ErrorType}", item.EventId, failures, MaxFailures, ex.GetType().Name);
                    if (failures >= MaxFailures)
                    {
                        ops.CompleteInbound(item.EventId, "failed");
                        _failures.Remove(item.EventId);
                    }
                }
            }

            signal.Outbound.Writer.TryWrite(true);
            await ProcessingSignal.WaitAsync(signal.Inbound, TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}

public sealed class OutboxWorker(OutboxDispatcher dispatcher, ProcessingSignal signal, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.DispatchPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError("Outbox dispatch failed: {ErrorType}", ex.GetType().Name);
            }

            await ProcessingSignal.WaitAsync(signal.Outbound, TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
