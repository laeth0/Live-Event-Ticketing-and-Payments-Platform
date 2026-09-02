# 8 · Implementation Roadmap

Eight phases, beginner → advanced. **Do not skip ahead.** Phase 3 deliberately asks you to
write and keep the *broken* implementations, because Phase 4 and 5 are about replacing them
with fixes whose failure modes you have personally watched.

Each phase has an **exit criterion**. If it is not met, the phase is not done, regardless of how
much code exists.

| Phase | Theme | Rough effort |
|---|---|---|
| 0 | Environment and skeleton | 0.5 day |
| 1 | Domain, schema, declarative constraints | 2 days |
| 2 | Transaction boundaries and atomicity | 2 days |
| 3 | Break it on purpose — the anomaly lab | 3 days |
| 4 | Isolation levels | 2 days |
| 5 | Locking strategies and queues | 3 days |
| 6 | Failures, retries, idempotency, outbox | 3 days |
| 7 | Production hardening and load | 3 days |

---

## Phase 0 · Environment and skeleton

### Learning objective
Have a reproducible PostgreSQL 16 + .NET environment where two sessions can be stepped
deterministically, and where every experiment can be re-run from zero.

### Features implemented
Health endpoints, migration runner, seed script, `psql` lab access.

### ACID concepts practiced
None yet — but you verify `SHOW default_transaction_isolation` returns `read committed` and
prove `READ UNCOMMITTED` is an alias (CH-06a), which sets the baseline for everything after.

### Database concepts practiced
Extensions (`btree_gist`), schemas, roles and role-level settings, connection pooling limits.

### Implementation tasks
1. `docker-compose.yml`: `postgres:16`, the API, a worker host, Prometheus, Grafana.
2. Solution skeleton (§11.2) with `NpgsqlDataSource` registered once as a singleton.
3. Migration tool of choice (raw SQL files + a small runner, or DbUp/FluentMigrator). Migration
   `001` creates schemas, `btree_gist`, and the `concourse_app` / `concourse_migrator` roles
   with `statement_timeout`, `idle_in_transaction_session_timeout`, `lock_timeout` set at role
   level.
4. `make lab` / `pwsh ./lab.ps1`: opens two `psql` shells with the `A>` / `B>` prompts and `%x`
   transaction indicators from `roadmap/00-start-here.md §3.2`.
5. `POST /ops/reset` (Development only) that truncates and re-seeds deterministically.

### Tests required
- `T-U0` migrations apply to an empty database and are idempotent on re-run.
- `T-I0` Testcontainers spins a PostgreSQL 16 container, applies migrations, and
  `SELECT 1` succeeds.

### Exit criterion
Two `psql` sessions side by side, `docker compose up` from scratch to a healthy API in under
60 seconds, and a green `T-I0`.

---

## Phase 1 · Domain, schema, and declarative constraints

### Learning objective
Internalise **rung 1** of the correctness ladder: an invariant expressed as a constraint holds
against every writer at every isolation level, forever, including code you have not written yet.

### Features implemented
F-1 (seat import), F-2 (create event), F-6 (availability read), F-7 (create cart),
catalog CRUD.

### ACID concepts practiced
`C` in ACID — what the database enforces versus what is your job (`L03 §5.2`). Statement-level
atomicity. Constraint violations as SQLSTATEs (`23505`, `23503`, `23514`, `23P01`).

### Database concepts practiced
Primary/foreign keys, `CHECK`, `UNIQUE`, **partial unique indexes**, **`EXCLUDE USING gist`**,
expression indexes, deferred constraint triggers, `tstzrange`, keyset pagination.

### Implementation tasks
1. All migrations from §4, in dependency order, each with `lock_timeout` set.
2. The `money.assert_journal_balanced` constraint trigger and the append-only rules.
3. Repositories/queries with **positional parameters only**.
4. The error-mapping middleware from §7.0 — every SQLSTATE mapped exactly once.
5. Write, for each of INV-1…INV-12, the **detection query** in
   `sql/invariants/INV-xx.sql`. They are used by `GET /ops/invariants` and by every concurrency
   test's assertion phase.

