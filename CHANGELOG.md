# Changelog

## [Unreleased]

### Added

- `PostgresProjectionState` and `PostgresFieldHintSource` — the durable profile now survives a restart.
  Register them alongside `AddPostgresRawStore`; with either one missing, a restarted process answers
  `NotProjected` because a query resolves its table through the proposed schema.
- `AddPostgresProjectionState` and `AddPostgresFieldHints` DI extensions. The Postgres data source is
  now registered with `TryAddSingleton`, so the three stores share one connection pool.

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
