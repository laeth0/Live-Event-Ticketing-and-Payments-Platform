# 11 · Technology Constraints & Architecture

---

## 11.1 Stack

| Layer | Choice | Version | Note |
|---|---|---|---|
| Runtime | .NET | **8 (LTS)** | Matches `roadmap/00-start-here.md §3.4`. On .NET 9+ use `Guid.CreateVersion7()` for UUIDv7 keys; on 8, use a small local generator. |
| API | ASP.NET Core Minimal APIs | 8 | Endpoint-per-file; no MVC controllers needed |
| Database | PostgreSQL | **16** | `btree_gist`, `pg_stat_statements` |
| Driver | **Npgsql** | 8.x | `NpgsqlDataSource` is the app-wide factory |
| ORM | **EF Core** + `Npgsql.EntityFrameworkCore.PostgreSQL` | 8.x | **Restricted** — see §11.3 |
| Migrations | Plain SQL files + a small runner (or DbUp) | — | Not EF migrations: you need `CONCURRENTLY`, `NOT VALID`/`VALIDATE`, `EXCLUDE` and constraint triggers, and you need to read them as SQL |
| Workers | `BackgroundService` in a separate host | 8 | Deployed separately from the API |
| Metrics | OpenTelemetry .NET + Prometheus exporter | — | |
| Dashboards | Grafana + `postgres_exporter` | — | |
| Tests | **xUnit** + FluentAssertions + **Testcontainers.PostgreSql** | — | |
| Architecture tests | NetArchTest or ArchUnitNET | — | Enforces T-U6 |
| Load tests | **k6** | — | Scenarios in `loadtests/` |
| Containers | Docker Compose | — | One command to a running system |

**No message broker.** The outbox publishes to an `ILogger`-backed fake bus with a
consumer-side dedup table. Adding RabbitMQ or Kafka would add operational surface without
adding a single ACID concept — the interesting part (the outbox and idempotent consumers) is
fully exercised without it. §12.13 records the trade-off.

---

## 11.2 Solution structure

```
Concourse.sln
├── src/
│   ├── Concourse.Api/                 ASP.NET Core host, endpoints, middleware
│   │   ├── Endpoints/                 one file per resource
│   │   ├── Middleware/                ProblemDetails + SQLSTATE mapping, idempotency
│   │   └── Ops/                       /ops/locks, /ops/transactions, /ops/invariants
│   ├── Concourse.Workers/             reaper, outbox publisher, reconciler, payout, rollup
│   ├── Concourse.Domain/              entities, value objects, domain exceptions — NO I/O
│   ├── Concourse.Application/         use cases; each = one unit of work
│   │   ├── Catalog/  Sales/  Money/
│   │   └── Abstractions/              ITransactionRunner, IClock, IPaymentGateway
│   ├── Concourse.Persistence/         Npgsql data access, SQL, TransactionRetry, outbox writer
│   │   ├── Sql/                       *.sql files, one per query, embedded resources
│   │   ├── TransactionRetry.cs        ← the ONLY place a write transaction is opened
│   │   └── ReadModels/                EF Core DbContext (read-only, see §11.3)
│   └── Concourse.Infrastructure/      fake PSP, fake bus, metrics, clock
├── db/
│   ├── migrations/                    001_schemas.sql … NNN_*.sql (forward-only)
│   ├── invariants/                    INV-01.sql … INV-12.sql (detection queries)
│   └── seed/                          idempotent demo data
├── tests/
│   ├── Concourse.UnitTests/
│   ├── Concourse.IntegrationTests/    Testcontainers, one collection fixture
│   ├── Concourse.ConcurrencyTests/    T-C*, T-Q*, T-X*
│   └── Concourse.ArchitectureTests/
├── tools/
│   ├── ConcurrencyLab/                RunParallel, StepGate, Stats
│   └── AnomalyLab/                    a CLI that replays every psql timeline from §6
├── loadtests/                         k6 scenarios + thresholds
├── docs/
│   ├── decisions/                     ADR-001 … (one per §12 decision)
│   └── findings/                      CH-01.md … CH-07.md (your measurements)
├── docker-compose.yml
└── ops/                               prometheus.yml, grafana dashboards, alert rules
```

