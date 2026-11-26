using NBitcoin;
using NBitcoin.RPC;

namespace Maijichain.BitcoinPoc;

public enum ChainState
{
    Proposed,
    Built,
    Reviewed,
    Signed,
    Broadcast,
    Confirmed,
    Failed,
    Cancelled,
}

/// <summary>
/// POC transaction lifecycle (spec §10.2). Unconfirmed is never final, and a confirmation that disappears in a
/// reorganisation moves the state back to Broadcast. None of this touches the expense ledger.
/// </summary>
public sealed class ChainTransactionTracker(string transferId, int requiredConfirmations)
{
    private readonly List<(ChainState State, string Note)> _history = [(ChainState.Proposed, "selected for POC")];

    public string TransferId { get; } = transferId;

    public ChainState State { get; private set; } = ChainState.Proposed;

    public uint256? TxId { get; private set; }

    public int Confirmations { get; private set; }

    public IReadOnlyList<(ChainState State, string Note)> History => _history;

    public void MarkBuilt() => Move(ChainState.Built, "PSBT built", ChainState.Proposed);

    public void MarkReviewed() => Move(ChainState.Reviewed, "amount, fee and change reviewed", ChainState.Built);

    public void MarkSigned(uint256 txId)
    {
        Move(ChainState.Signed, $"signed {txId}", ChainState.Reviewed);
        TxId = txId;
    }

    public void MarkBroadcast() => Move(ChainState.Broadcast, "submitted", ChainState.Signed);

    public void Cancel(string reason) => Move(ChainState.Cancelled, reason, ChainState.Proposed, ChainState.Built, ChainState.Reviewed);

    public void Fail(string reason) => Move(ChainState.Failed, reason, ChainState.Signed, ChainState.Broadcast);

    /// <summary>Feeds the latest confirmation count; null means the node no longer knows the transaction.</summary>
    public void Observe(int? confirmations)
    {
        if (State is not (ChainState.Broadcast or ChainState.Confirmed))
        {
            throw new InvalidOperationException($"Cannot observe confirmations in state {State}.");
        }

        if (confirmations is null)
        {
            Move(ChainState.Failed, "dropped from mempool or replaced", ChainState.Broadcast, ChainState.Confirmed);
            return;
        }

        Confirmations = confirmations.Value;
        if (State == ChainState.Broadcast && Confirmations >= requiredConfirmations)
        {
            Move(ChainState.Confirmed, $"{Confirmations} confirmation(s)", ChainState.Broadcast);
        }
        else if (State == ChainState.Confirmed && Confirmations < requiredConfirmations)
        {
            Move(ChainState.Broadcast, "confirmation lost in a reorganisation", ChainState.Confirmed);
        }
    }

    private void Move(ChainState next, string note, params ChainState[] allowedFrom)
    {
        if (!allowedFrom.Contains(State))
        {
            throw new InvalidOperationException($"Cannot go from {State} to {next}.");
        }

        State = next;
        _history.Add((next, note));
    }
}

public interface IChainClient
{
    Task<IReadOnlyList<Coin>> ListUnspentAsync(BitcoinAddress address, int minConfirmations, CancellationToken ct);

    Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct);

    /// <summary>Confirmations of a transaction; 0 while in the mempool; null if the node does not know it.</summary>
    Task<int?> GetConfirmationsAsync(uint256 txId, CancellationToken ct);
}

/// <summary>Bitcoin Core JSON-RPC adapter. Needs no node wallet: UTXOs come from <c>scantxoutset</c> (run the node with -txindex).</summary>
public sealed class BitcoinCoreRpcChainClient(RPCClient rpc) : IChainClient
{
    public async Task<IReadOnlyList<Coin>> ListUnspentAsync(BitcoinAddress address, int minConfirmations, CancellationToken ct)
    {
        var tip = await rpc.GetBlockCountAsync(ct).ConfigureAwait(false);
        var scan = await rpc.StartScanTxoutSetAsync(new ScanTxoutSetParameters(address), ct).ConfigureAwait(false);
        return scan.Outputs
            .Where(o => tip - o.Height + 1 >= minConfirmations)
            .Select(o => o.Coin)
            .OrderBy(c => c.Outpoint.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct) => rpc.SendRawTransactionAsync(tx, ct);

    public async Task<int?> GetConfirmationsAsync(uint256 txId, CancellationToken ct)
    {
        try
        {
            var info = await rpc.GetRawTransactionInfoAsync(txId, ct).ConfigureAwait(false);
            return (int)info.Confirmations;
        }
        catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY)
        {
            return null;
        }
    }
}
