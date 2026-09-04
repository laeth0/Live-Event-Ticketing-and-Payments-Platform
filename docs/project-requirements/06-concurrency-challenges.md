# 6 · Required Concurrency Challenges

These are **mandatory drills**, not optional exercises. Each one has the same shape:

1. **Reproduce the anomaly** with two `psql` sessions (a deterministic, step-numbered timeline).
2. **Reproduce it in C#** with a parallel harness, so it is a failing automated test.
3. **Fix it** — in several ways where several exist.
4. **Measure** the fixes against each other under load.
5. **Write it up** in `docs/findings/CH-xx.md` with your own numbers.

The write-ups are the portfolio artefact. Anyone can paste a `SELECT … FOR UPDATE`; a table of
throughput and abort rates for four different fixes to the same bug is evidence of engineering
judgement.

**Shared harness requirement.** Build `tools/ConcurrencyLab` once (Phase 3):

- `RunParallel(int workers, Func<int, Task> body)` — N tasks, each with its **own connection**,
  released simultaneously by a `Barrier`, so they genuinely collide.
- `StepRunner` — two connections stepped deterministically through a script using
  `pg_advisory_lock` as a rendezvous, so timeline tests are not flaky.
- `Stats` — successes, business rejections, `40001`, `40P01`, `23505`, `23P01`, `55P03`, retry
  attempts, wall time, p50/p95/p99.

---

## CH-01 · Lost update

**Business setting.** GA inventory (`sales.ticket_type_inventory`) — F-8 / TX-02.

### Step 1 — reproduce in `psql`

Setup: `allocated = 10, reserved = 0, sold = 0`.

| Step | Session A | Session B | Expected |
|---|---|---|---|
| 1 | `BEGIN;` | `BEGIN;` | |
| 2 | `SELECT reserved FROM sales.ticket_type_inventory WHERE ticket_type_id=$T;` → `0` | | A decides: `0 + 4 = 4` |
| 3 | | `SELECT reserved … ;` → `0` | B decides: `0 + 4 = 4` |
| 4 | `UPDATE … SET reserved = 4 WHERE ticket_type_id=$T;` | | `UPDATE 1` |
| 5 | `COMMIT;` | | |
| 6 | | `UPDATE … SET reserved = 4 WHERE ticket_type_id=$T;` | blocks, then `UPDATE 1` |
| 7 | | `COMMIT;` | |
| 8 | `SELECT reserved …;` | | **`4`** — expected 8. Four reservations vanished. |

Note what step 6 proves: the row lock made B *wait*, B then re-read the row — and it still
wrote the stale literal `4` it had computed at step 3. **Waiting does not fix a lost update.**

### Step 2 — reproduce in C#

`T-C1`: 20 parallel workers each reserve 1 ticket against `allocated = 20`, using the naive
read-modify-write. Assert the test **fails**: `reserved < 20` and the sum of successful
responses exceeds `reserved`.

### Step 3 — implement all four fixes

| # | Fix | Implementation | When it is the right choice |
|---|---|---|---|
| **A** | **Atomic SQL write** | `UPDATE … SET reserved = reserved + $1 WHERE ticket_type_id = $2 AND reserved + sold + $1 <= allocated` → rows-affected | Accumulation on one row: counters, balances, stock. **This is the answer for F-8.** |
| **B** | **Optimistic version** | `UPDATE … SET reserved = $new, version = version + 1 WHERE ticket_type_id = $1 AND version = $2` → 0 rows ⇒ reload, recompute, retry (bounded) | Read a whole aggregate, mutate it in C#, write it back. **This is the answer for F-4 (event edit).** |
| **C** | **Pessimistic `FOR UPDATE`** | `SELECT reserved FROM … WHERE ticket_type_id=$1 FOR UPDATE`, then decide, then `UPDATE` | You must read several related rows *fresh* before deciding, and you would rather wait than retry. **This is the answer for F-14 (wallet).** |
| **D** | **`REPEATABLE READ` + retry** | Same naive body, at `RepeatableRead`; the second writer gets `40001` and the retry re-reads | Rarely the best tool for a single row; included so you *observe* first-updater-wins. |

### Step 4 — measure

32 workers, one row, 5 000 total operations. Report:

| Fix | Throughput (ops/s) | p99 (ms) | Retries | Aborts | Final value correct? |
|---|---|---|---|---|---|
| naive | — | — | — | — | **no** |
| A atomic | | | 0 | 0 | yes |
| B version | | | | | yes |
| C `FOR UPDATE` | | | 0 | 0 | yes |
| D `REPEATABLE READ` | | | | | yes |

