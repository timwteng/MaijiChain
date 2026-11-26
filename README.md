# Maijichain

A LINE bot that keeps a friend group's shared expenses in an auditable, append-only ledger and settles everyone with
the **fewest possible transfers**. It also includes an isolated NBitcoin proof of concept that turns a settlement
transfer into a Bitcoin test-network transaction.

Built for a private 11-person group and used for over three years. This repository is the v1.0 rebuild, written against
the reviewed specification in [`docs/`](docs/).

```
/join amy                   → Welcome @amy! You are the group coordinator.
/add 900 hot pot            → D1 · hot pot · 900 TWD
                              @amy 300 TWD · @bob 300 TWD · @cat 300 TWD      [Confirm] [Cancel]
  (Confirm)                 → E1 posted: hot pot 900 TWD.
                              Now: @amy +600 TWD · @bob −300 TWD · @cat −300 TWD
/plan                       → S1: 2 transfer(s), the minimum possible
                              1. @bob → @amy 300 TWD  (/paid S1-1)
                              2. @cat → @amy 300 TWD  (/paid S1-2)
/paid S1-1                  → P1: @bob says they paid @amy 300 TWD.           [Received] [Not received]
```

## What makes it interesting

- **Provably minimal settlement.** Minimising transfers is NP-hard in general, but for at most 20 unsettled members a
  subset dynamic program solves it exactly in milliseconds. In random 11-person groups with NT$50 amounts, the common
  greedy heuristic was suboptimal half the time. Larger inputs fall back to greedy, and the reply says so.
- **Fair rounding.** NT$100 ÷ 3 still gives 34/33/33, but who gets the extra dollar rotates per expense, using a hash of
  the expense ID. The usual "lowest ID pays" rule cost one simulated member NT$2,711 more than another over 3,000 expenses.
- **One event, one effect.** Signed webhooks are fsynced to an inbox before the 200 response. De-duplication by
  `webhookEventId`, per-command idempotency keys and state checks mean redelivery, crashes and double taps never post twice.
- **History is never rewritten.** Every change is an event in a checksummed journal. Corrections are reversals with a
  reason, and replay rebuilds and re-verifies everything.
- **Money moves only when the receiver says so.** "I paid" is a claim; the creditor confirms it.
- **Private and cheap by default.** LINE IDs are hashed before storage, message text is purged shortly after processing, and
  the free Reply API is preferred over quota-consuming pushes.
- **Blockchain as an adapter.** The NBitcoin POC builds, reviews, signs and tracks PSBTs on regtest, signet or testnet4.
  It cannot reference the ledger, and a test enforces that.

## Repository layout

| Path | Contents |
|---|---|
| `src/Maijichain.Domain` | Allocation, postings, balance sheet, netting, `GroupLedger` aggregate (no I/O) |
| `src/Maijichain.Application` | `LedgerService` (per-group serialisation, journal-first apply) and ports |
| `src/Maijichain.Persistence` | Checksummed append-only journals; compacted operations store (inbox / outbox) |
| `src/Maijichain.Line` | Signature check, webhook receiver, command parser, event processor, replies, outbox dispatcher |
| `src/Maijichain.WebHost` | ASP.NET Core endpoint `/webhooks/line`, `/healthz`, background workers |
| `src/Maijichain.BitcoinPoc` | NBitcoin PSBT builder, signer, broadcast guard, chain tracker, Bitcoin Core RPC client |
| `tests/…` | Domain, integration and POC tests, plus the transaction-case corpus |
| `docs/` | Specification v1.0, review findings, architecture, test report, archived v0.9 |

## Run the tests

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet test --solution Maijichain.slnx
```

Tests use xUnit.net v3 on Microsoft Testing Platform (`global.json` opts `dotnet test` into MTP mode).

### Optional: Bitcoin regtest end-to-end test

Install [Bitcoin Core](https://bitcoincore.org/en/download/) 28 or newer
(`winget install --id BitcoinCoreProject.BitcoinCore -e`). The installer does not add `bitcoind` to `PATH`, and
`-daemon` is not supported on Windows, so run the node in its own terminal and leave it open:

```powershell
# Terminal 1: the regtest node (pick any password; Ctrl+C stops it)
& "C:\Program Files\Bitcoin\daemon\bitcoind.exe" -regtest -txindex=1 -listen=0 -rpcuser=maiji -rpcpassword=regtest123 -rpcport=18443
```

```powershell
# Terminal 2: the tests
$env:MAIJICHAIN_REGTEST_RPC = "maiji:regtest123@127.0.0.1:18443"
dotnet test --solution Maijichain.slnx
```

On Linux or macOS, use `bitcoind -regtest -daemon …` with the same options.

The test mines to a fresh key, builds a PSBT for a settlement, signs, verifies, broadcasts, checks that zero
confirmations are not final, mines a block and confirms. No node wallet is needed.

## Run the bot

1. Create a Messaging API channel in the [LINE Developers Console](https://developers.line.biz/console/). Note the
   channel secret and issue a channel access token.
2. Store secrets outside the repository (see `.env.example`):
   ```powershell
   cd src/Maijichain.WebHost
   dotnet user-secrets set "Line:ChannelSecret" "<channel secret>"
   dotnet user-secrets set "Line:ChannelAccessToken" "<access token>"
   dotnet user-secrets set "Line:IdentityPepper" "<32+ random characters>"
   dotnet run
   ```
3. Expose the app over **HTTPS** (a reverse proxy, a tunnel, or a cloud host), then set the webhook URL to
   `https://<host>/webhooks/line`.
4. In the console, turn on **Use webhook** and **Webhook redelivery**. Allow the account to join group chats, and turn
   off the Official Account's own auto-reply messages.
5. Invite the bot to the group. Everyone sends `/join <nickname>`. `/help` lists the commands.

Data lives in `data/` (git-ignored): `ledgers/*.journal.jsonl` is the financial record, so back it up. `ops.jsonl` holds
operational state.

## Honest scope

- This is a rebuild. Tests here prove the rebuild's behaviour, not the historical app's. See spec §16 for what to
  confirm about the original.
- The case corpus ships with **15 synthetic cases**. Add the anonymised historical cases using
  [`tests/fixtures/transaction-cases/README.md`](tests/fixtures/transaction-cases/README.md) before citing "150 cases".
- The Bitcoin component is a test-network proof of concept with disposable keys. It is not a wallet and not custody.
- Single instance: the file journal assumes one process per data directory.

## Documentation

- [Requirements and functional specification v1.0](docs/requirements-and-functional-specification.md)
- [Spec review: 24 findings and their resolutions](docs/spec-review.md)
- [Architecture](docs/architecture.md)
- [Test report](docs/test-report.md)
