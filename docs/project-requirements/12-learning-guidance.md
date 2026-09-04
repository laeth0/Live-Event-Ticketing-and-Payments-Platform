# 12 · Learning Guidance — Every Major Decision

For each decision: **why this approach**, **what the alternatives are**, and **what you are
trading away**. Write one ADR per section in `docs/decisions/` as you implement it — in your
own words, with your own measurements. That file set is what turns this from "a project I
built" into "a project I can defend".

---

## 12.1 The correctness ladder — the meta-decision

**The rule.** For every invariant, walk the ladder top to bottom and stop at the first rung
that fits:

1. **Declarative constraint** — `UNIQUE`, partial unique, `CHECK`, `FOREIGN KEY`, `EXCLUDE`.
2. **Atomic single-statement write** — `UPDATE … SET n = n + $1 WHERE <guard>`, `INSERT … ON
   CONFLICT`, data-modifying CTE. Check rows-affected.
3. **Explicit lock** — `SELECT … FOR UPDATE` on the row the invariant is *about*;
   `SKIP LOCKED` to claim work; an advisory lock for a scope with no row.
4. **Isolation level** — `REPEATABLE READ` for a consistent multi-read; `SERIALIZABLE` + a
   bounded jittered retry + an idempotent body for cross-row invariants you could not make
   declarative.

**Why this order.** Each rung is enforced by fewer parties than the one below it:

| Rung | Holds against | Fails if |
|---|---|---|
| 1 constraint | **every** writer, at every level, forever — including a migration, a future endpoint, and a human in `psql` | the invariant is genuinely inexpressible per row |
| 2 atomic write | every writer that uses this statement | someone writes a different query |
| 3 lock | every writer that takes the same lock | someone forgets to take it |
| 4 `SERIALIZABLE` | only transactions that are **themselves** `SERIALIZABLE` | one `READ COMMITTED` writer touches the same data |

**The concrete result in this project.** Nine of twelve invariants land on rung 1 or 2; two use
rung 3; exactly one genuinely needs rung 4 — and even that one (INV-9) turned out to be
expressible as rung 1 once the count was materialised onto the parent row. That is the finding
you should carry away: *most "we need `SERIALIZABLE`" is really "we needed a unique index" or
"we needed `SET n = n + 1`"*.

**The alternative philosophy** — "put everything at `SERIALIZABLE` and stop thinking" — is
defensible for a correctness-critical, low-contention domain **with** the retry and idempotency
infrastructure in place. Its costs are real and you will measure them in Phase 7: mandatory
retry loops on every path, retry storms on hot rows, an idempotency tax on every body,
predicate-lock memory and false-positive aborts under escalation, no coverage on replicas or
across databases, and a fatter latency tail. §12.5 goes deeper.

---

## 12.2 Money as `bigint` minor units

**Why.** Exact integer arithmetic, no rounding surprises, no locale ambiguity, trivially fast,
and it makes `CHECK (balance_minor >= 0)` a genuinely cheap constraint. `bigint` holds ±9.2
quintillion cents — far past any plausible ceiling.

**Alternatives.** `numeric(14,2)` — also exact, and self-documenting about the scale; the
course's lab schema uses it. `money` — PostgreSQL's own type, locale-dependent and generally
avoided. `double precision` — always wrong for money.