**Expected shape of the result** (predict before you run): A ≈ C ≫ B > D. A and C both
serialise on one row lock with no wasted work. B wastes a round trip per conflict. D wastes the
whole transaction per conflict. Explain any surprise.

**Deliverable.** `docs/findings/CH-01.md` with the table, and one paragraph answering: *why did
waiting on the row lock in step 6 of the psql timeline not prevent the lost update?*

---

## CH-02 · Write skew

Two instances, deliberately different in shape.

### CH-02a — "at least one enabled payment route" (no counter possible at first)

**Business setting.** F-5 / TX-11 / INV-9. This is the direct analogue of the course's
`on_call` example.

Setup: event `E` is `onsale` with routes `psp_primary` and `psp_backup`, both enabled.

**Part A — `REPEATABLE READ` does not save you**

| Step | Session A | Session B | Expected |
|---|---|---|---|
| 1 | `BEGIN ISOLATION LEVEL REPEATABLE READ;` | `BEGIN ISOLATION LEVEL REPEATABLE READ;` | |
| 2 | `SELECT count(*) FROM catalog.payment_route WHERE event_id=$E AND is_enabled;` → `2` | | |
| 3 | | same query → `2` | |
| 4 | `UPDATE catalog.payment_route SET is_enabled=false WHERE provider='psp_primary' AND event_id=$E;` | | `UPDATE 1` |
| 5 | | `UPDATE … WHERE provider='psp_backup' AND event_id=$E;` | `UPDATE 1` — **different row, no block** |
| 6 | `COMMIT;` | `COMMIT;` | both succeed |
| 7 | `SELECT count(*) … WHERE is_enabled;` | | **`0`** — invariant broken |

**Part B — `SERIALIZABLE` catches it**

Same timeline with `ISOLATION LEVEL SERIALIZABLE`. Step 6: A commits. Step 7: B gets
`ERROR: could not serialize access due to read/write dependencies among transactions`,
`SQLSTATE 40001`.

While both transactions are open, from a **third** session:
```sql
SELECT pid, locktype, mode, relation::regclass, page, tuple
FROM pg_locks WHERE mode = 'SIReadLock' ORDER BY pid;
```
Record what you see: how many rows, at what granularity (`tuple` / `page` / `relation`). Those
rows are the record of "who read what" that lets SSI find the cycle. Paste the output into the
write-up.

**Part C — the three fixes, compared.** Implement all three from TX-11 (SSI, guard-row lock,
materialised counter + `CHECK`) and fill in the comparison table there with your own numbers
from `k6` at 50 concurrent route toggles.

**Deliverable.** `docs/findings/CH-02a.md` containing: the two timelines, the `pg_locks`
output, the three-way benchmark, and one paragraph answering *why `REPEATABLE READ` cannot
detect this but `SERIALIZABLE` can* — in terms of "rows written by both" versus "read/write
dependency".

### CH-02b — write skew on a `SUM` (refunds)

**Business setting.** F-18 / TX-09 / INV-7. Payment with `captured_minor = 12000`,
`refunded_minor = 0`.

**Reproduce.** `T-C2b`: four parallel refunds of `5000`, `4000`, `3000`, `1000` against the
naive `SELECT SUM(...)`-then-`INSERT` implementation. Assert the test fails: total refunded
`13000 > 12000`.

**Fix.** The counter + `CHECK` from TX-09. Re-run: exactly three succeed, the fourth gets
`RefundExceedsCapture`, `refunded_minor = 12000`.

**Then also fix it with `SERIALIZABLE`** and measure both at 32 concurrent refunds.

**Deliverable.** `docs/findings/CH-02b.md` with a paragraph on **materialising the conflict**:
what exactly changed such that `READ COMMITTED` became sufficient? (Answer to aim for: the
decision predicate stopped ranging over many rows and became a single row that all writers
must update, converting a predicate conflict — invisible to MVCC — into a row conflict, which
is the one thing MVCC handles natively.)

---

## CH-03 · Phantom read

**Business setting.** F-12 / TX-14 / INV-4 — "at most 6 tickets per customer per event", plus
a second, purer variant: "at most 10 active holds per cart".

### Step 1 — see the phantom, and see `REPEATABLE READ` prevent the *read* but not the *bug*

| Step | Session A (`READ COMMITTED`) | Session B | Expected |
|---|---|---|---|
| 1 | `BEGIN;` | | |
| 2 | `SELECT count(*) FROM sales.hold h JOIN sales.cart c ON c.id=h.cart_id WHERE c.customer_id=$C AND c.event_id=$E AND h.released_at IS NULL;` → `2` | | |
| 3 | | `INSERT INTO sales.hold (…) VALUES (…);` *(autocommit)* | `INSERT 0 1` |
| 4 | `SELECT count(*) … ;` (same query) | | **`3`** — a phantom row appeared |
| 5 | `COMMIT;` | | |

