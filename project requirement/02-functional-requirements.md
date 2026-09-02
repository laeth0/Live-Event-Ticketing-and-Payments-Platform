# 2 · Functional Requirements

Twenty features. Each one is specified in the same six-part format:
**Business goal · Workflow · Database operations · Transaction requirements · Concurrency
problems · Concepts practiced.**

Features are ordered roughly by implementation order, which matches the phases in §8.

Legend for the *Concepts practiced* rows — these map to the roadmap lessons:
`L01` boundaries · `L02` MVCC · `L03` ACID · `L04` read phenomena · `L05` lost update / write
skew · `L06` isolation levels · `L07` locking · `L08` retry / idempotency · `L09` boundary
placement · `L10` production strategy.

---

## F-1 · Import a venue seat map

**Business goal.** An operator uploads a venue's seating chart (sections, rows, seat numbers)
so events at that venue can sell reserved seats. Real charts contain duplicates and malformed
rows; one bad row must not discard the other 4 999.

**Workflow.**
1. Operator posts a CSV/JSON payload of up to 20 000 seats for a venue.
2. System validates the venue exists and is not archived.
3. System inserts seats one by one; rows violating uniqueness or check constraints are
   recorded as *rejected* and skipped.
4. System returns `{ inserted, rejected[] }`.

**Database operations.** `SELECT` venue; N × `INSERT INTO catalog.seat`; one `COMMIT`.

**Transaction requirements.** One transaction for the whole import (so a crash leaves no
half-map), but per-row recoverability inside it. Bad rows must not poison the transaction.

**Concurrency problems.** Two operators importing the same map concurrently → duplicate seats.
A long import holds a snapshot and pins the VACUUM horizon.

**Concepts practiced.** `L01` transaction state poisoning (`25P02`) · `L09` **savepoints** as
genuine try/fallback (`SAVEPOINT`/`ROLLBACK TO SAVEPOINT` per row) · `L09` savepoint cost
(`xid` burn, subtransaction SLRU) · `L02` a long transaction pins `OldestXmin` · batching
strategy (chunk into transactions of ~1 000 rows and justify the chunk size).

---

## F-2 · Create an event with ticket types

**Business goal.** An organizer defines a show: date, venue, currency, per-customer ticket
limit, and one or more ticket types (GA tiers with an allocation, or reserved-seat types bound
to venue sections).

**Workflow.**
1. Organizer posts the event with its ticket types in one request.
2. System validates: `starts_at > doors_open_at`, currency matches organizer, allocations ≥ 0,
   total reserved-seat allocation ≤ number of seats in the referenced sections.
3. System creates `event`, N × `ticket_type`, N × `ticket_type_inventory` (allocated = N,
   reserved = 0, sold = 0), and for reserved types one `event_seat` row per venue seat.
4. Event is created in status `draft`.

**Database operations.** `INSERT event`; `INSERT ticket_type` ×N; `INSERT
ticket_type_inventory` ×N; `INSERT event_seat` ×(seats).

**Transaction requirements.** Atomic: an event with ticket types but no inventory rows, or
with only half its `event_seat` rows, is a corrupt event that would oversell.

**Concurrency problems.** None significant — this is a low-frequency write. It is here to
establish the *shape* of a multi-table unit of work before contention is introduced.

**Concepts practiced.** `L01` atomicity across four tables · `L09` boundary = the whole
creation, not per table · `L03` which invariants become `CHECK`/`FOREIGN KEY` here rather than
application code.

---

## F-3 · Publish an event (go on sale)

**Business goal.** Move an event `draft → onsale`. This is the moment the herd arrives, so the
transition must guarantee the event is actually sellable.

**Workflow.**
1. Organizer calls publish.
2. System verifies: at least one ticket type with `allocated > 0`; **at least one enabled
   payment route** (INV-9); `starts_at` in the future.
3. System sets `status = 'onsale'`, `published_at = now()`, bumps `version`.
4. System writes an `EventPublished` outbox row.

**Database operations.** `SELECT` event `FOR UPDATE`; `SELECT count(*)` on ticket types and
payment routes; `UPDATE event`; `INSERT outbox`.

**Transaction requirements.** The checks and the state change must be one atomic decision. A
concurrent route-disable (F-5) must not be able to slip between the check and the write.