### Dependency direction

`Api`/`Workers` → `Application` → `Domain`. `Persistence` and `Infrastructure` implement
`Application.Abstractions` and are referenced only by the composition root. `Domain` references
nothing.

**The rule that matters most here:** a use case in `Application` is *one unit of work*. It
receives a connection and transaction, does read → decide → write, and returns. It never opens
a transaction itself, never calls `HttpClient`, never publishes. That shape is what makes it
safe to run inside a retry loop (`L08 §4.4`), and it is enforced by `T-U6`.

---

## 11.3 The Npgsql / EF Core split

This is a deliberate architectural decision, not a compromise.

| Use raw **Npgsql** for | Use **EF Core** for |
|---|---|
| Every write path | Catalog read models and list/detail endpoints |
| Anything with `FOR UPDATE`, `SKIP LOCKED`, `NOWAIT` | Simple projections with LINQ |
| Anything with `ON CONFLICT`, data-modifying CTEs, `RETURNING` | The organizer dashboard's queries |
| Anything whose isolation level or lock behaviour is part of the contract | |
| The retry helper and all transaction control | |

**Why.** The whole point of this project is that the SQL *is* the design. Rows-affected as a
business signal, guard predicates in the `WHERE`, `SKIP LOCKED`, deferred constraints — these
are either awkward or invisible through an ORM, and hiding them defeats the exercise. EF Core
earns its place on read paths where LINQ genuinely is faster to write and nothing subtle
depends on the generated SQL.

**Where EF Core is used, these rules apply:**

```csharp
// read models are never tracked and never write
options.UseNpgsql(cs, o => o.EnableRetryOnFailure(
        maxRetryCount: 6,
        maxRetryDelay: TimeSpan.FromSeconds(2),
        errorCodesToAdd: new[] { "40001", "40P01" }))     // not retried by default — add them
       .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
```

- The built-in `NpgsqlRetryingExecutionStrategy` retries **connection-level transient** errors;
  serialization and deadlock codes must be added explicitly.
- With an execution strategy, any explicit `BeginTransaction` + multiple `SaveChanges` block
  **must** be wrapped in `context.Database.CreateExecutionStrategy().ExecuteAsync(...)` so the
  whole block re-runs — otherwise only one `SaveChanges` retries and the unit of work is torn.
- If a future write path does use EF Core, its concurrency token is `IsRowVersion()` on the
  `version` column (Shape B from `L05 §7.1`), and `DbUpdateConcurrencyException` is handled by
  reload-and-reapply, **not** by the `40001` retry loop — they are different failures.

---

## 11.4 Data access essentials

### One data source, registered once

```csharp
var builder = new NpgsqlDataSourceBuilder(connectionString);
builder.ConnectionStringBuilder.MaxPoolSize   = 40;    // §3.1 connection budget
builder.ConnectionStringBuilder.MinPoolSize   = 5;
builder.ConnectionStringBuilder.Timeout       = 5;     // connect timeout, seconds
builder.ConnectionStringBuilder.CommandTimeout = 5;    // matches statement_timeout
builder.ConnectionStringBuilder.ApplicationName = "concourse-api";  // shows in pg_stat_activity
builder.UseLoggerFactory(loggerFactory);
services.AddSingleton(builder.Build());
```

`ApplicationName` is not cosmetic: it is how you tell API traffic from worker traffic in
`pg_stat_activity` during a load test. Workers set `concourse-worker-<kind>`.

### The one place a write transaction is opened