Repeat with `BEGIN ISOLATION LEVEL REPEATABLE READ` — step 4 now returns `2`. **PostgreSQL is
stricter than the SQL standard here** (`L04 §4.3`): the standard only requires `SERIALIZABLE`
to prevent phantoms.

### Step 2 — prove that preventing the phantom does *not* prevent the bug

| Step | Session A (`REPEATABLE READ`) | Session B (`REPEATABLE READ`) | Expected |
|---|---|---|---|
| 1 | `BEGIN ISOLATION LEVEL REPEATABLE READ;` | `BEGIN ISOLATION LEVEL REPEATABLE READ;` | |
| 2 | `SELECT count(*) …` → `4` | | A: `4 + 2 <= 6` ⇒ allowed |
| 3 | | `SELECT count(*) …` → `4` | B: `4 + 2 <= 6` ⇒ allowed |
| 4 | `INSERT INTO sales.hold (…, quantity=2);` | | ok |
| 5 | | `INSERT INTO sales.hold (…, quantity=2);` | ok — **different row, no conflict** |
| 6 | `COMMIT;` | `COMMIT;` | both succeed |
| 7 | `SELECT sum(quantity) …` | | **`8`** — limit of 6 exceeded |

This is the single most important step in the whole document. Each transaction had a perfectly
stable, phantom-free view. The invariant still broke. *Not seeing concurrent changes is not the
same as preventing two transactions from jointly breaking an invariant* (`L04 §4.4`).

### Step 3 — prove `FOR UPDATE` cannot fix it

| Step | Session A | Session B | Expected |
|---|---|---|---|
| 1 | `BEGIN;` | `BEGIN;` | |
| 2 | `SELECT id FROM sales.hold h JOIN sales.cart c … WHERE c.customer_id=$C … FOR UPDATE;` → 2 rows locked | | |
| 3 | | same query → **returns immediately**… | …because it locks *the same two existing rows*, and A has not blocked B on rows B wants to insert |
| 4 | `INSERT …` | `INSERT …` | both succeed |

`FOR UPDATE` locks **rows the query returned**. It cannot lock rows that do not exist yet
(`L07 §2`). Write this down; it is the most commonly-believed false thing about row locks.

### Step 4 — the two fixes

- **Guard row** (`sales.customer_event_allocation`) at `READ COMMITTED` — TX-14 A.
- **`SERIALIZABLE` + retry** — TX-14 B.

### Step 5 — measure

`T-C3`: 20 parallel requests for the same (customer, event), each asking for 2 tickets against
a limit of 6.

| Implementation | Successes | Business rejections | `40001` | Retries | p99 | Correct? |
|---|---|---|---|---|---|---|
| naive `count(*)` + insert, RC | 20 | 0 | 0 | 0 | | **no** |
| naive, `REPEATABLE READ` | 20 | 0 | 0 | 0 | | **no** |
| guard row, RC | 3 | 17 | 0 | 0 | | yes |
| `SERIALIZABLE` + retry | 3 | 17 | many | many | | yes |

**Deliverable.** `docs/findings/CH-03.md` with all four timelines, the measurement table, and a
paragraph explaining why the guard row makes `READ COMMITTED` sufficient — and why that is not
an argument that `SERIALIZABLE` is bad.

---

## CH-04 · Deadlocks

**Business setting.** F-19 / TX-08 — mutual wallet transfers.

### Step 1 — create one

| Step | Session A | Session B | Expected |
|---|---|---|---|
| 1 | `BEGIN;` | `BEGIN;` | |
| 2 | `UPDATE money.account_balance SET version=version WHERE account_id=$ALICE;` | | A holds Alice |
| 3 | | `UPDATE money.account_balance SET version=version WHERE account_id=$BOB;` | B holds Bob |
| 4 | `UPDATE … WHERE account_id=$BOB;` | | A waits for B |
| 5 | | `UPDATE … WHERE account_id=$ALICE;` | after ~`deadlock_timeout` (1 s): one session gets `ERROR: deadlock detected`, **`SQLSTATE 40P01`**; the other proceeds |
| 6 | winner `COMMIT;` | loser `ROLLBACK;` | |

Capture the **full** error including the `DETAIL:` line naming both processes and both
statements. Paste it into the write-up — it is the single most useful diagnostic artefact for
production deadlocks, and `log_lock_waits = on` is what makes it appear in the server log.

