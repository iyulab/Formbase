# Formbase

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

**A raw-first document engine that lets you store data before you design its schema — then projects a queryable structure once you declare one.**

Formbase sits on top of [MorphDB](https://github.com/iyulab/MorphDB) (runtime-flexible relational storage) and adds the layer MorphDB deliberately leaves out: turning a stream of documents into a typed, queryable table on your terms. It is the engine realization of [Formology](https://github.com/iyulab/formology)'s three layers — humans write documents, the system derives data, and (in a later stage) intelligence grows an ontology.

> Status: **core engine, in active development (0.x).** The raw-first intake, hint-driven projection, and MorphDB adapter are implemented and tested. The LLM-driven ontology layer is a deliberate future stage, wired for via a port but not yet built. See [Roadmap](#roadmap).

## The idea

```
── Human layer: documents ───────────────────────────────
  Input adapters (M3L, a form UI, an external system)
        │  each produces { FormType, Document }
        ▼
── System layer: data ───────────────────────────────────
  [Intake]        accept documents — no declaration required
        ▼
  [Raw store]     append-only source of truth (formbase-owned)
        ▼  (once field hints are declared)
  [Projection]    drop-and-rebuild a typed table in MorphDB
        ▼
  [Record query]  query / aggregate the projected records

── Intelligence layer: ontology (future) ────────────────
  An LLM-based schema proposer plugs into the same port and
  infers structure from the raw documents themselves.
```

Three things make this different from "define a table, then insert rows":

- **Declaration is never required to accept data.** Documents land in the raw store immediately. Structure is declared later, and the raw stream is the source of truth — the typed table is a rebuildable projection of it.
- **`FormType` is the unit of typing, and it stays inside Formbase.** MorphDB only ever sees a generic table; the form concept never leaks into it.
- **Less-derived is a state, not a failure.** The engine never demands that input be complete. No hints means no projection — but intake still succeeds and raw reads always work. An unmappable document becomes a recorded `ProjectionSkip`, not an exception. What the input has not decided stays empty rather than being filled with a plausible default, because **a wrong value is never discovered while an empty one can still be filled in.**

## How it works

A document's life:

1. **Intake** — `AcceptAsync(formType, body)` appends the document to the raw store and returns immediately. First-seen form types auto-register; a caller-supplied id makes re-submission idempotent. Success means the data is durable, whether or not a projection exists.
2. **Projection** — when a form type has declared field hints, `ProjectAsync(formType)` drops any existing table, recreates it from the proposed schema, streams the raw documents through deterministic value mapping (recording — never discarding — any that can't be mapped), and records the watermark it reached. Because raw is the source of truth, a schema change needs no `ALTER` diffing: the table is simply rebuilt.
3. **Reading** — there are two questions with two paths:
   - *"Show me this document"* → the raw store, always available.
   - *"Query / aggregate these records"* → the projected table. If there is no projection yet you get a distinct `NotProjectedException` (never a misleading empty result); if raw has advanced past the projection the result is flagged `Stale`; if the backing store is down you get `ProjectionUnavailableException`. Results carry a total order (any `QuerySpec.OrderBy` keys, then the system watermark as a tie-breaker), so `Limit`/`Offset` paging is deterministic.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;

var services = new ServiceCollection();
services.AddFormbaseInMemory();          // self-contained, no external dependencies
await using var provider = services.BuildServiceProvider();

var engine = provider.GetRequiredService<FormbaseEngine>();
var hints  = provider.GetRequiredService<InMemoryFieldHintSource>();

var qc = FormTypeRef.Create("quality-check");

// 1) Accept documents with no schema declared.
await engine.AcceptAsync(qc, DocumentBody.Parse("""{"lot":"L-1","qty":10}"""));
await engine.AcceptAsync(qc, DocumentBody.Parse("""{"lot":"L-2","qty":20}"""));

// 2) Declare structure after the fact, then project.
hints.Declare(new FormTypeHints(qc, "quality_checks",
[
    new FieldHint("lot", ColumnType.Text, Nullable: false),
    new FieldHint("qty", ColumnType.Integer),
]));
await engine.ProjectAsync(qc);

// 3) Now the records are queryable.
var result = await engine.QueryAsync(qc, new QuerySpec(
    Filters: new Dictionary<string, object?> { ["qty"] = 20 }));
// result.Rows -> the L-2 record
```

## Architecture

Eight ports define the engine; everything else composes them.

| Port | Responsibility |
|------|----------------|
| `IRawStore` | Append-only source of truth. Owned by Formbase. |
| `IIntakeService` | Accept documents (raw-first, no declaration required). |
| `IFieldHintSource` | Supply the declared structure for a form type — the input to schema proposal. |
| `ISchemaProposer` | Propose a table schema for a form type — the seam where schema intelligence plugs in. |
| `IProjector` | Drop-and-rebuild the projected table from raw. |
| `IProjectionState` | Track the watermark each projection reached. |
| `IRecordQuery` | Query projected records; distinguish not-projected / stale / unavailable. |
| `IProjectionStore` | The typed-table target — the adapter seam over the backing database. |

**Projects**

- `Formbase.Core` — primitives, the six ports, the projector/intake/query services, and in-memory implementations. **Zero external package dependencies.**
- `Formbase.MorphDb` — `IProjectionStore` implemented over `MorphDB.Client`, plus `AddMorphDbProjectionStore`. A thin translation layer; all projection policy stays in the core.
- `Formbase.Postgres` — the durable, append-only `IRawStore` over PostgreSQL (direct Npgsql, never through MorphDB), plus the durable `IProjectionState` and `IFieldHintSource` adapters. Registration helpers: `AddPostgresRawStore`, `AddPostgresProjectionState`, `AddPostgresFieldHints`. Appends are serialized so watermark assignment order equals commit order.
- `Formbase.DependencyInjection` — `AddFormbaseCore` / `AddFormbaseInMemory` wiring. Each adapter package ships its own registration helper, so this package stays free of adapter dependencies.

**Design decisions worth knowing**

- **`ISchemaProposer` is where the ontology layer will live.** The current `HintSchemaProposer` reads declared field hints. A later proposer plugs into the same port — no core change. Its job is to **read what a form already declares**, not to invent structure from values: looking at a `product_name` column alone can never tell you whether it is a snapshot, a denormalization, or a mistake. The form can — a "filled-in" box and an "attached" box are different boxes.
- **Raw lives in Formbase, not MorphDB.** Formbase owns its source of truth, so a MorphDB outage never blocks intake or document reads, and re-projection is a full scan Formbase controls rather than something tunneled through a REST API.
- **`FormType` never reaches MorphDB.** Projected tables are generic; the form concept is a Formbase-internal string.
- **Layout is outside; structure is inside.** How a form is laid out, rendered, or printed is an adapter/UI concern and never enters the engine. Which parts of a form define an entity boundary is *derivation policy* and belongs in the core. The declaration vocabulary that carries that distinction is still open — see Roadmap.

### Durable composition

The three Postgres registrations belong together. Registering only the raw store leaves the
projection state and the field hints in process memory, so a restart forgets the projection —
a query then answers `NotProjected` even though both databases still hold the data.

```csharp
services.AddFormbaseCore();
services.AddPostgresRawStore(connectionString);        // raw = source of truth
services.AddPostgresProjectionState(connectionString); // the engine's own ledger
services.AddPostgresFieldHints(connectionString);      // what a form type projects into
services.AddMorphDbProjectionStore(morphDbUrl);
```

Declare hints through the concrete source, since declaring is not on the port:

```csharp
var hints = provider.GetRequiredService<PostgresFieldHintSource>();
await hints.DeclareAsync(new FormTypeHints(type, "qc_table",
    [new FieldHint("serial", ColumnType.Text, Nullable: false)]));
```

## Building and testing

```bash
dotnet build Formbase.slnx
dotnet test  Formbase.slnx          # default suite — no Docker required
```

Live tests stand up real backing services via Testcontainers and are excluded from the default build. The two suites need different things, so each has its own switch:

```bash
dotnet test Formbase.slnx -p:IncludePostgresLiveTests=true   # Docker only — self-contained
dotnet test Formbase.slnx -p:IncludeMorphDbLiveTests=true    # Docker only — the fixture seeds its own project
dotnet test Formbase.slnx -p:IncludeLiveTests=true           # umbrella: both
```

Both suites are self-contained: each fixture starts what it needs and, for MorphDB, provisions the project its requests are scoped to. Set `FORMBASE_MORPHDB_URL` to run the MorphDB suite against an already-running service instead of starting one. Readiness waits are bounded at two minutes, so an unreachable service fails the run rather than stalling it.

## Roadmap

Implemented:

- Raw-first intake, append-only raw store, idempotent re-submission
- **Durable Postgres raw store** — Formbase-owned source of truth over Npgsql, contract-verified against a real PostgreSQL (including concurrent appends); the in-memory raw store remains the reference implementation
- **Durable Postgres projection state and field hints** — `PostgresProjectionState` and `PostgresFieldHintSource` survive a restart alongside the raw store, closing the gap where a restarted process forgot a projection that both databases still held
- Hint-driven projection (drop-and-rebuild), deterministic value mapping, skip recording, staleness detection
- **Shape-aware staleness** — the projection state records a `ProjectionStamp` (watermark + table name + schema fingerprint of what was materialized). Redeclaring hints without re-projecting reads `Stale` even though no document arrived; a declaration that moved to a new table name reads `NotProjected` instead of masquerading as a transient backend outage
- Record query with not-projected / stale / unavailable distinction, and deterministic ordering/paging
- MorphDB projection-store adapter — type- and API-verified; the projection-store contract has been run end-to-end against a real MorphDB service and passes with the client release that carries the batch and value-mapping fixes (see below)
- DI composition and contract test suites for the store ports

Known gaps (audited 2026-07-20 against Formology):

- **Absent and null are indistinguishable in a projection.** `DocumentMapper` treats a field that was never in the document and a field explicitly written as `null` the same way — both become `null` in a nullable column. After a schema grows, re-projection therefore cannot tell *"left blank"* from *"that box did not exist yet."* Raw keeps the truth, but a query over the projection quietly conflates the two. Fix planned; raw is unaffected.
- **The declaration vocabulary is flat.** A form type maps to one flat list of columns, so a child section (1:N), a reference to another entity (FK), and the time-binding of a value (is it true *now*, or was it true *then*?) have nowhere to be expressed. This is a known deferral — the richer format is the open question in the core design — and it is the single largest gap between this engine and the methodology it implements.

Planned (later stages, each its own effort):

- **Richer declaration vocabulary** — resolve the deferred hint format so section structure and time-binding survive into the projection. Architectural; needs a decision, not just an implementation
- **Ontology layer** — an `ISchemaProposer` that reads structure a form already declares, plus scheduled/threshold-driven projection triggers
- **MorphDB live verification as a CI gate** — the `morphdb-live` job runs the live contract suite against the published server image on every push, watching for client/server drift
- Input adapters (M3L and others) that produce `FormType` + `Document`
- Richer querying (non-equality filters) and non-blocking re-projection

## License

Apache License 2.0 — see [LICENSE](./LICENSE).
