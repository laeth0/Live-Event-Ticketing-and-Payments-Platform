# 7 · API Design

REST over JSON. Every write endpoint declares its **isolation level, locking strategy,
constraints relied upon, and retry policy** — that declaration is part of the contract, not an
implementation detail, and it is reviewed like code.

---

## 7.0 Conventions

| Aspect | Rule |
|---|---|
| Base | `/api/v1` |
| Actor | `X-Actor-Id: <uuid>` + `X-Actor-Role: customer|organizer|support|ops` (auth is stubbed; the role check is not) |
| Idempotency | `Idempotency-Key: <=200 chars` **required** on every non-idempotent `POST`. Missing ⇒ `400`. |
| Concurrency token | `ETag: "<version>"` on gettable aggregates; `If-Match` required on `PATCH`. Missing ⇒ `428 Precondition Required`. |
| Money in JSON | `{"amountMinor": 4500, "currency": "EUR"}` — integers only, never decimals |
| Errors | RFC 9457 `application/problem+json` with an extra `code` field (a stable machine string) |
| Time | RFC 3339 UTC with `Z` |
| Pagination | keyset (`?after=<id>&limit=`), never `OFFSET`, on every collection |
| Read freshness | every advisory read carries `Cache-Control: no-store` and an `X-Snapshot-At` header |

### Global error mapping

The **only** place SQLSTATEs become HTTP statuses. One middleware, one table, no ad-hoc
`catch` blocks in handlers.

| Condition | SQLSTATE | HTTP | `code` | Retryable by client? |
|---|---|---|---|---|
| Unique violation (idempotency key in flight, duplicate cart) | `23505` | `409 Conflict` | `duplicate_request` / `already_exists` | after backoff |
| Exclusion violation (seat already held) | `23P01` | `409 Conflict` | `seat_unavailable` | yes, different seat |
| Check violation (invariant tripwire hit) | `23514` | `500` **and page someone** | `invariant_violation` | no — this means a query was wrong |
| Foreign key violation | `23503` | `422` | `invalid_reference` | no |
| Not-null / type / cast | `23502`, `22P02` | `400` | `invalid_request` | no |
| Lock wait exceeded | `55P03` | `503` + `Retry-After: 1` | `busy` | yes |
| Statement timeout | `57014` | `504` | `timeout` | yes |
| Serialization failure after all retries | `40001` | `503` + `Retry-After: 1` | `conflict_retry_exhausted` | yes |
| Deadlock after all retries | `40P01` | `503` + `Retry-After: 1` | `conflict_retry_exhausted` | yes |
| Business rule rejected | — | `409` or `422` | e.g. `sold_out`, `customer_limit_exceeded`, `insufficient_funds`, `last_payment_route`, `refund_exceeds_capture`, `promo_exhausted` | **no** |
| Idempotency key reused with a different body | — | `422` | `idempotency_key_reused` | no |

`23514` mapping to `500` is deliberate: a `CHECK` firing means an application query was wrong
and a tripwire caught it. That is an incident, not a user error.

---

## 7.1 Catalog

### `POST /api/v1/venues/{venueId}/seats:import`

Bulk import a seat map (F-1, TX-03).

- **Request** `{ "seats": [ { "section": "A", "row": "12", "number": "7" }, … ] }` (≤ 20 000)
- **Response `200`** `{ "inserted": 19988, "rejected": [ { "index": 412, "code": "23505" }, … ] }`
- **Transaction behaviour** chunks of 1 000 rows, one transaction per chunk, a `SAVEPOINT` per
  row inside the chunk.
- **Isolation** `READ COMMITTED`.
- **Locking** none beyond insert locks.
- **Constraints relied upon** `seat_unique_in_venue`.
- **Retry** none at the transaction level; rejected rows are reported, not retried.
- **Errors** `404` unknown venue · `413` payload too large.

### `POST /api/v1/events`

Create an event with ticket types (F-2).

