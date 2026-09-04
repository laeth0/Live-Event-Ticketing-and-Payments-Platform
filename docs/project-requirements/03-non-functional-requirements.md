# 3 · Non-Functional Requirements

These are the production constraints the design has to satisfy. They exist to force real
engineering decisions: every number below makes at least one naive implementation fail.

---

## 3.1 Performance requirements

### Reference workload — "the onsale"

| Parameter | Value |
|---|---|
| Event capacity | 4 000 (2 500 GA across 3 tiers, 1 500 reserved seats) |
| Concurrent customers at onsale | **5 000** |
| Peak arrival rate | **1 500 req/s** for 30 s, then decaying |
| Hottest single row | one `ticket_type_inventory` row taking **~400 writes/s** |
| Hottest predicate | `seat_hold` on ~200 front-section seats |
| Steady-state background | 4 outbox publishers, 3 hold reapers, 1 PSP reconciler |
| Connection budget | **60** PostgreSQL connections total (API pool 40, workers 20) |

The connection budget is deliberately small. It forces short transactions: at 1 500 req/s with
40 connections, the average write transaction must complete in **under 25 ms** or the pool
saturates. That single number invalidates "transaction per HTTP request" before you write a
line of code.

### Latency targets (server-side, excluding client network)

| Endpoint class | p50 | p95 | p99 | Hard timeout |
|---|---|---|---|---|
| `GET` availability / read models | 15 ms | 60 ms | 120 ms | 2 s |
| `POST` hold (GA or seats) | 20 ms | 90 ms | **250 ms** | 3 s |
| `POST` checkout | 25 ms | 120 ms | 300 ms | 3 s |
| `POST` pay (wallet) | 30 ms | 150 ms | 400 ms | 5 s |
| `POST` pay (card, incl. PSP) | 350 ms | 900 ms | 1 800 ms | 10 s |
| Admin / catalog writes | 40 ms | 200 ms | 500 ms | 5 s |

p99 for holds includes retry waits. If your `SERIALIZABLE` variant blows the 250 ms p99 under
contention while the `EXCLUDE`-constraint variant does not, that *is* the finding — record it.

### Throughput targets

| Scenario | Target |
|---|---|
| Seat holds against 200 contended seats | ≥ 400 successful holds/s, **0** double-holds |
| GA holds against one tier row | ≥ 400 reservations/s at `READ COMMITTED` + atomic write |
| Wallet payments | ≥ 200/s with ≤ 1 % transactions needing a retry |
| Outbox drain | ≥ 2 000 events/s across 4 workers, linear scaling to 4 workers |
| Hold reaper | ≥ 5 000 holds/min across 3 workers; adding workers must **increase** throughput (this fails without `SKIP LOCKED` — measure both) |

### Database-side budgets

| Metric | Budget |
|---|---|
| Average write-transaction duration | ≤ 25 ms |
| p99 write-transaction duration | ≤ 200 ms |
| Longest permitted transaction (any) | 5 s (`statement_timeout = 5s` for the app role) |
| `idle in transaction` | ≤ 15 s (`idle_in_transaction_session_timeout = 15s`), alert at 5 s |
| Serialization failure rate (`40001`) | ≤ **2 %** of transactions on `SERIALIZABLE` paths at peak |
| Deadlock rate (`40P01`) | ≤ **0.05 %** of transactions; any sustained rate is a lock-ordering bug |
| Retries exhausted | **0** in steady state; any occurrence is a paging alert |
| Rollback ratio (`pg_stat_database`) | ≤ 5 % excluding deliberate anomaly labs |
| `age(backend_xmin)` of the oldest snapshot | ≤ 5 million xids; alert above |

### Scalability requirement

Adding a second and third API instance must increase throughput roughly linearly for holds and
payments, and must **not** increase the `40001` rate super-linearly. If it does, the design has
a hot row that needs the Phase-7 append-only redesign rather than more replicas.

---

## 3.2 Consistency requirements

### Hard invariants (never observably violated, at any load, under any interleaving)

These restate §1.4 with the observability contract attached.

