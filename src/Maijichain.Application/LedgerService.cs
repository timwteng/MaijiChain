using System.Collections.Concurrent;
using Maijichain.Domain;

namespace Maijichain.Application;

/// <summary>Outcome of a command, projected while the group lock is still held.</summary>
public sealed record CommandOutcome<T>(LedgerEvent Event, bool IsDuplicate, T Value);

/// <summary>
/// Runs commands against group ledgers. Per group, commands are serialised; each accepted command is appended to the
/// journal (one event = one atomic record) before the in-memory aggregate is updated, so a crash can never leave
/// memory ahead of disk (NFR-04).
/// </summary>
public sealed class LedgerService(IEventJournal journal, IClock clock)
{
    private readonly ConcurrentDictionary<string, GroupSlot> _groups = new(StringComparer.Ordinal);

    public IClock Clock => clock;

    public async Task<bool> ExistsAsync(string groupId, CancellationToken ct)
    {
        var slot = _groups.GetOrAdd(groupId, _ => new GroupSlot());
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadAsync(groupId, slot, ct).ConfigureAwait(false) is not null;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Creates the group if it does not exist yet; idempotent.</summary>
    public async Task OpenGroupAsync(string groupId, string name, Currency currency, RemainderPolicy policy, string sourceKey, CancellationToken ct)
    {
        var slot = _groups.GetOrAdd(groupId, _ => new GroupSlot());
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await LoadAsync(groupId, slot, ct).ConfigureAwait(false) is not null)
            {
                return;
            }

            var opened = GroupLedger.Open(groupId, name, currency, policy, new CommandContext(clock.UtcNow, sourceKey));
            var ledger = GroupLedger.Replay([opened]); // validate before writing
            await journal.AppendAsync(groupId, opened, ct).ConfigureAwait(false);
            slot.Ledger = ledger;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task<CommandOutcome<T>> ExecuteAsync<T>(
        string groupId,
        Func<GroupLedger, CommandContext, Decision> decide,
        Func<GroupLedger, Decision, T> project,
        string sourceKey,
        CancellationToken ct)
    {
        var slot = _groups.GetOrAdd(groupId, _ => new GroupSlot());
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ledger = await LoadAsync(groupId, slot, ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.GroupNotOpen, "This chat has no Maijichain group yet.");
            var decision = decide(ledger, new CommandContext(clock.UtcNow, sourceKey));
            if (!decision.IsDuplicate)
            {
                await journal.AppendAsync(groupId, decision.Event, ct).ConfigureAwait(false);
                try
                {
                    ledger.Apply(decision.Event);
                }
                catch
                {
                    slot.Ledger = null; // force a full replay from the journal on next access
                    throw;
                }
            }

            return new CommandOutcome<T>(decision.Event, decision.IsDuplicate, project(ledger, decision));
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public async Task<T> QueryAsync<T>(string groupId, Func<GroupLedger, T> query, CancellationToken ct)
    {
        var slot = _groups.GetOrAdd(groupId, _ => new GroupSlot());
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ledger = await LoadAsync(groupId, slot, ct).ConfigureAwait(false)
                ?? throw new DomainException(ErrorCodes.GroupNotOpen, "This chat has no Maijichain group yet.");
            return query(ledger);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private async Task<GroupLedger?> LoadAsync(string groupId, GroupSlot slot, CancellationToken ct)
    {
        if (slot.Ledger is null)
        {
            var events = await journal.ReadAllAsync(groupId, ct).ConfigureAwait(false);
            slot.Ledger = events.Count == 0 ? null : GroupLedger.Replay(events);
        }

        return slot.Ledger;
    }

    private sealed class GroupSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public GroupLedger? Ledger { get; set; }
    }
}
