# 1 · Project Overview

---

## 1.1 Identity

| Field | Value |
|---|---|
| **Project name** | **Concourse** — Live Event Ticketing & Payments Platform |
| **Business domain** | Ticketing marketplace with an embedded wallet and double-entry ledger |
| **Actors** | Customer, Organizer, Support agent, Platform operator, background Workers, simulated Payment Service Provider (PSP) |
| **Primary datastore** | PostgreSQL 16 (single primary; no distributed transactions) |
| **Engineering theme** | *Correct under concurrency, provably, with tests that fail on the naive implementation* |

The name is literal: a concourse is the place where a crowd converges on a small number of
gates. That is exactly the shape of the workload — tens of thousands of requests converging on
a few hundred rows in a few seconds.

---

## 1.2 Why this domain is the right one for learning ACID

A good ACID-teaching domain has to satisfy five properties. Ticketing satisfies all five at
once, which is why it is used here rather than banking alone or booking alone.

| Property needed | How ticketing supplies it |
|---|---|
| **Contention is inherent, not artificial** | An onsale is a designed thundering herd. 50 000 people hit `POST /carts/{id}/holds` for the same 2 000 seats in the same 30 seconds. You do not have to invent load; the domain generates it. |
| **The unit of contention is a single row** | One seat = one row. Two people wanting seat `A-12` is a physical row conflict — the cleanest possible demonstration of row locks, `FOR UPDATE`, and lost updates. |
| **Some invariants are single-row, some span rows** | "This seat is sold once" is a unique index (rung 1). "This customer holds at most 6 tickets for this event" spans rows that *do not exist yet* — the textbook phantom/write-skew case that a unique index cannot express. Having both in one system is what makes the constraint-vs-isolation ladder teachable. |
| **Money is involved, so correctness is not negotiable** | A double-entry ledger gives you a machine-checkable global invariant (`SUM(entries) = 0` per journal, and materialised balance = `SUM` of its entries). Any concurrency bug shows up as *money created or destroyed*, which a test can assert with zero ambiguity. |
| **External systems force boundary discipline** | Card authorization goes to a PSP that is not in your transaction. You cannot hold `BEGIN…COMMIT` across it. This forces the reserve → authorize → confirm/compensate design and the outbox, which is lesson 09 made mandatory rather than theoretical. |

Two more properties make it good *portfolio* material specifically:

- **The failure modes are famous.** "The site oversold the show" and "I was charged twice" are
  incidents everybody understands. When you can demonstrate, with a reproducible test, that
  your implementation cannot do either, that is a legible engineering claim.
- **The naive implementation is genuinely tempting.** `SELECT count(*)` then `INSERT` is what
  most people write. The project is structured so you write the wrong version first, watch a
  test catch it, and then fix it — which is how the *why* actually sticks.

### What was rejected and why

| Candidate domain | Why not chosen |
|---|---|
| Bank transfer only | Excellent for lost update and deadlock, but has almost no natural phantom/write-skew surface, and no queue/reaper work. Too narrow. |
| Generic e-commerce | Stock decrements are a good atomic-counter lesson, but carts/checkout in most e-commerce designs are eventually consistent by business choice, which weakens the "must be correct now" pressure. |
| Hospital appointments | Great write-skew story (on-call staffing), but the money layer and the throughput profile are missing. Its best idea — "at least one X must remain" — is *imported* into Concourse as the payment-route invariant. |
| Blog / todo / admin CRUD | No contention. Explicitly excluded by the brief. |

---

## 1.3 Real-world scenario (the story the system has to survive)

> **20:00:00.000** — Organizer *Northgate Live* puts *Aurora Fields — Night 2* on sale.
> Capacity 4 000: 2 500 general admission across three price tiers and 1 500 reserved seats.
> A promo code `EARLY25` gives €5 off, capped at a €2 500 total discount budget and one
> redemption per customer.
>
> **20:00:00.100** — 61 000 requests arrive in the first two seconds. Roughly 9 000 of them are
> `POST /carts/{cartId}/holds/seats` for seats in the same two front sections.
>
> **20:00:01** — A customer's browser times out and their client retries `POST /orders/{id}/pay`
> three times with the same `Idempotency-Key`. Meanwhile a mobile app retries a top-up.
>
> **20:00:05** — Two ops engineers, reacting to a PSP incident, each disable a different payment
> route for the event from two different dashboard tabs. Each one checks "is another route
> still enabled?" first. Both see *yes*. Both commit.
>
> **20:03:00** — Holds placed at 20:00 begin expiring. Three reaper workers wake up and race to
> reclaim the same expired holds while customers are still checking out against them.
>
> **20:07:00** — A group organiser transfers €400 from their wallet to a friend's while the
> friend simultaneously transfers €150 back. Both handlers lock two wallet rows.
>
> **21:30:00** — Support issues three partial refunds for one order, concurrently, from three
> agents who each checked "have we refunded more than we captured?" first.
>
> **02:00:00** — The nightly payout job pays out settled revenue to 400 organizers while
> refunds for tonight's show are still landing.
>
> **02:15:00** — Finance runs a consistency report across five tables and needs it to reflect
> exactly one instant, not a smear across ten seconds of writes.

Each of those paragraphs is a concrete anomaly in this document:

