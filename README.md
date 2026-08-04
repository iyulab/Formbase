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
2. **Projection** — when a form type has declared field hints, `ProjectAsync(formType)` drops any existing table, recreates it from the proposed schema, streams the raw documents through deterministic value mapping (recording — never discarding — any that can't be mapped, and counting per column how many documents never carried the field at all, as opposed to answering `null`), and records the watermark it reached. Because raw is the source of truth, a schema change needs no `ALTER` diffing: the table is simply rebuilt. When to re-project is a pluggable policy (`IProjectionTrigger`): the built-in trigger fires immediately on a shape change and at a configurable document-lag threshold for new data; drive `ProjectionSupervisor.RunOnceAsync` from whatever cadence your host owns.
3. **Reading** — there are two questions with two paths:
   - *"Show me this document"* → the raw store, always available.
   - *"Query / aggregate these records"* → the projected table. If there is no projection yet you get a distinct `NotProjectedException` (never a misleading empty result); if raw has advanced past the projection the result is flagged `Stale`; if the backing store is down you get `ProjectionUnavailableException`. Results carry a total order (any `QuerySpec.OrderBy` keys, then the system watermark as a tie-breaker), so `Limit`/`Offset` paging is deterministic.

## Install

Current release: **0.7.0**. Formbase projects into MorphDB over its client, so the two move
together — **`Formbase.* 0.7.0` pairs with MorphDB `0.9.x`**. Pin the MorphDB server image to
that line (`ghcr.io/iyulab/morphdb:0.9.0`); the compatible pair is stated with every release in
[CHANGELOG.md](CHANGELOG.md).

Start with the core and the DI helpers, then add only the adapters you actually run:

```bash
dotnet add package Formbase.Core                 # engine, ports, in-memory implementations
dotnet add package Formbase.DependencyInjection  # AddFormbaseCore / AddFormbaseInMemory
dotnet add package Formbase.MorphDb              # IProjectionStore over MorphDB
dotnet add package Formbase.Postgres             # durable raw store, projection state, field hints
dotnet add package Formbase.SchemaIntelligence   # optional: LLM-backed ISchemaProposer
```

`Formbase.Core` has zero external package dependencies, and the in-memory profile below needs
nothing else — the adapters are what bring in Npgsql, the MorphDB client, and
`Microsoft.Extensions.AI`.

**Or run it as a service.** `Formbase.Host` serves the engine over HTTP so a consumer does not have
to be a .NET process in the same container — see [docs/API.md](docs/API.md). The host is a packaging
of the library rather than a replacement for it: embedding the engine directly stays supported, and
the packages above are unchanged by its existence.

```bash
dotnet run --project src/Formbase.Host
```

By default it composes the in-process stores, which needs nothing else running and loses everything
on restart. A deployment selects the durable profile instead:

```bash
Formbase__Store=Durable ConnectionStrings__Formbase='Host=db;Database=formbase;Username=formbase;Password=…' Formbase__MorphDb__Url=http://morphdb:8080 Formbase__MorphDb__ProjectId=<the id POST /api/projects returned> dotnet run --project src/Formbase.Host
```

PostgreSQL then holds the raw stream, the projection state and the declarations; MorphDB holds the
projected tables. `Formbase__Schema` (default `formbase`) isolates the Postgres side, and
`FORMBASE_MORPHDB_URL` overrides the MorphDB address for a run.

**A durable profile missing any of those refuses to start.** Falling back to the in-process stores
would leave the host running, answering, and losing every document on restart — a failure the
operator would meet as missing data long after the configuration that caused it. Provisioning the
MorphDB project stays theirs: the engine never administers MorphDB.

**Schema intelligence is an extension.** Supply a model endpoint, key and name and the engine infers
structure for fields nobody declared; supply none and every other capability is unchanged — the host
starts without model credentials, always.

```bash
Formbase__Llm__Endpoint=https://api.openai.com Formbase__Llm__ApiKey=… Formbase__Llm__Model=gpt-4o-mini dotnet run --project src/Formbase.Host
```

`FORMBASE_LLM_ENDPOINT` / `_API_KEY` / `_MODEL` work too — the same names the LLM live suite reads,
so an endpoint you have already pointed that at works here unchanged. Supplying only some of the
three is refused at startup: a host with half of them looks like it has intelligence installed and
fails on the first proposal.

`GET /settings` reports what an instance was composed as — its namespace, whether it is durable, and
whether intelligence is installed. Credentials are never reported back.

A container image and a compose file are not here yet.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
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

- `Formbase.Core` — primitives, the ports above, the projector/intake/query services, and in-memory implementations. **Zero external package dependencies.**
- `Formbase.MorphDb` — `IProjectionStore` implemented over `MorphDB.Client`, plus `AddMorphDbProjectionStore`. A thin translation layer; all projection policy stays in the core.
- `Formbase.Postgres` — the durable, append-only `IRawStore` over PostgreSQL (direct Npgsql, never through MorphDB), plus the durable `IProjectionState` and `IFieldHintSource` adapters. Registration helpers: `AddPostgresRawStore`, `AddPostgresProjectionState`, `AddPostgresFieldHints`. Appends are serialized so watermark assignment order equals commit order.
- `Formbase.DependencyInjection` — `AddFormbaseCore` / `AddFormbaseInMemory` wiring. Each adapter package ships its own registration helper, so this package stays free of adapter dependencies.

**Design decisions worth knowing**

- **`ISchemaProposer` is where the ontology layer will live.** The current `HintSchemaProposer` reads declared field hints. A later proposer plugs into the same port — no core change. Its job is to **read what a form already declares**, not to invent structure from values: looking at a `product_name` column alone can never tell you whether it is a snapshot, a denormalization, or a mistake. The form can — a "filled-in" box and an "attached" box are different boxes.
- **Raw lives in Formbase, not MorphDB.** Formbase owns its source of truth, so a MorphDB outage never blocks intake or document reads, and re-projection is a full scan Formbase controls rather than something tunneled through a REST API.
- **`FormType` never reaches MorphDB.** Projected tables are generic; the form concept is a Formbase-internal string.
- **Layout is outside; structure is inside.** How a form is laid out, rendered, or printed is an adapter/UI concern and never enters the engine. Which parts of a form define an entity boundary is *derivation policy* and belongs in the core; the vocabulary that carries the distinction is declared through `FieldHint`/`RelationHint` (see below).

### Reading a declaration back

A form type's declared vocabulary — the identity/display split (`SourceKey`), the time binding
(`FieldBinding` plus its target), relations, and the declaration version — travels declaration →
proposal → projection → fingerprint. To read it back, resolve the proposer the engine itself
projects through:

```csharp
var proposer = provider.GetRequiredService<ISchemaProposer>();
var schema   = await proposer.ProposeAsync(qc);   // null when nothing is declared yet

foreach (var column in schema!.Columns)
{
    // column.Name          — the projected column
    // column.ExtractionKey — the raw key it reads from (SourceKey when it differs)
    // column.Binding       — Stored / Snapshot / Reference
    // column.BindingTarget — "table.column" for a bound field
}
// schema.Relations, schema.DeclarationVersion
```

What that call does and does not answer:

- It reports **what the current declaration proposes**, not what the projected table currently
  holds. `engine.GetProjectionStatusAsync(qc)` closes the gap: a redeclaration changes the
  fingerprint, so the status reads `Stale` even when no new document arrived.
- With `Formbase.SchemaIntelligence` registered the same call returns declaration and inference
  composed — declared axes carried through unchanged, undeclared columns answered by the model,
  the declaration winning on conflict. The read-back is the same call either way.
- A `FieldBinding.Reference` column is declared but **not yet resolved**: it projects empty, and
  each `ProjectAsync` names those columns in `ProjectionResult.UnresolvedReferences`.

Declaring is deliberately not on this port — it belongs to whichever `IFieldHintSource` you
registered (see the durable composition below).

### Durable composition

The three Postgres registrations belong together. Registering only the raw store leaves the
projection state and the field hints in process memory, so a restart forgets the projection —
a query then answers `NotProjected` even though both databases still hold the data.

Every MorphDB schema and data request is scoped to a project, so the composition also needs a
provisioned project: a one-time `POST /api/projects` (the response body carries the `id`),
which stays the consumer's responsibility — the engine never administers MorphDB. Register the
store with that id:

```csharp
services.AddFormbaseCore();
services.AddPostgresRawStore(connectionString);        // raw = source of truth
services.AddPostgresProjectionState(connectionString); // the engine's own ledger
services.AddPostgresFieldHints(connectionString);      // what a form type projects into
services.AddMorphDbProjectionStore(morphDbUrl, provisionedProjectId);
```

The bare `AddMorphDbProjectionStore(morphDbUrl)` overload leaves the client unscoped: intake
still works (raw-first), but the first projection fails with the server's `MISSING_PROJECT`.
Use it only when the client factory overload supplies the scope another way.

One operational note: the durable stores share a connection pool, so an outage that fails a
rebuild can also fail the state cleanup that follows. The caller always sees the original
rebuild failure — the cleanup's own exception rides on `Exception.Data` under
`Projector.ClearFailureDataKey`, where a host can log it.

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
- Record query with not-projected / stale / unverified / unavailable distinction, and deterministic ordering/paging
- MorphDB projection-store adapter — the projection-store contract runs end-to-end against the published MorphDB server image; the `morphdb-live` CI job repeats that run on every push, watching for client/server drift
- **HTTP surface** (`Formbase.Host`) — intake with a first-class idempotency key, raw reads, declaration reads, projection runs and state, and record queries, described by a generated OpenAPI document and held to [docs/API.md](docs/API.md) by a parity gate. Writing a declaration is not on the surface yet, and neither is a container image.
- DI composition and contract test suites for the store ports
- **Absence accounting** — a projection distinguishes a field a document never had from one explicitly written `null`: `ProjectionResult.AbsentFieldCounts` reports, per column, how many landed rows carried no such box at all (per-row distinction awaits the declaration-version work below)
- **Projection triggers** — `IProjectionTrigger` (watermark-lag policy) plus `ProjectionSupervisor`; the hosting cadence (timer, hook) stays with the host
- **LLM schema proposer** — `Formbase.SchemaIntelligence` implements `ISchemaProposer` over any `IChatClient` (provider-agnostic via Microsoft.Extensions.AI), with strict proposal parsing and a hallucination guard; graduated from its spike after live-model quality measurement (100% parse/projection survival, zero required-flag violations across a six-shape catalog). Registering it **composes** over the declared structure rather than replacing it — the declaration answers for what it states, the model for the rest, and the declaration wins on conflict, because a proposer that reads values never held the fact
- **Declaration vocabulary** — a form type's hints carry four axes beyond name/type/nullability: identity-vs-display (`SourceKey`, so a renamed field keeps its data), time binding (`FieldBinding` Stored/Snapshot/Reference with a target), relations (`RelationHint`, each form type still its own table), and a declaration version. Each survives declaration → proposal → projection → fingerprint, and is readable back through the proposer (see [Reading a declaration back](#reading-a-declaration-back)); defaults reproduce the pre-vocabulary shape
- **Tri-state projection integrity** — if a failed rebuild's state cleanup also fails, the stamp is marked unverified and a query throws `ProjectionUnverifiedException` rather than serving a half-built table as fresh

Known gaps (audited 2026-07-20 against Formology):

- **Per-row absent-vs-null** — the aggregate counts above do not yet mark *which* row predates a grown schema; the declaration version is now recorded, so the per-row distinction is the remaining step.
- **Reference resolution is declared, not executed** — a `FieldBinding.Reference` (true-now) column carries its declared meaning but the engine does not yet resolve the referenced value, so it projects **empty** and the column is named in `ProjectionResult.UnresolvedReferences`; it is never filled with the document's own copy, which would be a wrong value wearing a plausible face. Separately, the MorphDB adapter does not yet materialize declared relations as virtual FKs (its client exposes no relations API — the FK column data still projects normally).

Planned (later stages, each its own effort):

- **Ontology layer** — proposals that read structure a form already declares, growing from the `ISchemaProposer` seam
- Input adapters (M3L and others) that produce `FormType` + `Document` — the `Formbase.M3L` spike now fills the vocabulary axes it once measured as gaps; productizing it is a separate decision
- Richer querying (non-equality filters) and non-blocking re-projection

## License

Apache License 2.0 — see [LICENSE](./LICENSE).