**Concurrency problems.** *Write skew*: publish reads "1 route enabled → OK" while a
concurrent transaction disables that route reading "event is only draft → safe to disable".
Both commit; an `onsale` event has zero routes.

**Concepts practiced.** `L05` write skew across two different rows · `L06` `SERIALIZABLE` ·
`L07` guard-row lock as the alternative · `L08` retry loop · `L03` outbox instead of publishing
inside the transaction.

---

## F-4 · Edit an event (reprice / re-allocate)

**Business goal.** An organizer changes prices or increases an allocation from a dashboard that
may be open in several tabs, or edited by two staff members at once. The last writer must not
silently discard the other's change.

**Workflow.**
1. Client `GET`s the event; response carries `ETag: "<version>"`.
2. Client `PATCH`es with `If-Match: "<version>"`.
3. System applies changes only if `version` still matches; otherwise `409 Conflict` with the
   current representation so the client can re-apply.
4. Allocation may only *increase* while `onsale`; decreasing below `reserved + sold` is
   rejected.

**Database operations.**
`UPDATE catalog.event SET …, version = version + 1 WHERE id = $1 AND version = $2` → check
rows-affected; `UPDATE catalog.ticket_type_inventory SET allocated = $n WHERE ticket_type_id =
$1 AND $n >= reserved + sold`.

**Transaction requirements.** `READ COMMITTED` is sufficient — correctness comes from the
optimistic guard in the `WHERE` clause, not from the isolation level.

**Concurrency problems.** *Lost update* — the classic read-modify-write-with-a-literal. Two
tabs both loaded price = €40; one sets €35, the other sets €45; without the version guard one
change vanishes silently.

**Concepts practiced.** `L05` lost update, fix #3 (**optimistic concurrency / version column**)
· `L04` non-repeatable read as the underlying cause · rows-affected as a conflict signal · HTTP
`ETag`/`If-Match` as the transport-level expression of the same idea.

> **Design note.** `version` lives on the *cold* `event` row. Sales counters live on a
> *separate* hot row (`ticket_type_inventory`) precisely so that ticket sales do not collide
> with organizer edits. See §12.4.

---

## F-5 · Manage payment routes

**Business goal.** Ops can enable/disable payment providers per event during a provider
incident — but an `onsale` event must never be left with zero routes (INV-9).

**Workflow.**
1. Operator lists routes for the event.
2. Operator disables one.
3. System refuses if this is the last enabled route for an `onsale` event.

**Database operations.**
`SELECT count(*) FROM catalog.payment_route WHERE event_id = $1 AND is_enabled`;
`UPDATE catalog.payment_route SET is_enabled = false WHERE id = $2`.

**Transaction requirements.** The count and the update must be serializable with respect to any
other route change on the same event.

**Concurrency problems.** **The canonical write skew.** Two operators, two different rows, each
reads `count = 2`, each disables a different route, both commit, count becomes 0. `REPEATABLE
READ` does *not* prevent this — the two transactions write different rows, so there is no
first-updater-wins collision.

**Concepts practiced.** `L05` write skew (§4.2) · `L06` SSI and the dangerous structure ·
`L08` `40001` retry where the retry re-reads and the *business rule* — not the engine —
produces the correct rejection · `L07` guard-row lock as the blocking alternative · `L10`
comparing both under load.

> **Required:** implement this feature **twice** (`SERIALIZABLE` + retry, and `FOR UPDATE` on
> the `event` guard row) behind a config switch, and report abort rate / p99 for both under
> `k6`. This is the project's headline comparison.

---

## F-6 · Browse availability

**Business goal.** A customer sees remaining GA counts per tier and a seat map with each seat
marked available / held / sold.

**Workflow.** `GET /events/{id}/availability` returns tier counts and, for reserved types, the
seat states as of one instant.

**Database operations.** `SELECT` from `ticket_type_inventory`; `SELECT` seats left-joined to
active holds (`during @> now()`) and to issued tickets.

**Transaction requirements.** Read-only. The response must be internally consistent — it must
not show a seat as *available* in the seat map while the tier counter says zero remaining,
because those came from two different snapshots.

**Concurrency problems.** *Non-repeatable read / torn read* across the two queries at
`READ COMMITTED`. Also: availability is **advisory** — it is stale the moment it is serialised.
The API contract must say so, and the hold endpoint must never trust it.

