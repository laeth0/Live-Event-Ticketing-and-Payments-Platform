# 5 · Transaction Scenarios

Fifteen worked scenarios. Each follows the same template:

**Normal workflow · What goes wrong without care · Anomaly · Incorrect implementation ·
Correct implementation · Isolation level · Locking strategy · Why it works.**

Implement the *incorrect* version first in Phase 3 and let the concurrency test catch it. A
fix you have never seen fail is a fix you do not understand.

---

## TX-01 · Concurrent seat hold

**Normal workflow.** Customer picks seat `H-12`; system creates a 15-minute hold; the seat
disappears from other customers' maps.

**Without care.** Two customers pick `H-12` within the same millisecond. Both see it as
available. Both create a hold. Both check out. Two people arrive at seat `H-12`.

**Anomaly.** Write skew / phantom insert. The rows that would have changed the decision are the
*other transaction's not-yet-existing hold row*, so no row lock can protect it.

**Incorrect implementation**
```sql
BEGIN;                                             -- READ COMMITTED
SELECT 1 FROM sales.seat_hold
 WHERE event_id = $1 AND seat_id = $2
   AND released_at IS NULL AND during @> now();    -- "is it free?"  → 0 rows for BOTH sessions
INSERT INTO sales.seat_hold (id, event_id, seat_id, cart_id, during)
VALUES ($3, $1, $2, $4, tstzrange(now(), now() + interval '15 minutes'));
COMMIT;                                            -- both succeed
```

**Correct implementation**
```sql
BEGIN;                                             -- READ COMMITTED
INSERT INTO sales.seat_hold (id, event_id, seat_id, cart_id, during)
VALUES ($3, $1, $2, $4, tstzrange(now(), now() + interval '15 minutes'));
-- EXCLUDE USING gist (event_id =, seat_id =, during &&) WHERE (released_at IS NULL)
--   → the loser gets SQLSTATE 23P01 exclusion_violation
COMMIT;
```
```csharp
catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ExclusionViolation)
    => throw new SeatUnavailableException(seatId);      // → HTTP 409, never retried
```

**Isolation level.** `READ COMMITTED`. Raising it would add nothing.

**Locking strategy.** None explicit. The exclusion constraint's index probe serialises the two
inserters; the loser waits for the winner's outcome and then fails.

**Why it works.** The check and the write are the *same statement*, so there is no window
between them. And because it is a constraint, it holds against every writer — a
`READ COMMITTED` job, a buggy future endpoint, or a human in `psql`. That is why rung 1 beats
rung 4 (`L10 §2`). Note carefully what does **not** work: `SELECT … FOR UPDATE` cannot lock a
hold row that does not exist yet (`L07 §2`).

---

## TX-02 · General-admission inventory reservation

**Normal workflow.** Customer reserves 4 tickets of a 2 000-seat tier; `reserved` goes 1 996 →
2 000; the next request is told the tier is sold out.

**Without care.** Ten concurrent requests each read `reserved = 1 996`, each computes
`1 996 + 4 = 2 000` in C#, each writes the literal `2 000`. Forty tickets are handed out from
four remaining seats.

**Anomaly.** **Lost update** (`L05 §4.1`), the read-modify-write-with-a-literal form. Note that
the row lock does *not* save you here: the second writer waits, re-reads, and then still writes
the stale literal it computed earlier.

**Incorrect implementation**
```csharp
var reserved = await ReadReservedAsync(conn, tx, ticketTypeId);   // 1996
if (reserved + qty > allocated) throw new SoldOutException();
await ExecAsync(conn, tx,
    "UPDATE sales.ticket_type_inventory SET reserved = $1 WHERE ticket_type_id = $2",
    reserved + qty, ticketTypeId);                                // writes a literal
```

**Correct implementation**
```sql
UPDATE sales.ticket_type_inventory
   SET reserved = reserved + $1, updated_at = now()
 WHERE ticket_type_id = $2
   AND reserved + sold + $1 <= allocated;
-- rows-affected 1 ⇒ reserved;  0 ⇒ sold out / lost the race
```
```csharp
if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new SoldOutException();
```
Backstop: `CONSTRAINT inventory_never_oversells CHECK (reserved + sold <= allocated)`.

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** Implicit row lock taken by the `UPDATE`. Writers queue on one row; at
~400 writes/s that queue is short because the transaction is a few milliseconds long.

**Why it works.** The new value is derived from the row *as locked and re-read by the engine*
(`L05 §4.1`, "atomic write"), never from a value the application saw earlier. The guard lives
in the `WHERE`, so the decision and the write are indivisible, and `rows-affected` carries the
business outcome. The `CHECK` makes an oversell physically impossible even if someone later
writes the query wrongly.

---

## TX-03 · Bulk seat-map import

