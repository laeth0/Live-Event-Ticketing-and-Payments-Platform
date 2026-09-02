# 9 · Testing Strategy

The tests are the deliverable. A concurrency fix without a test that fails without it is an
assertion, not engineering.

Five layers:

| Layer | Runs against | Speed | Purpose |
|---|---|---|---|
| **Unit** | in-memory | ms | pure business rules and mapping |
| **Integration** | real PostgreSQL (Testcontainers) | 100s of ms | that the SQL and the constraints do what you think |
| **Concurrency** | real PostgreSQL, N connections | seconds | that anomalies are prevented |
| **Invariant / property** | real PostgreSQL, randomised ops | seconds–minutes | that no interleaving breaks INV-1…INV-12 |
| **Load (k6)** | the running system | minutes | that correctness survives the §3.1 workload |

---

## 9.1 Test infrastructure

### PostgreSQL fixture

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("concourse")
        .WithCommand("-c", "log_lock_waits=on",
                     "-c", "deadlock_timeout=200ms",     // faster deadlock tests
                     "-c", "max_connections=200")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await Migrator.ApplyAsync(_pg.GetConnectionString());
        DataSource = new NpgsqlDataSourceBuilder(_pg.GetConnectionString())
            .EnableParameterLogging(false)
            .Build();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _pg.DisposeAsync();
    }
}
```

- **One container per test collection**, not per test — container startup dominates otherwise.
- **Isolation between tests** via a `TRUNCATE … RESTART IDENTITY CASCADE` + reseed helper, not
  by "roll back the test transaction". Concurrency tests need *committed* data across several
  connections, so the ambient-transaction trick is unusable here. Say so in a comment; it is a
  question every reviewer asks.
- `deadlock_timeout = 200ms` in tests only, so CH-04 runs in under a second. Note in the test
  why production stays at `1s`.

### Deterministic two-session stepping

Timeline tests must not depend on `Task.Delay`. Use advisory locks as a rendezvous:

```csharp
// A reaches its checkpoint, then waits for B to reach the same one.
await using var a = await _ds.OpenConnectionAsync();
await using var b = await _ds.OpenConnectionAsync();

var gate = new StepGate(a, b);          // wraps pg_advisory_lock(key) handshakes
await gate.A("BEGIN ISOLATION LEVEL SERIALIZABLE");
await gate.A("SELECT count(*) FROM catalog.payment_route WHERE event_id=$1 AND is_enabled", eventId);
await gate.B("BEGIN ISOLATION LEVEL SERIALIZABLE");
await gate.B("SELECT count(*) FROM catalog.payment_route WHERE event_id=$1 AND is_enabled", eventId);
await gate.A("UPDATE catalog.payment_route SET is_enabled=false WHERE id=$1", routeA);
await gate.B("UPDATE catalog.payment_route SET is_enabled=false WHERE id=$1", routeB);
await gate.A("COMMIT");
var ex = await Assert.ThrowsAsync<PostgresException>(() => gate.B("COMMIT"));
Assert.Equal(PostgresErrorCodes.SerializationFailure, ex.SqlState);
Assert.Contains("read/write dependencies", ex.MessageText);
```

### Parallel collision harness

```csharp
var stats = await ConcurrencyLab.RunParallelAsync(
    workers: 20,
    body: async (i, ct) => await _client.PostAsync($"/api/v1/carts/{carts[i]}/holds/ga",
                                                   Json(new { ticketTypeId, quantity = 1 }), ct));
