using NBitcoin;

namespace Maijichain.BitcoinPoc;

public sealed class PocPolicyException(string message) : Exception(message);

public sealed class PocOptions
{
    /// <summary>regtest (automated tests), signet or testnet4 (public demos). Mainnet and testnet3 are refused.</summary>
    public string Network { get; set; } = "regtest";

    public long MaxFeeSats { get; set; } = 20_000;

    public decimal MaxFeeRateSatPerVByte { get; set; } = 50m;

    /// <summary>Confirmations before a test transaction is shown as Confirmed. Use 1 on regtest, more on public test networks.</summary>
    public int RequiredConfirmations { get; set; } = 1;
}

public static class PocNetwork
{
    public static Network Resolve(string name) => name.Trim().ToLowerInvariant() switch
    {
        "regtest" => Network.RegTest,
        "signet" => Bitcoin.Instance.Signet,
        "testnet4" => Network.TestNet4,
        "main" or "mainnet" or "bitcoin" => throw new PocPolicyException("Mainnet is not allowed in the Maijichain POC."),
        "test" or "testnet" or "testnet3" => throw new PocPolicyException("testnet3 is deprecated (removed from Bitcoin Core 30); use testnet4 or signet."),
        _ => throw new PocPolicyException($"Unknown network '{name}'."),
    };

    /// <summary>FR-BTC-02: an address must belong to the configured network.</summary>
    public static BitcoinAddress ParseAddress(string text, Network network)
    {
        try
        {
            return BitcoinAddress.Create(text.Trim(), network);
        }
        catch (FormatException)
        {
            throw new PocPolicyException($"Address is not valid on {network.Name}.");
        }
    }

    /// <summary>Loads a disposable test key. Refuses mainnet keys so a real wallet key cannot be used by mistake.</summary>
    public static Key LoadTestKey(string wif, Network network)
    {
        if (network.ChainName == ChainName.Mainnet)
        {
            throw new PocPolicyException("Mainnet is not allowed in the Maijichain POC.");
        }

        try
        {
            return Key.Parse(wif.Trim(), network);
        }
        catch (FormatException)
        {
            throw new PocPolicyException($"The key is not a {network.Name} WIF key.");
        }
    }
}

/// <summary>
/// FR-BTC-03: converting a ledger amount to satoshis is an explicit, labelled test input. There is deliberately no
/// exchange-rate service: test coins have no price.
/// </summary>
public sealed record TestConversion(long SatsPerMinorUnit)
{
    public long ToSats(long minorUnits) => SatsPerMinorUnit > 0 ? checked(minorUnits * SatsPerMinorUnit) : throw new PocPolicyException("Test conversion rate must be positive.");

    public override string ToString() => $"test rate: 1 minor unit = {SatsPerMinorUnit} sats (not a market price)";
}

/// <summary>A settlement transfer selected for the POC, carried as plain values so this project never touches the ledger.</summary>
public sealed record SettlementInstruction(string TransferId, long LedgerAmountMinor, string CurrencyCode, long AmountSats, string RecipientAddress);