- **Request** `{ organizerId, venueId, name, currency, doorsOpenAt, startsAt, maxTicketsPerCustomer, ticketTypes:[{name, kind, priceMinor, allocated, sections?}] }`
- **Response `201`** the event with `ETag: "0"`.
- **Transaction behaviour** one transaction: `event` + `ticket_type` ×N +
  `ticket_type_inventory` ×N + `event_seat` ×(seats).
- **Isolation** `READ COMMITTED`. **Locking** none. **Retry** none.
- **Errors** `422` `starts_at <= doors_open_at`, allocation exceeds section seat count.

### `PATCH /api/v1/events/{eventId}`

Edit an event (F-4, TX-04).

- **Headers** `If-Match: "<version>"` required.
- **Request** any of `{ name, maxTicketsPerCustomer, ticketTypes:[{id, priceMinor, allocated}] }`
- **Response `200`** updated event with the new `ETag`; **`409`** with the current
  representation on a version mismatch.
- **Transaction behaviour** one transaction; the event `UPDATE` carries
  `AND version = $ifMatch`; allocation changes carry `AND $new >= reserved + sold`.
- **Isolation** `READ COMMITTED`.
- **Locking** **optimistic** — no locks. Contention is hourly; blocking would be waste.
- **Constraints relied upon** `event.version`, `inventory_never_oversells`.
- **Retry** none — a `409` is a real conflict the client must resolve by re-reading.
- **Errors** `428` missing `If-Match` · `409` stale version · `422` allocation below sold.

### `POST /api/v1/events/{eventId}/publish`

Go on sale (F-3).

- **Response `200`** `{ status: "onsale", publishedAt }`
- **Transaction behaviour** one transaction: verify ≥1 ticket type with allocation, verify
  `enabled_route_count >= 1`, set status, write outbox row.
- **Isolation** `READ COMMITTED` (the `event_onsale_needs_route` `CHECK` makes the invariant
  unconditional, so no higher level is needed).
- **Locking** `SELECT … FROM catalog.event WHERE id=$1 FOR UPDATE` — serialises publish against
  a concurrent route disable.
- **Constraints relied upon** `event_onsale_needs_route`.
- **Errors** `409` `no_payment_route`, `no_allocation`, `already_onsale`.

### `PATCH /api/v1/events/{eventId}/payment-routes/{routeId}`

Enable/disable a route (F-5, TX-11, CH-02a). **The project's headline endpoint.**

- **Request** `{ "isEnabled": false }`
- **Response `200`** the route · **`409 last_payment_route`** if it would leave zero.
- **Transaction behaviour** three implementations behind
  `Concurrency:RouteToggleStrategy = Serializable | GuardRow | Counter`:

| Strategy | Isolation | Locking | Constraint | Retry |
|---|---|---|---|---|
| `Serializable` | `SERIALIZABLE` | none (SSI predicate tracking) | — | **required**, bounded + full jitter |
| `GuardRow` | `READ COMMITTED` | `SELECT … FROM catalog.event WHERE id=$1 FOR UPDATE` | — | `40P01` only |
| `Counter` *(default)* | `READ COMMITTED` | implicit row lock on `catalog.event` | `event_onsale_needs_route` | `40P01` only |

- **Errors** `409 last_payment_route` (business, never retried) · `503` if retries are exhausted
  in `Serializable` mode.

---

## 7.2 Browsing

### `GET /api/v1/events/{eventId}/availability`

- **Response `200`**
  ```json
  { "snapshotAt": "2026-09-02T20:00:03Z",
    "tiers": [ { "ticketTypeId": "…", "priceMinor": 4500, "remaining": 812 } ],
    "seats": [ { "seatId": "…", "section": "A", "row": "12", "number": "7", "state": "available" } ] }
  ```
- **Transaction behaviour** one `REPEATABLE READ` **read-only** transaction so the tier counts
  and the seat map come from one snapshot, then `COMMIT` immediately.
- **Isolation** `REPEATABLE READ`. **Locking** none (MVCC).
- **Contract note** the response is **advisory**. `state: "available"` is not a promise; the
  hold endpoint is the only authority. Documented in the OpenAPI description so no client
  builds a correctness assumption on it.
