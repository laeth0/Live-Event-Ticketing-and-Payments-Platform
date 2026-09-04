# 00 · Start Here — Transactions, ACID & Isolation (PostgreSQL + Npgsql)

This is a self-paced course built from `../ACID.json` (module **L06.6 — "Transactions, ACID
and isolation"**) and `../prompt.md`. It goes well beyond the one-line notes in that file: the
goal is that you understand *why* each mechanism exists, *how it behaves internally*, and *how
to apply it in a real .NET backend*.

Target engine: **PostgreSQL 16** (behaviour notes call out where 12–16 differ).
Target client: **.NET 8 + Npgsql 8** (`NpgsqlDataSource` API).

---

## 1. Reading order

Read the files in numeric order. Each one assumes the previous ones.

| # | File | What it gives you | ACID.json items |
|---|------|-------------------|-----------------|
| 00 | `00-start-here.md` | Setup, conventions, glossary | — |
| 01 | `01-what-is-a-transaction.md` | The unit of work, BEGIN/COMMIT/ROLLBACK, WAL, transaction states | L06.6.1 |
| 02 | `02-mvcc-snapshots-and-visibility.md` | MVCC, `xmin`/`xmax`, snapshots, visibility, VACUUM horizon | foundation for 03–10 |
| 03 | `03-acid-honestly.md` | Each letter precisely, what the DB does *not* give you, false claims | L06.6.2 |
| 04 | `04-anomalies-part1-read-phenomena.md` | Dirty read, non-repeatable read, phantom read | L06.6.3, L06.6.4, L06.6.5 |
| 05 | `05-anomalies-part2-lost-update-write-skew.md` | Lost update (3 fixes), write skew, read-only anomaly | L06.6.6, L06.6.7 |
| 06 | `06-postgresql-isolation-levels.md` | READ COMMITTED, REPEATABLE READ, SERIALIZABLE + SSI, comparison tables | L06.6.8, L06.6.9, L06.6.10 |
| 07 | `07-locking-and-explicit-concurrency-control.md` | Row locks, `FOR UPDATE`/`SKIP LOCKED`, deadlocks, `pg_locks` | supports L06.6.6, L06.6.7, L06.6.11 |
| 08 | `08-serialization-failures-and-retry-patterns.md` | 40001/40P01, retry loop with jitter, idempotency | L06.6.11 |
| 09 | `09-transaction-boundaries-in-application-code.md` | Where the boundary belongs, savepoints, Npgsql pitfalls | L06.6.1 |
| 10 | `10-production-guidelines-and-decision-guide.md` | Synthesis matrices, choosing a strategy, anti-patterns, alerting | all |
| 11 | `11-capstone-exercises-and-answers.md` | Predict-then-run exercises, self-check Q&A, mini-project, ACID self-audit | all |

---

## 2. How every lesson file is structured

Each lesson (01–11) uses the same sections, so you always know where to look:

1. **Learning objectives** — what you should be able to do afterwards.
2. **Mental model** — the intuitive picture, the common misconceptions, and where the
   simplified picture diverges from what PostgreSQL actually does.
3. **Key terms** — every term defined the first time it is used.
4. **Why it exists** — the problem this mechanism solves.
5. **How it works internally** — the mechanism, tied back to MVCC, snapshots, and locks.
6. **Diagrams** — Mermaid diagrams; each is followed by a **"Reading the diagram"**
   paragraph so the picture is never left to interpretation.
7. **Hands-on lab** — reproducible `psql` experiments shown as **step-numbered two-session
   timelines** (Session A | Session B), with the expected output at each step.
8. **In application code (C# / Npgsql)** — idiomatic usage, isolation level, error handling.
9. **Practical exercises** — **Beginner** (verify understanding), **Intermediate**
   (implement / test it), **Advanced** (production-level thinking).
10. **Key takeaways** — the compressed version to review later.

Conventions:

- Code blocks are labelled `sql`, `psql`, or `csharp`.
- Wherever behaviour differs from the SQL standard, the text says
  **"SQL standard says X, PostgreSQL does Y"** explicitly.
- Identifiers are `snake_case`. Application queries always use parameters (`$1`, `$2`, …).

---

## 3. Lab setup (do this once)

### 3.1 A PostgreSQL 16 instance

```bash
docker run --name acidlab -e POSTGRES_PASSWORD=acid -e POSTGRES_DB=acidlab \
  -p 5432:5432 -d postgres:16

# a psql shell inside the container
docker exec -it acidlab psql -U postgres -d acidlab
```

Confirm the version and the default isolation level:

```sql
SELECT version();
SHOW default_transaction_isolation;   -- expect: read committed
```

### 3.2 Two side-by-side sessions

Almost every experiment needs **two** independent sessions. Open two terminals:

```bash
# Terminal A
docker exec -it acidlab psql -U postgres -d acidlab

# Terminal B
docker exec -it acidlab psql -U postgres -d acidlab
```

A helpful prompt so you can tell them apart and always see transaction state:

```psql
\set PROMPT1 '%/ %x A> '   -- run in Terminal A  (%x shows * when in a transaction)
\set PROMPT1 '%/ %x B> '   -- run in Terminal B
```

Turn on timing and expanded output when useful:

```psql
\timing on
\x auto
```

### 3.3 Shared schema for the labs

Run this once in either session:

```sql
CREATE SCHEMA IF NOT EXISTS lab;
SET search_path = lab, public;

CREATE TABLE IF NOT EXISTS account (
    id         bigint PRIMARY KEY,
    owner      text        NOT NULL,
    balance    numeric(12,2) NOT NULL CHECK (balance >= 0),
    version    integer     NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS counter (
    id    text PRIMARY KEY,
    n     bigint NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS on_call (
    engineer   text PRIMARY KEY,
    is_on_call boolean NOT NULL
);

CREATE TABLE IF NOT EXISTS room_booking (
    id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    room_id  int  NOT NULL,
    during   tstzrange NOT NULL
);

TRUNCATE account, counter, on_call, room_booking;
INSERT INTO account (id, owner, balance) VALUES
    (1, 'alice', 1000.00),
    (2, 'bob',   1000.00);
INSERT INTO counter (id, n) VALUES ('hits', 0);
INSERT INTO on_call (engineer, is_on_call) VALUES ('ada', true), ('grace', true);
```

Reset helper (re-run between experiments):

```sql
TRUNCATE room_booking;
UPDATE account SET balance = 1000.00, version = 0;
UPDATE counter SET n = 0 WHERE id = 'hits';
UPDATE on_call SET is_on_call = true;
```

### 3.4 A .NET project for the Npgsql sections

```bash
mkdir acidlab-cs && cd acidlab-cs
dotnet new console
dotnet add package Npgsql --version 8.*
```

`Program.cs` skeleton used throughout the course:

```csharp
using System.Data;
using Npgsql;

const string connString =
    "Host=localhost;Port=5432;Username=postgres;Password=acid;Database=acidlab;" +
    "Search Path=lab,public";

await using var dataSource = NpgsqlDataSource.Create(connString);

await using var conn = await dataSource.OpenConnectionAsync();
await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);

await using (var cmd = new NpgsqlCommand("SELECT balance FROM account WHERE id = $1", conn, tx))
{
    cmd.Parameters.Add(new NpgsqlParameter { Value = 1L });
    var balance = (decimal)(await cmd.ExecuteScalarAsync())!;
    Console.WriteLine($"balance = {balance}");
}

await tx.CommitAsync();
```

> **Npgsql `IsolationLevel` mapping.** `ReadCommitted` → `READ COMMITTED`,
> `RepeatableRead` → `REPEATABLE READ`, `Serializable` → `SERIALIZABLE`.
> `ReadUncommitted` is accepted but PostgreSQL runs it as `READ COMMITTED`.
> Npgsql maps `IsolationLevel.Snapshot` to `REPEATABLE READ` — prefer `RepeatableRead`
> so the intent is obvious. `IsolationLevel.Unspecified` uses the server default
> (`default_transaction_isolation`, normally `READ COMMITTED`).

---

## 4. Mini-glossary (full definitions appear in the lessons)

| Term | One-line meaning |
|------|------------------|
| **Transaction** | A group of statements that commit or roll back as one unit. |
| **MVCC** | Multi-Version Concurrency Control: readers see a snapshot of old row versions instead of being blocked by writers. |
| **Tuple** | One physical version of a row on disk. An `UPDATE` creates a new tuple. |
| **`xmin` / `xmax`** | System columns on every tuple: the transaction id that created it / the transaction id that deleted or locked it. |
| **xid** | 32-bit transaction id. |
| **Snapshot** | The set of transaction ids whose effects a query is allowed to see. |
| **Anomaly / phenomenon** | An observable behaviour that a truly serial execution could never produce. |
| **Isolation level** | The dial that trades concurrency for how many anomalies are prevented. |
| **Serialization failure** | `SQLSTATE 40001`: PostgreSQL aborted your transaction because committing it would break serializability. Retry it. |
| **SSI** | Serializable Snapshot Isolation: PostgreSQL's non-blocking implementation of `SERIALIZABLE`. |
| **Savepoint** | A named marker inside a transaction you can partially roll back to. |
| **Idempotent** | Running it again produces the same end state — safe to retry. |

---

## 5. The one-paragraph version

A transaction is atomic and durable almost for free. **Isolation is the part you have to
think about.** PostgreSQL's default, `READ COMMITTED`, gives each *statement* a fresh
snapshot and prevents dirty reads only; it still allows non-repeatable reads, phantoms,
lost updates through read-modify-write, and write skew. `REPEATABLE READ` gives the whole
*transaction* one snapshot and additionally blocks non-repeatable reads, phantoms (PostgreSQL
is stricter than the standard here), and lost updates (via a `40001` error on the second
writer). `SERIALIZABLE` adds predicate tracking (SSI) and is the only level that also
prevents write skew and read-only anomalies — at the cost that you **must** wrap the work in
a bounded, jittered retry loop and make it idempotent. Everything else in this course is
detail on those sentences.

Continue to `01-what-is-a-transaction.md`.
