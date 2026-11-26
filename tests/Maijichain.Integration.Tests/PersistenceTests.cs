using System.Text;
using Maijichain.Application;
using Maijichain.Domain;
using Maijichain.Persistence;

namespace Maijichain.Integration.Tests;

public sealed class JournalTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private async Task<LedgerService> SeededServiceAsync(FileEventJournal journal)
    {
        var service = new LedgerService(journal, _clock);
        await service.OpenGroupAsync("g1", "Friends", Currency.Twd, RemainderPolicy.RotateByExpenseKey, "open", Ct);
        foreach (string nick in new[] { "amy", "bob", "cat" })
        {
            await service.ExecuteAsync("g1", (g, ctx) => g.Join("id-" + nick, nick, ctx), (_, d) => d, "join-" + nick, Ct);
        }

        return service;
    }

    [Fact]
    public async Task Every_event_type_round_trips_through_the_journal()
    {
        var journal = new FileEventJournal(_dir.Path);
        var service = await SeededServiceAsync(journal);
        MemberId Id(GroupLedger g, string n) => g.FindByNickname(n)!.Id;
        var drafted = await service.ExecuteAsync("g1", (g, c) => g.ProposeExpense(Id(g, "amy"), Id(g, "amy"), 999, "dinner 🍜", new DateOnly(2026, 11, 19), new WeightedSplit([(Id(g, "amy"), 2), (Id(g, "bob"), 1)]), c), (_, d) => (ExpenseDrafted)d.Event, "k1", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ConfirmDraft(Id(g, "amy"), drafted.Value.DraftId, c), (_, d) => d, "k2", Ct);
        var d2 = await service.ExecuteAsync("g1", (g, c) => g.ProposeExpense(Id(g, "bob"), Id(g, "bob"), 10, "snack", default, new EqualSplit([Id(g, "bob")]), c), (_, d) => (ExpenseDrafted)d.Event, "k3", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.DiscardDraft(Id(g, "bob"), d2.Value.DraftId, "oops", c), (_, d) => d, "k4", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ReverseExpense(Id(g, "amy"), "E1", "test", c), (_, d) => d, "k5", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ProposeExpense(Id(g, "amy"), Id(g, "amy"), 300, "taxi", default, new ExactSplit([new Share(Id(g, "bob"), 100), new Share(Id(g, "cat"), 200)]), c), (_, d) => d, "k6", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ConfirmDraft(Id(g, "amy"), "D3", c), (_, d) => d, "k7", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.CreatePlan(Id(g, "cat"), c), (_, d) => d, "k8", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ClaimPlanTransfer(Id(g, "cat"), "S1", 0, c), (_, d) => d, "k9", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.RejectPayment(Id(g, "amy"), "P1", "not yet", c), (_, d) => d, "k10", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ClaimPayment(Id(g, "bob"), Id(g, "amy"), 100, c), (_, d) => d, "k11", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ConfirmPayment(Id(g, "amy"), "P2", c), (_, d) => d, "k12", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ChangeRole(Id(g, "amy"), Id(g, "bob"), MemberRole.Coordinator, c), (_, d) => d, "k13", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.ClosePeriod(Id(g, "amy"), "Nov", c), (_, d) => d, "k14", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.RecordReceipt(Id(g, "amy"), Id(g, "cat"), 200, c), (_, d) => d, "k15", Ct);
        await service.ExecuteAsync("g1", (g, c) => g.Deactivate(Id(g, "cat"), Id(g, "cat"), c), (_, d) => d, "k16", Ct);

        var original = await service.QueryAsync("g1", g => g, Ct);
        var reloaded = GroupLedger.Replay(await new FileEventJournal(_dir.Path).ReadAllAsync("g1", Ct));

        // Records holding lists compare lists by reference, so compare the persisted form: it must be lossless.
        Assert.Equal(original.Events.Select(JournalJson.Serialize), reloaded.Events.Select(JournalJson.Serialize));
        Assert.Equal(original.Balances.All, reloaded.Balances.All);
        var typesWritten = original.Events.Select(e => e.GetType().Name).Distinct().Count();
        Assert.Equal(13, typesWritten);
    }

    [Fact]
    public async Task Restart_rebuilds_balances_and_keeps_idempotency_keys()
    {
        var service = await SeededServiceAsync(new FileEventJournal(_dir.Path));
        await service.ExecuteAsync("g1", (g, c) => g.RecordReceipt(g.FindByNickname("amy")!.Id, g.FindByNickname("bob")!.Id, 70, c), (_, d) => d, "evt-1", Ct);

        var restarted = new LedgerService(new FileEventJournal(_dir.Path), _clock);
        var again = await restarted.ExecuteAsync("g1", (g, c) => g.RecordReceipt(g.FindByNickname("amy")!.Id, g.FindByNickname("bob")!.Id, 70, c), (_, d) => d, "evt-1", Ct);

        Assert.True(again.IsDuplicate);
        Assert.Equal(70, await restarted.QueryAsync("g1", g => g.Balances[g.FindByNickname("bob")!.Id], Ct));
    }

    [Fact]
    public async Task Torn_final_write_is_discarded_and_overwritten()
    {
        var journal = new FileEventJournal(_dir.Path);
        await SeededServiceAsync(journal);
        string path = journal.PathFor("g1");
        long goodLength = new FileInfo(path).Length;
        await File.AppendAllTextAsync(path, "{\"$type\":\"payment.posted\",\"seq", Ct); // crash mid-write: no newline

        var diagnostics = new List<string>();
        var recovered = new FileEventJournal(_dir.Path, diagnostics.Add);
        var service = new LedgerService(recovered, _clock);
        await service.ExecuteAsync("g1", (g, c) => g.RecordReceipt(g.FindByNickname("amy")!.Id, g.FindByNickname("bob")!.Id, 5, c), (_, d) => d, "after-crash", Ct);

        Assert.Single(diagnostics);
        Assert.DoesNotContain("\"seq\n", await File.ReadAllTextAsync(path, Ct));
        Assert.True(new FileInfo(path).Length > goodLength);
        Assert.Equal(5, GroupLedger.Replay(await new FileEventJournal(_dir.Path).ReadAllAsync("g1", Ct)).LastSequence);
    }

    [Fact]
    public async Task Edited_record_is_detected_as_corruption()
    {
        var journal = new FileEventJournal(_dir.Path);
        await SeededServiceAsync(journal);
        string path = journal.PathFor("g1");
        string text = await File.ReadAllTextAsync(path, Ct);
        await File.WriteAllTextAsync(path, text.Replace("\"nickname\":\"bob\"", "\"nickname\":\"rob\"", StringComparison.Ordinal), Ct);

        await Assert.ThrowsAsync<JournalCorruptedException>(() => new FileEventJournal(_dir.Path).ReadAllAsync("g1", Ct));
    }

    [Fact]
    public void Unsafe_group_ids_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new FileEventJournal(_dir.Path).PathFor("../escape"));

    [Fact]
    public async Task Concurrent_commands_are_serialised_without_gaps()
    {
        var service = await SeededServiceAsync(new FileEventJournal(_dir.Path));
        var tasks = Enumerable.Range(0, 40).Select(i => service.ExecuteAsync(
            "g1", (g, c) => g.RecordReceipt(g.FindByNickname("amy")!.Id, g.FindByNickname("bob")!.Id, 1, c), (_, d) => d, $"c-{i}", Ct));
        await Task.WhenAll(tasks);

        var events = await new FileEventJournal(_dir.Path).ReadAllAsync("g1", Ct);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.Equal(40, GroupLedger.Replay(events).Balances[new MemberId("m002")]);
    }
}

