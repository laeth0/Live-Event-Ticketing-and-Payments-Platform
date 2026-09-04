# 10 · Observability Requirements

You cannot claim a concurrency design works if you cannot see it working. Everything in this
section exists so that during the Phase-7 load test you can *watch* the contention and explain
it, rather than inferring it from a latency graph.

Stack: OpenTelemetry .NET → Prometheus → Grafana, plus `postgres_exporter` for the server-side
views. All of it runs in `docker-compose.yml`.

---

## 10.1 Application metrics

Every metric carries `unit_of_work` (e.g. `hold_seats`, `pay_wallet`, `refund`, `reaper_batch`)
and, where relevant, `strategy` (`Naive|Serializable|GuardRow|Counter`) so the Phase-7
comparisons are queries rather than spreadsheets.

| ID | Metric | Type | Labels | Why it matters |
|---|---|---|---|---|
| **M-1** | `concourse_tx_duration_seconds` | histogram | `unit_of_work`, `isolation`, `outcome` | The §3.1 budget (avg ≤ 25 ms). A rising tail means transactions are getting wider — the first symptom of a boundary regression. Includes retry waits in a separate `_total_with_retries` variant so you can see the two apart. |
| **M-2** | `concourse_tx_total` | counter | `unit_of_work`, `isolation`, `outcome` (`committed`, `rolled_back`, `business_rejected`) | Rollback ratio per unit of work — far more actionable than the database-wide number. |
| **M-3** | `concourse_tx_retry_attempts` | histogram | `unit_of_work`, `sqlstate` | Attempts used per **success**. Healthy is a spike at 0–1. A fat tail is a hotspot. |
| **M-4** | `concourse_tx_retries_exhausted_total` | counter | `unit_of_work` | Every increment is a user-visible `503`. **Target: zero.** Any non-zero value is a design defect, never a reason to raise `maxAttempts`. |
| **M-5** | `concourse_tx_conflicts_total` | counter | `unit_of_work`, `sqlstate` (`40001`/`40P01`), `conflict_kind` (`concurrent_update`/`rw_dependency`) | Splits first-updater-wins from SSI cycles — **different bugs, different fixes**. Derived from the exception message, which is why the tests assert on it. |
| **M-6** | `concourse_lock_wait_seconds` | histogram | `unit_of_work`, `resource` | Time spent inside the explicit `FOR UPDATE` statements. Rising = a hot row; compare against M-5 to decide "lock more, abort less" or the reverse. |
| **M-7** | `concourse_business_rejections_total` | counter | `code` (`sold_out`, `customer_limit_exceeded`, `insufficient_funds`, `seat_unavailable`, `promo_exhausted`, `last_payment_route`, `refund_exceeds_capture`) | These are *correct outcomes*. Tracking them separately keeps them out of the error budget and shows the invariants doing their job. |
| **M-8** | `concourse_idempotency_replays_total` | counter | `endpoint` | How often clients retry. A spike usually precedes a latency incident. |
| **M-9** | `concourse_outbox_unpublished_age_seconds` | gauge | — | Age of the oldest unpublished row. The single best health signal for the outbox. |
| **M-10** | `concourse_outbox_backlog` | gauge | — | Count of unpublished rows. |
| **M-11** | `concourse_worker_claim_batch_size` | histogram | `worker` | If the reaper consistently claims 0, its index or predicate is wrong. If it always claims the full limit, it is behind. |
| **M-12** | `concourse_worker_stuck_jobs` | gauge | `kind` | Rows in `processing` past `locked_at + 5 min` — crashed workers. |
| **M-13** | `concourse_invariant_check` | gauge (0/1) + `_duration_seconds` | `invariant` (`INV-1`…`INV-12`) | Continuous proof. Scraped from a background run of the §7.6 checker. |
| **M-14** | `concourse_psp_call_duration_seconds`, `concourse_psp_outcome_total` | histogram, counter | `outcome` | Confirms the PSP call is outside any transaction: M-14's p99 must **not** appear inside M-1's p99 for `pay_card`. That comparison is the boundary regression test. |

### Instrumentation rule

Metrics are emitted by `TransactionRetry` and the HTTP/worker middleware — **not** sprinkled
through handlers. One place to emit, one place to change, and no handler can forget.

---

## 10.2 Structured logs

Log **events**, not sentences. Every entry carries `traceId`, `unitOfWork`, `actorId`,
`idempotencyKey` (hashed), and `attempt`.