### Tests required
- `T-U1` domain validation rules (amounts positive, time ordering, currency match).
- `T-I1` each constraint rejects its violation: duplicate seat → `23505`; overlapping seat hold
  → `23P01`; `reserved + sold > allocated` → `23514`; unbalanced journal → error at `COMMIT`,
  **not** at the offending `INSERT` (this is the deferred-constraint proof).
- `T-I2` `GET /ops/invariants` returns all-green on a seeded database.

### Exit criterion
You can state, for each of the twelve invariants, *which* database object enforces it — or that
none does and why (INV-4, INV-9, INV-11, INV-12).

---

## Phase 2 · Transaction boundaries and atomicity

### Learning objective
Place `BEGIN` and `COMMIT` deliberately around one unit of work, and prove that an interrupted
unit leaves nothing behind.

### Features implemented
F-13 (checkout, without idempotency yet), F-16 (issue tickets), the outbox **table and writes**
(publisher comes in Phase 6), F-3 (publish).

### ACID concepts practiced
Atomicity across multiple tables · what `ROLLBACK` does and does not undo (`L03 §5.1`) ·
`25P02` transaction poisoning · savepoints as genuine try/fallback · `idle in transaction` ·
sequences are non-transactional.

### Database concepts practiced
`BEGIN`/`COMMIT`/`ROLLBACK`, `SAVEPOINT`/`ROLLBACK TO`/`RELEASE`, WAL and the durability point,
`pg_stat_activity.state`, `backend_xmin`.

### Implementation tasks
1. A single `IUnitOfWork`/`ITransactionRunner` seam so transactions are opened in **one** place.
2. Checkout as one transaction across `ticket_order`, `ticket_order_line`, `hold`, `outbox`.
3. F-1's savepoint-per-row bulk import, with configurable chunk size.
4. `await using` on **both** connection and transaction everywhere; `CancellationToken` threaded
   through every command.
5. **The `idle in transaction` lab:** a Development-only endpoint that opens a transaction,
   writes, `await Task.Delay(5s)`, then commits. Observe it in `GET /ops/transactions` and in
   `VACUUM (VERBOSE)` output. Then delete the endpoint and write down why it was a bad idea.

### Tests required
- `T-I3` throw between the ticket insert and the order update ⇒ neither persists.
- `T-I4` bulk import of 1 000 rows with 5 deliberate violations ⇒ 995 committed, 5 reported,
  one `COMMIT`.
- `T-I5` a statement error inside an explicit transaction, then another statement ⇒ `25P02`;
  after `ROLLBACK` the connection is usable.
- `T-I6` an identity gap survives a rollback (proves sequences are non-transactional).
- `T-P1` (performance) chunk-size sweep for the importer: 1 / 100 / 1 000 / 20 000, reporting
  wall time and peak `age(backend_xmin)`.

### Exit criterion
For every handler you have written, you can point at the exact line where the transaction opens
and closes, and justify why nothing outside the unit of work is inside it.

---

## Phase 3 · Break it on purpose — the anomaly lab

### Learning objective
See every anomaly happen in your own system, with your own data, and have a failing automated
test for each.

### Features implemented
Naive versions of F-8, F-12, F-11, F-18, F-5, F-14. Plus `tools/ConcurrencyLab`.

### ACID concepts practiced
Dirty read (impossible) · non-repeatable read · phantom read · lost update · write skew ·
read-only anomaly · MVCC snapshot timing.

### Database concepts practiced
`xmin`/`xmax`/`ctid` inspection, `pg_current_snapshot()`, `pageinspect`, `pg_locks` with
`SIReadLock`, `pg_advisory_lock` as a test rendezvous.

### Implementation tasks
1. Build `ConcurrencyLab` (§6 shared harness): `RunParallel`, `StepRunner`, `Stats`.
2. Implement the **naive** version of every anomaly-prone path, behind
   `Concurrency:Strategy = Naive` configuration so both versions coexist.
3. Run every `psql` timeline in CH-01, CH-02a, CH-03, CH-06 and record the actual output.
4. Write `docs/findings/CH-06.md` (read phenomena) — the shortest write-up, do it first.