**Normal workflow.** 20 000 seats imported; 12 duplicates rejected; 19 988 committed.

**Without care.** The first duplicate raises `23505`; every subsequent statement returns
`25P02 in_failed_sql_transaction`; the whole import is lost.

**Anomaly.** Not a concurrency anomaly — a **transaction-state** failure (`L01 §7.2`).

**Incorrect implementation.** One `BEGIN`, 20 000 `INSERT`s, `COMMIT`, no error containment —
or the opposite mistake, 20 000 autocommitted inserts, which costs 20 000 WAL `fsync`s.

**Correct implementation**
```csharp
foreach (var chunk in rows.Chunk(1_000))
{
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    foreach (var row in chunk)
    {
        await tx.SaveAsync("row_sp", ct);                    // no round trip; rides the next command
        try   { await InsertSeatAsync(conn, tx, row, ct); await tx.ReleaseAsync("row_sp", ct); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation
                                                     or PostgresErrorCodes.CheckViolation)
        { await tx.RollbackAsync("row_sp", ct); rejected.Add((row, e.SqlState)); }
    }
    await tx.CommitAsync(ct);
}
```

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** None beyond the inserts' own.

**Why it works.** A `SAVEPOINT` per row contains the error so the outer transaction survives
(`L09 §5.4`). Chunking bounds two costs at once: the snapshot never lives long enough to pin
the VACUUM horizon (`L02 §5.6`), and the number of subtransactions per transaction stays low
enough to avoid subtransaction-SLRU pressure. **Required measurement:** run it with chunk sizes
1, 100, 1 000, 20 000 and report wall time and `age(backend_xmin)`. This is the scenario that
teaches savepoints are not free.

---

## TX-04 · Organizer edits an event during an onsale

**Normal workflow.** Two staff have the pricing page open. One raises the VIP price, the other
increases the GA allocation. Both changes survive.

**Without care.** The second `PATCH` writes the full entity it loaded a minute ago and silently
reverts the first change.

**Anomaly.** **Lost update** across an HTTP think-time gap — the version that no isolation level
can help with, because the two requests are in different transactions minutes apart.

**Incorrect implementation**
```sql
UPDATE catalog.ticket_type SET price_minor = $1, name = $2 WHERE id = $3;   -- last write wins
```

**Correct implementation**
```sql
UPDATE catalog.event
   SET max_tickets_per_customer = $1, updated_at = now(), version = version + 1
 WHERE id = $2 AND version = $3;              -- $3 comes from the client's If-Match
-- rows-affected 0 ⇒ 409 Conflict + current representation
```
Allocation increases go to the inventory row with their own guard:
```sql
UPDATE sales.ticket_type_inventory
   SET allocated = $1
 WHERE ticket_type_id = $2 AND $1 >= reserved + sold;
```

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** None — **optimistic concurrency**. Contention here is measured in edits
per hour; blocking would be waste.

