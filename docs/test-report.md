# Test report

| Field | Value |
|---|---|
| Date | 2026-11-20 |
| Code | Maijichain v1.0 rebuild (this repository, initial version) |
| Result | **177 passed, 0 failed, 0 skipped** (3 suites, regtest enabled) |
| Platform | Ubuntu 24.04, .NET SDK 10.0.115 (`net10.0`), Release build |
| NBitcoin | 10.0.21, compiled from the `v10.0.21` tag |
| Bitcoin node | Bitcoin Core v32.1, compiled from source, `-regtest -txindex=1`, no wallet |

## Results

| Suite | Classes (tests) | Result |
|---|---|---|
| `Maijichain.Domain.Tests` | Allocation (12), CaseCorpus (16), Currency (15), GroupLedger (22), Netting (10) | **75 / 75** |
| `Maijichain.Integration.Tests` | Architecture (3), CommandParser (23), Host (4), Journal (6), LineMessagingClient (8), LineSignature (7), OperationsStore (5), OutboxDispatcher (6), Pipeline (8), WebhookReceiver (5) | **75 / 75** |
| `Maijichain.BitcoinPoc.Tests` | ChainTracker (5), RegtestEndToEnd (1), SettlementTransaction (21) | **27 / 27** |

All three suites were run **three times in a row** with identical results. The POC suite was also run five times after
the determinism fix (R22). Without `MAIJICHAIN_REGTEST_RPC`, the regtest test is reported as skipped and the rest pass.

The counts include the property-based tests, which run many generated cases each:

| Property test | Generated cases | Checks |
|---|---|---|
| `Property_plans_settle_preserve_positions_and_exact_is_optimal` | 1,500 balance sheets | Replay clears all; transfers positive; volume = credits; ≤ k − 1; **exact count = brute-force optimum**; greedy ≥ exact; input-order independence |
| `Property_equal_shares_are_positive_sum_exactly_and_differ_by_at_most_one` | 5,000 splits | Positive; exact sum; max − min ≤ 1 |
| `Property_random_activity_never_breaks_invariants` | 120 groups × 40 random commands | Zero-sum after every command; plans settle; replay equality |
| `Rotation_spreads_rounding_units_evenly_while_legacy_rule_is_biased` | 3,000 expenses × 11 members | Each member within ±15 % of the mean under rotation |

## Additional verification

| Check | Outcome |
|---|---|
| Real host smoke run (Kestrel + `curl`, signature from `openssl dgst -hmac`) | `/healthz` 200 · signed webhook 200 · identical redelivery 200 with no duplicate journal entry · forged signature 401 · 0 raw LINE IDs in the journal · expense date in UTC+8 |
| Bot conversation replay (`/join` ×3 → `/add` → Confirm → `/plan` → `/paid` → Received → weighted `/add` → `/bal`) | Replies match the README example |
| Rounding-bias measurement (3,000 expenses, 11 members) | v0.9 rule: 2,711 … 0 units per member; v1.0 rule: 1,310–1,370 |
| Greedy vs exact (2,000 random 11-member groups, NT$50 steps) | Greedy above the minimum in 1,009 groups (1,167 extra transfers) |
| All `src` projects with warnings-as-errors | 0 warnings |

## How the tests were executed here, and what to run on your machine

The build sandbox could not reach nuget.org. To still run the real test code:

- **NBitcoin 10.0.21** was compiled from its GitHub release tag into a local package feed. Its two dependencies came from
  the .NET source-build package set.
- **Test sources** from `tests/` were compiled unchanged against the real xUnit.net v3 assertion library, built from
  [`xunit/assert.xunit`](https://github.com/xunit/assert.xunit) source. A minimal reflection runner was used with xUnit's
  execution semantics: a new class instance per test, `IDisposable`/`IAsyncDisposable`, `[Fact]`, `[Theory]`,
  `[InlineData]`, `[MemberData]` and dynamic skip. The runner was checked against deliberately failing, theory, skipped
  and async tests. This harness is **not** part of the repository.

Not executed here, so please run these on your PC:

1. `dotnet test --solution Maijichain.slnx`. This restores `xunit.v3.mtp-v2` 4.0.3 and runs under the real xUnit v3 /
   Microsoft Testing Platform runner, including the xUnit analyzers.
2. Live LINE API calls. The client was tested against stubbed HTTP responses and the documented contract.
3. Windows-specific behaviour (file paths, fsync). The code uses only cross-platform .NET APIs.

## Acceptance-case coverage

TC-001 through TC-020 in spec §13.2 map to named tests. Each one passed in the runs above.