public sealed class OperationsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Inbound_events_are_accepted_once_even_across_restarts()
    {
        var store = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        Assert.True(store.TryAcceptInbound(new InboxItem("evt-1", 1, _clock.UtcNow, "{}")));
        Assert.False(store.TryAcceptInbound(new InboxItem("evt-1", 1, _clock.UtcNow, "{}")));

        var restarted = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        Assert.False(restarted.TryAcceptInbound(new InboxItem("evt-1", 1, _clock.UtcNow, "{}")));
        Assert.Single(restarted.PendingInbound());
    }

    [Fact]
    public void Processed_payloads_are_purged_but_ids_retained_for_dedupe()
    {
        var store = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        store.TryAcceptInbound(new InboxItem("evt-secret", 1, _clock.UtcNow, "{\"text\":\"my bank PIN is 1234\"}"));
        store.CompleteInbound("evt-secret", "ignored:chatter");
        store.Compact();

        string file = File.ReadAllText(_dir.File("ops.jsonl"));
        Assert.DoesNotContain("PIN", file, StringComparison.Ordinal);
        Assert.False(new FileOperationsStore(_dir.File("ops.jsonl"), _clock).TryAcceptInbound(new InboxItem("evt-secret", 1, _clock.UtcNow, "{}")));

        _clock.Advance(FileOperationsStore.DedupeRetention + TimeSpan.FromHours(1));
        new FileOperationsStore(_dir.File("ops.jsonl"), _clock).Compact();
        Assert.DoesNotContain("evt-secret", File.ReadAllText(_dir.File("ops.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_inbound_is_ordered_by_event_timestamp()
    {
        var store = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        store.TryAcceptInbound(new InboxItem("late", 300, _clock.UtcNow, "{}"));
        store.TryAcceptInbound(new InboxItem("early", 100, _clock.UtcNow, "{}"));
        Assert.Equal(new[] { "early", "late" }, store.PendingInbound().Select(i => i.EventId).ToArray());
    }

    [Fact]
    public void Outbox_attempts_push_quota_and_routes_survive_restart()
    {
        var store = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        store.EnqueueOutbound(new OutboxItem("m1", "g1", "rt", _clock.UtcNow, "[]", true, 11));
        store.EnqueueOutbound(new OutboxItem("m1", "g1", "rt", _clock.UtcNow, "[]", true, 11)); // idempotent
        store.RecordOutboundAttempt("m1");
        store.EnqueueOutbound(new OutboxItem("m2", "g1", null, _clock.UtcNow, "[]", true, 11));
        store.CompleteOutbound("m2", DeliveryChannel.Push, 11);
        store.RememberRoute("g1", "Cabc");

        var restarted = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        var pending = Assert.Single(restarted.PendingOutbound());
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(11, restarted.PushRecipientsInMonth(_clock.UtcNow));
        Assert.Equal(0, restarted.PushRecipientsInMonth(_clock.UtcNow.AddMonths(1)));
        Assert.Equal("Cabc", restarted.FindRoute("g1"));
    }

    [Fact]
    public void Torn_tail_in_operations_file_is_ignored()
    {
        var store = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        store.TryAcceptInbound(new InboxItem("evt-1", 1, _clock.UtcNow, "{}"));
        File.AppendAllText(_dir.File("ops.jsonl"), "{\"$type\":\"in.acc", Encoding.UTF8);
        var restarted = new FileOperationsStore(_dir.File("ops.jsonl"), _clock);
        Assert.Single(restarted.PendingInbound());
    }
}
