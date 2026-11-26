# Maijichain spec review (v0.9 → v1.0)

Reviewed: `Maijichain_Requirements_and_Functional_Specification.md` v0.9 (portfolio draft), 2026-11-20.
Outcome: v0.9 is a strong design. This review raised **24 concerns**. Each one is **resolved** in the v1.0 spec
(`requirements-and-functional-specification.md`). Where a fix is code, it is built and tested in this repository.

How to read this: every finding has a severity, the v0.9 text it concerns, the evidence, and the resolution, with the
code and tests that implement it. "Rebuild" means this repository. Nothing here is a claim about how the original
three-year-old app behaved. Those facts are still listed in v1.0 §16 for the owner to confirm.

| Severity | Meaning |
|---|---|
| **High** | Would produce wrong balances, unfair results or a spec that contradicts itself |
| **Medium** | Real operational, cost, privacy or security risk |
| **Low** | Wording, accuracy of claims or future-proofing |

---

## 1. Summary of findings

| # | Sev. | Finding | Resolution (where) |
|---|---|---|---|
| R1 | High | INV-09 makes a settlement plan stale after its own first payment, so a multi-transfer plan can never be completed | Plans go stale only on balance changes *not* caused by their own payments (`GroupLedger.IsPlanStale`) |
| R2 | High | "Responsible member confirms" is ambiguous: a debtor could clear their own debt | The creditor confirms; debtor claims stay pending; creditor-recorded receipts post immediately |
| R3 | High | The rounding rule always charges the lowest member IDs. Over 3,000 simulated expenses the first member absorbed NT$2,711, the 11th NT$0 | Remainder order rotates per expense key (SHA-256). The v0.9 rule is kept only for replaying legacy data |
| R4 | High | Greedy netting is called "appropriate for 11 members", but it was not minimal in 50% of random 11-member groups | Exact minimum-transfer solver for ≤ 20 unsettled members; greedy is kept as a labelled fallback |
| R5 | Medium | INV-03 (positive shares) is impossible when an amount is smaller than the participant count | Explicit `amount.too_small` rejection |
| R6 | Medium | Confirmation double-tap is not caught by `webhookEventId` de-duplication (each tap is a new event) | State-based idempotency on drafts and payments, plus a per-command idempotency key |
| R7 | Medium | Webhook redelivery is assumed, but LINE disables it by default and does not guarantee it | Design does not rely on it; durable inbox before 200; enable redelivery as a deployment step |
| R8 | Medium | The outbox "retry notification" can exhaust the free LINE quota: pushes are charged per group member | Reply API first (free); push only for financial confirmations, with a monthly budget and retry key |
| R9 | Medium | Group member enrolment is unspecified; listing group members needs a verified account | Self-enrolment with `/join <nickname>`; clear handling when LINE omits the user ID |
| R10 | Medium | Raw LINE user and group IDs would sit in the financial record | Keyed hashes (HMAC + server pepper) in the ledger; raw chat ID only where push needs it |
| R11 | Medium | Raw message text retention is a "should" with no mechanism | Inbox payloads are purged by compaction; logs carry IDs and outcomes only (tested) |
| R12 | Medium | "One database transaction" names no store; atomicity, crash recovery and corruption handling are undefined | Append-only checksummed journal: one record per command, fsync before acknowledge, torn-write recovery |
| R13 | Medium | Period closing by date would let back-dated expenses rewrite a closed period | Periods close at a ledger *sequence*; the expense date is informational |
| R14 | Medium | Members leaving the group is undefined | Deactivate only at zero balance with no open items; a returning member keeps their ID |
| R15 | Medium | Testnet guidance is out of date: testnet3 is deprecated and removed from Bitcoin Core 30 | regtest for automation; signet or testnet4 for demos; mainnet and testnet3 refused in code |
| R16 | Medium | FR-BTC-06 ("never add change to fee") conflicts with the dust rule | Dust absorbed into the fee is disclosed in the preview and forces manual review |
| R17 | Medium | A historical dependency risk: QBitNinja's public API | The chain adapter talks to a Bitcoin Core node you control (no node wallet needed) |
| R18 | Medium | Platform end-of-life: .NET 8 and 9 lost support on 2026-11-10 | Target .NET 10 LTS (supported to Nov 2028) |
| R19 | Low | Constant-time signature comparison is not required | `CryptographicOperations.FixedTimeEquals` on the decoded HMAC |
| R20 | Low | TWD minor unit: ISO 4217 says 2 decimals; the spec says integer TWD | Currency scale is explicit configuration, stored in the journal; TWD defaults to 0 |
| R21 | Low | TC-001 pins *which* member gets the extra dollar | Acceptance is "34/33/33, sums to 100, repeatable" |
| R22 | Low | Non-deterministic PSBTs: NBitcoin shuffles outputs and coins randomly by default (found by a failing test) | Shuffling kept but seeded from the transfer ID |
| R23 | Low | Key custody and the planned social key recovery are not in the spec | Added as roadmap requirement FR-BTC-13 with a recommended Bitcoin-native design |
| R24 | Low | The "150 transaction cases" evidence has no format or runner | Versioned case-file schema plus a data-driven test that replays every file |

