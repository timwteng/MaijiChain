using System.Text.Json;
using Maijichain.Application;
using Maijichain.Line;

namespace Maijichain.Integration.Tests;

/// <summary>End-to-end behaviour through the real receiver, processor, journal, operations store and dispatcher.</summary>
public sealed class PipelineTests : IDisposable
{
    private readonly Pipeline _p = new();

    public void Dispose() => _p.Dispose();

    private async Task JoinAllAsync()
    {
        await _p.SayAsync(Webhooks.Text("Uamy", "/join amy"), Webhooks.Text("Ubob", "/join bob", timestamp: 1_763_600_000_001), Webhooks.Text("Ucat", "/join cat", timestamp: 1_763_600_000_002));
    }

    private static string? DraftIdIn(IEnumerable<string> texts) =>
        texts.Select(t => System.Text.RegularExpressions.Regex.Match(t, @"\bD\d+\b")).FirstOrDefault(m => m.Success)?.Value;

    [Fact]
    public async Task Expense_flow_preview_confirm_and_double_tap()
    {
        await JoinAllAsync();
        var preview = await _p.SayAsync(Webhooks.Text("Uamy", "/add 300 dinner"));
        string draft = DraftIdIn(preview)!;
        Assert.Contains(preview, t => t.Contains("@amy 100 TWD", StringComparison.Ordinal));
        Assert.Equal(0, await _p.BalanceAsync("amy")); // nothing posted yet (UR-03)

        var lastCall = _p.Line.Calls.Last();
        using (var doc = JsonDocument.Parse(lastCall.Messages))
        {
            var template = doc.RootElement[1].GetProperty("template");
            Assert.Equal("confirm", template.GetProperty("type").GetString());
            Assert.Equal($"a=confirm&id={draft}", template.GetProperty("actions")[0].GetProperty("data").GetString());
        }

        var posted = await _p.SayAsync(Webhooks.Postback("Uamy", $"a=confirm&id={draft}"));
        Assert.Contains(posted, t => t.Contains("E1 posted", StringComparison.Ordinal));
        Assert.Equal(200, await _p.BalanceAsync("amy"));

        var doubleTap = await _p.SayAsync(Webhooks.Postback("Uamy", $"a=confirm&id={draft}"));
        Assert.Contains(doubleTap, t => t.Contains("already posted", StringComparison.Ordinal));
        Assert.Equal(200, await _p.BalanceAsync("amy")); // TC-006
    }

    [Fact]
    public async Task TC012_reprocessing_or_redelivery_never_doubles_an_effect()
    {
        await JoinAllAsync();
        string id = Webhooks.NextId();
        string got = Webhooks.Text("Uamy", "/got 50 @bob", id);
        await _p.SayAsync(got);

        // Simulate a crash after the ledger commit but before the inbox was marked processed.
        var replay = new InboxItem(id, 1_763_600_000_000, _p.Clock.UtcNow, got);
        string outcome = await _p.Processor.ProcessAsync(replay, TestContext.Current.CancellationToken);
        Assert.Equal("duplicate", outcome);
        await _p.SayAsync(Webhooks.Text("Uamy", "/got 50 @bob", id, redelivery: true)); // LINE redelivery: deduplicated at ingress

        Assert.Equal(50, await _p.BalanceAsync("bob"));
        var events = await _p.Journal.ReadAllAsync(_p.GroupKey, TestContext.Current.CancellationToken);
        Assert.Single(events, e => e is Maijichain.Domain.PaymentPosted);
    }

    [Fact]
    public async Task Ordinary_chatter_and_unsupported_events_produce_no_reply()
    {
        await JoinAllAsync();
        int before = _p.Line.Calls.Count;
        await _p.SayAsync(Webhooks.Text("Ubob", "see you at 7?"), Webhooks.Text("Ucat", "👍"));
        Assert.Equal(before, _p.Line.Calls.Count);
    }

