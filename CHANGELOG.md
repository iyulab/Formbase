# Changelog

## 0.9.0

Pairs with MorphDB `0.11.x`, up from `0.9.x` — the `MorphDB.Client` dependency had already moved
to `0.11.0` (a formbase-only dependency round) while the documented pair, the two live-suite
fixtures' default pins (`0.9.0` and `0.10.0`, disagreeing with each other), and the Docker
bundle's pin (`0.10.0`) each stayed at a different, older line. Verified against a live
`morphdb:0.11.0` container before changing the recommendation — the full MorphDb live contract
suite (31 tests, both fixtures) passes against it. No `Formbase.*` behavior changes; this release
exists to bring the stated pair, the tested pair, and the installed dependency back into
agreement.

### Fixed

- **The advertised MorphDB pair had drifted from what the tree actually depends on and tests
  against.** Four places named four different versions (README/CHANGELOG `0.9.x`, one live test
  fixture `0.9.0`, another `0.10.0`, `docker-compose.yml` `0.10.0`) while `Directory.Packages.
  props` already declared `MorphDB.Client 0.11.0`. All four now agree on `0.11.x`/`0.11.0`, and
  the second live test fixture's server image is overridable via `FORMBASE_MORPHDB_IMAGE` (it
  previously ignored that variable, unlike the first), so the scheduled drift watch now covers
  both.

## 0.8.0

### Added

- **A declaration can be removed, and the caller can tell "removed" from "there was nothing".**
  `InMemoryFieldHintSource.Remove` and `PostgresFieldHintSource.DeleteAsync` answer whether a
  declaration was there rather than swallowing the fact. Removing a declaration is usually paired
  with dropping what it built, and those are different situations.
- **`InvalidQueryException`** — the engine's refusal when a query names a column the declaration
  does not have. Pure addition; nothing previously threw it.
- **Projection status reports the last completed run's skip count, not only the count at the moment
  a run finished.** The response already named which documents a run skipped and how many while the
  caller who triggered it was still looking; reading status afterward — "did the last run lose
  anything" — had no way to tell zero skips from a run that silently dropped most of what it saw.
  The count is host-local: a restart or a different instance answers `null` rather than a stale
  number, and re-running the projection is what refreshes it.

### Changed

- **A record query that names an undeclared column is refused rather than answered with an empty
  page.** This applies to a filter and to an ordering key alike. Dropping a filter widens the
  result and dropping an ordering key leaves rows in an order nobody asked for, and both answered
  `200` — so a mistyped column name arrived looking like an answer to the caller's question when
  it was an answer to a different one.
  **A value that does not fit the column it names still answers no rows**: that column exists and
  the comparison is meaningful, so it is an ordinary empty result rather than a malformed request.
  The projection's bookkeeping columns are not declared columns and are refused with the rest;
  rows have never carried them.

### Fixed

- **A schema proposer's own failure answered the framework's generic 500 instead of a documented
  problem type.** Every other engine refusal already mapped to a stable `/problems/...` type; a
  malformed model proposal and an unreachable model endpoint were indistinguishable from the host
  itself being broken. They now answer `400 /problems/schema-proposal-invalid` and
  `503 /problems/schema-proposer-unavailable` respectively, documented in the error table and
  cross-referenced from the endpoint that can raise them.

### Added — the instance, which ships as an image rather than a package

The host is not a packable project, so none of this reaches NuGet. This release is the first to
publish it as a container image, and the image is how it becomes something to run.

- **An HTTP surface for intake and raw reads**, for putting a declaration in force, for reading
  back the declaration a form type currently has, for removing one along with the projection it
  built, and for running a projection and asking what state it is in. Projected records are read
  back with the request saying which data it is addressed to.
- **A caller's mistake answers 4xx on every route**, not only on the routes that happened to carry
  a guard. This now includes a value the surface cannot bind at all — a failure that happens before
  any endpoint runs, and so was answered by the framework rather than from the documented table. It
  answers `/problems/invalid-request` with the status the failure carries, so the instruction to
  branch on `type` keeps working where the caller most needs it.
- **The error surface no longer depends on the name the instance was started under.** Whether a
  binding failure reached the instance's own handler was a framework default that differs by
  environment, which made the documented table hold while developing and not in the configuration
  the image ships with. It now holds in both.
- **The instance composes durable stores when the deployment asks for them**, and says what it is:
  a bare instance reports that it is not durable rather than implying otherwise.
- **Schema intelligence installs like an extension** rather than being wired in.
- **A deployable shape** — a container image and a bundle that stands the instance up next to the
  stores it needs.

## 0.7.0

### Documentation

- **The compatibility pair line is held to the released version.** The version is stated twice in
  the Install section — as the current release and again inside the pair — and only the first was
  gated, so the second could go stale on its own and send a reader pinning a version the two lines
  disagree about.