| ID | Invariant | How it is checked at runtime |
|---|---|---|
| INV-1 | A reserved seat appears on at most one non-void ticket per event | `GET /ops/invariants` → `SELECT event_id, seat_id, count(*) … HAVING count(*) > 1` must be empty |
| INV-2 | No two overlapping active holds on one event seat | guaranteed by `EXCLUDE`; verified by a query that must return 0 rows |
| INV-3 | `reserved + sold ≤ allocated` for every ticket type | `CHECK`; verified by scan |
| INV-4 | Per-customer ticket cap respected | verified by aggregate query vs `limit_snapshot` |
| INV-5 | Every journal balances to zero; no negative wallet balance | deferred constraint trigger + `CHECK`; verified by `SELECT journal_id FROM journal_entry GROUP BY 1 HAVING sum(amount_minor) <> 0` |
| INV-6 | `account_balance.balance_minor = SUM(journal_entry.amount_minor)` per account | full reconciliation query; **the single most valuable test in the project** |
| INV-7 | `refunded_minor ≤ captured_minor` per payment | `CHECK`; verified by scan |
| INV-8 | Promo budget and per-customer redemption respected | `CHECK` + `UNIQUE`; verified by scan |
| INV-9 | Every `onsale` event has ≥ 1 enabled payment route | verified by scan; **cannot** be a `CHECK` — this is why it needs SSI or a guard lock |
| INV-10 | One effect per idempotency key | verified by scan for duplicate `(endpoint, key)` effects |
| INV-11 | Payouts ≤ settled revenue − reserve | verified by per-organizer aggregate |
| INV-12 | Each committed order emits its events at least once and is applied at most once downstream | outbox unpublished-age metric + consumer dedup table |

**Money conservation** is the umbrella statement: for any window, `Σ` of all
`journal_entry.amount_minor` across all accounts is exactly `0`. No concurrency bug can hide
from that.

### Soft consistency (explicitly allowed to be stale)

Documented as such in the API contract, so nobody builds a correctness assumption on them:

| Data | Staleness allowed | Why it is safe |
|---|---|---|
| `GET /events/{id}/availability` counts | up to 2 s | advisory only; the hold endpoint re-checks authoritatively |
| Seat map states | up to 2 s | same |
| `event_sales_daily` rollups | up to 60 s | reporting only, never a decision input |
| Outbox delivery | at-least-once, seconds of lag | consumers deduplicate |
| Search/list endpoints | per-statement snapshot (`READ COMMITTED`) | no invariant depends on them |

### Consistency rules for reads

1. Any response assembled from **more than one query** whose parts could contradict each other
   must run inside one `REPEATABLE READ` transaction (F-6, F-21 statement, ops invariant
   report).
2. Long reports (> 1 s) use `SERIALIZABLE READ ONLY DEFERRABLE` so they never abort and never
   cause a writer to abort.
3. No read transaction may stay open longer than 30 s on the primary.

---

## 3.3 Reliability requirements

### Retry behaviour

| Rule | Requirement |
|---|---|
| Retryable SQLSTATEs | **only** `40001`, `40P01`, `40000` |
| Never retried | `23505`, `23503`, `23514`, `23P01`, `22P02`, `25P02`, `55P03`, and every domain exception |
| Retry unit | the **entire** unit of work — reads, decision and writes — on a **fresh connection and transaction** |
| Attempts | bounded, default **6** |
| Backoff | exponential with **full jitter**: `wait = random(0, min(cap, base · 2^attempt))`, `base = 20 ms`, `cap = 2 s` |
| Exhaustion | throw `TransactionRetriesExhaustedException` → HTTP `503` + `Retry-After: 1`; increment `M-4`; log the last conflict with the unit-of-work name |
| Placement | side effects (publish, email, PSP calls) happen **outside** the retried block, always |
| Lock waits | `SET LOCAL lock_timeout = '3s'` on user-facing writes that touch hot rows, so a stuck lock becomes a fast retryable `55P03` rather than a pile-up |

Implementation lives in exactly one class (`TransactionRetry`). Opening a write transaction
anywhere else in the codebase is a review failure — this is enforced by an architecture test
(T-U6).

### Duplicate request handling