**Concepts practiced.** `L04` non-repeatable read · `L06` `REPEATABLE READ` for a multi-query
response · `L02` snapshot timing (first statement, not `BEGIN`) · `L10` "reads are advisory,
writes are authoritative" as an API design principle.

---

## F-7 · Create a cart

**Business goal.** A customer starts a purchase for one event. The cart is the scope for holds
and the per-customer limit.

**Workflow.** `POST /carts {eventId}` → returns cart id and `expiresAt = now() + 15 min`.
A customer may have at most one `open` cart per event.

**Database operations.** `INSERT INTO sales.cart … ON CONFLICT (customer_id, event_id) WHERE
status = 'open' DO NOTHING RETURNING *`, then `SELECT` the existing one if nothing returned.

**Transaction requirements.** Single statement; autocommit is fine.

**Concurrency problems.** Double-click / client retry creating two carts → the per-customer
limit becomes trivially bypassable. Prevented by a **partial unique index**, not by a
`SELECT`-then-`INSERT` check.

**Concepts practiced.** `L10` rung 1 (declarative constraint) · `L05` "check-then-insert" is a
race; `ON CONFLICT` is not · `L08` idempotent by construction.

---

## F-8 · Hold general-admission tickets

**Business goal.** Reserve `n` GA tickets of a tier for this cart for 15 minutes so the
customer can pay without losing them, and so the platform never sells more than it has.

**Workflow.**
1. Customer posts `{ ticketTypeId, quantity }`.
2. System checks the event is `onsale` and the cart is `open` and not expired.
3. System enforces the **per-customer limit** (INV-4).
4. System reserves inventory.
5. System writes a `hold` row with `expires_at`.

**Database operations.**
```sql
UPDATE sales.ticket_type_inventory
   SET reserved = reserved + $1
 WHERE ticket_type_id = $2
   AND reserved + sold + $1 <= allocated;      -- guard in the WHERE
-- rows-affected = 0  ⇒  sold out (or race lost)
INSERT INTO sales.hold (…) VALUES (…);
```

**Transaction requirements.** The inventory reservation and the hold row must be atomic —
reserved inventory with no hold row leaks capacity forever; a hold row with no reservation
oversells.