- **The README's quickstart now carries the `using` its own sample needs.** `ISchemaProposer` lives
  in `Formbase.Core.Ports`, which the import list omitted, so anyone copying the "reading a
  declaration back" sample got `CS0246` on their first build. The samples are now compiled and run
  as tests — including the results they claim, such as the filtered query returning the second
  document and the proposer answering `null` before anything is declared — so a renamed method or a
  changed signature breaks the suite rather than a reader's first five minutes.

### Fixed

- **Turning on LLM schema intelligence no longer discards the declaration.** `AddLlmSchemaProposer`
  replaced whichever `ISchemaProposer` was registered before it, so the hint-reading proposer
  registered by `AddFormbaseCore` was gone and with it every declared axis — source keys, time
  bindings and their targets, relations, the table name and the declaration version. A proposal
  carries no record of what it did not carry, so nothing said so. The registration now composes
  over the earlier proposer instead of replacing it.

- **A column declared `FieldBinding.Reference` no longer projects the document's own copy of the
  value.** A reference reads true *now* — the target's current value — which this stage does not
  evaluate yet. The projector was falling back to whatever the document itself carried, which is
  fixed-then data: declaring the same target as `Snapshot` and as `Reference` produced identical
  columns, and when the target later changed, the reference column silently kept the old value with
  nothing marking it stale (no skip, `Projected`, not `Stale`). The column is now left empty
  instead. An empty box can still be filled; a wrong value is never found.

### Added

- `DeclaredFirstSchemaProposer` — composes two proposers under one rule: the declaration is carried
  through unchanged and inference answers only for what was never declared. It knows nothing about
  LLMs; `AddLlmSchemaProposer` is one arrangement of it. A declared column claims both the raw key
  it reads and the name it lands under, so a field already declared under another name is not
  proposed a second time, and declared columns keep their position because column order is part of
  the schema fingerprint. On conflict the declaration wins — not as a preference between
  implementations, but because a proposer that observes documents never held the fact.
- `ProjectionResult.UnresolvedReferences` — the declared columns whose reference binding the engine
  did not resolve, in declared order. Emptying a box without saying so is the same silence in a
  quieter form, so the projection result names them. `ProjectionResult.Completed` gains the
  corresponding parameter (breaking only for direct factory callers).

### Changed

- An unresolved reference column is created **nullable** even when declared `Nullable: false`.
  The engine leaves the column empty, so carrying the declared NOT NULL through to the table would
  have the store reject every row the engine itself emptied. The declaration is unchanged — only
  the physical column this stage can honor.
- Unresolved reference columns no longer appear in `ProjectionResult.AbsentFieldCounts`. That count
  records "the document never carried this field", which was never the fact in question here: the
  box was not the document's to fill.

> **Upgrade note.** If you declared a reference binding and relied on the projected column holding
> the document's copy, declare it as `Snapshot` instead — that is what the data actually was.
>
> This reaches generated hints too: the M3L adapter (spike, not packaged) maps a *soft* binding to
> `Reference` and a *hard* one to `Snapshot`, so columns from soft-bound fields are now empty as
> well. That is the intended reading of a soft binding — it names the target's current value, which
> this stage does not evaluate — but it is a visible change for anything projecting adapter output.
>
> If you use `AddLlmSchemaProposer` *and* declare field hints for the same form type, the proposed
> schema changes shape: it is now the declared one, extended with whatever the model found that the
> declaration did not mention. That is a new fingerprint, so the next projection is a rebuild. To
> keep the model answering for everything, register `LlmSchemaProposer` as the `ISchemaProposer`
> yourself instead of calling the extension.

### Documentation

- The README now has an **Install** section — the five published packages, the current version, and
  the MorphDB line they pair with. That last fact had to be reconstructed from this changelog by a
  consumer once; it is now stated where it is looked for, and three tests hold the README to the
  version, the package set, and the server image the live suite runs against.
- **Reading a declaration back** is documented rather than left to be discovered: resolve
  `ISchemaProposer` and the declared axes come back, with or without schema intelligence
  registered. The section also states what the call does not answer — it reports what the
  declaration proposes, not what the projected table currently holds.

### Internal

- The release workflow creates the GitHub release itself instead of relying on someone doing it by
  hand — 0.6.0 shipped without one and nothing said so for ten days — and refuses to publish a
  version this changelog does not describe.

### Dependencies

- `M3L.Native` 0.6.1, `Microsoft.Extensions.AI.*` 10.8.3, and the test packages
  (`Microsoft.NET.Test.Sdk` 18.8.1, `Testcontainers` 4.13.0, `AwesomeAssertions` 9.5.0). Patch and
  minor only; no vulnerability advisories were open against the previous set.

## 0.6.0