---

## 2. Findings in detail

### R1 (High): a settlement plan contradicts itself after its first payment

**v0.9.** INV-09: "A plan calculated from ledger version *v* cannot be confirmed against a changed ledger without
recalculation." FR-SET-03 records each confirmed transfer as ledger entries.

**Problem.** Confirming transfer 1 of a 3-transfer plan changes the ledger version. Under INV-09, transfers 2 and 3 are
now stale and must be recalculated. The recalculated plan has new IDs, so the plan can never be completed as a unit.
The opposite reading, "ignore staleness", lets someone pay an outdated amount after a new expense.

**Resolution.**
- A plan is stale when a balance-changing event after its base sequence is **not** one of the plan's own payments.
- Staleness is checked when a debtor **claims a transfer from a plan**, because that is where the plan supplies the
  amount. A payment is a fact. Recording money that really moved is never blocked.
- One transfer of a plan can be claimed only once, unless the creditor rejects the claim.

**Built in:** `GroupLedger.IsPlanStale`, `ClaimPlanTransfer`. **Tests:** `Plan_survives_its_own_payments_and_settles_everyone`,
`TC011_plan_becomes_stale_when_ledger_changes_for_another_reason`, `A_plan_transfer_is_claimed_once_unless_rejected`, and
the pipeline test `TC011_paying_from_an_outdated_plan_is_refused`.

### R2 (High): who confirms a settlement?

**v0.9.** UR-09: "only after the responsible member confirms it". The role table says "confirm own settlement".

**Problem.** If "own settlement" means the debtor, a member can erase their own debt by typing "I paid". The member who
can verify a payment is the one who received it.

**Resolution.** These are domain rules:
1. The debtor's "I paid" (`/paid`) creates a **pending** claim. Balances do not move.
2. Only the creditor can confirm (Received) or reject (Not received) the claim.
3. The creditor can also record a receipt directly (`/got`). It posts immediately, because anyone may reduce what is
   owed *to them*.