| Event | Level | Fields | When |
|---|---|---|---|
| `TransactionConflict` | Information | `sqlstate`, `conflictKind`, `attempt`, `waitMs`, `unitOfWork` | Every `40001`/`40P01`. **Information, not Warning** — it is expected behaviour, and logging it as an error trains people to ignore errors. |
| `TransactionRetriesExhausted` | Error | + `attempts`, `lastMessage`, `lastDetail` | Exhaustion. Include the server's `DETAIL` — for deadlocks it names both statements. |
| `DeadlockDetected` | Warning | + full `DETAIL`, `blockingPids` | Every `40P01`, even when the retry succeeds; a rising rate is a lock-ordering bug. |
| `BusinessRuleRejected` | Information | `code`, `entityId` | Every `409`/`422` from a business rule. |
| `IdempotencyReplay` | Information | `endpoint`, `keyHash`, `originalStatus` | Replay served from the stored response. |
| `IdempotencyFingerprintMismatch` | Warning | `endpoint`, `keyHash` | A client bug or an attack. |
| `OutboxPublishFailed` | Warning | `outboxId`, `attempts`, `nextAttemptAt`, `error` | Publisher failure; escalates to Error after 10 attempts. |
| `SagaStranded` | Warning | `paymentId`, `status`, `ageSeconds` | Reconciler found a payment stuck past its deadline. |
| `SagaCompensated` | Warning | `paymentId`, `action` | A compensating action ran — always worth a human glance. |
| `InvariantViolated` | **Critical** | `invariant`, `sampleRows` | Any INV check failing. This should never happen; if it does, everything else is secondary. |
| `LongTransaction` | Warning | `pid`, `openForMs`, `query` | Sampled from `pg_stat_activity` by the ops scraper. |

**Never logged:** card tokens, full emails, wallet balances at `Information`, raw request
bodies containing money movements. The idempotency key is logged **hashed** — it is
client-supplied and can be guessed.

---

## 10.3 PostgreSQL monitoring

Scraped every 10 s by an ops background service and exposed on `/metrics`, and available
on demand through `GET /ops/locks` and `GET /ops/transactions`.

```sql
-- M-DB1 rollback ratio (climbing = 40001 storm, deadlocks, or a deploy throwing mid-transaction)
SELECT xact_commit, xact_rollback,
       round(100.0 * xact_rollback / NULLIF(xact_commit + xact_rollback, 0), 2) AS rollback_pct
FROM pg_stat_database WHERE datname = current_database();

-- M-DB2 deadlocks since stats reset
SELECT deadlocks, temp_files, blks_hit, blks_read
FROM pg_stat_database WHERE datname = current_database();

-- M-DB3 VACUUM-horizon health: oldest transaction and oldest snapshot
SELECT max(now() - xact_start)  AS oldest_xact,
       max(age(backend_xmin))   AS oldest_snapshot_xid_age
FROM pg_stat_activity WHERE state <> 'idle';

-- M-DB4 idle-in-transaction offenders
SELECT pid, usename, application_name, now() - state_change AS idle_for, left(query, 200) AS query
FROM pg_stat_activity
WHERE state = 'idle in transaction' AND now() - state_change > interval '5 seconds';

-- M-DB5 current blocking tree
SELECT a.pid, now() - a.query_start AS waited, left(a.query,200) AS blocked_query,
       pg_blocking_pids(a.pid) AS blocked_by
FROM pg_stat_activity a
WHERE cardinality(pg_blocking_pids(a.pid)) > 0;

-- M-DB6 ungranted locks with the object
SELECT pid, locktype, mode, granted, relation::regclass, transactionid
FROM pg_locks WHERE NOT granted;

-- M-DB7 SSI predicate-lock pressure — page/relation granularity means ESCALATION,
--        which causes false-positive 40001s
SELECT mode, granted, coalesce(relation::regclass::text,'-') AS rel,
       CASE WHEN tuple IS NOT NULL THEN 'tuple'
            WHEN page  IS NOT NULL THEN 'page'
            ELSE 'relation' END AS granularity,
       count(*)
FROM pg_locks WHERE mode = 'SIReadLock'
GROUP BY 1,2,3,4;

-- M-DB8 HOT update ratio on the hot rows (low ratio ⇒ an index is causing churn)
SELECT relname, n_tup_upd, n_tup_hot_upd,
       round(100.0 * n_tup_hot_upd / NULLIF(n_tup_upd,0), 1) AS hot_pct,
       n_dead_tup, last_autovacuum
FROM pg_stat_user_tables
WHERE relname IN ('ticket_type_inventory','account_balance','event','promo_code');

-- M-DB9 wraparound headroom
SELECT datname, age(datfrozenxid) AS xid_age FROM pg_database ORDER BY xid_age DESC;

-- M-DB10 index usage — proves the partial claim indexes are actually being used
SELECT relname, indexrelname, idx_scan, idx_tup_read
FROM pg_stat_user_indexes
WHERE indexrelname IN ('hold_reapable_idx','outbox_pending_idx','job_claimable_idx',
                       'payment_stranded_idx');
```

### Required server settings

| Setting | Value | Reason |
|---|---|---|
| `log_lock_waits` | `on` | Logs any wait longer than `deadlock_timeout` — the primary contention forensic |
| `log_min_duration_statement` | `200ms` | Catches the slow statements inside your transactions |
| `deadlock_timeout` | `1s` (prod), `200ms` (tests) | Default is right for production |
| `idle_in_transaction_session_timeout` | `15s` (role level) | Kills leaked transactions before they pin the horizon |
| `statement_timeout` | `5s` (app role) | Bounds the blast radius of a bad query |
| `lock_timeout` | `3s` (app role), `3s` explicitly in migrations | Converts a stuck lock into a fast retryable error |
| `default_transaction_isolation` | `read committed` | Leave it; levels are chosen per unit of work |
| `max_pred_locks_per_transaction` | raise only after observing escalation in M-DB7 | Phase-7 exercise |
| `track_io_timing` | `on` | Meaningful `pg_stat_statements` |
| `shared_preload_libraries` | `pg_stat_statements` | Statement-level attribution |