| Story beat | Anomaly | Where it is specified |
|---|---|---|
| 9 000 requests for the same seats | Lost update / double-sell | `CH-01`, `TX-01` |
| Client retries the payment three times | Duplicate processing | `TX-06`, `F-11` |
| Two engineers disable two routes | **Write skew** | `CH-02`, `TX-11` |
| Reapers race customers over expiring holds | Phantom + queue contention | `CH-03`, `TX-07` |
| Two mutual wallet transfers | **Deadlock (40P01)** | `CH-04`, `TX-08` |
| Three concurrent partial refunds | Write skew on a `SUM` predicate | `CH-02b`, `TX-09` |
| Nightly payout vs. incoming refunds | Write skew + `SKIP LOCKED` batch | `TX-10` |
| Finance report across five tables | Non-repeatable read / torn read | `TX-12` |

---

## 1.4 The business problems that require transaction correctness

Stated as **invariants**. These are the system's contract. Every one of them has (a) an
enforcement mechanism, (b) a test that violates it on a naive implementation, and (c) a runtime
check in `GET /ops/invariants`.

| ID | Invariant | Enforcement rung (see §12.1) | Breaks if… |
|---|---|---|---|
| **INV-1** | A reserved seat is sold at most once per event. | 1 — partial unique index on `ticket(event_id, seat_id) WHERE status <> 'void'` | you check availability with a `SELECT` and then `INSERT` |
| **INV-2** | Two active holds cannot overlap in time on the same event seat. | 1 — `EXCLUDE USING gist (event_id =, seat_id =, during &&)` | you use `WHERE expires_at > now()` as your only guard |
| **INV-3** | `reserved + sold ≤ allocated` for every GA ticket type. | 1 + 2 — `CHECK` on the inventory row + atomic `UPDATE … WHERE` | you read the counter into the app and write back a literal |
| **INV-4** | A customer never holds or owns more than `max_tickets_per_customer` for one event. | 3 — materialised guard row locked `FOR UPDATE`, or 4 — `SERIALIZABLE` | you `SELECT count(*)` then `INSERT` (phantom / write skew) |
| **INV-5** | Every journal balances: `SUM(journal_entry.amount_minor) = 0` per journal, and no customer wallet balance is ever negative. | 1 — `DEFERRABLE INITIALLY DEFERRED` constraint trigger + `CHECK (balance_minor >= 0)` | you write one side of the entry and fail before the other |
| **INV-6** | The materialised `account_balance.balance_minor` always equals `SUM` of that account's journal entries. | 2 — both written in the same transaction, balance updated atomically | you update the balance outside the transaction that writes entries |
| **INV-7** | Total refunded for a payment never exceeds the amount captured. | 1 + 2 — `refunded_minor` counter with `CHECK (refunded_minor <= captured_minor)` | you `SUM(refunds)` then `INSERT` a refund |
| **INV-8** | A promo code never grants more than its budget, nor more than one redemption per customer. | 1 + 2 — counter + `CHECK`, plus `UNIQUE (promo_code_id, customer_id)` | you `SUM(redemptions)` then `INSERT` |
| **INV-9** | An event that is `onsale` always has at least one enabled payment route. | 4 — `SERIALIZABLE` + retry, or 3 — guard-row lock | two admins disable two different routes concurrently |
| **INV-10** | A payment `Idempotency-Key` is applied at most once, no matter how many times it is submitted or internally retried. | 1 — `UNIQUE (idempotency_key)` + stored response | you rely on "we checked, it wasn't there" |
| **INV-11** | An organizer's payouts never exceed settled revenue minus the rolling reserve. | 3 — `FOR UPDATE` on the payable account row | you compute availability from a `SUM` and insert a payout row |
| **INV-12** | Every committed order that requires an event emits it exactly once downstream. | outbox + consumer dedup (at-least-once + idempotent consumer) | you publish before `COMMIT`, or after it without a durable record |

**The point of the invariant table.** Nine of the twelve are enforced by something *cheaper
than an isolation level* — a constraint, a `CHECK`ed counter, or a row lock. Only INV-4, INV-9
and INV-11 genuinely need `SERIALIZABLE` or a guard-row lock, and even for those the project
requires you to implement **two** solutions and compare their abort rates under load. That
distribution is the single most important lesson in the whole project, and it is why the
domain was chosen with both kinds of invariant in it.

---

## 1.5 Scope boundaries

**In scope**

- Catalog (organizers, venues, seats, events, ticket types, payment routes)
- Sales (carts, holds, promos, checkout, orders, ticket issuance)
- Money (wallets, double-entry ledger, card payments via a fake PSP, refunds, payouts)
- Background workers (hold reaper, outbox publisher, PSP reconciler, payout batch, rollups)
- Ops surface (invariant checker, lock/transaction introspection, metrics)

**Explicitly out of scope** (they add work without adding a single new ACID concept)

- Authentication/authorization beyond a stub `X-Actor-Id` header and a role check
- Real payment provider integration, PCI concerns, 3-D Secure
- Front-end, email/SMS delivery, PDF ticket rendering, seat-map rendering
- Multi-region, sharding, read replicas as a correctness mechanism (they appear only as a
  *discussion* in §12 because SSI does not work on standbys)
- Distributed transactions / 2PC across services (the outbox exists precisely to avoid them)

**Deliberately simplified**

- The PSP is an in-process fake with configurable latency, failure and duplicate-callback
  behaviour, so you can test the saga's failure windows deterministically.
- Notifications are `ILogger` calls made by the outbox publisher.
- One currency per event; no FX. Cross-currency ledgers are a different course.
