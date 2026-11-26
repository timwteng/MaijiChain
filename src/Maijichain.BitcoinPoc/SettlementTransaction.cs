using NBitcoin;

namespace Maijichain.BitcoinPoc;

public sealed record PreviewInput(string Outpoint, long Sats);

/// <summary>Everything a reviewer must see before signing (FR-BTC-05).</summary>
public sealed record ProposalPreview(
    string Network,
    string TransferId,
    string Recipient,
    long AmountSats,
    IReadOnlyList<PreviewInput> Inputs,
    string ChangeAddress,
    long ChangeSats,
    long FeeSats,
    decimal FeeRateSatPerVByte,
    int VirtualSize,
    long DustAddedToFeeSats,
    IReadOnlyList<string> Warnings)
{
    public long InputSats => Inputs.Sum(i => i.Sats);

    /// <summary>Excessive fees or dust absorbed into the fee require an explicit human decision; nothing is signed automatically.</summary>
    public bool RequiresManualReview => Warnings.Count > 0;
}

public sealed record SettlementProposal(PSBT Psbt, ProposalPreview Preview, IReadOnlyList<Coin> Coins);

/// <summary>
/// Builds an unsigned PSBT (BIP 174) for one settlement transfer. Construction and signing are separate steps, so the
/// signer can be another device or person (FR-BTC-07).
/// </summary>
public sealed class SettlementTransactionBuilder(PocOptions options)
{
    public SettlementProposal Build(SettlementInstruction instruction, IReadOnlyList<Coin> spendable, BitcoinAddress changeAddress, FeeRate feeRate)
    {
        var network = PocNetwork.Resolve(options.Network);
        var recipient = PocNetwork.ParseAddress(instruction.RecipientAddress, network);
        if (changeAddress.Network != network)
        {
            throw new PocPolicyException($"Change address is not on {network.Name}.");
        }

        if (instruction.AmountSats <= 0)
        {
            throw new PocPolicyException("Amount must be positive.");
        }

        var amount = Money.Satoshis(instruction.AmountSats);
        if (amount < new TxOut(Money.Zero, recipient.ScriptPubKey).GetDustThreshold())
        {
            throw new PocPolicyException($"{amount.Satoshi} sats is below the dust threshold for the recipient script.");
        }

        if (spendable.Count == 0 || spendable.Sum(c => c.Amount.Satoshi) < amount.Satoshi)
        {
            throw new PocPolicyException("The test wallet does not have enough confirmed coins for this transfer.");
        }

        // Output order and coin selection stay randomised (the change position is not a giveaway), but seeded by the
        // transfer ID so the same review input always yields the same PSBT (NFR-02).
        int seed = DeterministicSeed(instruction.TransferId);
        var builder = network.CreateTransactionBuilder();
        builder.ShuffleRandom = new Random(seed);
        builder.CoinSelector = new DefaultCoinSelector(seed);
        builder.AddCoins(spendable);
        builder.Send(recipient, amount);
        builder.SetChange(changeAddress);
        builder.SendEstimatedFees(feeRate);

        PSBT psbt;
        try
        {
            psbt = builder.BuildPSBT(sign: false);
        }
        catch (NotEnoughFundsException)
        {
            throw new PocPolicyException("The test wallet cannot cover the amount plus the network fee.");
        }

        var tx = psbt.GetGlobalTransaction();
        var inputs = psbt.Inputs.Select(i => new PreviewInput(i.PrevOut.ToString(), i.GetCoin()?.Amount.Satoshi ?? throw new PocPolicyException("Input without UTXO data."))).ToList();
        long recipientSats = tx.Outputs.Where(o => o.ScriptPubKey == recipient.ScriptPubKey).Sum(o => o.Value.Satoshi);
        long changeSats = tx.Outputs.Where(o => o.ScriptPubKey == changeAddress.ScriptPubKey).Sum(o => o.Value.Satoshi);
        if (recipientSats != amount.Satoshi || tx.Outputs.Count(o => o.ScriptPubKey == recipient.ScriptPubKey) != 1 ||
            tx.Outputs.Any(o => o.ScriptPubKey != recipient.ScriptPubKey && o.ScriptPubKey != changeAddress.ScriptPubKey))
        {
            throw new PocPolicyException("Unexpected outputs in the built transaction.");
        }

        long inputSats = inputs.Sum(i => i.Sats);
        long fee = inputSats - recipientSats - changeSats; // conservation: inputs = recipient + change + fee (FR-BTC-04)
        if (!psbt.TryGetFee(out var psbtFee) || psbtFee.Satoshi != fee || fee <= 0)
        {
            throw new PocPolicyException("Fee does not reconcile with inputs and outputs.");
        }

        psbt.TryGetVirtualSize(out int vsize);
        long estimatedFee = builder.EstimateFees(tx, feeRate).Satoshi;
        long dust = changeSats == 0 && fee > estimatedFee ? fee - estimatedFee : 0;
        decimal rate = vsize > 0 ? decimal.Round((decimal)fee / vsize, 2) : 0m;

        var warnings = new List<string>();
        if (fee > options.MaxFeeSats || rate > options.MaxFeeRateSatPerVByte)
        {
            warnings.Add($"Fee {fee} sats ({rate} sat/vB) exceeds the configured limit.");
        }

        if (dust > 0)
        {
            warnings.Add($"Change of {dust} sats is below the dust limit and would be added to the fee.");
        }

        var preview = new ProposalPreview(network.Name, instruction.TransferId, recipient.ToString(), amount.Satoshi, inputs, changeAddress.ToString(), changeSats, fee, rate, vsize, dust, warnings);
        var used = spendable.Where(c => inputs.Any(i => i.Outpoint == c.Outpoint.ToString())).ToList();
        return new SettlementProposal(psbt, preview, used);
    }

    private static int DeterministicSeed(string transferId) =>
        BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(transferId)), 0);
}

/// <summary>Signing boundary: takes a reviewed proposal and test keys, returns a finalized transaction.</summary>
public static class PsbtSigner
{
    public static Transaction Sign(SettlementProposal proposal, bool reviewed, params Key[] keys)
    {
        if (!reviewed)
        {
            throw new PocPolicyException("A proposal must be reviewed before signing.");
        }

        var psbt = proposal.Psbt.Clone();
        psbt.SignWithKeys(keys);
        psbt.Finalize();
        return psbt.ExtractTransaction();
    }
}

/// <summary>FR-BTC-09: the signed transaction must be exactly what was reviewed, script-valid and within fee policy.</summary>
public static class BroadcastGuard
{
    public static void Verify(Transaction signed, SettlementProposal proposal, PocOptions options)
    {
        var network = PocNetwork.Resolve(options.Network);
        var unsigned = proposal.Psbt.GetGlobalTransaction();
        if (signed.GetHash() != unsigned.GetHash())
        {
            throw new PocPolicyException("Signed transaction differs from the reviewed proposal.");
        }

        var builder = network.CreateTransactionBuilder();
        builder.AddCoins(proposal.Coins);
        if (!builder.Verify(signed, Money.Satoshis(proposal.Preview.FeeSats), out var errors))
        {
            throw new PocPolicyException("Transaction failed verification: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        if (proposal.Preview.FeeSats > options.MaxFeeSats)
        {
            throw new PocPolicyException("Fee exceeds policy; broadcast refused.");
        }
    }
}