- **Errors** `404`.

### `GET /api/v1/customers/{customerId}/statement?from=&to=`

Balance plus ledger entries as of one instant (TX-12).

- **Transaction behaviour** one transaction reading `account_balance` and `journal_entry`.
- **Isolation** `REPEATABLE READ`; `SERIALIZABLE READ ONLY DEFERRABLE` when the range exceeds
  90 days.
- **Guarantee** the returned balance always equals the sum of the returned entries plus the
  opening balance. A test asserts this while transfers run (T-C6a, T-C6b).

---

## 7.3 Carts and holds

### `POST /api/v1/carts`

- **Request** `{ eventId }` · **Response `201`/`200`** `{ cartId, expiresAt }`
- **Transaction behaviour** single statement
  `INSERT … ON CONFLICT DO NOTHING RETURNING *`, then read back on conflict.
- **Isolation** `READ COMMITTED`. **Locking** none.
- **Constraints relied upon** `cart_one_open_per_customer_event_uk` (partial unique).
- **Retry** none — idempotent by construction, so no key is needed.
- **Errors** `409 event_not_onsale`.

### `POST /api/v1/carts/{cartId}/holds/ga`

Hold GA tickets (F-8, TX-02, TX-14).

- **Request** `{ ticketTypeId, quantity }` (1–6)
- **Response `201`** `{ holdId, expiresAt, remaining }`
- **Transaction behaviour** one transaction, in this order:
  1. upsert + guarded `UPDATE` on `customer_event_allocation` (rows-affected 0 ⇒
     `customer_limit_exceeded`);
  2. guarded `UPDATE` on `ticket_type_inventory` (rows-affected 0 ⇒ `sold_out`);
  3. `INSERT` the hold row.
- **Isolation** `READ COMMITTED`.
- **Locking** implicit row locks on the allocation row and the inventory row, acquired in that
  fixed order **in every code path** (allocation before inventory) — the canonical ordering that
  keeps holds and reaper batches from deadlocking.
- **Constraints relied upon** `within_customer_limit`, `inventory_never_oversells`.
- **Retry** `40P01` only.
- **Errors** `409 sold_out` · `409 customer_limit_exceeded` · `409 cart_expired` ·
  `409 event_not_onsale` · `422 quantity_out_of_range`.

### `POST /api/v1/carts/{cartId}/holds/seats`

Hold specific seats (F-9, TX-01).

- **Request** `{ seatIds: ["…","…"] }` (≤ 6)
- **Response `201`** `{ holds: [{ holdId, seatId, expiresAt }] }`
- **Transaction behaviour** one transaction; ids sorted ascending; one `INSERT` per seat with
  `during = tstzrange(now(), now() + '15 minutes')`; allocation guard first, as above.
- **Isolation** `READ COMMITTED`.
- **Locking** none explicit — the `EXCLUDE` constraint serialises competitors. Sorting the ids
  removes the multi-seat deadlock.
- **Constraints relied upon** `seat_hold_no_overlap` (GiST `EXCLUDE`), `within_customer_limit`.
- **Retry** `40P01` only. `23P01` is **never** retried — it is a real business outcome.
- **Errors** `409 seat_unavailable` (with the offending `seatId`) · `409 customer_limit_exceeded`
  · `404 seat_not_in_event`.

### `DELETE /api/v1/carts/{cartId}/holds/{holdId}`

Release a hold (F-10).

- **Response `204`**, and `204` again on a repeat (idempotent).
- **Transaction behaviour** one data-modifying CTE: `DELETE`/mark released `RETURNING`, then
  decrement inventory and allocation **only for rows this statement actually released**.
- **Isolation** `READ COMMITTED`. **Locking** implicit.
- **Constraints relied upon** `CHECK (reserved >= 0)`, `CHECK (held >= 0)` as underflow
  tripwires.
- **Errors** `404` unknown hold.