```csharp
public static async Task<T> ExecuteAsync<T>(
    NpgsqlDataSource dataSource,
    IsolationLevel level,
    string unitOfWork,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> body,
    int maxAttempts = 6,
    TimeSpan? baseDelay = null,
    TimeSpan? capDelay = null,
    CancellationToken ct = default)
{
    var @base = baseDelay ?? TimeSpan.FromMilliseconds(20);
    var cap   = capDelay  ?? TimeSpan.FromSeconds(2);

    for (var attempt = 0; ; attempt++)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(level, ct);
        try
        {
            var result = await body(conn, tx, ct);
            await tx.CommitAsync(ct);
            Metrics.RecordSuccess(unitOfWork, level, attempt);
            return result;
        }
        catch (PostgresException ex) when (IsRetryable(ex.SqlState))
        {
            Metrics.RecordConflict(unitOfWork, ex.SqlState, ClassifyConflict(ex.MessageText));
            await SafeRollbackAsync(tx);
            if (attempt + 1 >= maxAttempts)
            {
                Metrics.RecordExhausted(unitOfWork);
                throw new TransactionRetriesExhaustedException(unitOfWork, maxAttempts, ex);
            }
            var ceiling = Math.Min(cap.TotalMilliseconds, @base.TotalMilliseconds * Math.Pow(2, attempt));
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling), ct);
        }
        catch
        {
            await SafeRollbackAsync(tx);
            throw;                      // business or programming error — never retried
        }
    }
}

private static bool IsRetryable(string? sqlState) =>
    sqlState is PostgresErrorCodes.SerializationFailure     // "40001"
             or PostgresErrorCodes.DeadlockDetected         // "40P01"
             or PostgresErrorCodes.TransactionRollback;     // "40000"

// distinguishes first-updater-wins from an SSI cycle — different fixes, so different metrics
private static string ClassifyConflict(string message) =>
    message.Contains("read/write dependencies", StringComparison.Ordinal)
        ? "rw_dependency" : "concurrent_update";
```

### Non-negotiable Npgsql hygiene

- `await using` on **both** the connection and the transaction, always.
- The `CancellationToken` is passed to every `ExecuteAsync`, `ExecuteScalarAsync`,
  `ExecuteNonQueryAsync` and `CommitAsync`.
- **One connection per unit of work.** Never run two commands concurrently on one
  `NpgsqlConnection`.
- Positional parameters (`$1`, `$2`) — never interpolation.
- Savepoints via `tx.SaveAsync(name)` / `tx.RollbackAsync(name)` / `tx.ReleaseAsync(name)`.
  `Save` deliberately does not round-trip; the `SAVEPOINT` statement rides along with the next
  command.
- `SET LOCAL` (never bare `SET`) for per-transaction settings such as `lock_timeout`.
- A failed `CommitAsync` means **unknown**, not "rolled back" — resolve by natural-key lookup
  (`L03 §8.2`).

### Connection-pooler note

The project does not require PgBouncer, but §12.14 documents what would break in
**transaction-pooling** mode — session-level advisory locks, bare `SET`, `LISTEN`/`NOTIFY`,
`WITH HOLD` cursors, auto-prepared statements — and why using `pg_advisory_xact_lock` (not
`pg_advisory_lock`) and `SET LOCAL` throughout means the answer is "nothing".

---

## 11.5 Docker Compose