### Tests required
Each of these must **fail** against the naive implementation and is marked
`[Trait("Anomaly","expected-fail")]` until the corresponding phase fixes it:
- `T-C1` lost update — 20 workers, GA inventory oversells.
- `T-C2a` write skew — two workers disable two routes, zero remain.
- `T-C2b` write skew on `SUM` — four refunds exceed the capture.
- `T-C3` phantom — 20 workers blow the per-customer limit.
- `T-C13` promo budget overspend.
- `T-C6a` torn read — statement balance disagrees with its own entries at `READ COMMITTED`.

### Exit criterion
Six red tests, six timelines with pasted real output, and `docs/findings/CH-06.md` written. You
can explain each failure in one sentence naming the anomaly.

---

## Phase 4 · Isolation levels

### Learning objective
Choose a level per unit of work with a written justification, and experience `40001` from both
`REPEATABLE READ` and `SERIALIZABLE`.

### Features implemented
F-6 and F-21 at `REPEATABLE READ`; F-5 and F-12 at `SERIALIZABLE` (the SSI variants);
`GET /ops/invariants` at `SERIALIZABLE READ ONLY DEFERRABLE`.

### ACID concepts practiced
Snapshot per statement vs per transaction · first-updater-wins (`40001`, "concurrent update") ·
SSI dangerous structures (`40001`, "read/write dependencies") · read-only anomaly · the fact
that SSI protects you only if **every** participant is `SERIALIZABLE`.

### Database concepts practiced
`BEGIN TRANSACTION ISOLATION LEVEL …`, `SET TRANSACTION READ ONLY, DEFERRABLE`, `25001`
(changing the level after the first query), `SIReadLock` rows and their granularity,
`max_pred_locks_per_transaction`.

### Implementation tasks
1. Every `BeginTransactionAsync` call site takes an **explicit** level with a one-line comment
   justifying it against the §7.7 table.
2. Implement TX-11 strategy `Serializable` and TX-14 implementation B.
3. Implement the consistent-read endpoints (availability, statement, invariant checker).
4. Prove `25001`: try to change the level after the first query and capture the error.
5. Demonstrate the mixed-level trap: run the `SERIALIZABLE` route toggle while a
   `READ COMMITTED` job disables a route, and show the invariant still breaks. Write down the
   conclusion.

### Tests required
- `T-C2a` now passes at `SERIALIZABLE` (with a temporary hand-rolled retry — the real helper
  arrives in Phase 6).
- `T-C3` now passes at `SERIALIZABLE`.
- `T-I7` `REPEATABLE READ` write collision raises `40001` with message *"could not serialize
  access due to concurrent update"*.
- `T-I8` `SERIALIZABLE` write skew raises `40001` with message *"…read/write dependencies among
  transactions"* — assert on the **message**, not just the SQLSTATE, so the two causes stay
  distinguishable.
- `T-C6b` the statement endpoint is internally consistent under concurrent transfers.
- `T-I9` the mixed-level test above **fails to protect** the invariant, and is asserted as such.

### Exit criterion
The §7.7 table is real: every endpoint's level is set in code and justified in a comment, and
you have observed both `40001` messages.

---

## Phase 5 · Locking strategies and queues

### Learning objective
Use explicit locks where they beat both optimism and isolation, and build queue consumers that
scale.

### Features implemented
F-9 (`EXCLUDE` seat holds), F-14 (wallet payment with ordered `FOR UPDATE`), F-17 (reaper),
F-19 (transfers), F-20 (payouts), TX-11 strategies `GuardRow` and `Counter`.

### ACID concepts practiced
Pessimistic vs optimistic concurrency · locks convert aborts into waits · deadlocks and their
avoidance · why `FOR UPDATE` cannot prevent phantom inserts.

### Database concepts practiced
Row lock modes and the conflict matrix · `FOR UPDATE` / `FOR NO KEY UPDATE` / `FOR SHARE` ·
`NOWAIT`, `SKIP LOCKED`, `lock_timeout`, `deadlock_timeout` · `pg_blocking_pids()` ·
`pg_advisory_xact_lock` · partial indexes matching a claim predicate.

### Implementation tasks
1. Wallet payment and transfers with canonical-order `FOR UPDATE` in a single statement.
2. The reaper with `FOR UPDATE SKIP LOCKED` and the data-modifying CTE from TX-07.
3. The payout batch: `SKIP LOCKED` across organizers, one transaction per organizer,
   `FOR UPDATE` on the balance row.
