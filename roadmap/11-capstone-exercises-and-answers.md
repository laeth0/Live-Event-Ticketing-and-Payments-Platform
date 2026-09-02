# 11 · Capstone — Exercises, Self-Check & Mini-Project

This file consolidates practice for the whole module. Do the **predict-then-run** exercises
first (one per `ACID.json` item), then the **self-check quiz** (answers are in a separate
section — don't peek), then the **mini-project**, then the **ACID self-audit** from
`ACID.json` item L06.6.2.

Everything uses the lab schema from `00-start-here.md` §3.3. Reset between exercises:

```sql
TRUNCATE lab.room_booking;
UPDATE lab.account  SET balance = 1000.00, version = 0;
UPDATE lab.counter  SET n = 0 WHERE id = 'hits';
UPDATE lab.on_call  SET is_on_call = true;
```

---

## 1. How to use this file

- **Predict-then-run:** for every step, write down the value/error you expect *before*
  executing it. The learning is in the mismatch. Then confirm against the "what you should
  see" notes.
- Keep two `psql` sessions open (`A>` and `B>` prompts, `%x` on — see `00-start-here.md`).
- For the C# parts, use the console project from `00-start-here.md` §3.4 plus the
  `TransactionRetry` helper from lesson 08 §8.1.

---

## 2. Predict-then-run, one per module item

### L06.6.1 — Transaction boundaries & savepoints

Predict each result:

| Step | Session A | Predict | 
|---|---|---|
| 1 | `BEGIN;` | |
| 2 | `INSERT INTO lab.counter VALUES ('p1', 1);` | |
| 3 | `SAVEPOINT s;` | |
| 4 | `INSERT INTO lab.counter VALUES ('p1', 2);` | *(duplicate key?)* |
| 5 | `ROLLBACK TO SAVEPOINT s;` | |
| 6 | `INSERT INTO lab.counter VALUES ('p2', 2);` | |
| 7 | `COMMIT;` | |
| 8 | `SELECT id FROM lab.counter WHERE id LIKE 'p%' ORDER BY id;` | |

<details><summary>What you should see</summary>

Step 4: `ERROR: duplicate key value violates unique constraint "counter_pkey"` (`23505`).
Step 5: `ROLLBACK` — the transaction is usable again because the error was contained by the
savepoint. Step 7: `COMMIT`. Step 8: `p1`, `p2` (the failed insert at step 4 left nothing;
`p1` from step 2 survived because `ROLLBACK TO SAVEPOINT s` only reverted work *after* `s`).
Cleanup: `DELETE FROM lab.counter WHERE id LIKE 'p%';`
</details>

### L06.6.2 — ACID honestly

Without running anything, mark each **true/false**, then verify the ones you can:

1. A `ROLLBACK` restores a `serial`/identity column to its previous value.
2. `NOTIFY` sent in a transaction that later rolls back is still delivered.
3. At `READ COMMITTED`, being "inside a transaction" prevents another session from changing
   a row you already read.
4. `COMMIT` returning success means the row is on all replicas.
5. A `CHECK (balance >= 0)` constraint is enforced regardless of isolation level.

<details><summary>Answers</summary>

1. **False** — sequences are non-transactional; the value is consumed (lab: lesson 03 §7.2).
2. **False** — rolled-back `NOTIFY` is dropped; committed `NOTIFY` is delivered (lesson 03 §7.3).
3. **False** — that's a non-repeatable read; possible at `READ COMMITTED` (lesson 04 §6.2A).
4. **False** — durable on the primary WAL by default; replicas depend on `synchronous_commit`
   + `synchronous_standby_names` (lesson 03 §5.4).
5. **True** — declarative constraints hold against every writer at every level (lesson 10 §2).
</details>

### L06.6.3 — Dirty reads

| Step | Session A | Session B | Predict |
|---|---|---|---|
| 1 | `SET default_transaction_isolation='read uncommitted'; BEGIN;` | | |
| 2 | `SHOW transaction_isolation;` | | |
| 3 | | `BEGIN; UPDATE lab.account SET balance = -999 WHERE id = 1;` | |
| 4 | `SELECT balance FROM lab.account WHERE id = 1;` | | |
| 5 | | `ROLLBACK;` | |
| 6 | `COMMIT; RESET default_transaction_isolation;` | | |

<details><summary>What you should see</summary>

Step 2: `read committed` — PostgreSQL upgraded `read uncommitted`. Step 4: `1000.00` — B's
uncommitted `-999` is invisible. Dirty reads cannot be produced in PostgreSQL at any level.
</details>

### L06.6.4 — Non-repeatable reads

Run lesson 04 lab 6.2 Part A (`READ COMMITTED`) and Part B (`REPEATABLE READ`). Predict the
value A sees at step 4 in each. Then change Part B's Session B to a `DELETE` of row 1 instead
of an `UPDATE`; predict whether A still sees the row at step 4.

<details><summary>What you should see</summary>

Part A step 4: `1300.00` (changed). Part B step 4: `1000.00` (stable). With B doing a
`DELETE`: A **still sees** row 1 at `REPEATABLE READ` because A's snapshot predates the
delete; A would only stop seeing it after A commits/rolls back.
</details>

### L06.6.5 — Phantom reads

| Step | Session A (`REPEATABLE READ`) | Session B | Predict |
|---|---|---|---|
| 1 | `BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;` | | |
| 2 | `SELECT count(*) FROM lab.account WHERE balance > 500;` | | |
| 3 | | `INSERT INTO lab.account (id,owner,balance) VALUES (3,'carol',900);` | |
| 4 | `SELECT count(*) FROM lab.account WHERE balance > 500;` | | |
| 5 | `INSERT INTO lab.account (id,owner,balance) VALUES (3,'dave',700);` | | *(what error?)* |
| 6 | `ROLLBACK;` | | |

<details><summary>What you should see</summary>

Step 4: `2` — no phantom at `REPEATABLE READ` (PostgreSQL is stricter than the SQL standard).
Step 5: `ERROR: duplicate key value violates unique constraint "account_pkey"` (`23505`) —
because B's committed row 3 *does* exist physically even though A's snapshot can't see it; the
unique index still enforces uniqueness. This is a great illustration that "invisible to my
snapshot" ≠ "not there." Cleanup: `DELETE FROM lab.account WHERE id = 3;`
</details>

### L06.6.6 — Lost updates

Reproduce lesson 05 lab 6.1 (produce the lost update). Then, before running each fix, predict
the final value of `counter.n`:

- Fix 1: both sessions `UPDATE lab.counter SET n = n + 1 WHERE id='hits';`
- Fix 2: both sessions `SELECT n ... FOR UPDATE` then `UPDATE ... SET n = <read+1>`
- Fix 3: `UPDATE ... SET n = <read+1> WHERE id='hits' AND n = <read>` and re-read+retry on 0 rows

<details><summary>What you should see</summary>

Lab 6.1: final `n = 1` (one increment lost). All three fixes: final `n = 2`. Fix 3's second
session gets `UPDATE 0` on its first try, re-reads `n = 1`, retries with `... AND n = 1`, gets
`UPDATE 1`.
</details>

### L06.6.7 — Write skew

Run lesson 05 lab 6.5 Part A (`REPEATABLE READ`) and Part B (`SERIALIZABLE`). Predict:
(a) does Part A leave `count(*) where is_on_call` at 0 or 1? (b) in Part B, which session
gets `40001`, the first or second to `COMMIT`? (c) after adding the `EXCLUDE` constraint
(Part C), what error does the second overlapping `INSERT` get, and at what isolation level?

<details><summary>What you should see</summary>

(a) Part A → **0** (invariant broken; `REPEATABLE READ` does not stop write skew).
(b) The **second** to `COMMIT` gets `40001` ("could not serialize access due to read/write
dependencies"); the first commits. (c) `ERROR: 23P01 exclusion_violation`, at **any**
isolation level — the constraint doesn't care.
</details>

### L06.6.8 — READ COMMITTED

| Step | Session A (`READ COMMITTED`) | Session B | Predict |
|---|---|---|---|
| 1 | `BEGIN;` | | |
| 2 | `SELECT sum(balance) FROM lab.account;` | | |
| 3 | | `BEGIN; UPDATE lab.account SET balance = balance - 100 WHERE id = 1; UPDATE lab.account SET balance = balance + 100 WHERE id = 2; COMMIT;` | |
| 4 | `SELECT sum(balance) FROM lab.account;` | | |
| 5 | `SELECT balance FROM lab.account WHERE id = 1;` then `SELECT balance FROM lab.account WHERE id = 2;` (two statements) | | |
| 6 | `COMMIT;` | | |

<details><summary>What you should see</summary>

Step 2 and step 4: both `2000.00` (the transfer nets to zero — sum is coincidentally stable
here). The real point: step 5's two separate `SELECT`s each get a **fresh snapshot**, so if B
runs *another* transfer between them, A can see `id=1` post-transfer and `id=2` pre-transfer —
a sum across the two reads that never existed. Try inserting a `B>` transfer between A's two
step-5 statements and predict the mismatch.
</details>

### L06.6.9 — REPEATABLE READ

| Step | Session A (`REPEATABLE READ`) | Session B | Predict |
|---|---|---|---|
| 1 | `BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ;` | | |
| 2 | `SELECT balance FROM lab.account WHERE id = 1;` | | |
| 3 | | `UPDATE lab.account SET balance = balance + 50 WHERE id = 1;` *(autocommit)* | |
| 4 | `SELECT balance FROM lab.account WHERE id = 1;` | | |
| 5 | `UPDATE lab.account SET balance = balance - 10 WHERE id = 1;` | | *(what error, what SQLSTATE?)* |
| 6 | `ROLLBACK;` | | |

<details><summary>What you should see</summary>

Step 4: unchanged from step 2 (frozen snapshot). Step 5:
`ERROR: could not serialize access due to concurrent update`, **SQLSTATE 40001** —
first-updater-wins; B modified the row after A's snapshot and committed. The fix is the
lesson-08 retry loop: `ROLLBACK`, re-`BEGIN`, re-read (`balance` now includes B's +50),
re-`UPDATE`.
</details>

### L06.6.10 — SERIALIZABLE & SSI

Run lesson 05 lab 6.5 Part B. While both transactions are open, from a **third** session run:

```sql
SELECT pid, locktype, mode, relation::regclass, page, tuple
FROM pg_locks
WHERE mode = 'SIReadLock'
ORDER BY pid;
```

Predict: how many `SIReadLock` rows, and at what granularity (`tuple` vs `page` vs
`relation`)? Then predict which transaction aborts and with which message.

<details><summary>What you should see</summary>

Several `SIReadLock` rows for the two sessions that ran `SELECT count(*) ... WHERE
is_on_call` — normally `tuple`/`page` granularity on `on_call` (it's tiny, so possibly
`relation`). These records of "who read what" are what let SSI find the read/write cycle. The
**second committer** aborts with `ERROR: could not serialize access due to read/write
dependencies among transactions`, SQLSTATE `40001`.
</details>

### L06.6.11 — Serialization failures & retry loops

In C#, run the write-skew unit of work (count on-call, refuse if it would drop to 0, else set
`engineer` off call) with the `TransactionRetry.ExecuteAsync` helper at `Serializable`, from
**two** parallel tasks (`ada` and `grace`). Predict:

- How many of the two tasks succeed in setting someone off call?
- What does the other task's result / exception look like?
- If you remove the "refuse if would drop to 0" check, what happens on the retry?

<details><summary>What you should see</summary>

Exactly **one** task sets its engineer off call. The other: its first attempt hits `40001`
(SSI), the helper retries; the retry re-reads `count = 1`, the business rule throws
`LastEngineerCannotLeaveException`, which is **not** a `PostgresException` so the helper does
**not** retry it — it bubbles out. Without the check, the retry would re-read `count = 1`,
proceed to set the second engineer off call, and commit — `count = 0`. The retry loop makes
the operation *serializable*, not *correct*: the correctness still comes from your invariant
check running against the fresh read.
</details>

---

## 3. Self-check quiz (answers in §5 — don't peek)

1. PostgreSQL implements how many *distinct* isolation behaviours, and what happens if you
   ask for `READ UNCOMMITTED`?
2. Under `READ COMMITTED`, when exactly is a snapshot taken? Under `REPEATABLE READ`?
3. An `UPDATE` and a `DELETE` on the same logical row leave what behind physically? Describe
   `xmin`/`xmax` on each tuple.
4. Give the visibility rule for a tuple against snapshot `(xmin, xmax, xip[])` in one
   sentence per clause.
5. Why does a single long-open transaction cause table bloat across the *whole* database?
6. Distinguish a non-repeatable read from a phantom read. Which does PostgreSQL's
   `REPEATABLE READ` prevent, and how does that differ from the SQL standard?
7. Distinguish a lost update from write skew by the "same row / different rows" tell. Which
   isolation level is the minimum that prevents each in PostgreSQL?
8. Name the two different error *messages* that share `SQLSTATE 40001` and which level
   produces each.
9. What does `READ COMMITTED` do when an `UPDATE ... WHERE p` reaches a row another
   transaction just committed a change to? (Name the behaviour and its consequence.)
10. What is an SIRead lock, does it block anything, and why can it cause a *false-positive*
    `40001`?
11. Write the full-jitter backoff formula and say why fixed backoff is worse under
    contention.
12. Your retried transaction body inserts a row representing a real-world payment. What two
    things make that body safe to run more than once?
13. Where should the transaction boundary go relative to an HTTP request, and what must never
    appear between `BEGIN` and `COMMIT`?
14. Give three mechanisms that enforce "no two overlapping bookings for a room" and rank them
    by preference.
15. `SERIALIZABLE` guarantees serializable execution only under what condition about the
    *other* transactions touching the same data?

---

## 4. Mini-project — a concurrency-correct wallet + booking service

Build a small .NET service (console or minimal API) over the lab schema. Requirements:

### 4.1 Features

1. `POST /transfer {from, to, amount}` — move funds between two `account` rows.
   - Invariant: no balance goes negative; the two updates are atomic; total money is
     conserved.
2. `POST /wallet/{id}/credit {amount, idempotencyKey}` — add funds.
   - Invariant: applying the same `idempotencyKey` twice credits once.
3. `POST /rooms/{id}/book {from, to}` — book a time range for a room.
   - Invariant: no two bookings for the same room overlap.
4. `GET /accounts/{id}/statement` — return the balance and the last N ledger entries as of
   **one consistent instant**.
5. A background **outbox poller** that publishes a `TransferCompleted` event (just log it)
   after each successful transfer, exactly once.

### 4.2 Constraints on your implementation

- Every write goes through `TransactionRetry.ExecuteAsync` (lesson 08). Bodies must be
  re-run-safe.
- Choose the isolation level per endpoint using the lesson-10 decision guide and **write a
  one-line justification in a comment** for each.
- Prefer a declarative constraint over an isolation level wherever possible (feature 2:
  `UNIQUE (idempotency_key)`; feature 3: `EXCLUDE USING gist (room_id WITH =, during WITH
  &&)`).
- No external I/O between `BEGIN` and `COMMIT`. The event publish is post-commit / via the
  outbox.
- Add a `ledger` table with a `UNIQUE` natural key so a retried transfer can't double-post.

### 4.3 Acceptance tests (write these first)

| Test | Setup | Assert |
|---|---|---|
| T1 concurrent transfers | 50 parallel `A→B` and `B→A` transfers of random amounts | final `sum(balance)` unchanged; no negative balance; no lost transfer |
| T2 deadlock resistance | 50 parallel transfers with random `from`/`to` among 5 accounts | zero unhandled `40P01`; retries bounded; all eventually succeed or fail with a *business* error |
| T3 idempotent credit | fire the same `{amount, idempotencyKey}` 20× in parallel | balance increases by `amount` exactly once; 19 requests observe "already applied" |
| T4 no overlapping bookings | 20 parallel bookings for one room with overlapping ranges | exactly one succeeds; the rest get a clean 409 (`23P01` mapped) |
| T5 consistent statement | run `GET statement` in a loop while transfers run | balance always equals the sum of that statement's own ledger view; never a torn read |
| T6 outbox exactly-once | kill the process between `COMMIT` and the poller; restart | every completed transfer publishes exactly one event |

### 4.4 Stretch goals

- Add `GET /oncall` + `POST /oncall/{engineer}/leave` with the "≥ 1 on call" invariant.
  Implement it with `SERIALIZABLE` + retry **and** with a guard-row lock; compare abort rates
  under load.
- Add metrics from lesson 08 §8.5 and a `/debug/locks` endpoint running the lesson-10 §7.1
  blocking-tree query.
- Make one hot account receive 90% of transfers; measure `tx_retries_exhausted_total`; switch
  that path to an append-only ledger with async rollup and re-measure.

---

## 5. Quiz answers

1. **Three** (`READ COMMITTED`, `REPEATABLE READ`, `SERIALIZABLE`). `READ UNCOMMITTED` is
   accepted but runs as `READ COMMITTED`. (Lesson 06 §2.)
2. `READ COMMITTED`: at the **start of every statement**. `REPEATABLE READ`: **once**, at the
   first non-transaction-control statement (not at `BEGIN`). (Lesson 02 §5.4.)
3. `INSERT` tuple: `xmin = inserting xid`, `xmax = 0`. After `UPDATE` (xid U): old tuple
   `xmax = U`, plus a **new** tuple `xmin = U, xmax = 0`. After `DELETE` (xid D) of the live
   tuple: `xmax = D`. All versions stay on disk until `VACUUM`. (Lesson 02 §5.1.)
4. Tuple visible iff: **(a)** its `xmin` is committed **and** not hidden by the snapshot
   (`xmin < xmin` bound, or `< xmax` bound and not in `xip`) — else "doesn't exist yet";
   **and (b)** its `xmax` is 0/aborted/in-progress-or-future/lock-only — else "already
   deleted". (Lesson 02 §5.3.)
5. Its snapshot pins the global `OldestXmin`; `VACUUM` may not remove any dead tuple newer
   than that, anywhere in the database — so dead tuples from *all* tables accumulate for the
   transaction's lifetime. (Lesson 02 §5.6.)
6. Non-repeatable read = a **row you already read** changed/disappeared. Phantom = **new rows
   appear** (or matching rows vanish) in a **predicate** you re-run. PostgreSQL `REPEATABLE
   READ` prevents **both**; the SQL standard only requires `SERIALIZABLE` to prevent
   phantoms, so PostgreSQL is stricter. (Lesson 04 §2, §4.3.)
7. Lost update = two writers, **same row**, second write from a stale read. Write skew = two
   writers, **different rows**, joint result breaks an invariant. Minimum level in
   PostgreSQL: lost update → `REPEATABLE READ` (second writer gets `40001`); write skew →
   `SERIALIZABLE`. (Lesson 05 §2, §4.)
8. `"could not serialize access due to concurrent update"` — `REPEATABLE READ` (and
   `SERIALIZABLE`) on a direct write/write conflict. `"could not serialize access due to
   read/write dependencies among transactions"` — `SERIALIZABLE` SSI cycle. (Lesson 06 §5.)
9. It **waits** for that transaction; if it committed, it **re-fetches the latest row version
   and re-evaluates the `WHERE`** (EvalPlanQual). Consequence: the statement can act on rows
   newer than its own scan snapshot, and never raises `40001`. (Lesson 06 §5.1; lesson 04
   lab 6.4.)
10. A non-blocking marker that records "this `SERIALIZABLE` transaction read this
    tuple/page/relation," used only for conflict detection. It blocks nothing. Under memory
    pressure it **escalates** tuple→page→relation, so it can flag a conflict on data a
    transaction didn't really touch → spurious `40001`. (Lesson 06 §3, §5.3.)
11. `wait = random(0, min(cap, base · 2^attempt))`. Fixed backoff makes all transactions that
    collided at time *t* retry at *t + delay* together and collide again; jitter spreads
    them. (Lesson 08 §4.3.)
12. **(a)** No un-rolled-back side effects in the body — the event publish is post-commit or
    via an outbox row written inside the transaction. **(b)** The insert has a `UNIQUE`
    natural/idempotency key and uses `ON CONFLICT DO NOTHING` (or `MERGE`), so a replay
    no-ops. (Lesson 08 §4.4, §8.3.)
13. Around **one unit of work** — open at the first write (or first read that must be
    consistent with those writes), commit right after the last. Never between `BEGIN` and
    `COMMIT`: HTTP calls, message publishes, email, file I/O, or any non-PostgreSQL wait.
    (Lesson 09 §2, §5.2.)
14. **(1)** `EXCLUDE USING gist (room_id WITH =, during WITH &&)` — declarative, holds
    against every writer, no retry. **(2)** `SELECT ... FOR UPDATE` on the room's guard row —
    serialises writers with waits. **(3)** `SERIALIZABLE` + retry — works but costs
    retries/idempotency and needs every writer serializable. Prefer 1 > 2 > 3. (Lesson 05
    §4.2, §6.5C; lesson 10 §3.3.)
15. Only if **every** transaction touching that data is also `SERIALIZABLE`. SSI reasons only
    about serializable participants; one `READ COMMITTED` writer can still break the
    invariant. (Lesson 06 §5.3; lesson 10 §5.)

---

## 6. ACID self-audit (ACID.json item L06.6.2)

Find **one real claim about ACID in code you have already shipped that was actually false.**
Work through these usual suspects; for each that applies, write: *the claim*, *the code
pattern that assumes it*, *the exact interleaving/scenario that breaks it*, *the fix*, and
*which lesson covers it*.

- [ ] `SELECT x` … compute in app … `UPDATE SET x = @literal` — assumed no one writes between
      (lost update; lessons 04–05).
- [ ] `count(*)` / `SUM()` check then `INSERT`/`UPDATE` to enforce a limit or invariant —
      assumed "in a transaction ⇒ safe" (write skew; lesson 05).
- [ ] `BeginTransaction` held across an HTTP call, queue publish, or `Task.Delay` — assumed
      it's fine / assumed atomic across the call (lessons 03, 09).
- [ ] `publish event` / `send email` **before** `COMMIT`, or `INSERT ... RETURNING id` used
      to publish before commit — assumed `ROLLBACK` would undo it (lessons 03, 08).
- [ ] `catch (Exception) { retry(); }` around a DB unit of work — assumed all DB errors are
      transient and the body is idempotent (lesson 08).
- [ ] retrying just the failed statement / on the same aborted transaction — assumed the
      transaction/snapshot was still usable (lesson 08 §4.1–4.2).
- [ ] fixed-delay retry — assumed it spreads load (it re-synchronises the herd; lesson 08 §4.3).
- [ ] logged `"committed"` on a path where the connection could drop during `COMMIT` —
      assumed a failed `CommitAsync` means "definitely rolled back" (lesson 03 §8.2).
- [ ] relied on contiguous `serial`/identity values (no gaps) — assumed sequences are
      transactional (lesson 03 §5.1, §7.2).
- [ ] assumed `READ UNCOMMITTED` (or a `WITH (NOLOCK)` habit from SQL Server) does something
      in PostgreSQL — it's an alias for `READ COMMITTED` (lesson 04 §4.1).
- [ ] assumed `COMMIT` implies durability on replicas — depends on `synchronous_commit` /
      `synchronous_standby_names` (lesson 03 §5.4).
- [ ] mixed `SERIALIZABLE` and `READ COMMITTED` writers on the same tables — assumed SSI
      still protected the invariant (lesson 06 §5.3; lesson 10 §5).
- [ ] used `SELECT ... FOR UPDATE` to block an `INSERT` that would match the predicate —
      row locks don't cover not-yet-existing rows (lesson 07 §2).
- [ ] a `DEFERRABLE INITIALLY DEFERRED` constraint assumed to be checked per statement
      (lesson 03 §3).

**Deliverable:** a short write-up of the one (or more) you found, plus the fix you'd ship and
how you'd add a two-connection regression test (lesson 10 §8 step 1).

---

## 7. Where to go next

- The follow-up prompts at the bottom of `../prompt.md` (three-transaction write skew and the
  SSI conflict graph; `pg_locks`/`pg_stat_activity` walk-throughs; a failing real-world
  scenario per level; a graduated quiz).
- PostgreSQL docs, chapter **"Concurrency Control"** (13) — "Transaction Isolation",
  "Explicit Locking", "Serialization Failure Handling".
- The `postgres-pro` skill references in `../.agents/skills/postgres-pro/references/` —
  `maintenance.md` (VACUUM, `pg_locks`, wraparound), `performance.md` (indexes behind the
  constraints you'll add).
- Npgsql docs — "Basic Usage" (transactions, savepoints) and `PostgresErrorCodes` /
  `PostgresException`.

---

## 8. Key takeaways (whole module)

- **MVCC** keeps every row version; a **snapshot** decides which you see; **`READ COMMITTED`**
  re-snapshots per statement, **`REPEATABLE READ`/`SERIALIZABLE`** once per transaction.
- **ACID:** A and D are near-free from the WAL; **C** is your invariants (only declared
  constraints are enforced); **I** is the dial.
- **Anomaly ladder:** dirty read (never in PG) → non-repeatable read & phantom (stop at
  `REPEATABLE READ`) → lost update (stop at `REPEATABLE READ`, via `40001`) → write skew &
  read-only anomaly (stop only at `SERIALIZABLE`).
- **`SERIALIZABLE` = SSI + `40001` + a bounded, full-jitter retry loop + an idempotent body +
  every participant serializable.**
- **Layer correctness cheapest-first:** constraint → atomic write → explicit lock → isolation
  level.
- **Boundaries:** one transaction per unit of work; no external I/O inside; savepoints only
  for real try/fallback.
- **Operate it:** watch rollback ratio, deadlocks, oldest transaction/snapshot age,
  `idle in transaction`, SIReadLock granularity, and app-side retry histograms; alert on
  exhausted retries and stuck transactions.