**Concurrency problems.**
- **Lost update** if implemented as `SELECT reserved` → compute in C# → `UPDATE SET reserved =
  @literal`. Two customers both read 1 998 of 2 000, both write 1 999, two tickets vanish from
  the ledger and the tier oversells later.
- **Phantom / write skew** on the per-customer limit (see F-12).

**Concepts practiced.** `L05` lost update fix #1 (**atomic single-statement write**) · guard in
the `WHERE` + **rows-affected as the business signal** · `L10` rung 2 · `CHECK (reserved + sold
<= allocated)` as the belt-and-braces rung-1 backstop that makes an oversell physically
impossible even if the query is wrong.

---

## F-9 · Hold specific reserved seats

**Business goal.** A customer picks seats `H-12`, `H-13` from the map and holds them for 15
minutes. No two customers may hold the same seat at the same time, and an expired hold must
free the seat *without* a cleanup job having to run first.

**Workflow.**
1. Customer posts `{ seatIds: [...] }` (max 6).
2. System sorts seat ids (deadlock avoidance) and inserts one hold row per seat with
   `during = tstzrange(now(), now() + interval '15 minutes')`.
3. Any overlap with an existing hold, or with a sold ticket, rejects the whole request.

**Database operations.**
```sql
INSERT INTO sales.seat_hold (event_id, seat_id, cart_id, during)
VALUES ($1, $2, $3, tstzrange(now(), now() + interval '15 minutes'));
-- EXCLUDE USING gist (event_id WITH =, seat_id WITH =, during WITH &&)
--   ⇒ SQLSTATE 23P01 exclusion_violation if another hold overlaps
```

**Transaction requirements.** All-or-nothing across the seats in one request. `READ COMMITTED`
is sufficient — the exclusion constraint holds against every writer at every isolation level.

**Concurrency problems.**
- Two customers race for one seat → exactly one wins, the other gets `23P01` → mapped to
  `409 Conflict`.
- A hold that expires at `T` frees the seat at `T` automatically, because the *range* no longer
  overlaps a new `[now, now+15m)` range. The reaper (F-17) is then only a **bookkeeping**
  cleanup, not a correctness dependency. This is the single most elegant piece of the design
  and is worth understanding deeply.
- Two customers each grabbing seats `{A,B}` and `{B,A}` → deadlock unless ids are sorted.

**Concepts practiced.** `L05` write skew defeated by an **`EXCLUDE` constraint** (rung 1) ·
`L07` `FOR UPDATE` explicitly *cannot* do this job (it cannot lock rows that do not exist yet) ·
`L07` consistent lock ordering · `btree_gist` extension · `L10` "prefer a constraint over an
isolation level".

---

## F-10 · Release a hold

**Business goal.** The customer removes seats/tickets from the cart, or abandons it.

**Workflow.** Delete the `seat_hold` rows / decrement `ticket_type_inventory.reserved`, update
the per-customer guard row.

**Database operations.** `DELETE FROM sales.seat_hold WHERE …`;
`UPDATE ticket_type_inventory SET reserved = reserved - $1 WHERE … AND reserved >= $1`.

**Transaction requirements.** Atomic with the counter decrement. Must be **idempotent** — a
double release must not decrement twice (guard with `reserved >= $1` and rows-affected, and
delete-then-check).

**Concurrency problems.** Release racing the reaper (F-17) for the same expired hold → double
decrement → reserved underflows → phantom capacity. Guarded by `CHECK (reserved >= 0)` and by
making the decrement conditional on the delete actually removing a row (`WITH deleted AS
(DELETE … RETURNING …) UPDATE … FROM deleted`).

**Concepts practiced.** `L08` idempotency of compensating actions · data-modifying CTEs as a
way to make "delete and adjust" a single atomic statement · `CHECK` as an underflow tripwire.

---

## F-11 · Apply a promo code

**Business goal.** `EARLY25` gives €5 off, limited to a €2 500 total discount budget, a maximum
redemption count, and one redemption per customer.

**Workflow.**
1. Customer posts the code against their cart.
2. System validates the code exists for the event, is inside its validity range, and has budget
   left.
3. System records the intended redemption and stores the discount on the cart.

**Database operations.**
```sql
UPDATE sales.promo_code
   SET redeemed_minor  = redeemed_minor + $1,
       redemption_count = redemption_count + 1
 WHERE id = $2
   AND valid @> now()
   AND redeemed_minor + $1 <= budget_minor
   AND redemption_count + 1 <= max_redemptions;
-- rows-affected = 0 ⇒ exhausted / invalid
INSERT INTO sales.promo_redemption (promo_code_id, customer_id, cart_id, amount_minor) …;
-- UNIQUE (promo_code_id, customer_id) ⇒ 23505 on a second attempt by the same customer
```

**Transaction requirements.** Counter increment and redemption row in one transaction.

**Concurrency problems.** The naive version — `SELECT SUM(amount_minor) FROM promo_redemption
WHERE promo_code_id = $1`, compare to budget, then `INSERT` — is **write skew on a `SUM`
predicate**: N concurrent customers each see €2 495 used, each adds €5, and the budget is
blown by (N−1) × €5. `REPEATABLE READ` does not help; the rows written are all different.

**Concepts practiced.** `L05` write skew · **materialising the conflict**: turning a predicate
over many rows into a single counter row that concurrent writers collide on · `L10` rung 1+2
beats rung 4 · `CHECK (redeemed_minor <= budget_minor)` as the constraint that makes the
invariant true even against a buggy code path.

---

## F-12 · Enforce the per-customer ticket limit

**Business goal.** No customer may hold or own more than `event.max_tickets_per_customer`
(default 6) tickets for one event — the anti-scalping rule.

**Workflow.** Evaluated inside F-8, F-9 and F-13, never as a standalone endpoint.

**Naive database operations (wrong).**
```sql
SELECT coalesce(sum(quantity), 0)
  FROM sales.hold JOIN sales.cart ON … WHERE customer_id = $1 AND event_id = $2
UNION ALL … tickets owned …;
-- if total + requested <= limit then INSERT the hold
```

**Correct database operations.**
```sql
INSERT INTO sales.customer_event_allocation (customer_id, event_id, held, owned, limit_snapshot)
VALUES ($1, $2, 0, 0, $3)
ON CONFLICT (customer_id, event_id) DO NOTHING;

