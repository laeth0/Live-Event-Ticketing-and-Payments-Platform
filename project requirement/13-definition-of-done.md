# 13 · Definition of Done, Portfolio Packaging & Self-Audit

---

## 13.1 Acceptance checklist

The project is finished when every box is ticked. Boxes are ordered by phase, so this doubles
as a progress tracker.

### Environment and schema

- [ ] `docker compose up` reaches a healthy API with migrations applied in < 60 s from a clean
      volume.
- [ ] Two `psql` lab sessions can be opened with one command.
- [ ] Every migration sets `lock_timeout`; every index on a populated table is built
      `CONCURRENTLY`; every constraint on a populated table uses `NOT VALID` + `VALIDATE`.
- [ ] For each of INV-1…INV-12 you can name the database object that enforces it, or state
      explicitly that none does and why.
- [ ] A detection query exists in `db/invariants/` for all twelve.

### Correctness

- [ ] `GET /ops/invariants` returns green on a seeded database and stays green during every load
      test.
- [ ] `T-Q1` (2 000 randomised operations, 12 workers) passes, including INV-6 — the
      materialised balance equals the sum of its journal entries for every account.
- [ ] `Σ journal_entry.amount_minor = 0` across the entire database at all times.
- [ ] Every concurrency test ends by asserting the invariants, not just HTTP statuses.
- [ ] Every concurrency test runs ≥ 20 repetitions in CI without flaking.

### The anomalies

- [ ] **Lost update** (CH-01) reproduced in `psql` and in C#, then fixed **four** ways with a
      measurement table.
- [ ] **Write skew** (CH-02a) reproduced at `REPEATABLE READ`, prevented at `SERIALIZABLE`, with
      the `SIReadLock` output captured; then fixed **three** ways with a comparison table.
- [ ] **Write skew on a `SUM`** (CH-02b) reproduced and fixed with a counter + `CHECK`.
- [ ] **Phantom read** (CH-03) reproduced, shown to persist at `REPEATABLE READ`, shown to be
      unfixable by `FOR UPDATE`, then fixed **two** ways with a comparison.
- [ ] **Deadlock** (CH-04) reproduced with the full `DETAIL` captured, then eliminated by
      canonical lock ordering across 200 randomised transfers.
- [ ] **Serialization failures** (CH-05): the retry helper exists, only `40001`/`40P01`/`40000`
      are retried, the three-way backoff comparison is measured, a forced mid-body `40001`
      produces exactly one of everything, and retry exhaustion was induced and then designed
      away.
- [ ] **Read phenomena** (CH-06): dirty read shown to be impossible, non-repeatable read shown
      and fixed, EvalPlanQual behaviour observed at `READ COMMITTED` versus `40001` at
      `REPEATABLE READ`, and "invisible ≠ absent" demonstrated.
- [ ] **Queue contention** (CH-07): `SKIP LOCKED` scaling table produced; a worker killed
      mid-slow-job is recovered exactly once.

### Engineering discipline

- [ ] Exactly **one** place in the codebase opens a write transaction, enforced by an
      architecture test.
- [ ] No external I/O (`HttpClient`, bus, file) inside any retried delegate, enforced by an
      architecture test.
- [ ] Every `BeginTransactionAsync` call passes an **explicit** isolation level with a one-line
      justification comment.
- [ ] Every non-idempotent `POST` requires an `Idempotency-Key`, stores its response, and
      rejects a fingerprint mismatch with `422`.
- [ ] The SQLSTATE → HTTP mapping exists in exactly one middleware and is total.
- [ ] `await using` on both connection and transaction, and a `CancellationToken` on every
      database call, everywhere.
- [ ] No naive strategy can be enabled in `Production` (startup guard throws).

### Operability

- [ ] Metrics M-1…M-14 emitted, with `unit_of_work` and `strategy` labels.
- [ ] Three Grafana boards built **before** the first load test.
- [ ] Every alert in §10.5 has a rule and a runbook entry.
- [ ] `GET /ops/locks`, `/ops/transactions`, `/ops/invariants` all work and were actually used
      during a load test.
- [ ] The five runbook procedures in §10.5 are written, including the INV-6 repair procedure.

### Load and findings

- [ ] The `onsale` k6 scenario meets the §3.1 latency and throughput budgets with zero invariant
      violations, `40001` ≤ 2 %, `40P01` ≤ 0.05 %, retries exhausted = 0.
- [ ] The three-way `payment_route` comparison and the two-way per-customer-limit comparison are
      completed **with your own numbers**.
- [ ] The hot-row redesign (append-only + rollup) is implemented and measured before and after.
- [ ] Predicate-lock escalation was induced, observed in `pg_locks`, and mitigated.
- [ ] `docs/findings/CH-01.md` … `CH-07.md` all exist and contain measurements, not prose.
- [ ] `docs/decisions/` contains one ADR per §12 section, written in your own words.

---

## 13.2 The ACID self-audit (roadmap lesson 11 §6)

Required deliverable: `docs/findings/self-audit.md`.

Find **at least one real claim about ACID in code you have already shipped** (before this
project) that was actually false. For each, write: *the claim*, *the code pattern that assumes
it*, *the exact interleaving that breaks it*, *the fix you would ship*, and *how you would write
the two-connection regression test*.