4. The **slow-job** variant from CH-07: `processing` state, `locked_by`/`locked_at`, and a
   stuck-job reaper.
5. `pg_advisory_xact_lock(hashtext('payout:' || organizer_id))` for "only one payout run per
   organizer at a time", plus a written note on `hashtext` collision risk and why the `_xact_`
   variant (not the session variant) is mandatory behind a transaction-pooling pooler.
6. `SET LOCAL lock_timeout = '3s'` on every user-facing write that touches a hot row.
7. `GET /ops/locks` implementing the blocking-tree query.

### Tests required
- `T-C4a` deadlock reproduced; `T-C4b` zero deadlocks after canonical ordering across 200
  randomised transfers, with money conserved.
- `T-C7a` 50 workers contend for the same 10 seats ⇒ exactly 10 succeed, zero double-holds.
- `T-C7b` `SKIP LOCKED` scaling: 1/2/4/8 workers, with and without, throughput table.
- `T-C7c` kill a worker mid-slow-job ⇒ the job is recovered and completed exactly once.
- `T-C14` 30 concurrent payouts + refunds for one organizer ⇒ never below the reserve.
- `T-C1` now passes with fix C (`FOR UPDATE`) as well as fix A (atomic write).
- `T-I10` `NOWAIT` on a locked row returns `55P03`; `SKIP LOCKED` returns the unlocked subset.
- `T-P2` reaper throughput meets §3.1.

### Exit criterion
`T-C4b` green, the `SKIP LOCKED` scaling table produced, and you can recite the one-sentence
reason `FOR UPDATE` cannot solve CH-03.

---

## Phase 6 · Failures, retries, idempotency, outbox

### Learning objective
Make every retryable failure invisible to users, and make every retried body safe to run twice.

### Features implemented
`TransactionRetry` everywhere, F-13/F-14/F-18/F-20 idempotency keys, F-15 (card saga +
reconciler), F-22 (outbox publisher + consumer dedup).

### ACID concepts practiced
`40001`/`40P01` as retry requests, not bugs · why the whole unit of work re-runs on a fresh
connection · bounded exponential backoff with **full jitter** · idempotency as a precondition
for retry · atomicity does not cover external effects · "commit returned an error" is an
*unknown* outcome, not a rollback.

### Database concepts practiced
`ON CONFLICT DO NOTHING`/`DO UPDATE`, natural keys, `RETURNING`, outbox claim with
`SKIP LOCKED`, consumer dedup tables, `SET LOCAL`.

### Implementation tasks
1. `TransactionRetry.ExecuteAsync` exactly per CH-05 Step 1, with metrics `M-3`…`M-5`.
2. Replace **every** write path's transaction opening with it; delete the temporary retries
   from Phase 4.
3. Idempotency middleware: claim the key, run the handler, store status + body, replay on
   repeat, `422` on a fingerprint mismatch, purge job for expired keys.
4. Outbox publisher: `SKIP LOCKED` batches, exponential `next_attempt_at`, `attempts`,
   `last_error`, consumer writing `ops.processed_event` inside its own transaction.
5. Card saga (TX-15) with the fake PSP: configurable latency, failure rate, duplicate callbacks
   and a "timeout with unknown outcome" mode. Plus the reconciler.
6. Architecture test: no `BeginTransaction*` outside `TransactionRetry`; no `HttpClient` usage
   inside a retried delegate.

### Tests required
- `T-C5a` business exceptions and `23505` execute the body exactly once.
- `T-C5b` a forced `40001` mid-body ⇒ after retry, exactly one journal, one ticket set, one
  outbox row, one debit.
- `T-C5c` retry storm comparison (immediate / fixed / full jitter) with the table.
- `T-C5d` exhaustion under 64 workers on one row, then zero after the redesign.
- `T-C8a` idempotent payment: 20 parallel `POST /pay` with one key ⇒ one capture, 19 replays
  returning the identical body.
- `T-C8b` process killed (a) before the PSP call, (b) after it, (c) after `COMMIT` but before
  publish — each recovers to the correct state within the stated window.
- `T-C9` outbox exactly-once effect: publisher restarted mid-batch ⇒ at-least-once delivery,
  consumer dedup ⇒ one effect.