### `POST /api/v1/carts/{cartId}/promo`

Apply a promo code (F-11, TX-13).

- **Request** `{ code }` · **Response `200`** `{ discountMinor, totalMinor }`
- **Transaction behaviour** one transaction: guarded counter `UPDATE` on `promo_code`, then
  `INSERT promo_redemption`.
- **Isolation** `READ COMMITTED`. **Locking** implicit row lock on the promo row (a known hot
  spot — see §8 Phase 7).
- **Constraints relied upon** `promo_within_budget`, `promo_within_count`,
  `promo_once_per_customer`.
- **Errors** `409 promo_exhausted` · `409 promo_already_used` (from `23505`) · `404 promo_unknown`
  · `422 promo_expired`.

---

## 7.4 Checkout and payment

### `POST /api/v1/carts/{cartId}/checkout`

Freeze the cart into an order (F-13, TX-05).

- **Headers** `Idempotency-Key` required.
- **Request** `{}` (everything is server-side)
- **Response `201`** `{ orderId, totalMinor, currency, expiresAt, lines:[…] }`; a replay returns
  the identical body with `Idempotency-Replayed: true`.
- **Transaction behaviour** one transaction: claim the idempotency key, `SELECT … FOR UPDATE`
  the cart, revalidate every hold is live, compute totals from server prices, insert
  `ticket_order` + lines, extend holds to cover the payment window, store the response.
- **Isolation** `READ COMMITTED`.
- **Locking** `FOR UPDATE` on the cart row (prevents two concurrent checkouts of one cart from
  both revalidating and both proceeding).
- **Constraints relied upon** `order_idempotency_uk`, `order_one_per_cart`,
  `order_total_consistent`.
- **Retry** `40001`/`40P01` only; the body is idempotent (all inserts carry natural keys).
- **Errors** `409 hold_expired` · `409 cart_already_checked_out` · `409 duplicate_request` ·
  `422 idempotency_key_reused` · `400 missing_idempotency_key`.

### `POST /api/v1/orders/{orderId}/pay`

Pay by wallet or card (F-14/F-15, TX-06/TX-15).

- **Headers** `Idempotency-Key` required.
- **Request** `{ "method": "wallet" }` or `{ "method": "card", "cardToken": "tok_…" }`
- **Response `200`** `{ paymentId, status: "captured", tickets: [ { ticketId, seatId, barcode } ] }`
  · **`202`** `{ paymentId, status: "authorizing" }` when the PSP is slow (client polls
  `GET /payments/{id}`).

**Wallet path**

| Aspect | Value |
|---|---|
| Transaction behaviour | **one** transaction: lock both balance rows, guarded debit + credit, journal + entries (deferred balance check at `COMMIT`), inventory `reserved→sold`, seat holds converted to `[lower, infinity)`, tickets inserted, order `paid`, outbox rows |
| Isolation | `READ COMMITTED` |
| Locking | `SELECT … WHERE account_id IN ($from,$to) ORDER BY account_id FOR UPDATE`; `SET LOCAL lock_timeout = '3s'` |
| Constraints relied upon | `wallet_never_negative`, `journal_entry_balanced` (deferred), `ticket_natural_uk`, `ticket_one_live_per_seat_uk`, `payment_idempotency_uk`, `outbox_dedup_uk` |
| Retry | `40001`/`40P01`, bounded + full jitter; the body is idempotent |
| Side effects | **none inside the transaction** — the outbox worker publishes after commit |

**Card path**

| Aspect | Value |
|---|---|
| Transaction behaviour | **three** short transactions (TX-A create/extend, PSP call outside, TX-B record+issue) plus a reconciler job |
| Isolation | `READ COMMITTED` throughout |
| Locking | none held across the PSP call; `WHERE status='authorizing'` is the guard |
| Constraints | `payment_idempotency_uk`, `payment_attempt_uk`, `payment_capture_bound` |
| Retry | per-transaction retry on `40001`/`40P01`; the PSP call itself is retried by the reconciler using the payment's idempotency key, never blindly |