Work through the usual suspects and keep the ones that apply:

- [ ] `SELECT x` … compute in the app … `UPDATE SET x = @literal` — assumed nobody writes
      between (lost update).
- [ ] `count(*)` or `SUM()` check then `INSERT`/`UPDATE` to enforce a limit — assumed "in a
      transaction ⇒ safe" (write skew).
- [ ] A transaction held open across an HTTP call, a queue publish, or a `Task.Delay`.
- [ ] An event published, or an email sent, **before** `COMMIT`.
- [ ] `catch (Exception) { retry(); }` around a database unit of work.
- [ ] Retrying just the failed statement, or reusing the aborted transaction.
- [ ] Fixed-delay retry, assumed to spread load.
- [ ] Logged `"committed"` on a path where the connection could have dropped during `COMMIT`.
- [ ] Relied on contiguous `serial`/identity values.
- [ ] Assumed `READ UNCOMMITTED` (or a `WITH (NOLOCK)` habit) does something in PostgreSQL.
- [ ] Assumed `COMMIT` implies durability on replicas.
- [ ] Mixed `SERIALIZABLE` and `READ COMMITTED` writers on the same tables.
- [ ] Used `SELECT … FOR UPDATE` to block an `INSERT` that would match the predicate.
- [ ] Assumed a `DEFERRABLE INITIALLY DEFERRED` constraint is checked per statement.

Then answer the fifteen questions in §12.19 **without notes**, and record the ones you had to
look up — those are what you re-read.

---

## 13.3 Portfolio packaging

The code is table stakes. These are what make it legible to someone reviewing it in ten
minutes.

### `README.md` at the repository root

1. **One paragraph** on what Concourse is and why it exists (adapt §0.4).
2. **The headline claim, with evidence.** *"Twelve invariants, twelve detection queries, and a
   test suite that fails on the naive implementation of each."* Link to the findings.
3. **`docker compose up` → a running system.** Then a copy-pasteable sequence: create an event,
   publish it, hold a seat, check out, pay, refund, and watch `/ops/invariants` stay green.
4. **The anomaly demo.** `dotnet run --project tools/AnomalyLab -- lost-update` replays a
   timeline against a live database and prints the before/after. One command per anomaly.
5. **The isolation-level table from §7.7**, with the sentence that matters:
   *eighteen of twenty-two write paths are `READ COMMITTED`, made correct by a constraint, an
   atomic write, or a lock — and here is the measurement that shows why.*
6. **Links** to §5 (scenarios), §6 (drills), §12 (decisions), and the findings.

### `docs/findings/` — the actual portfolio

Seven files, one per drill, each containing: the reproduction, the fix or fixes, a
**measurement table**, and a paragraph of interpretation. This is the artefact that
distinguishes the project. Anybody can write `SELECT … FOR UPDATE`; a table showing four fixes
to the same bug with throughput, p99, retries and aborts is evidence of judgement.

### `docs/decisions/` — ADRs

One per §12 section. Context, decision, alternatives considered, consequences. Short — half a
page each. Write them *as you implement*, not at the end.

### Demo script (5 minutes, for an interview)

1. `docker compose up`; show `/ops/invariants` green. *(20 s)*
2. Flip `Concurrency:InventoryStrategy` to `Naive`; run `T-C1`; show 20 tickets sold from 15.
   *(60 s)*
3. Flip back to `AtomicWrite`; same test passes; explain the `WHERE` guard and rows-affected.
   *(60 s)*
4. Run the write-skew timeline live in two `psql` windows; show `REPEATABLE READ` failing and
   `SERIALIZABLE` catching it with `40001`; show the `SIReadLock` rows. *(90 s)*
5. Show the three-way comparison table; explain why the materialised counter wins. *(60 s)*
6. Start the `hot-seat` load test; show the Grafana concurrency board; point at conflicts split
   by `concurrent_update` versus `rw_dependency`, and at `retries_exhausted = 0`. *(60 s)*

If you can drive that script without notes, you can defend anything in this document.

---

## 13.4 Stretch goals (only after everything above is green)

| Goal | What it adds |
|---|---|
| Partition `money.journal_entry` by month | Constraint and index behaviour across partitions; how `UNIQUE` and FK semantics change; detach-old-partition as an archival strategy |
| Ticket resale marketplace | Seat holds that genuinely need **overlapping-in-time** ranges, which is where `EXCLUDE` stops being replaceable by a partial unique index |
| Multi-currency wallets | Cross-currency journals, FX rate snapshots, and why a rate read must be part of the same unit of work |
| A real broker (RabbitMQ/Kafka) behind the existing bus interface | Real redelivery, poison messages, consumer-group semantics — the distributed-systems layer on top of a correct database layer |
| `pg_stat_statements`-driven index review | Prove each index earns its place; find the one you added that nothing uses |
| Chaos schedule in CI | `T-X1`…`T-X6` on every nightly build, so recovery correctness does not rot |

---

## 13.5 The one-sentence summary you should be able to give

> *I built a ticketing and payments platform whose twelve business invariants are each enforced
> at the cheapest correct layer — nine by database constraints, two by row locks, one by
> serializable isolation — and I have the failing tests, the reproduction timelines and the
> throughput measurements that show why each choice is the right one.*