| Layer | Mechanism |
|---|---|
| Client → API | `Idempotency-Key` header required on every non-idempotent `POST` (checkout, pay, top-up, transfer, refund, payout) |
| Storage | `ops.idempotency_key(key PK, endpoint, actor_id, request_fingerprint, state, response_status, response_body, expires_at)` |
| Replay, same body | return the **stored response** with `Idempotency-Replayed: true`; never re-execute |
| Replay, different body | `422 Unprocessable Entity` — the key is bound to a request fingerprint |
| Concurrent replay (two in flight) | first inserts the key in state `in_progress`; the second gets `23505` → `409 Conflict, Retry-After: 1` |
| Internal retry (`40001`) | body must be idempotent by construction: every real-world-effect `INSERT` has a natural key + `ON CONFLICT DO NOTHING` |
| Downstream consumers | `ops.processed_event(consumer, event_id)` primary key; consumers are idempotent |
| Retention | idempotency keys kept 24 h, then purged by a job |

### Failure handling and recovery

| Failure | Required behaviour |
|---|---|
| API process crashes mid-request | No partial effect. Uncommitted transaction rolls back; committed work is durable and the client's retry with the same key is absorbed. |
| Crash between `COMMIT` and event publish | Outbox row is already committed; the publisher delivers it on restart. Test T-C8b kills the process in exactly this window. |
| Crash between PSP authorize and the recording transaction | Payment sits in `authorizing`; the reconciler queries the PSP by idempotency key within 60 s and either completes or voids. Test T-C8b. |
| `COMMIT` returns an error / connection drops during commit | Outcome is **unknown** — never assume rollback. Resolve by natural-key lookup, not by blind retry. |
| PostgreSQL restarts | API reconnects via the pool; in-flight transactions are lost, not half-applied; workers resume from durable state. |
| Worker dies holding a claim | Row locks release at disconnect. For the `processing`-state pattern, a reaper returns rows whose `locked_at` is older than 5 minutes back to `ready`. |
| Outbox consumer down | Backlog grows; alert on `M-9` unpublished age > 60 s; no data loss. |
| Retry exhaustion on a hot row | Serve `503` with `Retry-After`, alert, and treat it as a design defect to be fixed by a constraint/lock/redesign — never by raising `maxAttempts`. |

### Recovery objectives

| Objective | Target |
|---|---|
| RPO | 0 committed transactions lost (`synchronous_commit = on`, the default) |
| RTO (single-node dev/demo) | < 60 s: container restart + WAL replay, no manual repair |
| Data repair | Every invariant has a detection query; INV-6 additionally has a documented repair procedure (recompute the materialised balance from the journal inside one transaction) |
| Migration safety | Every migration runs with `lock_timeout = 3s`; indexes created `CONCURRENTLY`; constraints added `NOT VALID` then `VALIDATE` |

### Availability and degradation

- The hold and payment paths must keep working when the outbox publisher, reconciler or rollup
  worker is down. Workers are never in the request path.
- If the PSP is unavailable, wallet payments must continue to work — this is what the
  per-event `payment_route` table (INV-9) is for.
- Read endpoints must not be blocked by write contention (this is MVCC's promise; a test
  asserts availability p99 stays inside budget during the onsale load test).

---

## 3.4 Operational and security constraints

| Constraint | Requirement |
|---|---|
| PostgreSQL role | App connects as a non-superuser `concourse_app` with `SET default_transaction_isolation = 'read committed'`, `statement_timeout = 5s`, `idle_in_transaction_session_timeout = 15s`, `lock_timeout = 3s` set at role level |
| Migrations role | Separate `concourse_migrator` role; the app role has no DDL rights |
| Secrets | Connection strings from environment/user-secrets only; never in source, never in logs |
| Input | All SQL parameterised; all monetary amounts validated as positive integers; all ids validated as UUIDs before reaching SQL |
| PII in logs | Never log full card data (the fake PSP takes a token), emails, or wallet balances at `Information` level |
| Pooling | `NpgsqlDataSource` as the single app-wide factory; `Max Pool Size` set explicitly and below the connection budget; document the PgBouncer transaction-pooling implications even though the project does not require PgBouncer |
| Time | Server and containers UTC; all comparisons use `now()` (transaction timestamp) — never `clock_timestamp()` inside a transaction that must be internally consistent |