**Tests:** `Debtor_claim_moves_nothing_until_creditor_confirms`, `Creditor_can_reject_a_claim_and_record_receipts_directly`,
the pipeline test `Settlement_plan_payment_and_creditor_confirmation` (a third member's tap on "Received" is refused), and
corpus case SET-002.

### R3 (High): the rounding rule is deterministic but unfair

**v0.9.** §7.3: sort by member ID and give `baseShare + 1` to the first `remainder` members. It calls this the
"largest-remainder rule".

**Problem.** With an equal split every fractional remainder is equal, so the rule always lands on the same members. In
an 11-member group the remainder is 0–10, so the member with the 11th ID **never** absorbs a rounding dollar and the
first ID absorbs one in about 10 of 11 expenses. A seeded simulation of 3,000 expenses of NT$11–10,000 split across 11
members gave these rounding dollars per member:

```
v0.9 rule:  2711, 2416, 2152, 1881, 1631, 1352, 1077, 791, 514, 245, 0
v1.0 rule:  1345, 1325, 1310, 1330, 1341, 1363, 1370, 1358, 1346, 1346, 1336
```

Over three years of real use the gap is real money, and it falls on the same people each time. The name is also
inaccurate: for an equal split this is not a largest-remainder method.

**Resolution.** v1.0 uses true largest-remainder (Hamilton) apportionment for weighted splits. Ties, which include every
equal split, go in member-ID order **rotated by an offset derived from SHA-256 of the expense key**. The result is still
deterministic: the same expense always gives the same shares, and the preview shows them before anyone confirms. The
v0.9 rule remains available as `RemainderPolicy.FirstByMemberId`, so legacy history can be replayed exactly.

**Built in:** `Allocator.ByWeights`. **Tests:** `Rotation_spreads_rounding_units_evenly_while_legacy_rule_is_biased`,
`Weighted_split_uses_largest_remainder`, and a 5,000-case property test (positive, exact sum, max−min ≤ 1).

### R4 (High): greedy netting is not a good default for this group size

**v0.9.** §8.2 says the greedy heap is "appropriate for an 11-member group" and correctly declines to call it globally minimal.

**Evidence.** The minimum number of transfers equals *k − p*, where *p* is the largest number of disjoint zero-sum
groups the unsettled members split into ([arXiv:1402.6556](https://arxiv.org/abs/1402.6556); Verhoeff,
[*Settling Multiple Debts Efficiently*](https://infedu.vu.lt/journal/INFEDU/article/612/file/pdf), 2004). The decision
version is NP-complete. Bultel et al. prove this in §2, Theorem 4
([FUN 2018](https://drops.dagstuhl.de/storage/00lipics/lipics-vol100-fun2018/LIPIcs.FUN.2018.13/LIPIcs.FUN.2018.13.pdf));
v0.9's citation is correct. NP-hard does not mean slow at this size, though. A dynamic program over the 2^k subsets is
exact and runs in microseconds for k = 11.

Real balances are often round numbers, so zero-sum subsets are common. In 2,000 random 11-member groups with balances in
NT$50 steps, greedy used more transfers than the minimum in **1,009 groups**, 1,167 extra transfers in total. A minimal
counterexample is balances −3, −2, −2, +3, +4: greedy needs 4 transfers and the optimum is 3.

**Resolution.**
- `DebtSimplifier.Plan` uses the **exact** solver when at most 20 members are unsettled. That is about 1M states, under
  a second. Within each zero-sum group it settles greedily, which is optimal there because such a group has no smaller
  zero-sum subset.
- Above 20 unsettled members it falls back to greedy and **says so**: `ProvablyMinimal = false`, and the chat shows
  "simplified (not proven minimal)".
- Both methods move the same total amount, the sum of the credits, because money never passes through an intermediary.

**Tests:** a property test over 1,500 random groups checks the exact count against an independent brute-force oracle
(enumeration of all set partitions), plus settlement replay, positive transfers, the volume bound, the k − 1 bound and
order-independence. Other tests cover the worked example from §8.3, the counterexample and the 20-member timing.

### R5 (Medium): INV-03 cannot hold for very small amounts

Splitting NT$2 among 3 people gives `baseShare = 0`. v1.0 requires every share to be at least one minor unit and rejects
the expense with `amount.too_small`, so the user sees why. (Corpus case ALLOC-002.)

### R6 (Medium): de-duplicating webhooks does not stop a double tap

Two taps on "Confirm" are two webhook events with **different** `webhookEventId`s. v1.0 specifies two layers:
1. **Per-command idempotency key.** Each command carries its event ID. A repeat returns the original result, which
   covers redelivery and re-processing after a crash.
2. **State-based idempotency.** Confirming a draft that is already posted, or a payment that is already posted, returns
   the existing outcome without a new effect.

**Tests:** `TC006_second_confirmation_has_no_effect`, `TC005_repeated_source_key_returns_original_result`, and the
pipeline tests `Expense_flow_preview_confirm_and_double_tap` and `TC012_reprocessing_or_redelivery_never_doubles_an_effect`.

### R7 (Medium): LINE webhook redelivery is opt-in

LINE's documentation says webhook redelivery is disabled by default, that the number and timing of retries are not
disclosed, and that redelivery is not guaranteed. A redelivered event keeps its `webhookEventId` and reply token and
sets `deliveryContext.isRedelivery`
([LINE: Receive messages](https://developers.line.biz/en/docs/messaging-api/receiving-messages/)).

**Resolution.** Correctness does not depend on redelivery:
- The endpoint returns 200 only after every event is **fsynced** to the inbox.
- Processing is asynchronous and survives restarts. Redelivery, when it happens, is de-duplicated.
- The deployment checklist says to turn on "Webhook redelivery" in the LINE Developers Console.

### R8 (Medium): replies are free, pushes cost quota

Reply API messages are free in Taiwan. The free 輕用量 plan includes 200 messages a month. From **2026-11-01** the mid
and high plans cost NT$1,000 and NT$1,400 a month, and free message counts are unchanged
([LINE Biz-Solutions Taiwan](https://tw.linebiz.com/column/LINEOA-2026-Price-Plan/)). Pushes are counted per recipient,
so one push to a group of 500 counts as 500 messages
([Alibaba Cloud LINE FAQ](https://help.aliyun.com/en/chatapp/user-guide/line-channel-faq)). For an 11-person group, a
naive "retry by push" outbox gets about 18 notifications a month before the free quota is gone. Reply tokens are
single-use and must be used within about a minute.

**Resolution** (`OutboxDispatcher`):
1. The reply is attempted while the token is fresh (configurable `ReplyWindow`, default 50 s).
2. If the reply fails or the token has expired, **only financially important messages** (posted, reversed, payment
   claims and confirmations) are pushed. Previews, help and balance replies are dropped, and the user can ask again.
3. A **monthly push budget** (default 150 recipients) is enforced before pushing.
4. Each push carries a deterministic `X-Line-Retry-Key` (a UUID derived from the message ID), so retrying never
   delivers twice. A 409 on retry counts as "already sent".
5. A failed delivery never touches the ledger.

**Tests:** the six `OutboxDispatcherTests` and the client status-mapping tests.

### R9 (Medium): enrolling group members

Getting the user IDs of all group members is limited to verified accounts. Profile data also depends on user consent,
and without consent the webhook carries no user ID
([LINE: Get user IDs](https://developers.line.biz/en/docs/messaging-api/getting-user-ids/),
[Alibaba Cloud LINE FAQ](https://help.aliyun.com/en/chatapp/user-guide/line-channel-faq)).

**Resolution.** Members self-enrol with `/join <nickname>`. The first member becomes coordinator. Nicknames are unique,
and reserved words (`all`, `me`) are refused. If LINE omits the user ID, the bot explains how to allow profile access
instead of failing silently.

### R10 (Medium): no raw LINE identifiers in the financial record

LINE user IDs are stable, per-provider identifiers. The ledger stores `IdentityRef = HMAC-SHA256(pepper, "user:"+id)`,
and the group's internal ID is derived the same way. The raw chat ID is kept only in the operations store, because the
push fallback needs it. **Test:** `Ledger_never_stores_raw_line_ids_and_ops_store_forgets_message_text`; the real-host
smoke run confirmed zero raw IDs in the journal.

### R11 (Medium): retention of message text

The inbox must hold a webhook payload until it is processed. After that, the operations store compacts: it keeps only
the event ID, for de-duplication over 7 days, and drops the payload. Compaction runs at start-up and after every 25
completions, so raw text lives on disk for at most about 25 events. Logs record event IDs, outcomes and error types,
never message text or LINE IDs. **Tests:** `Processed_payloads_are_purged_but_ids_retained_for_dedupe`,
`Logs_never_contain_message_text_or_line_ids` (runs against the real host).

### R12 (Medium): the persistence model was undefined

v0.9 asks for one database transaction per posting but defers "where was the ledger persisted" to §16. v1.0 specifies
the guarantees, and the rebuild meets them without a database server:

- **Event-sourced, append-only journal** per group. Each accepted command is **one** JSON record. The record is fsynced
  before the in-memory state changes, so atomicity (NFR-04) holds by construction.
- Every line carries a SHA-256 checksum prefix. A final line without a newline is a torn write from a crash: it is
  discarded and overwritten. Any other checksum failure is corruption, and loading stops.
- Replay re-checks every posting rule: zero-sum, lines equal to the rule that produced them, contiguous sequence numbers.
- Commands for one group run one at a time. The store sits behind `IEventJournal`, so a SQL adapter can replace it if
  the app ever needs more than one instance.

**Tests:** round-trip of all 13 event types, restart idempotency, torn write, edited record, 40 concurrent commands
without gaps, replay rejecting tampered lines and sequence gaps.

### R13 (Medium): periods must close by sequence, not date

If a period were "all expenses dated in November", a back-dated expense entered in December would change November after
it closed. v1.0 closes a period at a ledger **sequence number** with a snapshot of balances. The expense date
(`OccurredOn`) is only descriptive. Balances stay cumulative, so the snapshot is the next period's opening balance.
**Test:** `Closing_a_period_snapshots_balances_that_later_activity_does_not_change`.

### R14 (Medium): members leaving

A member can be deactivated only at zero balance and with no open drafts or pending payments. An inactive member cannot
be charged. If they `/join` again, they keep their member ID and history.
**Test:** `Members_leave_only_when_settled_and_inactive_members_cannot_be_charged`.

### R15 (Medium): Bitcoin test networks have changed

Testnet3 was deprecated in Bitcoin Core 28 and removed in v30, after its difficulty-reset exploit and block storms.
Testnet4 (BIP 94, Core 28+) and signet are the public options
([BIP 94](https://bips.dev/94/), [testnet guide](https://www.spark.money/tools/bitcoin-testnet-faucet-guide)).
v0.9 also says testnet coins "have no real-world value". Testnet3 coins did gain speculative value, which was one of the
reasons for the reset.

**Resolution.** `PocNetwork.Resolve` accepts only `regtest`, `signet` and `testnet4`. Mainnet keys and addresses are
refused. NBitcoin 10.0.21 supports testnet4. One limitation is now documented: signet and testnet4 addresses share the
`tb1` prefix, so an address alone cannot tell them apart.

**Verified:** the end-to-end test ran against a real **Bitcoin Core v32.1** regtest node built for this review. It
mined, built a PSBT, signed, verified, broadcast, observed the 0-confirmation state, mined and confirmed.

### R16 (Medium): dust change versus "never add change to fee"

A change output below the dust threshold cannot be relayed, so NBitcoin folds it into the fee. v1.0 keeps the intent
of FR-BTC-06, which is no *silent* loss. The preview reports `DustAddedToFeeSats` and the proposal requires manual review.
**Test:** `Dust_change_is_disclosed_not_silently_burned`.

### R17 (Medium): chain data source

If the original POC used `QBitNinja.Client` and QBitNinja's public servers for UTXOs or
broadcasting, that dependency is fragile: a long-standing issue reports the testnet server broken, and the maintainer
pointed users to other services ([QBitNinja #110](https://github.com/MetacoSA/QBitNinja/issues/110)). v1.0 uses
`IChainClient`, implemented against a Bitcoin Core node you control. It gets UTXOs from `scantxoutset`, needs no node
wallet, and needs `-txindex` for confirmation lookups.

### R18 (Medium): .NET version

.NET 8 and .NET 9 reached end of support on **2026-11-10**. .NET 10 is the LTS release, supported to November 2028
([.NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)). The rebuild targets `net10.0`, and
`global.json` allows any 10.0 feature band.

### R19 (Low): constant-time signature comparison

LINE's documentation does not specify how to compare signatures
([Verify webhook signature](https://developers.line.biz/en/docs/messaging-api/verify-webhook-signature/)).
v1.0 decodes the base64 header, checks that it is 32 bytes, and compares with `CryptographicOperations.FixedTimeEquals`.
**Tests:** valid signature, reformatted JSON body rejected, missing or malformed header, wrong secret.

### R20 (Low): TWD minor unit

ISO 4217 gives TWD two decimal places, while everyday settlement uses whole dollars. The currency **scale** is explicit
group configuration, recorded in the journal's `GroupOpened` event. The default is TWD with scale 0, and the parser then
rejects decimals. A group can be opened with scale 2 if cents ever matter.

### R21 (Low): TC-001 wording

With rotation, which member receives 34 depends on the expense key. The acceptance criterion becomes: shares are
{34, 33, 33}, they sum to 100, and they are identical on every run. The corpus pins the legacy rule separately.

### R22 (Low): deterministic PSBT construction

A determinism test failed intermittently. NBitcoin's `TransactionBuilder` shuffles outputs and coin selection with an
unseeded `Random`. Shuffling is good for privacy, because the change position does not give it away. The fix keeps the
shuffle but seeds it from the transfer ID, so the same reviewed input always gives the same PSBT. The test is stable
over repeated runs.

### R23 (Low): key custody and social recovery

v0.9 covers disposable test keys only. Since social key recovery is planned for Maijichain, v1.0 adds **FR-BTC-13
(roadmap)**. Prefer a Bitcoin-native recovery path, such as a descriptor or miniscript policy like
`or(pk(owner), and(thresh(k, friend₁…friendₙ), older(N)))`, over Shamir-style seed splitting. With the native path the
key is never rebuilt on one device, the timelock gives the owner time to object, and the policy can be tested on regtest
like the rest of the POC. This review does not build it.

### R24 (Low): the "150 transaction cases" claim

v1.0 defines a versioned case-file schema (`tests/fixtures/transaction-cases/README.md`) and a data-driven test that
replays every `*.cases.json` file. Each case checks expected balances, error codes, plan size, zero-sum after every step
and replay equality. The repository ships **15 synthetic cases**, clearly labelled. The historical 150 cases should be
anonymised into the same format before the claim is published.

---

## 3. Related projects and what they changed in this review

| Project | What it is | Lesson applied |
|---|---|---|
| [Splitwise](https://splitwise.uservoice.com/knowledgebase/articles/107220-what-does-the-simplify-debts-setting-do) | Commercial; "simplify debts" is a per-group setting that restructures debts | Show the plan's quality ("minimum possible" vs "simplified"), not just a list |
| [Spliit](https://github.com/spliit-app/spliit) | MIT, Next.js + Prisma; no accounts; uneven splits | The no-account model matches a LINE group; accounts are not needed for trust if the ledger is auditable |
| [SplitPro](https://github.com/oss-apps/split-pro) | MIT, self-hosted Splitwise replacement | Self-hosting and data ownership are expected features in this space |
| [SplitEase](https://github.com/limitching/SplitEase) | React/Node; LINE login and LINE bot notifications; Dinic max-flow simplification | Max-flow simplification does not guarantee the minimum count (R4) |
| [Lightsplit](https://lightsplit.com/blog/lightsplit-line-bot-in-group-chat-split-bills) | 2026 bot for LINE and Telegram groups; mention-based commands; multi-currency auto-conversion | Same interaction surface. Maijichain's differentiators: an auditable append-only ledger, provably minimal settlement, idempotent ingress |
| [Brosplit write-up](https://abhishek-portfolio-liard-two.vercel.app/writing/brosplit-debt-simplification) | Argues against cross-currency simplification | Supports INV-08: one ledger per currency, never netted together |
| [Verhoeff 2004](https://infedu.vu.lt/journal/INFEDU/article/612/file/pdf), [Bultel et al. 2018](https://drops.dagstuhl.de/storage/00lipics/lipics-vol100-fun2018/LIPIcs.FUN.2018.13/LIPIcs.FUN.2018.13.pdf), [arXiv:1402.6556](https://arxiv.org/abs/1402.6556) | Theory of settling debts | Zero-sum partition gives an exact and fast solver for small groups (R4) |
