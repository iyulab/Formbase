# Changelog

## Unreleased

### Added

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
