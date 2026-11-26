# Transaction case corpus

Every `*.cases.json` file in this folder is replayed by `CaseCorpusTests` (Maijichain.Domain.Tests).
Use this format to turn the historical "150 transaction cases" into an auditable, runnable suite.

**Never commit real names, LINE IDs or real descriptions.** Use short member aliases and generic descriptions.

## File shape

```json
{
  "corpus": "historical-2023-2026",
  "synthetic": false,
  "note": "Anonymised from the group's records with every participant's consent.",
  "cases": [ { …case… } ]
}
```

## Case shape

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable ID, e.g. `HIST-042` |
| `category` | yes | `allocation`, `rounding`, `netting`, `correction`, `settlement`, `integration`, `failure` |
| `title` | yes | One line |
| `remainderPolicy` | no | `RotateByExpenseKey` (default) or `FirstByMemberId` (v0.9 rule, for legacy data) |
| `members` | yes | Nicknames, in join order; the first is coordinator |
| `steps` | yes | Operations, applied in order (below) |
| `expect.balances` | no | Nickname → expected balance in minor units; `{}` means everyone is zero |
| `expect.transfers` | no | Exact size of the settlement plan |
| `expect.maxTransfers` | no | Upper bound on the plan size |

Every case also checks, automatically: zero-sum after each step, the plan settles everyone, and journal replay rebuilds
identical balances.

## Step operations

| `op` | Fields | Effect |
|---|---|---|
| `expense` | `by`, `amount`, `split` (`equal`/`exact`/`weighted`), `participants` or `shares` or `weights`, optional `payer` | Draft and confirm an expense |
| `reverse` | `by`, `expense` (e.g. `E1`), `reason` | Reverse a posted expense |
| `receipt` | `by` (creditor), `from`, `amount` | Creditor records money received |
| `claim` | `by` (debtor), `to`, `amount` | Debtor claims a payment (pending) |
| `confirm-payment` | `by`, `payment` (e.g. `P1`) | Creditor confirms |
| `reject` | `by`, `payment` | Creditor rejects |
| `plan` | `by` | Create a settlement plan |
| `pay-plan` | `plan`, `transfer` (1-based) or `all: true` | Claim and confirm plan transfers |

Any step may carry `"error": "<code>"` (e.g. `split.mismatch`, `plan.stale`, `auth.denied`). The step must then fail
with exactly that code and leave balances unchanged.