Pairs with MorphDB `0.9.x`, unchanged from 0.5.0 — this is a formbase-only minor, so a `0.9.x`
server and `MorphDB.Client 0.9.0` stay as they are and only the `Formbase.*` packages move.

`Formbase.SchemaIntelligence` is published for the first time in this release, graduating from
spike to package after a quality measurement against a live model.

### Added

- **The declaration vocabulary carries four axes**, each surviving declaration → proposal →
  projection → fingerprint. `FieldHint` gains `SourceKey` (the raw extraction key, split from the
  projected name, so renaming a field carries its data through reprojection), `Binding`
  (`Stored`/`Snapshot`/`Reference`) and `Target`; `FormTypeHints` gains `Relations` and
  `DeclarationVersion`. Every default reproduces the previous shape exactly, so a declaration that
  uses none of them projects and fingerprints as before. Stage-1 semantics are preserve,
  fingerprint and deliver — `Reference` resolution is not executed.
- `Formbase.SchemaIntelligence` as a published package: `LlmSchemaProposer`, an `ISchemaProposer`
  over `Microsoft.Extensions.AI`'s `IChatClient`, with strict parsing and a hallucination guard.

### Changed

- **Breaking — record queries can now throw `ProjectionUnverifiedException`.** When a failed
  rebuild could not even be cleaned up, the projection is marked unverified and refused rather than
  served as if fresh. Catching the base `FormbaseException` already covers it; code that catches
  concrete types needs this one added. Reprojection is the answer, and the trigger performs it.
- **Breaking — `ProjectionState` gains `Unverified`.** An exhaustive switch over
  `GetProjectionStatusAsync().State` needs the new case. `QueryResult` exposes only `Stale`, so
  most query consumers are unaffected.
- **Breaking — Postgres `projection_state` gains a `verified` column.** Added by automatic
  migration (`ADD COLUMN IF NOT EXISTS … DEFAULT true`); existing rows read verified, which is the
  honest default for a completed projection. No consumer action.

### Fixed

- The projection trigger now rebuilds an unverified projection. It compared watermarks only, so a
  projection marked unverified stayed unverified while queries kept being refused — the automatic
  recovery the tri-state depends on never fired.

## 0.5.0

Pairs with MorphDB `0.9.x`: this release requires `MorphDB.Client 0.9.0`, so it is a minor for the
same reason 0.3.0 and 0.4.0 were — anyone who pinned the client directly sees a conflict, and a
`0.9.0` server answers differently (strict request envelopes, no `project_id` on any surface,
`VALIDATION_FAILED` retired, the CHECK grammar narrowed to the enforced set). The pairing is
`Formbase.* 0.4.0` with MorphDB `0.8.x`, and `Formbase.* 0.5.0` with MorphDB `0.9.x`.

### Added

- `IProjectionTrigger` port + `WatermarkLagTrigger` + `ProjectionSupervisor` — projection
  automation as a policy seam. The trigger decides whether a projection is due: a shape change
  (redeclared fingerprint, moved table) fires immediately, pure data lag waits for a configurable
  threshold (default 1), and nothing fires when no schema is proposable or a declared type has no
  documents. The supervisor composes decision with action (`RunOnceAsync`) — the loop cadence
  (timer, queue, after-intake hook) stays with the host. `AddFormbaseCore` registers both; override
  the trigger registration to change policy. Decisions carry the `ProjectionStatus` they were
  derived from, so a holding decision is observably policy, not ignorance.

- `Formbase.SchemaIntelligence` (spike, not yet packaged) — `LlmSchemaProposer`, an LLM-backed
  `ISchemaProposer` over `Microsoft.Extensions.AI`'s provider-agnostic `IChatClient`. Samples up to
  20 raw documents and asks the model for a standard JSON Schema (draft 2020-12) proposal. The model
  only decides the shape: the table name derives from the form type, a proposed property that appears
  in no sampled document is rejected (`SchemaProposalFormatException`), and a malformed proposal
  throws instead of being repaired. Passes the same `ISchemaProposer` contract suite as
  `HintSchemaProposer` — swapping schema intelligence touches nothing in the core.

- `ProjectionResult.AbsentFieldCounts` — per declared column, how many projected rows came from
  documents that did not carry the field at all. An explicit `null` is an answer and is not counted;
  a field the document never had is a different fact. The projected NULL still conflates both in the
  table (row-level distinction is a form-versioning concern, deliberately out of scope here) — the
  counts make the conflation visible per projection instead of silent. `ProjectionResult.Completed`
  gains the corresponding parameter (breaking only for direct factory callers).

### Changed

- A required-field skip now names which fact failed: `absent from the document` vs `is null`
  (previously both read `is missing`).

## 0.4.0