---

## 10.4 Dashboards

Three Grafana boards. Build them in Phase 7 *before* the load tests, so the first load test is
also the first time you watch them.

**Board 1 — Correctness**
INV-1…INV-12 status tiles (M-13) · business rejections by code (M-7) · money conservation
(`Σ journal_entry` should be a flat line at 0) · tickets issued vs. inventory sold · outbox
backlog and oldest unpublished age (M-9, M-10).

**Board 2 — Concurrency**
Conflicts/s split by `concurrent_update` vs `rw_dependency` (M-5) · retry-attempt histogram
heatmap (M-3) · retries exhausted (M-4) · deadlocks/min (M-DB2) · lock wait p99 (M-6) ·
blocking-tree depth (M-DB5) · SIReadLock granularity mix (M-DB7) · **strategy comparison panel**
filtered by the `strategy` label — the panel that produces your Phase-7 tables.

**Board 3 — Database health**
Rollback ratio (M-DB1) · oldest transaction and oldest snapshot age (M-DB3) · idle-in-transaction
count (M-DB4) · HOT update ratio and dead tuples (M-DB8) · transaction duration p50/p95/p99
(M-1) · connection pool utilisation · wraparound headroom (M-DB9).

---

## 10.5 Alerts and runbook

| Alert | Condition | Severity | First action |
|---|---|---|---|
| **Invariant violated** | any M-13 = 0 | **Page** | Freeze the affected write path (feature flag), snapshot the offending rows, run the detection query, find the interleaving. This is data corruption, not a latency issue. |
| **Retries exhausted** | M-4 > 0 over 5 min | **Page** | Identify the `unit_of_work` label → find the hot row → apply the ladder: can it be a constraint? an atomic write? a lock? Do **not** raise `maxAttempts`. |
| **Deadlock rate** | M-DB2 rate > 0.05 % of transactions | High | Read the `DETAIL` from `DeadlockDetected` logs; it names both statements. Find the two paths taking locks in different orders and impose the canonical order. |
| **Conflict storm** | M-5 rate > 3× the 1-hour baseline | High | Check whether it is `concurrent_update` (write/write on a hot row → lock or atomic write) or `rw_dependency` (SSI → check for predicate-lock escalation in M-DB7 first, it may be false positives). |
| **Long transaction** | M-DB3 `oldest_xact` > 5 min | High | `GET /ops/transactions`, find the `pid` and query. Usually a leaked transaction or a report that escaped the 30-second rule. |
| **Idle in transaction** | M-DB4 count > 5 for 1 min | High | A missing `await using`, an error path without rollback, or external I/O inside a transaction. Grep the handler named in the query. |
| **Snapshot age** | `age(backend_xmin)` > 5 000 000 | Medium | Same causes; the consequence is database-wide bloat, so it also degrades everything else. |
| **Rollback ratio** | M-DB1 > 3× baseline | Medium | Correlate with M-2 by `unit_of_work` — a deploy throwing mid-transaction looks identical to a conflict storm on this metric alone. |
| **Outbox backlog** | M-9 > 60 s | High | Is the publisher running? Are consumers erroring? Check `OutboxPublishFailed` and `attempts`/`last_error`. Never "fix" it by deleting rows. |
| **Stranded sagas** | M-12 or stranded payments > 0 for 5 min | High | The reconciler is down or the PSP is unreachable. Payments in `authorizing` are money at risk. |
| **SIRead escalation** | M-DB7 shows `page`/`relation` rising | Medium | Predicate locks are escalating → false-positive `40001`s. Narrow the read set with a better index, or raise `max_pred_locks_per_transaction`. |
| **HOT ratio drop** | M-DB8 `hot_pct` < 80 % on a hot table | Low | Someone added an index to a hot row. Ask whether it is worth the write amplification. |
| **Wraparound** | M-DB9 `xid_age` > 1.5 × 10⁹ | High | Autovacuum is not keeping up with freezing. |

### Runbook entries you must actually write

1. **"The invariant checker is red."** Detection query per invariant, how to identify affected
   rows, and the repair procedure — including the documented one for INV-6 (recompute
   `account_balance` from `journal_entry` inside one transaction, while blocking that account's
   writes with `FOR UPDATE`).
2. **"Everything is slow and `pg_locks` is full of ungranted rows."** The blocking-tree query,
   how to read it, and when terminating a backend (`pg_terminate_backend`) is justified.
3. **"We are getting 503s from one endpoint."** Retry-exhaustion triage down the ladder.
4. **"A payment is stuck in `authorizing`."** How to query the PSP by idempotency key manually
   and how to run the compensation.
5. **"A deploy needs a migration on a hot table."** `lock_timeout`, `CONCURRENTLY`, `NOT VALID`
   + `VALIDATE`, and why a pending `ALTER TABLE` blocks even `SELECT`s queued behind it.