// stats: Successes, BusinessRejections, ByStatus, BySqlState, RetryAttempts, P50/P95/P99, Elapsed
```

All workers start on a `Barrier` so they genuinely collide; each has its **own** connection
(sharing one `NpgsqlConnection` across tasks is undefined behaviour and would silently make the
test meaningless).

---

## 9.2 Unit tests

Fast, no database. They cover the parts that are pure decisions.

| ID | Verifies | Expected | Concept validated |
|---|---|---|---|
| `T-U1` | Money value object: no negative amounts, currency must match, integer minor units | throws on violation | domain validation, not concurrency |
| `T-U2` | Order total arithmetic: `total = subtotal − discount`, discount ≤ subtotal | correct totals | mirrors the `order_total_consistent` `CHECK` |
| `T-U3` | Full-jitter backoff: `wait ∈ [0, min(cap, base·2^n)]` for n = 0…10; distribution is not degenerate | bounds hold; 1 000 samples have variance > 0 | `L08 §4.3` |
| `T-U4` | Retry classifier: `40001`, `40P01`, `40000` retryable; `23505`, `23514`, `23P01`, `55P03`, `25P02`, domain exceptions not | exact classification | `L08 §4` — the single most dangerous thing to get wrong |
| `T-U5` | Canonical lock ordering helper returns ids ascending for both argument orders | deterministic | `L07 §5.4` |
| `T-U6` | **Architecture test**: no `BeginTransaction`/`BeginTransactionAsync` call sites outside `TransactionRetry`; no `HttpClient`/`IBus` types referenced inside retried delegates | zero violations | `L09 §5.2` enforced mechanically |
| `T-U7` | Idempotency fingerprint is stable for equivalent bodies and different for different ones | as specified | `L08` |
| `T-U8` | SQLSTATE → HTTP mapping table is total (every code in the enum has a mapping) | no gaps | §7.0 |

---

## 9.3 Integration tests (real PostgreSQL, single connection)

They prove the *schema* behaves as designed. Cheap, and they catch the "I thought that
constraint covered this" class of error.

| ID | Verifies | Expected | Concept validated |
|---|---|---|---|
| `T-I1a` | duplicate seat in a venue | `23505` | `UNIQUE` |
| `T-I1b` | overlapping seat hold, same event+seat | `23P01` | **`EXCLUDE USING gist`** |
| `T-I1c` | non-overlapping holds on the same seat (one expired) | both succeed | range semantics — expiry frees the seat without a reaper |
| `T-I1d` | `reserved + sold > allocated` | `23514` | `CHECK` tripwire |
| `T-I1e` | unbalanced journal | error at **`COMMIT`**, not at the `INSERT` | **deferred constraint** (`L03 §3`) |
| `T-I1f` | negative wallet balance | `23514` | conditional `CHECK` with the denormalised kind |
| `T-I1g` | second ticket for a live seat | `23505` from the partial unique index | INV-1 backstop |
| `T-I3` | exception mid-unit-of-work | nothing persisted | atomicity |
| `T-I4` | bulk import, 5 bad rows in 1 000 | 995 committed, 5 reported, one `COMMIT` | savepoints |
| `T-I5` | statement after an error without rollback | `25P02`; usable after `ROLLBACK` | transaction poisoning |
| `T-I6` | identity value consumed by a rolled-back insert is not reused | gap present | sequences are non-transactional |
| `T-I7` | `REPEATABLE READ` write collision | `40001`, message *"concurrent update"* | first-updater-wins |
| `T-I8` | `SERIALIZABLE` write skew | `40001`, message *"read/write dependencies"* | SSI |
| `T-I9` | `SERIALIZABLE` path + `READ COMMITTED` writer on the same rows | invariant **breaks** — asserted | `L06 §5.3`: SSI only covers participants |
| `T-I10` | `NOWAIT` on a locked row; `SKIP LOCKED` on a mixed set | `55P03`; only unlocked rows returned | `L07 §3` |
| `T-I11` | migration adding the `EXCLUDE` constraint | no `ACCESS EXCLUSIVE` held > 3 s | `CONCURRENTLY` + `NOT VALID`/`VALIDATE` |
| `T-I12` | `REPEATABLE READ` reader cannot see a committed row but still gets `23505` inserting its key | duplicate key error | "invisible ≠ absent" |

---

## 9.4 Concurrency tests

The core of the suite. Every one of them fails on a naive implementation — that is the
acceptance criterion for the test itself.

| ID | Setup | Asserts | Concept validated |
|---|---|---|---|
| `T-C1` | 20 workers reserve 1 GA ticket each; `allocated = 20` | exactly 20 succeed; `reserved = 20`; no worker sees a stale count; zero `23514` | **lost update** |
| `T-C1b` | same but `allocated = 15` | exactly 15 succeed, 5 get `sold_out`; `reserved = 15` | atomic write + rows-affected as the business signal |
| `T-C2a` | 2 workers disable 2 different routes on an `onsale` event | exactly one succeeds; `enabled_route_count ≥ 1`; INV-9 holds | **write skew** |
| `T-C2b` | 4 concurrent refunds totalling more than captured | `refunded_minor = captured_minor`; the excess refund is rejected; no `23514` | write skew on a `SUM` |
| `T-C3` | 20 requests for the same (customer, event), 2 tickets each, limit 6 | exactly 3 succeed; `held + owned = 6`; 17 get `customer_limit_exceeded` | **phantom → write skew** |
| `T-C3b` | same run repeated 50× | zero variance in the outcome | proves the fix is not luck |
| `T-C4a` | 100 transfers with random pairs, locking in argument order | `40P01` observed; recorded rate | **deadlock** |
| `T-C4b` | 200 transfers with canonical ordering | zero unhandled `40P01`; `SUM(balance)` conserved; every journal balances | deadlock avoidance |
| `T-C5a` | retried unit throwing a domain exception, and one throwing `23505` | body executed **once** each | retry classification |
| `T-C5b` | forced `40001` mid-payment body | one journal, `n` tickets, one outbox row, one debit | **idempotent retried body** |
| `T-C5c` | 32 workers, 3 backoff strategies | full jitter has the fewest total attempts and the best p99 | retry storms |
| `T-C5d` | 64 workers, one row, `SERIALIZABLE` | exhaustion observed, then zero after the `READ COMMITTED` + atomic redesign | exhaustion is a design signal |
| `T-C6a` | statement read at `READ COMMITTED` during transfers | **detects** a torn read (asserted as failing) | non-repeatable read |
| `T-C6b` | same at `REPEATABLE READ` | balance always equals the sum of its own returned entries | one snapshot |
| `T-C7a` | 50 workers hold the same 10 seats | exactly 10 succeed, 40 get `409 seat_unavailable`; zero double-holds | `EXCLUDE` under load |
| `T-C7b` | reaper with 1/2/4/8 workers, ±`SKIP LOCKED` | throughput scales only with `SKIP LOCKED`; zero double-decrements | queue contention |
| `T-C7c` | worker killed mid-slow-job | job recovered and completed exactly once | claim/reap lifecycle |
| `T-C8a` | 20 parallel `POST /pay` with one `Idempotency-Key` | one capture; 19 identical replays; one ledger journal | idempotency |
| `T-C8b` | process killed before / after the PSP call / after `COMMIT` | each state recovered by the reconciler or the publisher within budget | saga + outbox |
| `T-C9` | publisher restarted mid-batch | every event delivered ≥ once; consumer dedup ⇒ exactly one effect | transactional outbox |
| `T-C13` | 40 concurrent promo redemptions against a budget for 30 | exactly 30 succeed; `redeemed_minor ≤ budget_minor`; zero `23514` | write skew, counter fix |
| `T-C14` | 30 concurrent payouts + refunds for one organizer | payouts never exceed payable − reserve | write skew, guard-row fix |

**Assertion discipline.** Every concurrency test ends by running the INV detection queries. It
is not enough that the API returned sensible statuses; the database must be provably consistent
afterwards.

```csharp
await InvariantChecker.AssertAllAsync(_fixture.DataSource);   // last line of every T-C test
```

---

## 9.5 Invariant / property tests

Randomised operation sequences, run against the real system, checked against INV-1…INV-12.

| ID | Generator | Asserts |
|---|---|---|
| `T-Q1` | 2 000 random operations (hold / release / checkout / pay / refund / transfer / reap) across 10 customers, 3 events, 8 wallets, 12 parallel workers | all twelve invariants hold; `Σ journal_entry.amount_minor = 0`; every materialised balance equals the sum of its entries |
| `T-Q2` | same, with a random 5 % of operations aborted mid-transaction | no partial effects; invariants hold |
| `T-Q3` | same, with the reaper and outbox publisher running concurrently | invariants hold; no hold both released and converted |
| `T-Q4` | shrink on failure: when `T-Q1` fails, minimise the operation sequence and print it | reproducible minimal counterexample |

`T-Q1` is the single most valuable test in the project. It is the one that finds the bug you did
not think of, and INV-6 (materialised balance = `SUM` of entries) is the assertion that catches
almost everything.

---

## 9.6 Chaos tests

| ID | Fault injected | Expected |
|---|---|---|
| `T-X1` | API killed at a random point during the onsale load test | after restart, all invariants hold; no ticket without money; no money without a ticket |
| `T-X2` | PostgreSQL restarted mid-load | committed work survives; in-flight work is absent, never half-applied |
| `T-X3` | PSP returns timeouts for 30 s, then duplicate success callbacks | no double capture; the reconciler resolves every stranded payment |
| `T-X4` | Outbox consumer down for 60 s, then back | no event lost; each applied exactly once |
| `T-X5` | Connection pool exhausted (drop `Max Pool Size` to 5 under load) | requests fail fast with `503`, no deadlock, no corruption; recovery is automatic |
| `T-X6` | A session left `idle in transaction` for 60 s | `idle_in_transaction_session_timeout` terminates it (`25P03`); the API surfaces a clean error |

---

## 9.7 Load tests (k6)

| Scenario | Shape | Pass criteria |
|---|---|---|
| `onsale` | 1 500 rps for 30 s, 5 000 VUs, realistic browse→hold→checkout→pay funnel | §3.1 latency budgets met; zero invariant violations; `40001` ≤ 2 %; `40P01` ≤ 0.05 %; retries exhausted = 0 |
| `hot-seat` | 5 000 VUs contending for 200 seats | exactly 200 sold; the rest get clean `409`s; p99 hold ≤ 250 ms |
| `hot-wallet` | 90 % of transfers target one account | throughput ceiling recorded; deadlocks = 0; used to justify the Phase-7 redesign |
| `route-toggle` | 50 concurrent toggles, run three times (one per TX-11 strategy) | comparison table produced; invariant holds in all three |
| `limit-race` | 20 concurrent holds per customer × 200 customers | the guard-row and `SERIALIZABLE` variants both correct; abort rates recorded |
| `mixed-steady` | 30 minutes of realistic mixed traffic | no growth in `age(backend_xmin)`; rollback ratio stable; no unbounded outbox backlog |

Every k6 run polls `GET /api/v1/ops/invariants` every 5 seconds and **fails the run** on any
violation. A load test that only measures latency is measuring the wrong thing here.

---

## 9.8 Test-quality rules

1. **Every fix has a test that fails without it.** Keep the naive implementation reachable via
   configuration so the test can prove the difference, and assert the failure explicitly
   (`[Trait("Anomaly","expected-fail")]` on the naive variant).
2. **No `Thread.Sleep` for ordering.** Use the advisory-lock `StepGate` or a `Barrier`.
3. **Assert on SQLSTATE and, for `40001`, on the message text**, so a first-updater-wins
   conflict is never mistaken for an SSI cycle.
4. **Every concurrency test runs at least 20 times in CI** (`[Theory]` over a repetition
   index, or a `--repeat` flag). A race that reproduces once in ten is still a race.
5. **Every concurrency test asserts the invariants at the end**, not just the HTTP responses.
6. **Tests own their data.** Unique event/customer ids per test so parallel test execution does
   not create false contention — except where contention *is* the subject, which must be
   explicit.
7. **Record numbers, not just green.** Concurrency tests emit their `Stats` to
   `artifacts/findings/*.json`, which is what the `docs/findings/CH-xx.md` write-ups are built
   from.
