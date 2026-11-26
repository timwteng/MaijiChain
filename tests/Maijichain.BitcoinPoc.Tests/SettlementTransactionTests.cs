using System.Security.Cryptography;
using System.Text;
using NBitcoin;

namespace Maijichain.BitcoinPoc.Tests;

/// <summary>Offline PSBT tests: no node needed, keys and coins are deterministic test fixtures.</summary>
public sealed class SettlementTransactionTests
{
    private static readonly PocOptions Options = new() { Network = "regtest", MaxFeeSats = 20_000, MaxFeeRateSatPerVByte = 50m };
    private static readonly Network Net = Network.RegTest;
    private static readonly Key Payer = TestKey("payer");
    private static readonly Key Receiver = TestKey("receiver");
    private static readonly SettlementTransactionBuilder Builder = new(Options);

    internal static Key TestKey(string seed) => new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private static BitcoinAddress Address(Key key, Network network) => key.PubKey.GetAddress(ScriptPubKeyType.Segwit, network);

    private static Coin Fund(Key key, long sats, int n) =>
        new(new OutPoint(new uint256(SHA256.HashData(BitConverter.GetBytes(n))), 0), new TxOut(Money.Satoshis(sats), key.GetScriptPubKey(ScriptPubKeyType.Segwit)));

    private static SettlementInstruction Instruction(long sats, string? recipient = null) =>
        new("S1-1", 300, "TWD", sats, recipient ?? Address(Receiver, Net).ToString());

    private static SettlementProposal Build(long sats, long fundedSats, decimal satPerVByte = 2m) =>
        Builder.Build(Instruction(sats), [Fund(Payer, fundedSats, 1)], Address(Payer, Net), new FeeRate(satPerVByte));

    [Fact]
    public void TC014_change_output_is_explicit_and_value_is_conserved()
    {
        var preview = Build(30_000, 100_000).Preview;
        Assert.Equal(30_000, preview.AmountSats);
        Assert.True(preview.ChangeSats > 0);
        Assert.Equal(Address(Payer, Net).ToString(), preview.ChangeAddress);
        Assert.Equal(preview.InputSats, preview.AmountSats + preview.ChangeSats + preview.FeeSats); // FR-BTC-04
        Assert.InRange(preview.FeeRateSatPerVByte, 1.9m, 2.6m);
        Assert.Single(preview.Inputs);
        Assert.False(preview.RequiresManualReview);
    }

    [Fact]
    public void Proposal_is_an_unsigned_psbt()
    {
        var psbt = Build(30_000, 100_000).Psbt;
        Assert.All(psbt.Inputs, i => Assert.Empty(i.PartialSigs));
        Assert.False(psbt.IsAllFinalized());
        Assert.NotNull(psbt.Inputs[0].WitnessUtxo); // signer has what it needs (BIP 174)
    }

    [Fact]
    public void TC013_addresses_from_other_networks_are_rejected_before_building()
    {
        Assert.Throws<PocPolicyException>(() => Builder.Build(Instruction(30_000, Address(Receiver, Network.Main).ToString()), [Fund(Payer, 100_000, 1)], Address(Payer, Net), new FeeRate(2m)));
        Assert.Throws<PocPolicyException>(() => Builder.Build(Instruction(30_000, Address(Receiver, Network.TestNet4).ToString()), [Fund(Payer, 100_000, 1)], Address(Payer, Net), new FeeRate(2m)));
        Assert.Throws<PocPolicyException>(() => Builder.Build(Instruction(30_000), [Fund(Payer, 100_000, 1)], Address(Payer, Network.Main), new FeeRate(2m)));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("testnet3")]
    [InlineData("bogus")]
    public void Mainnet_and_deprecated_testnet3_are_refused(string network) => Assert.Throws<PocPolicyException>(() => PocNetwork.Resolve(network));

    [Theory]
    [InlineData("regtest")]
    [InlineData("signet")]
    [InlineData("testnet4")]
    public void Supported_test_networks_resolve(string network) => Assert.NotEqual(ChainName.Mainnet, PocNetwork.Resolve(network).ChainName);

    [Fact]
    public void Mainnet_private_keys_cannot_be_loaded()
    {
        Assert.Throws<PocPolicyException>(() => PocNetwork.LoadTestKey(Payer.GetWif(Network.Main).ToString(), Net));
        Assert.Equal(Payer.PubKey, PocNetwork.LoadTestKey(Payer.GetWif(Net).ToString(), Net).PubKey);
    }

    [Fact]
    public void Insufficient_test_funds_are_reported_without_signing()
    {
        Assert.Throws<PocPolicyException>(() => Build(30_000, 20_000));
        Assert.Throws<PocPolicyException>(() => Build(99_990, 100_000)); // covers amount but not the fee
    }