- **Errors** `409 insufficient_funds` · `409 order_not_payable` · `409 hold_expired` ·
  `409 no_payment_route` · `402 card_declined` · `409 duplicate_request` · `503` on retry
  exhaustion.

### `GET /api/v1/orders/{orderId}` · `GET /api/v1/payments/{paymentId}`

Read models. `READ COMMITTED`, single statement, no locks. `GET /payments/{id}` is the poll
target for the `202` card path and returns `initiated|authorizing|captured|failed|voided`.

---

## 7.5 Money

### `POST /api/v1/wallets/{walletId}/topups`

- **Headers** `Idempotency-Key` required.
- **Request** `{ amountMinor, currency, cardToken }` · **Response `201`** `{ balanceMinor }`
- **Transaction behaviour** PSP charge **outside** any transaction, then one short transaction:
  journal (`topup`) + entries + guarded balance `UPDATE`.
- **Isolation** `READ COMMITTED`. **Locking** implicit row lock on the balance row.
- **Constraints** `journal_reference_uk` makes the ledger posting idempotent; `payment_idempotency_uk`
  makes the charge idempotent.
- **Errors** `402 card_declined` · `409 duplicate_request`.

### `POST /api/v1/wallets/{walletId}/transfers`

Wallet-to-wallet (F-19, TX-08, CH-04).

- **Headers** `Idempotency-Key` required.
- **Request** `{ toWalletId, amountMinor, currency }` · **Response `201`** `{ journalId, balanceMinor }`
- **Transaction behaviour** one transaction: lock both balances **in ascending account id order
  within a single statement**, guarded debit + credit, journal + two entries.
- **Isolation** `READ COMMITTED`.
- **Locking** `FOR UPDATE` with canonical ordering — the designated deadlock drill.
- **Constraints** `wallet_never_negative`, `journal_entry_balanced`.
- **Retry** `40P01` and `40001`, bounded + full jitter.
- **Errors** `409 insufficient_funds` · `422 same_wallet` · `422 currency_mismatch`.

### `POST /api/v1/orders/{orderId}/refunds`

Partial or full refund (F-18, TX-09, CH-02b). Role `support` only.

- **Headers** `Idempotency-Key` required.
- **Request** `{ amountMinor, reason }` · **Response `201`** `{ refundId, refundedMinor, remainingRefundableMinor }`
- **Transaction behaviour** one transaction: guarded counter `UPDATE` on `payment`, insert
  `refund`, reversing journal + entries + balance updates, void tickets if fully refunded,
  outbox row.
- **Isolation** `READ COMMITTED`.
- **Locking** implicit row lock on the `payment` row — which is exactly the row the invariant is
  about.
- **Constraints** `payment_refund_bound` (`CHECK`), `refund_idempotency_uk`.
- **Retry** `40P01`/`40001` only.
- **Errors** `409 refund_exceeds_capture` · `409 order_not_refundable` · `409 duplicate_request`.

### `POST /api/v1/organizers/{organizerId}/payouts`

Manual trigger of the payout the nightly job also performs (F-20, TX-10).

- **Headers** `Idempotency-Key` required.
- **Request** `{ businessDate }` · **Response `201`** `{ payoutId, amountMinor }` ·
  **`204`** when nothing is payable.
- **Transaction behaviour** one transaction per organizer: `FOR UPDATE` on the payable balance,
  guarded `UPDATE` enforcing `balance - amount >= reserve`, insert payout, move money to
  `payout_clearing`.
- **Isolation** `READ COMMITTED`. **Locking** `FOR UPDATE` on `money.account_balance`.
- **Constraints** `payout_once_per_day_uk` — makes a re-run a no-op.
- **Errors** `409 below_reserve` · `409 already_paid_out` · `409 organizer_suspended`.

---

## 7.6 Ops and diagnostics

These exist because §10 requires them, and because they turn the roadmap's monitoring queries
into something you can actually watch during a load test.

### `GET /api/v1/ops/invariants`