    [Fact]
    public async Task Unregistered_users_are_asked_to_join_and_one_to_one_chats_are_redirected()
    {
        var reply = await _p.SayAsync(Webhooks.Text("Ustranger", "/add 100 lunch"));
        Assert.Contains(reply, t => t.Contains("/join", StringComparison.Ordinal));

        var direct = await _p.SayAsync(Webhooks.Text("Uamy", "/help", group: null));
        Assert.Contains(direct, t => t.Contains("group chat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settlement_plan_payment_and_creditor_confirmation()
    {
        await JoinAllAsync();
        string draft = DraftIdIn(await _p.SayAsync(Webhooks.Text("Uamy", "/add 900 hotel @amy @bob @cat")))!;
        await _p.SayAsync(Webhooks.Postback("Uamy", $"a=confirm&id={draft}"));

        var plan = await _p.SayAsync(Webhooks.Text("Ubob", "/plan"));
        Assert.Contains(plan, t => t.Contains("minimum possible", StringComparison.Ordinal));

        var claim = await _p.SayAsync(Webhooks.Text("Ubob", "/paid S1-1"));
        Assert.Contains(claim, t => t.Contains("please confirm", StringComparison.Ordinal));
        Assert.Equal(-300, await _p.BalanceAsync("bob"));

        var wrongPerson = await _p.SayAsync(Webhooks.Postback("Ucat", "a=confirm&id=P1"));
        Assert.Contains(wrongPerson, t => t.StartsWith("⚠", StringComparison.Ordinal));

        await _p.SayAsync(Webhooks.Postback("Uamy", "a=confirm&id=P1"));
        Assert.Equal(0, await _p.BalanceAsync("bob"));
        Assert.Equal(300, await _p.BalanceAsync("amy"));
    }

    [Fact]
    public async Task TC011_paying_from_an_outdated_plan_is_refused()
    {
        await JoinAllAsync();
        await _p.SayAsync(Webhooks.Text("Uamy", "/got 100 @bob"));
        await _p.SayAsync(Webhooks.Text("Ubob", "/plan"));
        await _p.SayAsync(Webhooks.Text("Ucat", "/got 40 @bob"));
        var reply = await _p.SayAsync(Webhooks.Text("Uamy", "/paid S1-1"));
        Assert.Contains(reply, t => t.Contains("out of date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_reverses_and_history_shows_it()
    {
        await JoinAllAsync();
        string draft = DraftIdIn(await _p.SayAsync(Webhooks.Text("Ubob", "/add 120 snacks @amy @bob")))!;
        await _p.SayAsync(Webhooks.Postback("Ubob", $"a=confirm&id={draft}"));
        await _p.SayAsync(Webhooks.Text("Ubob", "/undo E1 entered twice"));
        var history = await _p.SayAsync(Webhooks.Text("Ucat", "/history"));
        Assert.Contains(history, t => t.Contains("E1 reversed: entered twice", StringComparison.Ordinal));
        Assert.Equal(0, await _p.BalanceAsync("amy"));
    }

    [Fact]
    public async Task Ledger_never_stores_raw_line_ids_and_ops_store_forgets_message_text()
    {
        await JoinAllAsync();
        await _p.SayAsync(Webhooks.Text("Ubob", "my secret plans for friday"));
        _p.Ops.Compact();

        string ledgerText = string.Concat(Directory.GetFiles(_p.DataPath, "*.journal.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("Uamy", ledgerText, StringComparison.Ordinal);
        Assert.DoesNotContain(Webhooks.GroupId, ledgerText, StringComparison.Ordinal);
        string opsText = File.ReadAllText(Path.Combine(_p.DataPath, "ops.jsonl"));
        Assert.DoesNotContain("secret plans", opsText, StringComparison.Ordinal);
        Assert.DoesNotContain("Uamy", opsText, StringComparison.Ordinal);
    }
}