    [Fact]
    public void Excessive_fee_requires_review_and_broadcast_is_refused()
    {
        var proposal = Build(30_000, 100_000, satPerVByte: 200m);
        Assert.True(proposal.Preview.RequiresManualReview);
        Assert.Contains(proposal.Preview.Warnings, w => w.Contains("exceeds", StringComparison.Ordinal));
        var signed = PsbtSigner.Sign(proposal, reviewed: true, Payer);
        Assert.Throws<PocPolicyException>(() => BroadcastGuard.Verify(signed, proposal, Options));
    }

    [Fact]
    public void Dust_change_is_disclosed_not_silently_burned()
    {
        var reference = Build(30_000, 100_000).Preview;
        long feeWithChange = reference.FeeSats;
        var proposal = Build(30_000, 30_000 + feeWithChange + 100);
        Assert.Equal(0, proposal.Preview.ChangeSats);
        Assert.True(proposal.Preview.DustAddedToFeeSats > 0);
        Assert.True(proposal.Preview.RequiresManualReview);
        Assert.Equal(proposal.Preview.InputSats, proposal.Preview.AmountSats + proposal.Preview.FeeSats);
    }

    [Fact]
    public void Signing_requires_review_and_produces_a_verifiable_transaction()
    {
        var proposal = Build(30_000, 100_000);
        Assert.Throws<PocPolicyException>(() => PsbtSigner.Sign(proposal, reviewed: false, Payer));

        var tx = PsbtSigner.Sign(proposal, reviewed: true, Payer);
        BroadcastGuard.Verify(tx, proposal, Options);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey == Address(Receiver, Net).ScriptPubKey && o.Value.Satoshi == 30_000);
        Assert.False(proposal.Psbt.IsAllFinalized()); // original proposal untouched
    }

    [Fact]
    public void Wrong_key_cannot_produce_a_valid_transaction() =>
        Assert.ThrowsAny<Exception>(() => PsbtSigner.Sign(Build(30_000, 100_000), reviewed: true, Receiver));

    [Fact]
    public void Guard_rejects_a_transaction_that_differs_from_the_reviewed_one()
    {
        var reviewed = Build(30_000, 100_000);
        var other = PsbtSigner.Sign(Build(40_000, 100_000), reviewed: true, Payer);
        Assert.Throws<PocPolicyException>(() => BroadcastGuard.Verify(other, reviewed, Options));
    }

    [Fact]
    public void Builds_are_deterministic() =>
        Assert.Equal(Build(30_000, 100_000).Psbt.ToBase64(), Build(30_000, 100_000).Psbt.ToBase64());

    [Fact]
    public void Dust_sized_payments_are_refused() => Assert.Throws<PocPolicyException>(() => Build(100, 100_000));

    [Fact]
    public void Sats_conversion_is_an_explicit_labelled_test_input()
    {
        var rate = new TestConversion(10);
        Assert.Equal(3000, rate.ToSats(300));
        Assert.Contains("not a market price", rate.ToString(), StringComparison.Ordinal);
        Assert.Throws<PocPolicyException>(() => new TestConversion(0).ToSats(1));
    }
}

public sealed class ChainTrackerTests
{
    private static ChainTransactionTracker Broadcasted(int required = 1)
    {
        var t = new ChainTransactionTracker("S1-1", required);
        t.MarkBuilt();
        t.MarkReviewed();
        t.MarkSigned(uint256.One);
        t.MarkBroadcast();
        return t;
    }

    [Fact]
    public void TC015_zero_confirmations_is_broadcast_not_confirmed()
    {
        var t = Broadcasted();
        t.Observe(0);
        Assert.Equal(ChainState.Broadcast, t.State);
        t.Observe(1);
        Assert.Equal(ChainState.Confirmed, t.State);
    }

    [Fact]
    public void Reorganisation_moves_confirmed_back_to_broadcast()
    {
        var t = Broadcasted();
        t.Observe(2);
        t.Observe(0);
        Assert.Equal(ChainState.Broadcast, t.State);
        Assert.Contains(t.History, h => h.Note.Contains("reorganisation", StringComparison.Ordinal));
    }

    [Fact]
    public void Disappearing_transaction_fails_and_public_networks_wait_longer()
    {
        var dropped = Broadcasted();
        dropped.Observe(null);
        Assert.Equal(ChainState.Failed, dropped.State);

        var slow = Broadcasted(required: 6);
        slow.Observe(3);
        Assert.Equal(ChainState.Broadcast, slow.State);
    }

    [Fact]
    public void Illegal_transitions_are_refused()
    {
        var t = new ChainTransactionTracker("S1-1", 1);
        Assert.Throws<InvalidOperationException>(() => t.MarkSigned(uint256.One)); // not reviewed
        Assert.Throws<InvalidOperationException>(() => t.Observe(1));            // not broadcast
        t.MarkBuilt();
        t.Cancel("changed mind");
        Assert.Equal(ChainState.Cancelled, t.State);
        Assert.Throws<InvalidOperationException>(() => Broadcasted().Cancel("too late"));
    }

    [Fact]
    public void Poc_assembly_cannot_reach_the_ledger() =>
        Assert.DoesNotContain(typeof(SettlementTransactionBuilder).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Maijichain.", StringComparison.Ordinal));
}