UPDATE sales.customer_event_allocation
   SET held = held + $4
 WHERE customer_id = $1 AND event_id = $2
   AND held + owned + $4 <= limit_snapshot;    -- rows-affected = 0 ⇒ limit exceeded
-- plus CHECK (held + owned <= limit_snapshot) on the table
```

**Transaction requirements.** The allocation update happens in the *same* transaction as the
hold/ticket write. It is the guard row that serialises all of a customer's concurrent requests
for one event.

**Concurrency problems.** **Phantom read + write skew.** The rows that would change the
decision (other holds by the same customer) *do not exist yet* when the check runs, so no
`SELECT … FOR UPDATE` can lock them. This is the required phantom drill (`CH-03`).

**Concepts practiced.** `L04` phantom read · `L05` write skew, guard-row fix · `L07` why
`FOR UPDATE` provably cannot solve it · `L06` `SERIALIZABLE` as the alternative you must also
implement and benchmark.

---

## F-13 · Checkout (cart → order)

**Business goal.** Freeze the cart into an immutable order at a fixed price, ready for payment.
Submitting checkout twice must produce one order.

**Workflow.**
1. Client sends `Idempotency-Key`.
2. System validates all holds are still live (`during @> now()`, inventory still reserved).
3. System computes totals from *server-side* prices, applies the promo discount.
4. System creates `order` + `order_line` rows in status `pending_payment`.
5. System extends holds to cover the payment window.

**Database operations.** `SELECT … FOR UPDATE` on the cart; `SELECT` holds; `INSERT order`;
`INSERT order_line` ×N; `UPDATE seat_hold SET during = …`; `INSERT ops.idempotency_key`.

**Transaction requirements.** One transaction. `READ COMMITTED` + the idempotency-key unique
constraint.

**Concurrency problems.** Duplicate submission (browser retry, mobile flakiness) → two orders
for one cart → the customer is charged twice. Prevented by `UNIQUE (idempotency_key)` plus a
**stored response**, so the replay returns the *same* `201` body rather than a `409`.

**Concepts practiced.** `L08` idempotency keys end to end · `L03` "the client's knowledge of
the outcome" is not covered by atomicity · request-fingerprint checking (same key + different
body ⇒ `422`, a real production requirement).

---

## F-14 · Pay from wallet

**Business goal.** Debit the customer's wallet and credit the organizer's payable account,
recording a balanced double-entry journal, and issue tickets.

**Workflow.**
1. Validate order is `pending_payment` and unexpired.
2. Lock the two account rows **in a canonical order** (by account id).
3. Verify sufficient balance.
4. Write journal + entries; update both materialised balances.
5. Convert reservations to sales (`reserved -= n, sold += n`), delete holds, insert tickets.
6. Mark order `paid`, write `OrderPaid` + `TicketsIssued` outbox rows.

**Database operations.**
```sql
SELECT account_id, balance_minor FROM money.account_balance
 WHERE account_id = ANY($1) ORDER BY account_id FOR UPDATE;   -- canonical order

UPDATE money.account_balance SET balance_minor = balance_minor - $1, version = version + 1
 WHERE account_id = $2 AND balance_minor >= $1;                -- rows-affected guard