While step 4/5 are pending, from a third session:
```sql
SELECT a.pid, now()-a.query_start AS waited, a.query AS blocked, pg_blocking_pids(a.pid) AS blocked_by
FROM pg_stat_activity a WHERE cardinality(pg_blocking_pids(a.pid)) > 0;
```

### Step 2 — reproduce in C#

`T-C4a`: 100 parallel transfers with random `(from, to)` pairs among 5 wallets, locking in
argument order. Assert `40P01` occurs. Record the rate.

### Step 3 — the three mitigations

1. **Canonical lock ordering** — sort account ids, or lock both in one
   `… WHERE account_id IN ($a,$b) ORDER BY account_id FOR UPDATE`.
2. **Short transactions** — nothing slow between acquiring the first lock and committing.
3. **Retry `40P01`** — identical handling to `40001`, because some deadlocks (foreign-key
   locks, index-page ordering, future code paths) cannot be designed away.

### Step 4 — prove the fix

`T-C4b`: 200 parallel randomised transfers with canonical ordering. Assert:
- zero unhandled `40P01`,
- `SUM(balance_minor)` across all five wallets is unchanged,
- every journal balances,
- retry attempts ≈ 0.

Also verify the deadlock counter did not move:
```sql
SELECT deadlocks FROM pg_stat_database WHERE datname = current_database();
```

**Deliverable.** `docs/findings/CH-04.md`: the raw error `DETAIL`, the blocking-tree output, the
before/after deadlock rates, and one paragraph on why ordering removes the cycle (a deadlock
requires a cycle in the wait-for graph; a global acquisition order makes cycles impossible by
construction).

---

## CH-05 · Serialization failures and retry strategy

**Business setting.** Any `SERIALIZABLE` path — use CH-02a's route toggle and CH-03's limit.

### Step 1 — build the retry helper

`TransactionRetry.ExecuteAsync` per `L08 §8.1`. Non-negotiable properties:

- retries **only** `40001`, `40P01`, `40000`;
- retries the **whole** unit of work — reads, decision and writes;
- on a **fresh connection and transaction** each attempt (never the aborted one);
- bounded attempts (default 6);
- **full jitter**: `wait = random(0, min(cap, base · 2^attempt))`, `base = 20 ms`, `cap = 2 s`;
- throws `TransactionRetriesExhaustedException` when exhausted → HTTP `503` + `Retry-After`;
- emits `M-3` (attempts histogram), `M-4` (exhausted), `M-5` (failures by SQLSTATE);
- takes a `unitOfWork` name for metric labels;
- **no external I/O inside the delegate** — enforced by review and by an architecture test.

### Step 2 — prove business errors are not retried

`T-C5a`: a `SERIALIZABLE` unit of work that throws `LastPaymentRouteException` (a plain
domain exception, not a `PostgresException`). Assert the helper executes the body **once** and
rethrows. Then assert a `23505` is likewise executed once. Retrying a deterministic failure is
an infinite loop with extra steps.

### Step 3 — the retry-storm experiment

`T-C5c`: 32 workers, one contended unit of work, 2 000 operations, three strategies:

| Strategy | Total attempts | Successes | Wall time | p99 | Notes |
|---|---|---|---|---|---|
| immediate retry (no sleep) | | | | | expect the highest attempt count — the herd re-collides instantly |
| fixed 25 ms backoff | | | | | expect re-collision waves at t, t+25, t+50 |
| **full jitter** (20 ms base, 2 s cap) | | | | | expect the fewest attempts and the best p99 |

Plot attempts-per-success. **Predict the ranking before running it**, then explain the result:
fixed backoff preserves the herd because every loser waits the *same* amount and collides
again; randomising the whole interval de-correlates them (`L08 §4.3`).

### Step 4 — make retries impossible to break

`T-C5b`: force a `40001` on the *second* statement of the wallet-payment body (inject it with a
test hook or a concurrent conflicting writer). Assert after the retry succeeds:
- exactly one journal for the order,
- exactly `n` tickets,
- exactly one outbox row,
- the wallet debited exactly once.

If any of those doubles, the body is not idempotent and the retry loop is amplifying a bug
instead of fixing a conflict. Fix it with natural keys + `ON CONFLICT DO NOTHING`, not by
removing the retry.

### Step 5 — exhaustion is a design signal

`T-C5d`: 64 workers on one row at `SERIALIZABLE` until `tx_retries_exhausted_total > 0`. Then
fix it two ways and show exhaustion returns to zero:
1. `READ COMMITTED` + atomic `UPDATE … SET n = n + $1`,
2. a `FOR UPDATE` queue on the contended row.