### Exit criterion
`T-C5b`, `T-C8a`, `T-C8b` and `T-C9` green, and there is exactly one place in the codebase that
opens a write transaction.

---

## Phase 7 · Production hardening, observability and load

### Learning objective
Run the system under the §3.1 workload, see the contention in metrics, and fix a hotspot by
**design** rather than by turning a knob.

### Features implemented
Full observability (§10), the k6 suite, the hot-row redesign, the migration-safety exercise.

### ACID concepts practiced
Cost of `SERIALIZABLE` under contention · predicate-lock escalation and false positives ·
VACUUM horizon and bloat from long transactions · choosing a strategy per workload (`L10 §4`).

### Database concepts practiced
`pg_stat_database` rollback ratio and deadlocks · `pg_stat_activity` transaction and snapshot
age · `SIReadLock` granularity · `max_pred_locks_*` · `log_lock_waits` · `CREATE INDEX
CONCURRENTLY` · `ADD CONSTRAINT … NOT VALID` + `VALIDATE` · HOT update ratio
(`n_tup_hot_upd` vs `n_tup_upd`).

### Implementation tasks
1. All metrics `M-1`…`M-14` from §10, a Grafana dashboard, and the alert rules.
2. k6 scenarios: `onsale` (1 500 rps burst), `hot-seat` (200 seats, 5 000 VUs),
   `hot-wallet` (90 % of transfers into one account), `route-toggle` (the three-way comparison),
   `mixed-steady`.
3. Run the **three-way comparison** for TX-11 and the **two-way** for TX-14; fill in the tables
   in `docs/findings/`.
4. **The hot-row redesign.** Make one promo code or one GA tier take 90 % of writes; show the
   throughput ceiling and (if using `SERIALIZABLE`) rising `tx_retries_exhausted_total`. Then
   implement the append-only alternative — `INSERT` delta rows plus a periodic rollup into
   `event_sales_daily`, with the cap enforced by a coarse reservation — and re-measure. Write up
   what you traded away (exact real-time caps become approximate; reads get more expensive).
5. **Predicate-lock escalation.** Force `SIReadLock` escalation from `tuple` to `page`/
   `relation` on a `SERIALIZABLE` path with a large read set, observe false-positive `40001`s in
   `GET /ops/locks`, raise `max_pred_locks_per_transaction`, and show the rate drop.
6. **Migration safety exercise.** Introduce overlapping `seat_hold` rows into a large table,
   then add `seat_hold_no_overlap` with zero downtime: find and quarantine violators, build the
   GiST index `CONCURRENTLY`, attach the constraint using that index, and document the lock
   windows.
7. Runbook: what to do when each alert fires (§10.5).

### Tests required
- `T-P3` onsale load test meets the §3.1 latency and throughput budgets with **zero** invariant
  violations (`GET /ops/invariants` polled throughout and asserted green).
- `T-P4` `SKIP LOCKED` scaling table reproduced under load.
- `T-P5` the three-way route-toggle comparison, with numbers.
- `T-P6` hot-row before/after redesign.
- `T-C10` chaos: kill the API mid-load-test; after recovery, all invariants hold and no money
  is created or destroyed.
- `T-I11` migration safety: the `CONCURRENTLY` + `NOT VALID`/`VALIDATE` path never holds
  `ACCESS EXCLUSIVE` for more than 3 s (measured via `lock_timeout` failing the naive variant).

### Exit criterion
A load test that meets §3.1 while `GET /ops/invariants` stays green, plus written findings for
CH-01…CH-07 with your own numbers.

---

## Phase order rationale

Two ordering decisions are deliberate and worth understanding:

**Constraints before transactions (Phase 1 before Phase 2).** Most concurrency bugs are best
prevented by schema design. Starting with constraints means that by the time you reach the
isolation-level phase you already have a strong instinct for *"can I just make this
impossible?"* — which is the correct first question (`L10 §2`).

**Breaking it (Phase 3) before fixing it (Phases 4–6).** A fix you have never watched fail is
cargo cult. The six red tests at the end of Phase 3 are the project's spine: each one turns
green in a specific later phase, and you will remember which mechanism turned it green.
