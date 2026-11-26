using NBitcoin;
using NBitcoin.RPC;

namespace Maijichain.BitcoinPoc.Tests;

/// <summary>
/// Full lifecycle against a real Bitcoin Core regtest node (FR-BTC-12). Skipped unless MAIJICHAIN_REGTEST_RPC is set, e.g.
/// <c>bitcoind -regtest -txindex=1 -rpcuser=maiji -rpcpassword=… </c> and
/// <c>MAIJICHAIN_REGTEST_RPC=maiji:…@127.0.0.1:18443</c>.
/// </summary>
public sealed class RegtestEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Settlement_transaction_confirms_on_regtest_without_touching_anything_else()
    {
        string? setting = Environment.GetEnvironmentVariable("MAIJICHAIN_REGTEST_RPC");
        if (string.IsNullOrWhiteSpace(setting))
        {
            Assert.Skip("Set MAIJICHAIN_REGTEST_RPC=user:password@127.0.0.1:18443 to run against a regtest node.");
        }

        int at = setting.LastIndexOf('@');
        var rpc = new RPCClient(setting[..at], "http://" + setting[(at + 1)..], Network.RegTest);
        var options = new PocOptions { Network = "regtest", RequiredConfirmations = 1 };
        var payer = new Key();
        var receiver = new Key();
        var payerAddress = payer.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var receiverAddress = receiver.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        IChainClient chain = new BitcoinCoreRpcChainClient(rpc);

        await rpc.GenerateToAddressAsync(101, payerAddress, Ct); // one mature coinbase for a fresh test key
        var coins = await chain.ListUnspentAsync(payerAddress, minConfirmations: 101, Ct);
        Assert.NotEmpty(coins);

        var tracker = new ChainTransactionTracker("S1-1", options.RequiredConfirmations);
        var proposal = new SettlementTransactionBuilder(options).Build(
            new SettlementInstruction("S1-1", 300, "TWD", 3_000_000, receiverAddress.ToString()), coins, payerAddress, new FeeRate(2m));
        tracker.MarkBuilt();
        Assert.False(proposal.Preview.RequiresManualReview);
        tracker.MarkReviewed();

        var tx = PsbtSigner.Sign(proposal, reviewed: true, payer);
        tracker.MarkSigned(tx.GetHash());
        BroadcastGuard.Verify(tx, proposal, options);
        var txId = await chain.BroadcastAsync(tx, Ct);
        tracker.MarkBroadcast();
        Assert.Equal(tx.GetHash(), txId);

        tracker.Observe(await chain.GetConfirmationsAsync(txId, Ct));
        Assert.Equal(ChainState.Broadcast, tracker.State); // TC-015: in mempool, not final

        await rpc.GenerateToAddressAsync(1, payerAddress, Ct);
        tracker.Observe(await chain.GetConfirmationsAsync(txId, Ct));
        Assert.Equal(ChainState.Confirmed, tracker.State);

        var received = Assert.Single(await chain.ListUnspentAsync(receiverAddress, 1, Ct));
        Assert.Equal(3_000_000, received.Amount.Satoshi);
        Assert.Null(await chain.GetConfirmationsAsync(uint256.One, Ct));
    }
}