-- … credit side … INSERT journal + 2 entries (deferred balance check at COMMIT) …
```

**Transaction requirements.** Everything in one transaction: money moves, inventory converts,
tickets exist, outbox row written. Nothing external inside the boundary.

**Concurrency problems.**
- **Lost update** on the balance if computed in the app.
- **Deadlock (`40P01`)** if two payments lock the same two accounts in opposite orders — very
  real when a customer pays organizer A while a refund from organizer A pays that customer.
- Negative balance if the guard is a `SELECT` instead of a `WHERE` predicate.

**Concepts practiced.** `L07` **`SELECT … FOR UPDATE`**, canonical lock ordering, deadlock
detection and retry · `L05` atomic write with a guard · `L03` deferred constraint trigger for
double-entry balance · `L01` atomicity across five tables · `L08` outbox.

---

## F-15 · Pay by card (external PSP)

**Business goal.** Authorize a card through an external provider and capture it, without ever
holding a database transaction open across the network call.

**Workflow (three transactions + a reconciler).**
1. **TX-A (short):** create `payment` row in `initiated` with the idempotency key, extend the
   holds, `COMMIT`.
2. **No transaction:** call the PSP `authorize`. This can time out with an unknown outcome.
3. **TX-B (short):** record the authorization result, and on success do the same ledger +
   issuance work as F-14 against a `psp_clearing` account, `COMMIT`.
4. **Reconciler worker:** finds payments stuck in `initiated`/`authorizing` past a deadline,
   queries the PSP by idempotency key, and completes or compensates them.

**Database operations.** As above, plus `ops.job` rows for the reconciler and `outbox` rows for
the notifications.

**Transaction requirements.** **No external I/O between `BEGIN` and `COMMIT`.** Each database
step is its own short transaction. The saga's intermediate states are explicit and persisted,
never held in memory.

**Concurrency problems.** Crash between step 2 and step 3 ⇒ the customer is charged but has no
tickets. A duplicate PSP callback ⇒ double capture. Holds expiring mid-authorization ⇒ tickets
sold from under a paying customer.

**Concepts practiced.** `L09` **boundary placement** — the whole point of the feature · `L03`
atomicity does not extend outside the database · `L08` idempotency across a system boundary ·
reaper/reconciler design · explicit state machines instead of implicit in-flight state.

---

## F-16 · Issue tickets

**Business goal.** Turn paid order lines into individual tickets with unique barcodes.

**Workflow.** Inside the payment transaction (F-14 step 5 / F-15 TX-B): one `ticket` row per
unit, barcode from a collision-resistant generator, `UNIQUE (barcode)`.

**Database operations.** `INSERT INTO sales.ticket … ON CONFLICT (order_line_id, seq) DO
NOTHING` — so a retried payment transaction cannot double-issue.

**Transaction requirements.** Same transaction as the money movement. Tickets without money or
money without tickets are both unacceptable.

**Concurrency problems.** A retried transaction body (after `40001`) re-running the inserts.
Solved by a natural key `(order_line_id, seq)` and `ON CONFLICT DO NOTHING`, which is the
idempotency requirement from `L08 §8.3`.

**Concepts practiced.** `L08` idempotent inserts inside a retried body · `L05` partial unique
index preventing a seat from ever appearing on two live tickets (INV-1).

---

## F-17 · Hold expiry reaper

**Business goal.** Reclaim GA inventory from expired holds and clean up expired hold rows, with
several workers running concurrently and none of them processing the same hold twice.

**Workflow.**
1. Worker claims a batch of expired holds.
2. For each, decrement `reserved` and `customer_event_allocation.held`, delete the hold.
3. Commit and take the next batch.

**Database operations.**
```sql
BEGIN;
SELECT id, ticket_type_id, quantity, cart_id
  FROM sales.hold
 WHERE expires_at < now() AND released_at IS NULL
 ORDER BY expires_at
 FOR UPDATE SKIP LOCKED
 LIMIT 100;
-- … per row: UPDATE inventory, UPDATE allocation, UPDATE hold SET released_at = now() …
COMMIT;
```

**Transaction requirements.** Short transactions, one batch each. Never hold the lock while
doing per-row application work that could be slow.

**Concurrency problems.**
- Two reapers claiming the same hold → double decrement → inventory underflow. `SKIP LOCKED`
  makes this structurally impossible.
- A reaper racing a customer who is checking out against a hold that expires *right now* →
  the customer must lose cleanly, not get tickets that were reclaimed.
- Without `SKIP LOCKED`, N workers serialise on the first row and throughput collapses to one
  worker's — a result you are required to measure.

**Concepts practiced.** `L07` **`FOR UPDATE SKIP LOCKED`** queue pattern · `L07` the
`processing` + `locked_at` + reaper-of-the-reaper variant for slow work · `L04` phantom rows
appearing between batches · `L10` job-queue decision-guide entry.

---

## F-18 · Refund an order (fully or partially)

**Business goal.** A support agent refunds some or all of a captured payment. Total refunds must
never exceed what was captured (INV-7), even with several agents acting at once.

**Workflow.**
1. Agent posts `{ amount, reason }` with an `Idempotency-Key`.
2. System validates the order is refundable and the amount is available.
3. System writes the refund row, a reversing journal, updates balances, voids tickets if the
   refund is full.

**Database operations.**
```sql
UPDATE money.payment
   SET refunded_minor = refunded_minor + $1
 WHERE id = $2 AND status = 'captured'
   AND refunded_minor + $1 <= captured_minor;    -- rows-affected = 0 ⇒ over-refund