```yaml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_PASSWORD: concourse
      POSTGRES_DB: concourse
    command:
      - "postgres"
      - "-c"
      - "shared_preload_libraries=pg_stat_statements"
      - "-c"
      - "log_lock_waits=on"
      - "-c"
      - "log_min_duration_statement=200ms"
      - "-c"
      - "track_io_timing=on"
      - "-c"
      - "max_connections=200"
    ports: ["5432:5432"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d concourse"]
      interval: 2s
      retries: 30

  migrate:
    build: { context: ., dockerfile: docker/migrate.Dockerfile }
    depends_on: { postgres: { condition: service_healthy } }

  api:
    build: { context: ., dockerfile: docker/api.Dockerfile }
    depends_on: { migrate: { condition: service_completed_successfully } }
    environment:
      ConnectionStrings__Concourse: "Host=postgres;Database=concourse;Username=concourse_app;Password=…;Maximum Pool Size=40;Application Name=concourse-api"
      Concurrency__RouteToggleStrategy: "Counter"
      Concurrency__CustomerLimitStrategy: "GuardRow"
    ports: ["8080:8080"]

  workers:
    build: { context: ., dockerfile: docker/workers.Dockerfile }
    depends_on: { migrate: { condition: service_completed_successfully } }
    environment:
      ConnectionStrings__Concourse: "…;Maximum Pool Size=20;Application Name=concourse-worker"
      Workers__Reaper__Concurrency: "3"
      Workers__Outbox__Concurrency: "4"

  prometheus: { image: prom/prometheus, volumes: ["./ops/prometheus.yml:/etc/prometheus/prometheus.yml"], ports: ["9090:9090"] }
  grafana:    { image: grafana/grafana,  volumes: ["./ops/grafana:/etc/grafana/provisioning"], ports: ["3000:3000"] }
  pgexporter: { image: quay.io/prometheuscommunity/postgres-exporter, environment: { DATA_SOURCE_NAME: "…" } }

  k6:
    image: grafana/k6
    profiles: ["load"]
    volumes: ["./loadtests:/scripts"]
    command: ["run", "/scripts/onsale.js"]
```

`docker compose up` must reach a healthy API with migrations applied in under 60 seconds.
`docker compose --profile load run k6` runs a load scenario against it.

---

## 11.6 Configuration

Strategy switches exist so the same binary can run the naive, the locked and the serializable
implementations — which is what makes the Phase-7 comparisons a configuration change rather
than a branch.

```jsonc
{
  "Concurrency": {
    "RouteToggleStrategy":   "Counter",     // Naive | Serializable | GuardRow | Counter
    "CustomerLimitStrategy": "GuardRow",    // Naive | Serializable | GuardRow
    "InventoryStrategy":     "AtomicWrite", // Naive | AtomicWrite | ForUpdate | OptimisticVersion
    "Retry": { "MaxAttempts": 6, "BaseDelayMs": 20, "CapDelayMs": 2000 },
    "LockTimeout": "3s"
  },
  "Workers": {
    "Reaper":     { "Concurrency": 3, "BatchSize": 100, "IntervalMs": 1000, "UseSkipLocked": true },
    "Outbox":     { "Concurrency": 4, "BatchSize": 200, "IntervalMs": 250 },
    "Reconciler": { "StrandedAfter": "60s" },
    "Payout":     { "Cron": "0 2 * * *", "MinPayoutMinor": 1000 }
  },
  "Psp": { "LatencyMs": 300, "FailureRate": 0.02, "TimeoutRate": 0.01, "DuplicateCallbackRate": 0.01 }
}
```

`Naive` strategies are **blocked in `Production`** by a startup guard that throws — they exist
for the anomaly lab and the tests, and nothing else.

---

## 11.7 What is deliberately excluded

| Excluded | Why |
|---|---|
| Read replicas as a correctness mechanism | SSI does not work on standbys — a replica gives snapshot isolation regardless of the level you request. Discussed in §12.15, not implemented, so nobody accidentally learns the wrong lesson. |
| Distributed transactions / 2PC | The outbox and the saga exist precisely to avoid them. `PREPARE TRANSACTION` also pins the VACUUM horizon indefinitely if a coordinator dies. |
| A real message broker | Adds ops surface, no new ACID concept. |
| Sharding, partitioning | One meaningful exception: partitioning `journal_entry` by month is offered as an optional Phase-7 stretch, because it interacts with constraints and index maintenance. |
| Caching layer | It would mask the contention this project exists to study. |
| Real auth | Stubbed. The role check is real; the token validation is not. |
