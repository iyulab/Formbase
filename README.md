# Formbase

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)

**A raw-first document engine that lets you store data before you design its schema — then projects a queryable structure once you declare one.**

Formbase sits on top of [MorphDB](https://github.com/iyulab/MorphDB) (runtime-flexible relational storage) and adds the layer MorphDB deliberately leaves out: turning a stream of documents into a typed, queryable table on your terms. It is the engine realization of [Formology](https://github.com/iyulab/formology)'s three layers — humans write documents, the system derives data, and (in a later stage) intelligence grows an ontology.

> Status: **core engine, in active development (0.1.x).** The raw-first intake, hint-driven projection, and MorphDB adapter are implemented and tested. The LLM-driven ontology layer is a deliberate future stage, wired for via a port but not yet built. See [Roadmap](#roadmap).

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

Two things make this different from "define a table, then insert rows":

- **Declaration is never required to accept data.** Documents land in the raw store immediately. Structure is declared later (or, eventually, inferred), and the raw stream is the source of truth — the typed table is a rebuildable projection of it.
- **`FormType` is the unit of typing, and it stays inside Formbase.** MorphDB only ever sees a generic table; the form concept never leaks into it.

## How it works

A document's life:

1. **Intake** — `AcceptAsync(formType, body)` appends the document to the raw store and returns immediately. First-seen form types auto-register; a caller-supplied id makes re-submission idempotent. Success means the data is durable, whether or not a projection exists.
2. **Projection** — when a form type has declared field hints, `ProjectAsync(formType)` drops any existing table, recreates it from the proposed schema, streams the raw documents through deterministic value mapping (recording — never discarding — any that can't be mapped), and records the watermark it reached. Because raw is the source of truth, a schema change needs no `ALTER` diffing: the table is simply rebuilt.
3. **Reading** — there are two questions with two paths:
   - *"Show me this document"* → the raw store, always available.
   - *"Query / aggregate these records"* → the projected table. If there is no projection yet you get a distinct `NotProjectedException` (never a misleading empty result); if raw has advanced past the projection the result is flagged `Stale`; if the backing store is down you get `ProjectionUnavailableException`.

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

Six ports define the engine; everything else composes them.

| Port | Responsibility |
|------|----------------|
| `IRawStore` | Append-only source of truth. Owned by Formbase. |
| `IIntakeService` | Accept documents (raw-first, no declaration required). |
| `ISchemaProposer` | Propose a table schema for a form type — the seam where schema intelligence plugs in. |
| `IProjector` | Drop-and-rebuild the projected table from raw. |
| `IProjectionState` | Track the watermark each projection reached. |
| `IRecordQuery` | Query projected records; distinguish not-projected / stale / unavailable. |
| `IProjectionStore` | The typed-table target — the adapter seam over the backing database. |

**Projects**

- `Formbase.Core` — primitives, the six ports, the projector/intake/query services, and in-memory implementations. **Zero external package dependencies.**
- `Formbase.MorphDb` — `IProjectionStore` implemented over `MorphDB.Client`. A thin translation layer; all projection policy stays in the core.
- `Formbase.DependencyInjection` — `AddFormbaseCore` / `AddFormbaseInMemory` wiring.

**Design decisions worth knowing**

- **`ISchemaProposer` is where the ontology layer will live.** The current `HintSchemaProposer` reads declared field hints. A later LLM-based proposer infers schema from the raw documents and plugs into the same port — no core change.
- **Raw lives in Formbase, not MorphDB.** Formbase owns its source of truth, so a MorphDB outage never blocks intake or document reads, and re-projection is a full scan Formbase controls rather than something tunneled through a REST API.
- **`FormType` never reaches MorphDB.** Projected tables are generic; the form concept is a Formbase-internal string.

## Building and testing

```bash
dotnet build Formbase.slnx
dotnet test  Formbase.slnx          # default suite — no Docker required
```

Live tests stand up a real MorphDB container via Testcontainers and are excluded from the default build. Run them explicitly on a machine with Docker:

```bash
dotnet test Formbase.slnx -p:IncludeLiveTests=true
```

## Roadmap

Implemented:

- Raw-first intake, append-only raw store, idempotent re-submission
- Hint-driven projection (drop-and-rebuild), deterministic value mapping, skip recording, staleness detection
- Record query with not-projected / stale / unavailable distinction
- MorphDB projection-store adapter (API-verified against `MorphDB.Client` 0.5.0)
- DI composition and contract test suites for the store ports

Planned (later stages, each its own effort):

- **Ontology layer** — an LLM-based `ISchemaProposer` that infers structure from raw documents, plus scheduled/threshold-driven projection triggers
- **Formbase-owned Postgres raw store** — a durable append-only `IRawStore` (today's durable store is the projection target; the reference raw store is in-memory)
- Input adapters (M3L and others) that produce `FormType` + `Document`
- Richer querying (ordering, non-equality filters) and non-blocking re-projection

## License

Apache License 2.0 — see [LICENSE](./LICENSE).