INSERT INTO money.refund (…, idempotency_key) …;  -- UNIQUE (idempotency_key)
-- reversing journal + entries + balance updates
```

**Transaction requirements.** One transaction. `READ COMMITTED` suffices *because* the
predicate was materialised into a counter with a `CHECK`.

**Concurrency problems.** Naive `SELECT SUM(amount) FROM refund WHERE payment_id = $1` →
write skew, over-refund. Duplicate agent clicks → double refund without the idempotency key.

**Concepts practiced.** `L05` write skew on a `SUM` → counter fix · `L08` idempotency · `L03`
the reversing journal (never `DELETE` financial history) · `CHECK (refunded_minor <=
captured_minor)`.

---

## F-19 · Wallet-to-wallet transfer

**Business goal.** A group organiser splits costs by sending money to a friend's wallet.

**Workflow.** Debit A, credit B, one balanced journal, one outbox event.

**Database operations.** Lock both `account_balance` rows in canonical id order; two guarded
`UPDATE`s; `INSERT journal` + 2 entries.

**Transaction requirements.** Atomic; `sum(balance)` across the two accounts must be
unchanged; no balance may go negative.

**Concurrency problems.** **Deadlock** — this is the designated deadlock drill. A→B and B→A
concurrently, each locking in "from, to" order, form a cycle; one transaction dies with
`40P01`. Required work: reproduce it, capture the `DETAIL`, then fix it by canonical ordering
and prove 200 randomised concurrent transfers produce zero deadlocks.

**Concepts practiced.** `L07` deadlock formation, `deadlock_timeout`, victim selection,
consistent lock ordering · `L08` `40P01` is retryable exactly like `40001` · `L10` deadlock
rate as an alert.

---

## F-20 · Organizer payout run

**Business goal.** Nightly, pay each organizer their settled revenue minus a rolling reserve,
while refunds are still arriving.

**Workflow.**
1. Batch worker claims organizers with `FOR UPDATE SKIP LOCKED`.
2. Per organizer: compute available = payable balance − reserve.
3. If available ≥ minimum payout, write the payout row, move money to a `payout_clearing`
   account, write an outbox row for the bank file.

**Database operations.** `SELECT … FROM money.account_balance JOIN catalog.organizer … FOR
UPDATE OF account_balance SKIP LOCKED LIMIT 50`; guarded `UPDATE`; `INSERT payout`.

**Transaction requirements.** One transaction per organizer (not per batch), so one failing
organizer does not roll back 49 good payouts. Idempotency key = `(organizer_id, business_date)`
with a `UNIQUE` constraint, so re-running the nightly job is safe.

**Concurrency problems.** Payout racing a refund that reduces the payable balance → over-payout
(INV-11). Two payout runs (a cron overlap) paying twice. Both are exercised.

**Concepts practiced.** `L07` `SKIP LOCKED` batch + `FOR UPDATE` guard row · `L08` idempotency
key on a scheduled job · `L05` write skew avoided by locking the row the invariant is *about* ·
`L09` per-item transaction boundary inside a batch.

---

## Cross-cutting: workers and ops surface

| Component | Purpose | Key technique |
|---|---|---|
| **Outbox publisher** | Deliver domain events at least once after commit | `FOR UPDATE SKIP LOCKED` batch, `published_at` stamp, exponential `next_attempt_at`, consumer-side dedup table |
| **PSP reconciler** | Resolve payments stranded by a crash or timeout | claim by `SKIP LOCKED`, query PSP by idempotency key, complete or compensate |
| **Sales rollup** | Maintain `event_sales_daily` without hammering one hot row | append-only entries + periodic aggregation (the Phase-7 hot-row redesign) |
| **Invariant checker** (`GET /ops/invariants`) | Runtime proof that INV-1…INV-12 hold | `SERIALIZABLE READ ONLY DEFERRABLE` so the check itself is never a source of aborts |
| **Introspection** (`GET /ops/locks`, `/ops/transactions`) | Expose `pg_locks`, `pg_stat_activity`, blocking tree | the lesson-10 §7.1 queries as endpoints |