**Why it works.** The `WHERE version = $original` predicate is a conflict *detector*
(`L05 §4.1`, fix #3). Zero rows affected means "someone changed this since you read it", which
maps exactly onto HTTP `ETag`/`If-Match` → `409`. The client re-reads and re-applies. EF Core's
`IsRowVersion()` generates the same `WHERE` and throws `DbUpdateConcurrencyException`.

---

## TX-05 · Checkout submitted twice

**Normal workflow.** Cart → order in `pending_payment`, one order per cart.

**Without care.** The customer's browser retries after a timeout. Two orders, two payments, one
angry customer.

**Anomaly.** **Duplicate processing.** Atomicity guarantees nothing here — both transactions
were individually perfect (`L03 §5.1`, "the client's knowledge of the outcome").

**Incorrect implementation**
```sql
SELECT id FROM sales.ticket_order WHERE cart_id = $1;   -- none yet, for BOTH requests
INSERT INTO sales.ticket_order (...) VALUES (...);
```

**Correct implementation**
```sql
-- 1. claim the key; a concurrent duplicate gets 23505 here and is answered 409 + Retry-After
INSERT INTO ops.idempotency_key (key, endpoint, actor_id, request_fingerprint, state, expires_at)
VALUES ($1, 'POST /carts/{id}/checkout', $2, $3, 'in_progress', now() + interval '24 hours');

-- 2. do the work …

-- 3. store the response so a later replay returns the SAME body, not a conflict
UPDATE ops.idempotency_key
   SET state = 'completed', response_status = 201, response_body = $4
 WHERE key = $1;
```
plus `CONSTRAINT order_idempotency_uk UNIQUE (idempotency_key)` and
`CONSTRAINT order_one_per_cart UNIQUE (cart_id)` as independent backstops.

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** The unique index on the key is the mutex.

**Why it works.** Three layers: the key row makes the *request* at-most-once, the response body
makes the *replay* indistinguishable from the original, and the two unique constraints make the
*effect* at-most-once even if the key logic is bypassed. A replay with a different body is
`422` because the key is bound to a request fingerprint — otherwise a client bug could
"replay" a different purchase into a cached `201`.

---

## TX-06 · Pay an order from the wallet

**Normal workflow.** Debit customer wallet, credit organizer payable, convert reservations to
sales, issue tickets, write outbox rows. One transaction.

**Without care.** A balance goes negative; or money leaves the wallet and the tickets are never
issued; or two concurrent payments both pass the balance check.

**Anomaly.** Lost update on the balance; broken atomicity across five tables; negative balance.

**Incorrect implementation**
```csharp
var balance = await ReadBalanceAsync(...);          // 5000
if (balance < total) throw new InsufficientFunds();  // both concurrent payments pass
await ExecAsync("UPDATE money.account_balance SET balance_minor = $1 WHERE account_id = $2",
                balance - total, walletAccountId);
await _bus.PublishAsync(new OrderPaid(orderId));     // ← inside the transaction: ghost event
await tx.CommitAsync();
```

**Correct implementation**
```sql
BEGIN;   -- READ COMMITTED
SET LOCAL lock_timeout = '3s';

-- lock both accounts in canonical id order (deadlock avoidance)
SELECT account_id FROM money.account_balance
 WHERE account_id = ANY($1) ORDER BY account_id FOR UPDATE;

UPDATE money.account_balance
   SET balance_minor = balance_minor - $2, version = version + 1, updated_at = now()
 WHERE account_id = $3 AND balance_minor >= $2;          -- rows-affected 0 ⇒ InsufficientFunds

UPDATE money.account_balance
   SET balance_minor = balance_minor + $2, version = version + 1, updated_at = now()
 WHERE account_id = $4;

INSERT INTO money.journal (id, kind, reference_type, reference_id, currency)
VALUES ($5,'order_payment','ticket_order',$6,$7);        -- UNIQUE ⇒ idempotent on retry
INSERT INTO money.journal_entry (journal_id, account_id, amount_minor, currency)
VALUES ($5,$3,-$2,$7), ($5,$4,$2,$7);                    -- deferred trigger checks sum=0 at COMMIT

UPDATE sales.ticket_type_inventory
   SET reserved = reserved - $8, sold = sold + $8
 WHERE ticket_type_id = $9 AND reserved >= $8;

UPDATE sales.seat_hold SET converted_at = now(),
       during = tstzrange(lower(during), 'infinity')
 WHERE cart_id = $10 AND released_at IS NULL;            -- seat is now permanently occupied

INSERT INTO sales.ticket (...) VALUES (...)
ON CONFLICT (order_line_id, seq) DO NOTHING;             -- idempotent under retry

UPDATE sales.ticket_order SET status='paid', paid_at=now(), version=version+1
 WHERE id=$6 AND status='pending_payment';               -- rows-affected 0 ⇒ already paid/expired

INSERT INTO ops.outbox (aggregate_type, aggregate_id, event_type, dedup_key, payload)
VALUES ('ticket_order',$6,'OrderPaid','OrderPaid:'||$6::text,$11);
COMMIT;
-- the event is published by the outbox worker, after this COMMIT, outside this transaction
```

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** Pessimistic `FOR UPDATE` on both balance rows, acquired in ascending
`account_id` order, plus guard predicates in every `UPDATE`.

**Why it works.** The balance never goes negative because the guard is in the `WHERE` and the
`CHECK` is the backstop. The transaction is atomic across money, inventory, seats, tickets and
the outbox — the only way "paid but no ticket" can exist is a crash *before* commit, which
leaves nothing. External effects are strictly after commit (`L09 §5.2`). And every insert has a
natural key, so if the transaction were retried the body would converge instead of doubling.

> **Why `FOR UPDATE` and not just guarded `UPDATE`s?** The guarded updates alone are already
> safe. The explicit lock exists to take **both** rows in a deterministic order *before* any
> write, which is what removes the deadlock cycle in TX-08. Locking is here for deadlock
> avoidance, not for correctness of the arithmetic.

---

## TX-07 · Reaper reclaims a hold while the customer is checking out

**Normal workflow.** A hold expires at `T`. Reapers reclaim inventory. Customers who arrive
after `T` see the tickets again.

**Without care.** Two reapers claim the same hold and decrement `reserved` twice (underflow, or
a `CHECK` violation storm). Or a reaper reclaims a hold at `T` while a checkout that started at
`T − 5 ms` is mid-flight, and the customer gets tickets that were already given back.

**Anomaly.** Duplicate processing (two workers, one row) and a lost-update-shaped double
decrement.

**Incorrect implementation**
```sql
SELECT id FROM sales.hold WHERE expires_at < now() AND released_at IS NULL LIMIT 100;
-- … application loop … then per row:
UPDATE sales.ticket_type_inventory SET reserved = reserved - $1 WHERE ticket_type_id = $2;
UPDATE sales.hold SET released_at = now() WHERE id = $3;
```
Two workers select the same 100 ids and both decrement.

**Correct implementation**
```sql
BEGIN;   -- READ COMMITTED
WITH claimed AS (
    SELECT id, ticket_type_id, quantity, cart_id
      FROM sales.hold
     WHERE expires_at < now() AND released_at IS NULL AND converted_at IS NULL
     ORDER BY expires_at, id
     FOR UPDATE SKIP LOCKED
     LIMIT 100
), released AS (
    UPDATE sales.hold h SET released_at = now()
      FROM claimed c
     WHERE h.id = c.id AND h.released_at IS NULL      -- the second guard: only ever released once
 RETURNING h.ticket_type_id, h.quantity, h.cart_id
)
UPDATE sales.ticket_type_inventory i
   SET reserved = i.reserved - agg.qty
  FROM (SELECT ticket_type_id, sum(quantity) AS qty FROM released GROUP BY 1) agg
 WHERE i.ticket_type_id = agg.ticket_type_id AND i.reserved >= agg.qty;
COMMIT;
```
Checkout, in parallel, revalidates its holds:
```sql
SELECT count(*) FROM sales.hold
 WHERE cart_id = $1 AND released_at IS NULL AND converted_at IS NULL AND expires_at > now();
```
and for seats the `EXCLUDE` constraint already makes an expired hold non-blocking.

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** `FOR UPDATE SKIP LOCKED` for the claim; the row lock held to the end of a
short transaction; batches of 100.

**Why it works.** `SKIP LOCKED` makes "two workers, one row" structurally impossible — worker B
steps over the row worker A holds rather than queueing behind it (`L07 §6.2`). The
`released_at IS NULL` guard in the `UPDATE` makes the decrement conditional on this transaction
actually being the one that released the hold, so even a logic bug cannot double-decrement. The
checkout/reaper race resolves deterministically: whoever's transaction commits first wins, and
the loser's guarded `UPDATE` affects zero rows and raises a clean business error.

> **Required measurement.** Run the reaper with 1, 2, 4 workers, with and without `SKIP
> LOCKED`. Without it, throughput is flat (they serialise on the first row); with it, it scales.

---

## TX-08 · Two mutual wallet transfers — the deadlock

**Normal workflow.** Alice sends €400 to Bob; Bob sends €150 to Alice. Both complete.

**Without care.** Transaction 1 locks Alice then wants Bob; transaction 2 locks Bob then wants
Alice. A cycle. After `deadlock_timeout` (1 s) PostgreSQL kills one with `40P01`.

**Anomaly.** **Deadlock**, `SQLSTATE 40P01`.

**Incorrect implementation**
```sql
-- transfer(from, to): lock in argument order
SELECT balance_minor FROM money.account_balance WHERE account_id = $from FOR UPDATE;
SELECT balance_minor FROM money.account_balance WHERE account_id = $to   FOR UPDATE;
```

**Correct implementation**
```sql
-- lock both rows in ONE statement, in a deterministic order
SELECT account_id, balance_minor
  FROM money.account_balance
 WHERE account_id IN ($from, $to)
 ORDER BY account_id
   FOR UPDATE;
```
```csharp
// belt and braces at the application layer, for multi-statement paths
var (first, second) = fromId.CompareTo(toId) <= 0 ? (fromId, toId) : (toId, fromId);
```
plus `40P01` handled by the same retry helper as `40001`.

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** `FOR UPDATE` in **canonical ascending id order**, taken in a single
statement so the engine orders the acquisitions itself.

**Why it works.** A deadlock needs a cycle in the wait-for graph. If every transaction acquires
locks in the same global order, no cycle can form: the later transaction simply waits for the
earlier one and proceeds (`L07 §5.4`). Retry remains in place for the deadlocks you cannot
design away (FK locks, index page ordering, a future code path).

**Required exercise.** Reproduce it first. Capture the full `ERROR: deadlock detected` `DETAIL`
line. Then apply the fix and prove 200 randomised concurrent transfers across 5 wallets produce
**zero** deadlocks and a conserved total.

---

## TX-09 · Three concurrent partial refunds

**Normal workflow.** €120 captured. Agents refund €50, €40 and €30 concurrently — total €120,
all three succeed. A fourth €10 refund is rejected.

**Without care.** Each agent's transaction reads `SUM(refunds) = 0`, each concludes €120 is
available, all four commit: €130 refunded on a €120 payment.

**Anomaly.** **Write skew on a `SUM` predicate.** The transactions write *different* refund
rows, so `REPEATABLE READ` sees no conflict and raises nothing.

**Incorrect implementation**
```sql
SELECT coalesce(sum(amount_minor),0) FROM money.refund WHERE payment_id = $1;   -- 0 for all three
-- if captured - sum >= amount then:
INSERT INTO money.refund (...) VALUES (...);
```

**Correct implementation**
```sql
UPDATE money.payment
   SET refunded_minor = refunded_minor + $1, updated_at = now()
 WHERE id = $2
   AND status = 'captured'
   AND refunded_minor + $1 <= captured_minor;      -- rows-affected 0 ⇒ RefundExceedsCapture
INSERT INTO money.refund (id, payment_id, order_id, amount_minor, reason, idempotency_key)
VALUES (...);                                       -- UNIQUE (idempotency_key)
-- reversing journal + balance updates
```
Backstop: `CONSTRAINT payment_refund_bound CHECK (refunded_minor <= captured_minor)`.

**Isolation level.** `READ COMMITTED` — *because the predicate was materialised*.

**Locking strategy.** The implicit row lock on the `payment` row serialises all refunds for that
payment.

**Why it works.** The `SUM` over many rows became a **counter on one row**. A predicate-level
conflict, which MVCC cannot see, became a row-level conflict, which it handles natively
(`L05 §3`, "materialising the conflict"). The three legitimate refunds queue for microseconds
and all succeed; the fourth affects zero rows and is rejected cleanly. No `SERIALIZABLE`, no
retries, no aborts.

---

## TX-10 · Organizer payout while refunds are landing

**Normal workflow.** Nightly job pays each organizer `payable − reserve`.

**Without care.** The payout transaction computes availability from a `SUM` while a refund
transaction reduces the payable balance. Both commit. The platform paid out money it then had
to claw back.

**Anomaly.** Write skew (payout row and refund row are different rows) plus a stale aggregate.

**Incorrect implementation**
```sql
SELECT sum(amount_minor) FROM money.journal_entry WHERE account_id = $payable;  -- stale by design
-- if sum - reserve >= min then INSERT INTO money.payout …
```

**Correct implementation**
```sql
BEGIN;   -- READ COMMITTED, one transaction PER ORGANIZER
SELECT balance_minor
  FROM money.account_balance
 WHERE account_id = $payable
   FOR UPDATE;                       -- lock the row the invariant is ABOUT

UPDATE money.account_balance
   SET balance_minor = balance_minor - $amount, version = version + 1
 WHERE account_id = $payable
   AND balance_minor - $amount >= $reserve;     -- rows-affected 0 ⇒ skip this organizer
INSERT INTO money.payout (id, organizer_id, business_date, amount_minor)
VALUES ($1, $2, $3, $amount);                    -- UNIQUE (organizer_id, business_date)
-- balanced journal moving payable → payout_clearing
COMMIT;
```
Batch claim across organizers:
```sql
SELECT o.id, ab.account_id
  FROM catalog.organizer o JOIN money.account a ON a.owner_id = o.id AND a.kind='organizer_payable'
  JOIN money.account_balance ab ON ab.account_id = a.id
 WHERE o.status = 'active'
 ORDER BY o.id
   FOR UPDATE OF ab SKIP LOCKED
 LIMIT 50;
```

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** `FOR UPDATE` on the payable balance row (the invariant's subject),
`SKIP LOCKED` across organizers so one slow organizer does not stall the batch, and **one
transaction per organizer** so a single failure does not roll back 49 good payouts.

**Why it works.** The invariant is *about the balance row*, so locking that row converts the
write skew into an ordinary serialised write. The refund transaction must wait for the payout
(or vice versa), and whoever runs second sees the true balance. `UNIQUE (organizer_id,
business_date)` makes re-running the nightly job a no-op rather than a double payout — essential,
because cron jobs *do* get run twice.

---

## TX-11 · Disabling the last payment route (the headline write skew)

**Normal workflow.** Ops disables `psp_primary`; `psp_backup` and `wallet` remain; the event
keeps selling.

**Without care.** Two operators, two tabs, two different routes, one second apart. Each reads
"2 routes enabled → safe". Each disables a different row. Both commit. Zero routes; every
payment fails; the event is on sale and unsellable.

**Anomaly.** **Write skew** in its purest form (`L05 §4.2`) — the on-call example transplanted
into a payments system. `REPEATABLE READ` does **not** prevent it: the transactions write
different rows, so there is no first-updater-wins collision and no `40001`.

**Incorrect implementation**
```sql
BEGIN ISOLATION LEVEL REPEATABLE READ;
SELECT count(*) FROM catalog.payment_route WHERE event_id=$1 AND is_enabled;   -- 2, in both
-- if count > 1 then:
UPDATE catalog.payment_route SET is_enabled=false WHERE id=$2;                 -- different rows
COMMIT;                                                                        -- both succeed
```

### Correct implementation A — `SERIALIZABLE` + retry
```sql
BEGIN ISOLATION LEVEL SERIALIZABLE;
SELECT count(*) FROM catalog.payment_route WHERE event_id = $1 AND is_enabled;  -- SIRead lock recorded
-- business rule: if count <= 1 → LastPaymentRouteException (NOT retryable)
UPDATE catalog.payment_route SET is_enabled = false, updated_at = now() WHERE id = $2;
COMMIT;   -- second committer: 40001 "read/write dependencies among transactions"
```
The retry re-reads `count = 1` and the *business rule* — not the engine — produces the correct
rejection. That distinction is the whole lesson of `L08 §7.1`.

### Correct implementation B — guard-row lock
```sql
BEGIN;   -- READ COMMITTED
SELECT id FROM catalog.event WHERE id = $1 FOR UPDATE;      -- serialise all route changes for this event
SELECT count(*) FROM catalog.payment_route WHERE event_id = $1 AND is_enabled;
UPDATE catalog.payment_route SET is_enabled = false WHERE id = $2;
COMMIT;
```

### Correct implementation C — materialise the count (recommended)
```sql
BEGIN;   -- READ COMMITTED
UPDATE catalog.event
   SET enabled_route_count = enabled_route_count - 1, version = version + 1
 WHERE id = $1
   AND (status <> 'onsale' OR enabled_route_count - 1 >= 1);   -- rows-affected 0 ⇒ reject
UPDATE catalog.payment_route SET is_enabled = false, updated_at = now()
 WHERE id = $2 AND is_enabled;                                  -- rows-affected 0 ⇒ already disabled
COMMIT;
```
Backstop: `CHECK (status <> 'onsale' OR enabled_route_count >= 1)` on `catalog.event`.

| | A — `SERIALIZABLE` | B — guard row | C — materialised counter |
|---|---|---|---|
| Isolation level | `SERIALIZABLE` | `READ COMMITTED` | `READ COMMITTED` |
| Failure mode under contention | `40001` aborts + retries | lock waits | lock waits (µs) |
| Needs a retry loop | **yes** | no | no |
| Holds against a `READ COMMITTED` writer | **no** | only if that writer also takes the lock | **yes** (the `CHECK` is unconditional) |
| Extra schema | none | none | one counter column + `CHECK` |
| Correct if a future endpoint forgets the rule | no | no | **yes** |

**Required deliverable.** Implement all three behind a configuration switch, run the same k6
scenario against each, and report abort rate, p99 latency and throughput. Then write the
recommendation in your own words. The expected conclusion — C, then B, then A — is `L10 §2`'s
ladder demonstrated with your own numbers rather than taken on faith.

---

## TX-12 · The finance report that must reflect one instant

**Normal workflow.** Finance reads gross sales, refunds, fees, payable balances and outstanding
holds and reconciles them.

**Without care.** Each query gets its own snapshot at `READ COMMITTED`, so the report shows
sales counted after a transfer and balances counted before it. The numbers do not add up and
someone spends a day chasing a bug that does not exist.

**Anomaly.** **Non-repeatable read / phantom** across statements (`L04 §4.2–4.3`); at the strict
end, the **read-only serialization anomaly** (`L05 §4.3`).

**Incorrect implementation.** Five independent queries on five autocommitted statements.

**Correct implementation — short reports**
```csharp
await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
var sales    = await ReadAsync(conn, tx, "...");
var refunds  = await ReadAsync(conn, tx, "...");   // same snapshot
var balances = await ReadAsync(conn, tx, "...");
await tx.CommitAsync(ct);                          // commit read-only txns too: releases the snapshot
```

**Correct implementation — long reports and `GET /ops/invariants`**
```csharp
await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
await new NpgsqlCommand("SET TRANSACTION READ ONLY, DEFERRABLE", conn, tx).ExecuteNonQueryAsync(ct);
// may pause briefly waiting for a safe snapshot, then runs with ZERO abort risk
```

**Isolation level.** `REPEATABLE READ` for sub-second reports; `SERIALIZABLE READ ONLY
DEFERRABLE` for anything long or anything whose *own* abort would be unacceptable.

**Locking strategy.** None. Readers never block writers under MVCC.

**Why it works.** One snapshot for the whole transaction means all five queries observe the same
instant (`L02 §5.4`). `READ ONLY DEFERRABLE` waits for a snapshot proven not to participate in
any dangerous structure, so the report can never be the `40001` victim and can never cause a
writer to become one (`L06 §5.3`).

**The cost you must measure.** A `REPEATABLE READ` transaction pins `OldestXmin` for its whole
life. Run the report with a `pg_sleep(10)` in the middle and watch `age(backend_xmin)` and
`VACUUM (VERBOSE)` reporting non-removable tuples (`L02 §7.4`). Then set the 30-second read
timeout from §3.2 and explain why it exists.

---

## TX-13 · Promo budget overspend

**Normal workflow.** `EARLY25`: €5 off, €2 500 budget, 500 redemptions, one per customer.

**Without care.** 40 concurrent customers each read €2 495 spent, each grants €5, budget lands
at €2 695.

**Anomaly.** Write skew on a `SUM` (identical in shape to TX-09; included separately because
this one *also* needs a per-customer uniqueness rule, and the two are solved by two different
rung-1 mechanisms).

**Incorrect implementation**
```sql
SELECT coalesce(sum(amount_minor),0) FROM sales.promo_redemption WHERE promo_code_id=$1;
-- if sum + discount <= budget then INSERT INTO sales.promo_redemption …
```

**Correct implementation**
```sql
UPDATE sales.promo_code
   SET redeemed_minor = redeemed_minor + $1, redemption_count = redemption_count + 1
 WHERE id = $2
   AND valid @> now()
   AND redeemed_minor  + $1 <= budget_minor
   AND redemption_count + 1 <= max_redemptions;     -- rows-affected 0 ⇒ PromoExhausted
INSERT INTO sales.promo_redemption (id, promo_code_id, customer_id, cart_id, amount_minor)
VALUES (...);                                        -- UNIQUE (promo_code_id, customer_id) ⇒ 23505
```

**Isolation level.** `READ COMMITTED`.

**Locking strategy.** Implicit row lock on the promo row. Note the consequence: a wildly popular
promo code makes that row a **hot spot**, and every redemption serialises on it. Phase 7 asks
you to measure the ceiling and to discuss the append-only alternative (insert redemption rows,
enforce the budget with a periodically-rolled-up cap or a sharded counter) with its trade-offs.

**Why it works.** Budget → counter with a `CHECK`; "once per customer" → `UNIQUE`. Two different
aggregate predicates, two different declarative constructions, zero isolation-level changes.

---

## TX-14 · The per-customer ticket limit

**Normal workflow.** Limit 6. A customer holds 4, then requests 2 more: allowed. Then requests
1 more: rejected.

**Without care.** The customer opens six tabs and fires six requests for 4 tickets each. Every
request counts 0 existing tickets. All six succeed. 24 tickets.

**Anomaly.** **Phantom read** feeding a **write skew**. The decisive rows are the other
transactions' holds, which do not exist when the count runs. `SELECT … FOR UPDATE` provably
cannot help — there is nothing to lock.

**Incorrect implementation**
```sql
SELECT coalesce(sum(h.quantity),0)
  FROM sales.hold h JOIN sales.cart c ON c.id = h.cart_id
 WHERE c.customer_id = $1 AND c.event_id = $2 AND h.released_at IS NULL;
-- if total + requested <= 6 then INSERT the hold
```

**Correct implementation A — guard row (recommended)**
```sql
INSERT INTO sales.customer_event_allocation (customer_id, event_id, held, owned, limit_snapshot)
VALUES ($1, $2, 0, 0, $3)
ON CONFLICT (customer_id, event_id) DO NOTHING;

UPDATE sales.customer_event_allocation
   SET held = held + $4, updated_at = now()
 WHERE customer_id = $1 AND event_id = $2
   AND held + owned + $4 <= limit_snapshot;      -- rows-affected 0 ⇒ CustomerLimitExceeded
-- … then reserve inventory / insert the hold, same transaction …
```

**Correct implementation B — `SERIALIZABLE` + retry**
```csharp
await TransactionRetry.ExecuteAsync(_ds, IsolationLevel.Serializable, async (conn, tx, ct) =>
{
    var held = await CountHeldAndOwnedAsync(conn, tx, customerId, eventId, ct);   // SIRead locks
    if (held + requested > limit) throw new CustomerLimitExceededException();     // no retry
    await InsertHoldAsync(conn, tx, …, ct);
    return Unit.Value;
}, ct: ct);
```

**Isolation level.** A: `READ COMMITTED`. B: `SERIALIZABLE` + bounded jittered retry.

**Locking strategy.** A: implicit row lock on one guard row per (customer, event). B: none —
SSI predicate tracking and aborts.

**Why it works.** A materialises "how many does this customer have" onto one row, so six
concurrent tabs collide on one physical row and five of them see `rows-affected = 0`. B lets
SSI notice that each transaction read the set the others wrote, forming a dangerous structure,
and aborts all but one with `40001`; the retry re-reads the true count and rejects correctly.

**Required comparison.** Both, benchmarked at 20 concurrent requests per customer. Expect A to
show near-zero aborts and B to show a high abort rate with retry-inflated p99 — because SSI is
detecting a conflict that is *genuinely* there every time. Explain in your write-up why "the
guard row wins" is not a criticism of `SERIALIZABLE`.

---

## TX-15 · Card payment across an external PSP

**Normal workflow.** Authorize with the PSP, then record the capture, issue tickets, publish.

**Without care.** The obvious implementation opens a transaction, calls the PSP, and commits.
Under load: dozens of connections sit `idle in transaction` for 300 ms each, the pool is
exhausted, the VACUUM horizon is pinned, locks on holds are held across the network call — and
it is *still* not atomic, because the PSP charge is not rolled back by `ROLLBACK`.

**Anomaly.** Not an isolation anomaly — a **boundary** failure (`L09 §4`) plus a distributed
atomicity impossibility (`L03 §5.1`).

**Incorrect implementation**
```csharp
await using var tx = await conn.BeginTransactionAsync(ct);
await MarkPaymentInitiatedAsync(conn, tx, …);
var psp = await _psp.AuthorizeAsync(card, amount, ct);   // 300 ms of idle in transaction
await RecordCaptureAsync(conn, tx, psp, …);
await tx.CommitAsync(ct);                                // if this throws, the card is still charged
```

**Correct implementation — three short transactions plus a reconciler**
```
TX-A  BEGIN; INSERT payment(status='authorizing', idempotency_key)     -- UNIQUE
             UPDATE holds SET expires_at = now() + '5 minutes'
             INSERT ops.job(kind='psp_reconcile', run_after = now()+'60s', dedup_key=payment_id)
      COMMIT;                                          ~4 ms

(no transaction)  psp.AuthorizeAndCapture(idempotencyKey = payment.id)   ~300 ms

TX-B  BEGIN; UPDATE payment SET status='captured', captured_minor=$, psp_reference=$
               WHERE id=$ AND status='authorizing'      -- rows-affected 0 ⇒ someone else finished it
             … ledger + inventory + tickets + outbox, exactly as TX-06 …
             UPDATE ops.job SET state='done' WHERE dedup_key = $payment_id
      COMMIT;                                          ~8 ms

reconciler (SKIP LOCKED over ops.job / payment_stranded_idx):
      query the PSP by the payment's idempotency key
        → captured  ⇒ run TX-B's body (idempotent, guarded by status)
        → not found ⇒ TX: payment.status='failed', release holds, cancel order
```

**Isolation level.** `READ COMMITTED` for all three.

**Locking strategy.** No lock is held across the PSP call. State is carried in
`payment.status`, which doubles as the concurrency guard (`WHERE status = 'authorizing'`).

**Why it works.** Every database transaction is a few milliseconds long, so the pool, the lock
manager and the VACUUM horizon are all unaffected by a slow third party. The failure window is
explicit and bounded: a crash between the PSP call and TX-B leaves a row in `authorizing`, and
the reconciler resolves it within 60 seconds using the PSP's own idempotency key as the
authority. Atomicity across two systems is impossible; **detectability plus a compensating
action** is what you build instead (`L09 §9.7`).

**Required exercise.** Kill the API process (a) between TX-A and the PSP call, (b) between the
PSP call and TX-B, (c) between TX-B's `COMMIT` and the outbox publish. For each, state the
observable state, which component repairs it, and how long that takes. Then write the test
(T-C8).

---

## Scenario → concept coverage matrix

| Scenario | Atomicity | Isolation level exercised | Anomaly | Mechanism |
|---|---|---|---|---|
| TX-01 seat hold | ✔ | RC | write skew (insert) | `EXCLUDE` constraint |
| TX-02 GA inventory | ✔ | RC | lost update | atomic write + `CHECK` |
| TX-03 bulk import | ✔ | RC | — (`25P02`) | savepoints, chunking |
| TX-04 event edit | ✔ | RC | lost update | optimistic `version` |
| TX-05 checkout | ✔ | RC | duplicate processing | idempotency key + `UNIQUE` |
| TX-06 wallet pay | ✔✔ | RC | lost update, negative balance | `FOR UPDATE` + guarded writes |
| TX-07 reaper | ✔ | RC | duplicate processing | `FOR UPDATE SKIP LOCKED` |
| TX-08 transfer | ✔ | RC | **deadlock 40P01** | canonical lock order + retry |
| TX-09 refunds | ✔ | RC | write skew on `SUM` | counter + `CHECK` |
| TX-10 payout | ✔ | RC | write skew | guard-row `FOR UPDATE` + `SKIP LOCKED` |
| TX-11 routes | ✔ | **SER** / RC | **write skew** | 3 solutions compared |
| TX-12 report | — | **RR / SER RO DEFERRABLE** | non-repeatable, phantom, read-only anomaly | one snapshot |
| TX-13 promo | ✔ | RC | write skew on `SUM` | counter + `CHECK` + `UNIQUE` |
| TX-14 limit | ✔ | RC / **SER** | **phantom → write skew** | guard row vs SSI, compared |
| TX-15 card saga | ✔ (×3) | RC | boundary / distributed | short transactions + reconciler |