Runs every INV-1…INV-12 detection query and returns pass/fail with the offending rows (capped).

- **Isolation** `SERIALIZABLE READ ONLY DEFERRABLE` — the checker must never abort and must
  never cause a writer to abort. It may pause briefly waiting for a safe snapshot; that is the
  intended behaviour and is documented in the response as `waitedForSnapshotMs`.
- **Response `200`** `{ ok: true, checks: [ { id: "INV-6", ok: true, ms: 41 } ] }` ·
  **`500`** with details if any check fails.

### `GET /api/v1/ops/locks`

`pg_locks` + `pg_blocking_pids()` blocking tree, and `SIReadLock` rows grouped by granularity
(so predicate-lock escalation is visible during the `SERIALIZABLE` benchmarks).

### `GET /api/v1/ops/transactions`

`pg_stat_activity`: oldest transaction age, oldest `age(backend_xmin)`, `idle in transaction`
offenders with their queries.

### `POST /api/v1/ops/workers/{worker}/run-once`

Triggers one iteration of `reaper | outbox | reconciler | payout | rollup` synchronously — makes
worker behaviour deterministic in integration tests instead of timing-dependent. Role `ops`,
disabled outside `Development`/`Test`.

### `GET /health/live`, `GET /health/ready`, `GET /metrics`

Liveness, readiness (database reachable, migrations applied), Prometheus exposition.

---

## 7.7 Endpoint → isolation summary

The table you should be able to defend line by line in a review.

| Endpoint | Level | Mechanism | Retries |
|---|---|---|---|
| `POST /venues/{id}/seats:import` | RC | savepoints, chunked | no |
| `POST /events` | RC | constraints | no |
| `PATCH /events/{id}` | RC | **optimistic version** | no |
| `POST /events/{id}/publish` | RC | `FOR UPDATE` guard row + `CHECK` | `40P01` |
| `PATCH …/payment-routes/{id}` | **SER** \| RC \| RC | SSI \| guard row \| **counter + `CHECK`** | yes \| `40P01` \| `40P01` |
| `GET …/availability` | **RR** | none | no |
| `GET /customers/{id}/statement` | **RR** / **SER RO DEFERRABLE** | none | no |
| `POST /carts` | RC | partial unique index | no |
| `POST …/holds/ga` | RC | atomic guarded `UPDATE` ×2, fixed lock order | `40P01` |
| `POST …/holds/seats` | RC | **`EXCLUDE` constraint**, sorted ids | `40P01` |
| `DELETE …/holds/{id}` | RC | data-modifying CTE | `40P01` |
| `POST …/promo` | RC | counter + `CHECK` + `UNIQUE` | `40P01` |
| `POST …/checkout` | RC | idempotency key + `FOR UPDATE` cart | `40001`, `40P01` |
| `POST /orders/{id}/pay` (wallet) | RC | `FOR UPDATE` ordered + guarded writes | `40001`, `40P01` |
| `POST /orders/{id}/pay` (card) | RC ×3 | saga states, no lock across I/O | per transaction |
| `POST /wallets/{id}/topups` | RC | journal unique key | `40P01` |
| `POST /wallets/{id}/transfers` | RC | `FOR UPDATE` **canonical order** | `40001`, `40P01` |
| `POST /orders/{id}/refunds` | RC | counter + `CHECK` | `40001`, `40P01` |
| `POST /organizers/{id}/payouts` | RC | `FOR UPDATE` balance + daily `UNIQUE` | `40P01` |
| workers: reaper / outbox / reconciler / payout | RC | **`FOR UPDATE SKIP LOCKED`** | `40P01` |
| `GET /ops/invariants` | **SER RO DEFERRABLE** | none | no |

**The distribution is the lesson.** Eighteen of twenty-two write paths are `READ COMMITTED`,
made correct by a constraint, an atomic guarded write, or an explicit lock. Exactly one path
uses `SERIALIZABLE`, and only in one of its three implementations — and you will have measured
why the other two are preferred.