Write the rule down: **raising `maxAttempts` is never the fix.** Exhaustion means the workload
has a hotspot that needs a constraint, a lock, or a redesign.

**Deliverable.** `docs/findings/CH-05.md`: the three-way storm table with a chart, the
idempotency proof, and the exhaustion-then-redesign result.

---

## CH-06 · The read phenomena (proof drills)

Short, but required — they are how you build the mental model the other drills rely on.

**a. Dirty reads are impossible.** Set `default_transaction_isolation = 'read uncommitted'`,
`SHOW transaction_isolation` (expect `read committed`), have session B update a balance without
committing, and show session A still reads the old value. Conclusion to write down: there is no
PostgreSQL setting that produces a dirty read; `READ UNCOMMITTED` is an alias.

**b. Non-repeatable read.** Read `ticket_type.price_minor` twice around a committed concurrent
update: `READ COMMITTED` shows the change, `REPEATABLE READ` does not.

**c. `READ COMMITTED`'s concurrent-update re-check (EvalPlanQual).** Session B updates a row
inside an open transaction; session A runs `UPDATE … WHERE reserved > 0` and blocks; B commits;
A unblocks and re-evaluates its `WHERE` against the **new** version rather than failing. Then
rerun A at `REPEATABLE READ` and capture the `40001` instead. This one surprises everybody —
the same statement, at two levels, has two completely different behaviours (`L06 §5.1`).

**d. Invisible is not absent.** At `REPEATABLE READ`, session A counts seats and sees a stable
number while session B inserts and commits a conflicting row; A then tries to insert the same
key and gets `23505` for a row it cannot see. Snapshot invisibility does not suspend unique
indexes (`L11 §L06.6.5`).

**Deliverable.** `docs/findings/CH-06.md` — the four timelines with actual outputs, and the
PostgreSQL-specific anomaly × level table filled in from memory, then checked.

---

## CH-07 · Queue contention and `SKIP LOCKED`

**Business setting.** F-17 reaper and the outbox publisher.

**Reproduce the pile-up.** Run the reaper claim **without** `SKIP LOCKED` with 1, 2, 4, 8
workers over 10 000 expired holds. Throughput stays flat: every worker queues on the same first
row.

**Fix and re-measure.** Add `FOR UPDATE SKIP LOCKED`. Throughput should scale nearly linearly
to the point where the database, not the lock, is the bottleneck.

**The slow-job variant.** Change the work per claim to 500 ms. Show that holding a row lock for
500 ms × N workers is wrong, and implement the alternative: claim by setting
`state = 'processing'`, `locked_by`, `locked_at` in a short transaction, do the work outside any
transaction, complete in a second short transaction — plus a reaper that returns rows stuck in
`processing` for over 5 minutes to `ready`. Kill a worker mid-job and prove the job is
recovered exactly once (the completion is idempotent).

**Deliverable.** `docs/findings/CH-07.md`: the scaling table (workers × throughput, with and
without `SKIP LOCKED`), and the crashed-worker recovery test.

---

## Coverage checklist

| Required concept (from the brief) | Drill |
|---|---|
| Transaction boundaries, `BEGIN`/`COMMIT`/`ROLLBACK` | TX-03, TX-15, CH-05 |
| ACID properties | §1.4 invariants, CH-06, TX-06 |
| `READ COMMITTED` | CH-01, CH-06c, every RC path |
| `REPEATABLE READ` | CH-02a Part A, CH-03 Step 2, CH-06b, TX-12 |
| `SERIALIZABLE` | CH-02a Part B, CH-03 Step 4, CH-05 |
| MVCC behaviour | CH-06, TX-03 (`backend_xmin`), TX-12 (horizon) |
| Dirty reads | CH-06a |
| Non-repeatable reads | CH-06b |
| Phantom reads | CH-03 |
| Lost updates | CH-01 |
| Write skew | CH-02a, CH-02b |
| Optimistic concurrency | CH-01 fix B, TX-04 |
| Pessimistic locking | CH-01 fix C, TX-06, TX-10 |
| `SELECT FOR UPDATE` | TX-06, TX-08, TX-10 |
| `SKIP LOCKED` | CH-07, TX-07, TX-10 |
| Deadlock handling | CH-04 |
| Serialization failures (40001) | CH-02a, CH-05 |
| Retry strategies | CH-05 |
| Idempotency | CH-05 Step 4, TX-05, TX-15 |
| Transactional outbox | TX-06, T-C9, §2 "Cross-cutting: workers and ops surface" |
| Database constraints | TX-01, TX-09, TX-13, §4 throughout |
| Designing safe concurrent workflows | TX-15, CH-07, §8 Phase 7 |