**Trade-offs.** Minor units require discipline at the API boundary (every JSON amount is an
integer, every display multiplies by the currency's exponent) and they encode the scale
implicitly, which is awkward for currencies with 0 or 3 decimal places. `numeric` avoids both
problems at the cost of slightly slower arithmetic and a larger on-disk representation. Either
is defensible; **`float` is not**, and the reason to be able to say why is that "we used a
double for money" is a real production incident category.

---

## 12.3 UUIDv7 for business keys, `bigint identity` for append-only tables

**Why UUIDv7.** Application-generated (so a client can supply an id and retries are naturally
idempotent), globally unique (no coordination), and **time-ordered**, so B-tree inserts land at
the right edge instead of scattering random writes across the whole index — the property that
makes random UUIDv4 primary keys a write-amplification problem at scale.

**Why `bigint identity` for `journal_entry` and `outbox`.** They are append-only, high-volume,
never referenced by an external client, and benefit from the smallest possible key: 8 bytes
instead of 16, in a table that will have tens of millions of rows and several indexes.

**Alternatives.** `bigserial` everywhere (smallest, but requires a round trip to learn the id
and leaks volume to clients); UUIDv4 everywhere (simplest, worst index locality); ULID/KSUID
(equivalent to UUIDv7, less standard).

**Trade-offs.** UUIDs are 16 bytes and unreadable in logs. Sequences are non-transactional, so
identity values have gaps after a rollback — which is fine, and lesson 03 §7.2 makes the point
that *encoding meaning in contiguity is the actual bug*.

---

## 12.4 Splitting hot counters onto their own rows

**Why.** `ticket_type_inventory` and `account_balance` exist as separate tables from
`ticket_type` and `account` for three reasons, all of them concurrency reasons:

1. **Contention isolation.** An organizer editing a price must not collide with 400
   reservations per second. Different rows ⇒ different locks ⇒ no interference, and the
   optimistic `version` on the cold row never fights the sales path.
2. **HOT updates.** The hot rows have **no secondary indexes**, so every update is a heap-only
   tuple: no index entries added, less bloat, cheaper `VACUUM` (`L02 §5.5`). Verify with
   `n_tup_hot_upd / n_tup_upd` (M-DB8) — you should see > 95 %.
3. **Row width.** Updating a narrow row rewrites less; MVCC copies the whole tuple on every
   update, so a wide catalog row with a counter in it is a bloat engine.

**Alternative.** One table with everything. Simpler joins, one fewer insert on creation.

**Trade-offs.** An extra join for reads, an extra insert at creation, and the risk of an
orphaned counter row (mitigated by `ON DELETE CASCADE` and a creation path that is one
transaction). At low volume, the single table is fine. This project runs at 400 writes/s on one
row, which is exactly where the split starts paying — and you will be able to show the HOT
ratio to prove it.

---

## 12.5 Why `SERIALIZABLE` is not the default here

**Why not.** SSI only helps if **every** transaction touching the data is `SERIALIZABLE`
(`L06 §5.3`). This system has background workers, a migration path, an ops surface and a bulk
importer. A single `READ COMMITTED` writer defeats the guarantee — and worse, defeats it
*silently*, which is the failure mode that produces the most confident wrong code. Test `T-I9`
demonstrates this deliberately.

Additionally: predicate locks (`SIReadLock`) live in shared memory sized by
`max_pred_locks_per_transaction × (max_connections + max_prepared_transactions)`. Under
pressure they escalate tuple → page → relation, which produces **false-positive** `40001`s on
transactions that were never really in conflict. You will force this in Phase 7 and watch it in
M-DB7.

**When it *is* the right answer.** A cross-row invariant that cannot be expressed as a
constraint, cannot be reduced to a single-row atomic write, and cannot be protected by locking
a row that the invariant is about. In this system: nothing, once you materialise the counters —
which is the whole point of Phase 4's comparison.

**When it is the right *default*.** A ledger-like domain with low write contention, a team that
has already built the retry helper and the idempotency discipline, and where reasoning about
every interleaving by hand is more expensive than paying for occasional retries. That is a real
and defensible position; the honest version of it includes the cost table in `L10 §5`.

---

## 12.6 `EXCLUDE USING gist` versus a partial unique index for seats

**Why `EXCLUDE`.** Holds are time-boxed. Modelling a hold as *a seat occupied over an interval*
means the database can reject overlaps directly, and — this is the elegant part — **expiry is
automatic**: a hold whose range has passed no longer overlaps a new `[now, now + 15m)` range,
so the seat frees itself without any cleanup job. The reaper becomes bookkeeping rather than a
correctness dependency, which removes an entire class of "the reaper was down so seats were
stuck" incidents.

**Alternative A: `held_until timestamptz` + a partial unique index on `(event_id, seat_id)
WHERE released_at IS NULL`.** Cheaper (B-tree, not GiST) and simpler to explain. But
correctness now depends on *every* reader remembering `AND held_until > now()`, and on the
reaper actually running to clear the row so the unique index lets the next customer in. One
forgotten predicate is a bug the database cannot catch.

**Alternative B: `event_seat.status` flag updated under `FOR UPDATE`.** Familiar, works, and
serialises cleanly on one row. But it cannot express "this seat is held from 20:00 to 20:15 and
sold from 20:15 forever", it needs the reaper for correctness, and the flag can drift from
reality.

**Trade-offs of `EXCLUDE`.** GiST indexes are larger and slower to update than B-trees; the
constraint requires `btree_gist`; range types are unfamiliar to many teams; and debugging a
`23P01` requires knowing what the constraint means. Measure the insert cost in Phase 5 — if the
overhead is material and your holds never need to overlap in time (no resale windows, no
timed re-offers), Alternative A is a legitimate choice. **Make it with numbers, not by
preference.**

---

## 12.7 Guard row versus `SERIALIZABLE` for the per-customer limit

**Why the guard row.** `customer_event_allocation` converts a predicate over rows that do not
exist yet into **one physical row** that all of a customer's concurrent requests must update.
`READ COMMITTED` then handles it natively, with a `CHECK` as the backstop and zero aborts. This
technique — *materialising the conflict* — is the single most transferable idea in the project,
and it appears four times: the allocation row, the promo counter, the refund counter, and
`event.enabled_route_count`.

**Alternative: `SERIALIZABLE` + retry.** No schema change, no denormalisation to keep in sync,
and it is correct for *any* predicate, including ones you have not thought of yet.

**Trade-offs.**

| | Guard row | `SERIALIZABLE` |
|---|---|---|
| Aborts under contention | none (lock waits, µs) | high — SSI is detecting a real conflict every time |
| Extra schema and code | a table, an upsert, a `CHECK` | none |
| Holds against a non-participating writer | **yes** | no |
| Generalises to a new predicate | no — needs a new counter | **yes** |
| Failure mode when contended | queueing | retry storm |

The guard row wins here because there is exactly one predicate and it is stable. If the rule
were "at most 6 tickets, unless the customer is a member, unless the event is a benefit, unless
…", the counter approach starts to sprawl and `SERIALIZABLE` becomes the better trade. Say that
out loud in your write-up: **the guard row wins on *this* predicate, not in general.**

---

## 12.8 Optimistic versus pessimistic concurrency

**The rule of thumb.** Optimistic when conflicts are **rare** and retrying is cheap.
Pessimistic when conflicts are **common** and the work between read and write is short.

**In this project:**

| Path | Choice | Reason |
|---|---|---|
| Organizer edits an event (F-4) | **Optimistic** (`version`) | Conflicts are hourly; the human think-time gap between read and write is minutes — no lock should span that |
| Wallet payment (F-14) | **Pessimistic** (`FOR UPDATE`) | Conflicts on a popular account are constant; a lock wait of microseconds beats an abort and a full re-run |
| GA inventory (F-8) | **Neither** — atomic write | The whole decision fits in one statement, so there is no window to protect |

**Why OCC degrades under high single-row contention.** Every conflict wastes the entire attempt
— the reads, the decision, the round trips — and then the retry re-collides with the same herd.
At high contention on one row, pessimistic locking is strictly better: work is queued instead
of discarded. At the extreme, neither is right and the answer is a design change: an
append-only ledger with `SUM`/rollup, or sharded sub-counters, which is the Phase-7 exercise.

---

## 12.9 An append-only ledger, enforced by rules

**Why.** Financial history must never be edited. A refund is a *new reversing journal*, not an
`UPDATE` of the original. This makes every balance reconstructible, makes INV-6 checkable, and
means a concurrency bug is *visible* rather than overwritten.

**Why rules rather than permissions.** `CREATE RULE … DO INSTEAD NOTHING` on `UPDATE`/`DELETE`
makes the table append-only for every role including the app's, without needing a separate
role/grant scheme in a project that is not about authorisation.

**The trade-off, stated honestly.** `DO INSTEAD NOTHING` **silently discards** the write. A
developer who writes an `UPDATE money.journal_entry …` sees "0 rows affected" and may misread
it. The stricter alternative is a `BEFORE UPDATE OR DELETE` trigger that raises an exception —
noisier, but unmissable. A third option is `REVOKE UPDATE, DELETE ON money.journal_entry FROM
concourse_app`, which fails with a permission error and is arguably the most honest of the
three. Pick one, write down why, and note that the silent variant is the one you would *not*
ship to a large team.

---

## 12.10 A deferred constraint trigger for "every journal balances"

**Why deferred.** The transaction must be *allowed* to be unbalanced between the first entry
insert and the last one. `DEFERRABLE INITIALLY DEFERRED` moves the check to `COMMIT`, which is
exactly the semantics you want and is the one place in this system where the difference between
statement-time and commit-time checking is load-bearing (`L03 §3`).

**Alternatives.** Check it in application code (works until someone writes entries from a
different path — and someone always does). A multi-row `CHECK` (impossible — `CHECK` is
per-row). Insert both entries in one statement and check in the statement (works for two-entry
journals, breaks for three-legged journals with a platform fee — which this system has).

**Trade-offs.** A per-row `AFTER` trigger that queries the whole journal is not free: for an
N-entry journal it runs N times, each doing an aggregate. For journals of 2–4 entries this is
irrelevant; for a bulk posting of thousands of entries it would matter, and the fix would be a
per-statement trigger or a deferred check keyed off a collected set. Also: deferred violations
surface at `COMMIT`, so the error's stack trace points at the commit, not at the offending
insert — which is confusing the first time and is worth a comment in the code.

---

## 12.11 Idempotency: unique key, stored response, or both

**Why both.** A `UNIQUE` constraint alone makes the *effect* at-most-once but gives the retrying
client a `409` on a request that actually succeeded — so the client cannot distinguish "already
done" from "failed". Storing the response makes the replay **indistinguishable from the
original**, which is what clients need. And the request fingerprint prevents the nastier bug:
a client reusing a key for a *different* request and receiving a cached success for a purchase
it never made.

**Alternatives.** Natural keys only (`UNIQUE (cart_id)` — works, but only where a natural key
exists, and gives no replay semantics). `INSERT … ON CONFLICT DO NOTHING` plus a read-back
(good for simple inserts, insufficient for multi-step units of work). Client-side dedup only
(hopeless — the client is the thing that is retrying).

**Trade-offs.** A key table is extra writes on every request and needs a purge job. Storing
response bodies costs space and can leak data if the table is over-retained (24 h here). The
`in_progress` state creates a small window where a genuine concurrent duplicate gets a `409`
rather than the eventual response — the alternative (waiting for the first to finish) trades
that for holding a request open, and is worse under load.

---

## 12.12 Transactional outbox versus publish-after-commit versus 2PC

**Why the outbox.** The event row is written **in the same transaction** as the state change,
so "committed" and "will be published" are the same fact. A crash at any instant leaves a
consistent state: either both happened or neither. A separate poller publishes at-least-once,
and consumers deduplicate.

**Alternative A: publish inside the transaction.** If the commit then fails, you have emitted an
event for something that never happened — a *ghost event*. Also holds the transaction open
across network I/O.

**Alternative B: commit, then publish.** No ghost events, but a crash between the commit and the
publish loses the event permanently, with no record that it was owed.

**Alternative C: 2PC / `PREPARE TRANSACTION`.** Genuinely atomic across two resource managers,
and genuinely not worth it: a prepared transaction whose coordinator dies holds locks and pins
the VACUUM horizon **indefinitely**, until a human resolves it. It is one of the few ways to
take a PostgreSQL cluster down by doing nothing.

**Trade-offs of the outbox.** At-least-once, not exactly-once — consumers must be idempotent
(`ops.processed_event`). It adds latency (poll interval), a table that grows and needs pruning,
and a worker to operate. Ordering is only per-aggregate unless you enforce more. All of that is
cheaper than either ghost events or a stuck prepared transaction.

---

## 12.13 No real message broker

**Why.** The interesting parts — writing the outbox transactionally, claiming with
`SKIP LOCKED`, publishing at-least-once, deduplicating on the consumer — are all present with a
logging fake. RabbitMQ or Kafka would add operational surface, container weight and debugging
time without teaching a single additional ACID concept.

**Trade-off.** You do not exercise real broker failure modes (redelivery storms, poison
messages, consumer-group rebalances, ordering guarantees). If the project later needs to be a
"distributed systems" portfolio piece rather than a "database concurrency" one, swapping the
fake bus for a real broker is a one-interface change — which is itself a reason the interface
exists.

---

## 12.14 Connection pooler behaviour (documented, not deployed)

The project does not run PgBouncer, but the code is written so it could. In **transaction
pooling** mode these break:

| Breaks | Why | This project's answer |
|---|---|---|
| `pg_advisory_lock` (session) | The lock outlives the transaction and leaks onto whatever client gets that server connection next | Only `pg_advisory_xact_lock` is used — released at commit |
| bare `SET` | Persists on the server connection | Only `SET LOCAL`, inside a transaction |
| `LISTEN`/`NOTIFY` | Requires a stable session | Not used — the outbox is polled |
| `WITH HOLD` cursors, temp tables | Session state across transactions | Not used |
| Server-side prepared statements | Depends on the pooler version | `Max Auto Prepare = 0` if a pooler is introduced |

**The general lesson.** Every one of those mitigations is also just *good transaction hygiene*.
A codebase that keeps transactions short and self-contained is pooler-ready for free — which is
`L09 §8.5`'s point.

---

## 12.15 Read replicas are not a correctness tool

**Why they are excluded.** A physical standby gives you snapshot isolation regardless of the
level you request: **SSI does not run on replicas**. Code that says
`BeginTransactionAsync(IsolationLevel.Serializable)` against a replica connection is
*misleading* — it compiles, it runs, and it does not do what it says.

**What replicas are good for.** Long analytical reads, so they do not hold the primary's VACUUM
horizon. But note the reverse hazard: with `hot_standby_feedback = on`, a 30-minute query on
the replica pins the *primary's* horizon and causes bloat there. Watch
`pg_replication_slots.xmin` and `pg_stat_replication`.

**Trade-off.** In this project, long reports use `SERIALIZABLE READ ONLY DEFERRABLE` on the
primary and are capped at 30 seconds. That is the simple, honest choice at this scale, and the
replica discussion is documented so nobody "optimises" it later without knowing what they lose.

---

## 12.16 Raw SQL for writes, EF Core for reads

**Why.** The concurrency-control design *is* SQL: guard predicates in the `WHERE`, rows-affected
as a business signal, `FOR UPDATE`, `SKIP LOCKED`, `ON CONFLICT`, data-modifying CTEs, deferred
constraints. Through an ORM these are either unavailable, awkward, or — worst — invisible,
which defeats the entire exercise. Meanwhile EF Core genuinely is faster for list/detail
projections where nothing subtle depends on the emitted SQL.

**Alternatives.** All EF Core (`FromSqlRaw` for the hard parts — a worse version of both). All
Dapper (fine; the choice between Dapper and raw Npgsql here is style, and raw Npgsql keeps the
`NpgsqlDataSource`/transaction/savepoint APIs from the course front and centre). All raw ADO.NET
(more boilerplate on read paths for no benefit).

**Trade-offs.** Two data-access idioms in one codebase — you must draw the line clearly (§11.3)
and enforce it, or it degenerates. Raw SQL means manual mapping and no compile-time checking of
column names; mitigate with integration tests over every query and by keeping SQL in `.sql`
files rather than string literals scattered through handlers.

---

## 12.17 Retry parameters: 6 attempts, 20 ms base, 2 s cap, full jitter

**Why full jitter specifically.** `wait = random(0, min(cap, base · 2^n))` randomises the
**whole** interval. Fixed backoff (or "backoff ± 10 %") keeps the herd synchronised: everyone
who collided at *t* retries at *t + delay* and collides again. Randomising the entire window is
what actually de-correlates retriers, and Phase 6's three-way experiment will show it.

**Why bounded at 6.** A permanent hotspot would otherwise retry forever, converting a design
problem into an availability problem plus a latency problem. Six attempts with this backoff is
roughly 0–4 seconds of total wait — comfortably inside the endpoint's hard timeout, and enough
to clear any transient conflict.

**Why `base = 20 ms`.** Slightly longer than the p99 duration of the transactions being
retried, so the winner has actually committed and cleared before the loser tries again.

**Trade-offs.** More attempts hide hotspots (bad — you want to *see* M-4). Fewer attempts turn
ordinary conflicts into user-visible errors. A higher cap smears the latency tail. These numbers
are a starting point; **re-derive them from your own M-1 and M-3 histograms in Phase 7** and
record what you changed and why.

---

## 12.18 `SKIP LOCKED` claim versus a `processing`-state claim

**`FOR UPDATE SKIP LOCKED`** — claim and complete in one short transaction. Simplest, no
cleanup needed (a crashed worker's locks release on disconnect), and it scales linearly with
workers. **Use it whenever the work is fast** — the reaper, the outbox publisher.

**`state = 'processing'` + `locked_by` + `locked_at` + a stuck-job reaper** — claim in one short
transaction, do slow work outside any transaction, complete in another. **Use it whenever the
work is slow or involves external I/O** — the PSP reconciler.

**The failure to avoid.** Holding a row lock for the duration of a 500 ms external call. That is
the `idle in transaction` anti-pattern wearing a queue costume: it holds the snapshot, holds the
lock, and burns a connection.

**Trade-offs of the state-based claim.** You must write and operate the stuck-job reaper, choose
a timeout (5 minutes here), and make completion idempotent — because a job reclaimed after a
false-positive timeout will run twice. `SKIP LOCKED` has none of those problems and none of the
flexibility.

---

## 12.19 Questions you should be able to answer without notes

If you can answer these, the project has done its job.

1. Why did waiting on a row lock not prevent the lost update in CH-01?
2. Why does `REPEATABLE READ` prevent phantom reads in PostgreSQL when the SQL standard only
   requires `SERIALIZABLE` to — and why is that *still* not enough for CH-03?
3. Give the one-sentence tell that distinguishes a lost update from write skew.
4. Why can't `SELECT … FOR UPDATE` enforce "at most 6 tickets per customer"?
5. What exactly changed when you added `customer_event_allocation` such that `READ COMMITTED`
   became sufficient?
6. Name the two error messages that share `SQLSTATE 40001` and say which level produces each.
7. Why must a retry re-run the `SELECT`s and not just the `UPDATE`s?
8. Why is fixed-delay backoff worse than full jitter under a burst of conflicts?
9. What are the two properties that make a retried transaction body safe to run twice?
10. Why does one long-open transaction cause bloat across the *whole* database?
11. Why is `SERIALIZABLE` on one endpoint insufficient if another endpoint writes the same rows
    at `READ COMMITTED`?
12. Why does a seat hold modelled as a time range free the seat without a reaper?
13. What does a `CHECK` constraint firing at runtime tell you about your application code, and
    why is it a `500` rather than a `409`?
14. Why is `PATCH /events/{id}` optimistic while `POST /orders/{id}/pay` is pessimistic?
15. When is raising `maxAttempts` the right response to `tx_retries_exhausted_total > 0`?
    (Never. Say why.)