Pairs with MorphDB `0.8.x`: this release requires `MorphDB.Client 0.8.0`, so it is a minor for the
same reason 0.2.0 and 0.3.0 were — anyone who pinned the client directly sees a conflict, and a
`0.7.x` server refuses nothing but a `0.8.0` server answers differently (fail-loud unknown fields
and operators, no authentication surface). The pairing is `Formbase.* 0.3.0` with MorphDB `0.7.x`,
and `Formbase.* 0.4.0` with MorphDB `0.8.x`.

### Added

- `PostgresProjectionState` and `PostgresFieldHintSource` — the durable profile now survives a restart.
  Register them alongside `AddPostgresRawStore`; with either one missing, a restarted process answers
  `NotProjected` because a query resolves its table through the proposed schema.
- `AddPostgresProjectionState` and `AddPostgresFieldHints` DI extensions. The Postgres data source is
  now registered with `TryAddSingleton`, so the three stores share one connection pool.
- `ProjectionStamp` — what a completed projection materialized: watermark, table name, and the schema
  fingerprint (`TableSchema.Fingerprint()`). Recorded by the projector, compared by
  `ProjectionStatus.Evaluate`.

### Changed

- **Requires `MorphDB.Client 0.8.0`** (was `0.7.1`). The client's credential options
  (`ApiKey`/`JwtToken`) are gone with MorphDB's authentication sunset; Formbase never used them.
- **Query rows carry exactly the declared fields.** `RecordQuery` shapes every row to the proposed
  schema before returning it: `fb_doc_id`/`fb_watermark` bookkeeping and backend system columns
  (MorphDB's `_id`, `project_id`, `_created_at`, `_updated_at`, `_version`) no longer leak into
  query results. A declared column the physical table lacks (a drifted, stale shape) reads `null`,
  so the key set is unconditional. Consumers that read the leaked internals must stop — those
  values were never part of the row contract.
- **Staleness is now shape-aware** (fixes the "redeclaring hints leaves the projection silently
  stale" gap). Redeclaring hints without re-projecting reads `Stale` even though no document
  arrived; a declaration that moved to a new table name (or was removed) reads `NotProjected`, and a
  record query answers it with `NotProjectedException` instead of `ProjectionUnavailableException` —
  a projection gap is no longer misdiagnosed as a backend outage.
- **Breaking — `IProjectionState`** stores a `ProjectionStamp` instead of a bare watermark:
  `GetProjectedWatermarkAsync` → `GetAsync`, `SetProjectedAsync(type, watermark)` →
  `SetProjectedAsync(type, stamp)`. Custom implementations must persist all three fields.
- **Breaking — `ProjectionStatus.Evaluate(stamp, rawHead, currentSchema)`** takes the stamp and the
  currently proposed schema (was `(projectedWatermark, rawHead)`).
- **Breaking — `FormbaseEngine`** takes an `ISchemaProposer` (status derivation needs the current
  declaration). DI consumers are unaffected; direct constructor calls gain one argument.
- **Breaking — durable state schema**: `formbase.projection_state` gains `table_name` and
  `schema_fingerprint` columns. The table ships unreleased (same cycle as `PostgresProjectionState`
  itself), so no migration is provided; drop the table if you ran a pre-release build.

## 0.3.0

Follows MorphDB 0.7.0, which renamed the concept it had been calling a tenant.

**If you use `Formbase.MorphDb`, this is not optional.** MorphDB 0.7.0 accepts `X-Project-Id` and no
longer accepts `X-Tenant-Id`, with no transition period. `Formbase.MorphDb 0.2.0` sends the old header
through `MorphDB.Client 0.6.0`, so it cannot talk to a 0.7.0 server. Either upgrade both or pin your
MorphDB image to `0.6.0` — the pairing is `Formbase.* 0.2.0` with MorphDB `0.6.x`, and
`Formbase.* 0.3.0` with MorphDB `0.7.x`.

This is a minor rather than a patch for the same reason 0.2.0 was: it requires `MorphDB.Client 0.7.0`,
so anyone who pinned `0.6.0` directly sees a conflict. It is not a drop-in replacement.

### Changed

- **Requires `MorphDB.Client 0.7.0`** (was `0.6.0`).

### Unchanged

- **The engine's own surface.** Formbase never had a tenant or project concept — the constitution names
  authentication, authorization and tenant boundaries as non-goals, and the core holds none of them.
  The upstream rename reached exactly one place here, the live test fixture, which is the adapter
  boundary doing its job. No port, no public type, and no behaviour changed.

## 0.2.0

Required `MorphDB.Client 0.6.0`, which carried the batch-endpoint and value-mapping fixes. Not a
drop-in replacement over 0.1.0 for consumers who pinned the client directly.

## 0.1.0

First published release: raw-first intake, append-only raw store, hint-driven projection, record query
with not-projected / stale / unavailable distinction, the MorphDB projection-store adapter, and the
durable Postgres raw store.
