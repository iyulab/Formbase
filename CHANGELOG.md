# Changelog

## 0.17.2

A patch. Pairs with MorphDB `0.14.x`, unchanged from 0.17.1. No contract change.

### Fixed

- **`Formbase.Core` runs under Native AOT and trimming.** The package declares `IsAotCompatible`, and a
  bound field's stored entity reference (`EntityRef`) reads and writes its target form type through that
  type's own converter rather than back through the serializer, which needed reflection metadata a
  Native AOT host does not have. Before, a Native AOT publish of anything referencing `Formbase.Core`
  reported trim and AOT warnings from it. Stored shapes are unchanged.

### Changed

- `Formbase.M3L` builds on `M3L.Native` 0.19.0 (was 0.17.0).

## 0.17.1

A patch. Pairs with MorphDB `0.14.x`, unchanged from 0.17.0. No contract change.

### Fixed

- **Packages carry the license text.** Every package now ships `LICENSE` at its root beside the
  `Apache-2.0` license expression, so redistributing a package carries the license copy the license
  requires and tooling that collects third-party notices finds the text.
- **`WatermarkLagTrigger` counts a form type's own documents.** A lag threshold above one compared the
  watermark gap, but watermarks are shared by every form type, so other types' documents brought a
  projection due early. It now counts this type's documents after the projected watermark, reading no
  more than the threshold. The default threshold of one is unaffected.

## 0.17.0

Pairs with MorphDB `0.14.x`. A projection whose declaration has not changed reads only the documents
appended since its last run, with the result a rebuild would have, and a bound field names its value
column and, optionally, which record its value belongs to. Breaking for code that constructs or reads
`EntityRef`, for HTTP clients that send or read a bound field's `target`, and for implementers of
`IProjectionStore` and `IProjectionState`, which gain a member each.

### Changed

- **A bound field's target names its value column as `ValueField`, not `KeyField`.** The old name read
  as a lookup key, and declarations were written that way — `KeyField` was always the column the value
  comes from. `EntityRef(entity, valueField, lookupKey, viaField)`; on the wire, `target.valueField`
  replaces `target.keyField`. Declarations already stored by an earlier release read back unchanged.
  `RelationHint.KeyField` (a relation's link field) is unaffected.

### Added

- **A bound field can say which record its value belongs to.** `EntityRef.LookupKey` (the field on the
  target that identifies a record) and `EntityRef.ViaField` (the field of this declaration carrying that
  key) — optional, and only as a pair. On the wire, `target.lookupKey` and `target.viaField`.
- **A target naming a field that does not exist is refused.** Every declaration writer (in-memory,
  SQLite, PostgreSQL) refuses a `ViaField` that is not a field of the same declaration, and a
  `ValueField` or `LookupKey` that is not a field of the target form type when that form type is
  declared (`DeclaredTargets`); the host answers `400` `/problems/invalid-declaration`. An undeclared
  target is accepted unchecked.
- **A projection brings its table forward instead of rebuilding it when only documents were appended.**
  When the declaration, table and recorded state are unchanged since the last projection, a run reads
  only the documents appended since its watermark: rows of records they correct or retire are removed,
  their own rows inserted, and the recorded skips updated in place. The table and `GET
  …/projection/skips` are what a rebuild would have produced; the cost of a run follows what was
  appended, not the size of the raw stream. A first run, a changed declaration or table, a table left
  in doubt by a failed run, and the first run after upgrading still rebuild. `ProjectionResult.Mode`
  (and `mode` on `POST …/projection`: `rebuild` or `incremental`) says which happened; on an
  incremental run the run's counts and skip lists cover the documents it read.
- `IProjectionStore.ReplaceRowsAsync` (remove rows by record key or document, then insert — idempotent)
  and `IProjectionState.ApplyProjectedDeltaAsync` (compare-and-set on the recorded watermark, withdraw
  skips by record or document, append new ones). `ProjectionSkip.Key` and `ProjectionFieldSkip.Key`
  name the record a skipped document stood for; `ProjectionStamp.SkipsKeyed` says whether they do.
  SQLite and PostgreSQL state stores add the columns on first use.

### Fixed

- Two projections of one form type started at once in one process no longer race: they run one after
  the other. A run that finds the recorded projection moved under it marks it unverified, so the next
  run rebuilds rather than serving a table both runs wrote to.

## 0.16.0

Pairs with MorphDB `0.14.x`. An optional field whose value cannot be converted no longer drops its
document, and a timestamp without an offset reads the same on every host. Breaking for code that
implements `IProjectionState`.

### Changed

- **An optional field whose value cannot be converted empties that field instead of dropping the
  document.** A nullable column holding a value its type cannot take (`"next week"` in a `Timestamp`, an
  array in a `Text`) used to skip the whole document, so one bad value hid every other value it carried
  from every query. The row now lands with that column empty, and the emptied field is recorded (see
  Added). A required column that fails the same way still skips the document, as documented: the row
  cannot stand without it. Re-projecting an existing store can therefore insert rows that were skipped
  before.

### Added

- **Emptied fields are recorded like skipped documents.** `ProjectionFieldSkip` (document, field,
  reason) for each optional field a run emptied: in `ProjectionResult.SkippedFields`, recorded with the
  run by `IProjectionState` (`GetFieldSkipsAsync`, `FormbaseEngine.GetProjectionFieldSkipsAsync`) so it
  survives a restart, and replaced by the next run. They are counted neither in `Skipped` (the row
  landed) nor in `AbsentFieldCounts` (the document did carry a value). Host: `skippedFields` on the
  projection run response, `skippedFields` and `fieldCount` on `GET /formtypes/{type}/projection/skips`,
  and `lastRun.skippedFieldCount` on the projection status.
- **Breaking for `IProjectionState` implementers**: `SetProjectedAsync` takes the field skips next to
  the skips, and `GetFieldSkipsAsync` is new. The SQLite and PostgreSQL adapters keep them in a table of
  their own (`fb_projection_field_skips` / `projection_field_skips`), created on first use in an
  existing store.

### Fixed

- **A timestamp written without an offset is read as UTC, not in the host's zone.** A date alone
  (`"2026-01-15"`) or a date and time with no zone projected to a different instant on every machine
  that ran the projection — a date alone became the previous day east of Greenwich. Such a value is now
  that moment in UTC, so a date alone is its UTC midnight and reads back as the date written. Query
  filter values follow the same rule, so a filter written like the documents matches them. A value with
  an offset keeps it, as before.

## 0.15.0

Pairs with MorphDB `0.14.x`. A batch of documents is accepted in one durable write. Breaking for code
that implements `IRawStore` or `IIntakeService`.

### Added

- **Accepting a batch in one durable write.** `FormbaseEngine.AcceptManyAsync(type, documents)` takes
  documents and retirements of one form type (`IntakeDocument.Accept`, `IntakeDocument.Retire`) and stores
  them as one unit: when it returns all of them are durable, when it throws none is. Each accept on its own
  waits for its own commit — on SQLite, a sync to disk per document — so filling a store one document at a
  time is bound by that wait; a batch pays it once (10,000 documents into a SQLite file: about 13 times
  faster). On PostgreSQL a batch's inserts also travel together, a thousand to a round trip, so a database
  across a network is not waited on once per document. The batch's documents take consecutive watermarks in the order given. Idempotency keys work per
  document, so a batch cut off and sent again stores nothing twice; a key already holding another request
  refuses the whole batch with `IdempotencyKeyReusedException`, before anything is written.

### Changed

- **Breaking:** `IRawStore.AppendManyAsync(type, appends)` is new — a raw-store adapter implements it,
  and must store the batch atomically, which is why there is no default implementation that appends one
  at a time. `RawAppend.Distinct` and `RawAppend.EnsureRepeats` carry the idempotency rule an adapter
  checks before writing. `IIntakeService.AcceptManyAsync` is new as well.

## 0.14.1

Pairs with MorphDB `0.14.x`. Fixes for durable declarations: a declaration can no longer destroy the raw
documents of a single SQLite file, and the durable stores keep what a declaration says.

### Added

- `DeclaredTableName` — the rules every declaration writer applies before storing (`IsReserved`, `Same`,
  `EnsureDeclarable`, `EnsureUnclaimed`), and `TableNameInUseException`, raised when a declaration names a
  table another form type already projects into. Over HTTP it answers `409` `/problems/table-name-in-use`.

### Fixed

- A declaration can no longer name a table in the engine's `fb_` namespace. On a single SQLite file, which
  keeps the raw documents, projection state and declarations beside the projected tables, a form type
  declared into `fb_raw_documents` replaced the raw table on its first projection — every document of
  every form type in the file was lost — and `fb_field_hints` or `fb_projection_state` broke the file the
  same way. `InMemoryFieldHintSource.Declare`, `SqliteFieldHintSource.DeclareAsync` and
  `PostgresFieldHintSource.DeclareAsync` now throw `ArgumentException` for a blank or `fb_` table name, the
  host answers `400` `/problems/invalid-declaration`, and the SQLite projection store refuses to build or
  drop an `fb_` table, so a declaration stored before this check still cannot reach one.
- Two form types can no longer be declared into one table. Each rebuilt the table from its own documents,
  so a query of either read whichever projected last. The three declaration writers refuse the second
  (names compared ignoring case) with `TableNameInUseException`, atomically with the write; redeclaring a
  form type into its own table is unaffected. Declarations already stored that share a table keep
  projecting as before, but neither can be redeclared into that table until the other moves.

- The durable declaration stores (`SqliteFieldHintSource`, `PostgresFieldHintSource`) kept only a
  declaration's table and fields: every declaration read back as version 1 with no relations. On the
  durable host the second replacement of a form type was therefore refused as a version conflict, and
  declared relations were gone after a restart — so a `child` relation declared through that host never
  reached MorphDB as a relation. Both stores now keep `DeclarationVersion` and
  `Relations`; a table written by an earlier version is upgraded in place on first use, and the
  declarations in it read back as version 1 with no relations — what they were stored as.
  Versions and relations declared before this release were never stored, so an upgraded store reads
  those declarations as version 1 with no relations until they are declared again (on the host, send
  `expectedDeclarationVersion: 1` for that one replacement).
- A form type stored as JSON — the target of a bound field inside a durable declaration — read back as
  the empty default, because `FormTypeRef` could be written but not read. On the SQLite and PostgreSQL
  stores a form type with a bound field (`Snapshot` or `Reference` with a `Target`) therefore could not be
  projected at all: resolving the target's table failed with "Value must be set" — and on the durable
  host, declaring one answered `500`. It now reads back as
  the form type it was, including from declarations already on disk, so those form types project
  without being declared again.

### Changed

- `FormTypeRef` serializes as its identifier string (`"work-order"`) rather than as an object
  (`{"Value":"work-order"}`); both forms are read.
- A document skipped because an array arrived for a Text column is told both ways out: declare the column
  as Jsonb to keep the array as one value, or, when its items are rows of their own (a repeated section),
  append each item as a document of its own form type — projection does not split an array into rows.
  The skip used to name Jsonb only, which for a repeated section keeps every row in one column.

## 0.14.0

Pairs with MorphDB `0.14.x`. A correction is a new append — and now it can say which record it corrects. Breaking for code that
implements `IRawStore` or reads `StoredDocument.Body`, and for callers that passed a cancellation token
positionally to `AcceptAsync`/`AppendAsync`.

### Added

- **Record identity.** A document may name the record it belongs to with a `RecordKey` (an opaque
  string, compared exactly, scoped to the form type): `FormbaseEngine.AcceptAsync(type, body,
  recordKey: key)`, over HTTP `POST /formtypes/{type}/documents?recordKey=…`. The projection shows each
  record once — the document with the latest watermark — so a corrected record is one row and a count
  counts it once. Documents without a key are records of their own, exactly as before.
- **Retiring a record.** `FormbaseEngine.RetireAsync(type, key)`, over HTTP
  `DELETE /formtypes/{type}/records?recordKey=…`, appends a retirement: a document with no body that
  takes the record out of the projection while its earlier documents stay in the raw stream. Appending
  under the key again brings the record back. Idempotency keys work for retirements as for intake.
- Record keys are compared exactly and are not Unicode-normalized: the same text in two normalization
  forms (a file name from one system in NFC, from another in NFD) names two records. Normalize keys that
  come from file names or user input before sending them.
- `RecordFold.Latest` — the fold the projector applies (latest per key, retired keys dropped), public so
  any reader of the raw stream can get records rather than appends.
- Raw reads carry `recordKey` and `retired` (`StoredDocument.Key`, `StoredDocument.IsRetirement`). A
  retirement reads back with a null body; read `retired` rather than testing the body, since a document
  whose content is the JSON value `null` has a null body too.
- Projected tables gain the `fb_record_key` bookkeeping column. Record reads still return the declared
  fields only.

### Changed

- A durable host refuses to start — exit code `78`, naming the setting — when MorphDB answers that the
  configured `Formbase:MorphDb:ProjectId` does not exist. It used to report ready and fail its first
  projection with an unhandled error. A MorphDB that cannot be reached at startup does not stop the
  start, as before.
- **Breaking:** `IRawStore.AppendAsync(type, id, body, key = null, cancellationToken)` takes the record key
  before the cancellation token, and `IRawStore.RetireAsync` is new — a raw-store adapter implements both.
  `IIntakeService.AcceptAsync` likewise takes `recordKey` before the token, and `IIntakeService.RetireAsync`
  is new. A call that passed the token positionally names it (`cancellationToken: ct`).
- **Breaking:** `StoredDocument.Body` is nullable — null for a retirement. `StoredDocumentResponse.Body`
  is nullable over HTTP for the same reason, next to the new `recordKey` and `retired`.
- An idempotency key reused under another record key, or for a retirement where it named a document (or
  the reverse), is refused like a key reused with a different body.
- Schema intelligence skips retirements when sampling a form type's documents.
- **SQLite and PostgreSQL files and schemas from 0.13.x are upgraded in place** on first use: the raw
  table gains its record-key column (and, on SQLite, a retirement column), and every document it holds
  reads back as a record of its own. On PostgreSQL the check runs before any change, so an up-to-date
  schema takes no table lock at startup. An existing projected table gains `fb_record_key` the next time
  it is rebuilt; until then nothing in it is keyed, because nothing appended before the upgrade was.
  **The upgrade is one-way.** Once a record has been retired, 0.13.x can no longer read the form type's
  raw stream (it answers `500`), and a projection it runs leaves the table empty. Back up the database
  before upgrading if you may need to go back.
- A bound field's `target.keyField` (`EntityRef.KeyField`) is documented for what it is: the field on
  the target form type whose value the bound field carries, not a key for finding the target record.
  The API reference had shown it one way and nothing said which; nothing about its behaviour changes.

### Dependencies

- `Microsoft.Extensions.AI.Abstractions` and `Microsoft.Extensions.AI.OpenAI` 10.10.1 (was 10.10.0), a patch.
- `Formbase.MorphDb` depends on `MorphDB.Client` 0.14.0 (was 0.13.1), and the live tests and the compose file
  run the `ghcr.io/iyulab/morphdb:0.14.0` image.

## 0.13.0

Pairs with MorphDB `0.13.x`, `0.13.1` or later. Record queries grow from equality to ranges, text
matching and empty-column tests, and the engine counts records per group. `Formbase.Sqlite` is new: the
whole engine's storage in one SQLite file, for a process with no database server. One breaking change
for code: `QuerySpec.Filters` is a list of `FieldFilter` (`FieldFilter.Equal(column, value)` is the old
entry), and an `IProjectionStore` adapter implements `AggregateAsync`. On the MorphDB store, a filter for
records whose column is empty used to miss them.

### Added

- Record queries filter with ranges and text matching, not only equality. A `FieldFilter` names a
  column, an operator and a value: `Equal` on any column; `GreaterThan`, `GreaterThanOrEqual`,
  `LessThan` and `LessThanOrEqual` on integer, decimal and timestamp columns; `Contains` and
  `StartsWith`, ignoring case, on text columns; `IsNull` and `IsNotNull` (`FieldFilter.IsNull(column)`),
  which take no value, on any column — "the records whose judgement is still blank". Filters all apply, and values are coerced to the
  column's declared type as equality values already were, so a date range can be written as text. An
  operator the column's type does not answer, a range or text match against null, or a value given to
  `IsNull`/`IsNotNull`, is refused with
  `InvalidQueryException` (its new `InapplicableFilters` names them) rather than answered with no rows.
  Text ranges are left out on purpose: backends order text differently, and the same query must return
  the same rows whichever store answers it.
- `Formbase.Sqlite`: the raw store, projection store, projection state and field hints in one SQLite
  file (`AddSqliteRawStore` and `AddSqliteProjection` over one connection string — two different files
  are refused), for a process with no database server. Raw watermarks are never reused, so a restarted
  process finds its documents and its projections current. It passes the same contract tests as
  the in-memory and MorphDB stores: decimals compare numerically (kept as exact text under a numeric
  collation), instants compare as instants whatever their offset, and text matching ignores case beyond
  ASCII. Declared relations are not materialized.
- `FormbaseEngine.AggregateAsync` counts projected records, optionally per group (`AggregateSpec`:
  `GroupBy` columns and the same filters). Groups come back ordered by their keys, nulls first, with key
  values as the declared column type, and the answer carries `Stale` and refuses exactly as a query
  does. Ungrouped, it is one count — zero when nothing matches.

### Changed

- **Breaking**: `QuerySpec.Filters` is a list of `FieldFilter` instead of a column-to-value dictionary;
  `FieldFilter.Equal(column, value)` is the old entry. `IProjectionStore` gains `AggregateAsync`, which
  an adapter implements by counting rows per group; the in-memory and MorphDB stores do, and both pass
  the same contract tests. The HTTP record endpoint still takes equality filters only.
- `Formbase.M3L` depends on `M3L.Native` 0.17.0 (was 0.15.0), which targets .NET 10 like Formbase. The
  parsed declarations it reads are unchanged.

### Fixed

- On the MorphDB store, an equality filter with a null value — "the records whose column is empty" —
  was sent as a comparison with an empty string and missed the empty records. It is now sent as
  MorphDB's `isnull`, which needs MorphDB `0.13.1` or later; the in-memory store always answered it.
- The compose bundle's PostgreSQL health check asks over TCP. Asked over the Unix socket, it reported
  the database healthy while the image was still running its init scripts on a socket-only server
  that restarts afterwards, so on a first `docker compose up` MorphDB could start against a server
  that was about to go away.

## 0.12.0

Pairs with MorphDB `0.12.x`, unchanged. A durable schema now records the namespace it holds and
refuses a host serving another name, an idempotency key belongs to one request, and a refused
configuration ends the process with exit code `78`. Two changes are visible to existing deployments:
hosts that shared one schema under different namespaces no longer both start (see *Upgrading* under
the namespace section of the API reference), and a document naming a property twice is refused.

### Added

- `GET /settings` reports `storage` — the PostgreSQL schema and MorphDB project a durable host keeps
  its data in (`null` in-process). Two hosts reporting the same storage serve the same data, whatever
  namespace each answers to; the API reference and README now say that several namespaces sharing one
  PostgreSQL and one MorphDB need a schema and a project each.

### Changed

- A host that refuses its configuration — an unknown store profile, a durable profile missing a
  setting, half a model configuration, a schema holding another namespace — exits with code `78`
  (`EX_CONFIG`) and says why once. It used to end on an unhandled exception: the runtime's crash exit
  code, and the message repeated under a stack trace.

- A document whose JSON names a property twice in one object is refused with `400`
  `/problems/invalid-request` (and `DocumentBody.From`/`Parse` throw). It used to be accepted, and
  the durable store kept only the last value while the in-process store kept both, so the body read
  back depended on the store.

- A durable host's PostgreSQL schema records the namespace that first used it, and a host
  configured with another namespace over that schema refuses to start. Two names over one schema
  used to both start and serve each other's documents. A schema in use before this records the
  first namespace to start over it, so a deployment with one namespace per schema sees no change.
  A host whose database could not be reached at startup makes the check before its first request
  and answers `503` `/problems/namespace-unverified` until it passes; the readiness probe reports
  it too. The MorphDB project is not checked: namespaces sharing one replace each other's projected
  tables of the same name.

  If two namespaces already share one schema, decide which of them keeps it before upgrading, and
  start that host first and alone: whichever starts first claims the schema, so hosts started
  together are decided by which one got there first. Give every other namespace its own
  `Formbase:Schema` (and MorphDB project). The documents those hosts wrote while sharing stay in the
  claimed schema, under the namespace that claimed it — the raw stream does not record which host
  wrote a document, so Formbase cannot separate them.

### Fixed

- An `Idempotency-Key` sent again with a different request — another form type, or the same form
  type with a different body — is refused with `422` `/problems/idempotency-key-reused`. It used to
  answer `201` while storing nothing: the key's document stayed as it was and the new body was
  dropped. A retry of the accepted request still returns the same document; bodies are compared as
  JSON values, so property order and whitespace do not matter.

## 0.11.1

Pairs with MorphDB `0.12.x`. A patch: no surface moves. The packages carry their XML documentation,
the README quick start compiles as written, and the Docker release no longer republishes a version
already in the registry.

### Added

- The packages carry their XML documentation, so an IDE shows each member's comment from the package.

### Fixed

- The README quick start compiles as written. It builds a `ServiceCollection`, whose
  `BuildServiceProvider` lives in `Microsoft.Extensions.DependencyInjection`; `Formbase.DependencyInjection`
  depends only on the container abstractions, and the install list did not name the implementation, so
  a console app following the README failed with `CS1061`. The install list now names it (a host that
  already has a container skips it), and CI compiles the quick start against freshly packed packages
  and exactly the packages the README installs.

### Changed

- The Docker release no longer republishes a version that is already in the registry. A push that
  changed `Directory.Build.props` without changing `<Version>` used to rebuild that version from the
  pushed commit and move its image tags onto code never released under that number; the release now
  checks the registry first, publishes nothing when the tag exists, and fails rather than guesses when
  the registry cannot be asked. The NuGet release was already idempotent.

### Dependencies

- `M3L.Native` to 0.15.0. Its parse output lists metadata, custom-section and extension keys in
  sorted order rather than in an order that changed from run to run; its new lookup-path check
  (`M3L-E022`) is a validation diagnostic, and the hint adapter only parses, so a declaration it
  accepted before is accepted now.
- `MorphDB.Client` to 0.12.3, and the MorphDB server image in `docker-compose.yml`, the README and
  the live fixtures to `0.12.3` (a patch: XML documentation and packaging). The live suite passes
  unchanged against the published `0.12.3` image.

## 0.11.0

Pairs with MorphDB `0.12.x`. A minor: a form type's raw stream is now readable over HTTP, page by
page, and a projection that builds nothing says why — which changes the public
`NotProjectedException` constructor in `Formbase.Core`. Everything on the wire is additive. The M3L
hint adapter maps and records more of what a declaration says, on `M3L.Native` 0.14.0.

### Added

- `GET /formtypes/{type}/documents` reads a form type's raw stream page by page, oldest first, after
  a watermark cursor (`after`, `limit` — default 100, at most 1000). Until now the only raw read was a
  single document by an id the caller had to already hold, and records carry only declared columns —
  so across the HTTP boundary, fields nothing had declared could not be read at all. Each page
  carries `rawHead`, read before the page and never passed, so a caller is caught up exactly when the
  last watermark it received equals it; `limit=0` reads the head alone. An out-of-range `limit` or a
  negative `after` is refused with `400` rather than adjusted. In `Formbase.Core`,
  `FormbaseEngine.ReadDocumentsAsync` and `DocumentPage` are the same read for an embedding host.

### Changed

- The README's HTTP surface summary no longer says writing a declaration is not on the surface: `PUT`
  and `DELETE /formtypes/{type}/declaration` have been served since 0.8.0.
- `POST /formtypes/{type}/projection` says why it built nothing. A run on a form type with no
  declaration answered `200` with `projected: false` and three empty diagnostic lists — and empty
  lists read as "nothing was lost", so the response looked like a successful run with nothing to
  do. It now carries `notProjectedReason`: `noDeclaration` when only a declaration can give the form
  type a shape, `nothingToInfer` when schema intelligence is installed but had no documents to infer
  from; `null` whenever `projected` is `true`. Additive on the wire.
- The `/problems/not-projected` detail names the one remedy that applies instead of offering two.
  It used to say "declare field hints or trigger a projection" in every case, and with nothing
  declared the second remedy is a projection run that projects nothing — a loop the server never
  named. With nothing declared it now says to declare first; with a declared shape not yet built it
  says to trigger a projection. The problem `type` is unchanged. In `Formbase.Core`,
  `NotProjectedException` takes the state as a second constructor argument and exposes it as
  `HasSchema`.

- The M3L hint adapter now maps the whole numeric catalog. `byte`, `short` and `long` join
  `integer` in the integer slot — that slot is emitted as a 64-bit column, so no rung of the ladder
  loses a value — and `double` and `percentage` join `float`, `decimal` and `money` in the decimal
  slot. Until now a field declared with any of those five names was reported as an unmapped type
  and degraded to a text column, which understated what the vocabulary can carry. `binary` is
  deliberately unchanged: it is the one catalog type with no slot to map to, so it still degrades
  to text with a recorded gap.
- The M3L hint adapter counts two losses it used to let through unrecorded. A declared type
  parameter (`string(50)`, `decimal(10,2)`) is recorded as a constraint gap, and an array field —
  which lands in a single JSONB column — is recorded with the element type and item nullability it
  cannot carry. Neither behaviour changes: the same columns come out. What changes is that the gap
  list, which exists to make the loss countable, no longer omits these two.
- The M3L hint adapter records the three composition and ownership constructs M3L 0.13.0 makes
  structural. A model declared `::aspect(Base)` or `::subtype(Base)` is recorded with its base link,
  the same way an inherited model already is; a field contributed by `::extend` keeps its column
  and is recorded with the owner it came from; and a model under a `# Prefix:` (or namespace) owner
  is recorded with that owner, since the form type is still identified by the bare model name. The
  hints themselves are unchanged — before 0.13.0 the base kinds were not models at all and were
  skipped without a trace, so this also stops an aspect or subtype from being silently emitted as a
  standalone table.

### Documentation

- The compose bundle is documented as needing `--build`, and its opening smoke check is now
  `GET /health/ready` rather than `GET /settings`. The host service is built from the tree, so
  `docker compose up` on its own brings back whatever image the last build left — a checkout that
  has moved on since then comes up serving the older one. `/settings` answers `200` from any
  build and so cannot tell the two apart; the readiness probe answers `404` on a build that
  predates the store health checks. The container health check already asked the same question
  from inside and a stale bundle never reported healthy, but nothing said to look there.

### Dependencies

- `M3L.Native` to 0.14.0. 0.11.0 adds a multi-file validation entry point (`ValidateMulti` and its
  typed and result-returning siblings) alongside the existing multi-file parse, and 0.12.0 widens
  the type catalog with `byte`, `short` and `double` and states `float` as 32-bit; the three new
  type names are mapped by the hint adapter in this same release. 0.13.0 adds the `# Prefix:` owner
  header, `::extend` blocks (merged into their target's fields, each carrying its origin) and the
  `::aspect` / `::subtype` base kinds, which are now ordinary models with a base rather than untyped
  generic kinds — a document that used those three words as custom kinds changes meaning. The
  adapter's handling of them is described above. 0.14.0 resolves enum inheritance into an enum's
  values and tightens validation (a later hop of a lookup path without a reference, a value name
  repeated across inheritance, an argument on an extend header); the hint adapter parses without
  validating and maps enum types to text, so its output does not change.
- `MorphDB.Client` to 0.12.2, and the MorphDB server image in `docker-compose.yml`, the README and
  the live fixtures to `0.12.2`. The producer's releases since 0.12.0 are patches carrying dependency
  rounds and documentation corrections; no wire member moves. The suite with the live MorphDB tests
  passes unchanged against the published `0.12.2` image (330/330).
- `Microsoft.NET.Test.Sdk` to 18.10.1 (tests only).

## 0.10.1

Pairs with MorphDB `0.12.x` — the bundle, the docs and the live fixtures now name `0.12.0`. A patch:
nothing in the public surface moves. Besides the MorphDB pair it carries `M3L.Native` 0.10.0, whose
two releases since 0.8.0 are additive on the surface the hint adapter reads, and the workflows now
run the Node.js 24 majors of the actions they already used.

### Dependencies

- `MorphDB.Client` to 0.12.0, and the MorphDB server image in `docker-compose.yml`, the README and
  the live fixtures to `0.12.0`. MorphDB 0.12.0 removes four wire members no code path ever
  honoured (a webhook delivery's field casing, an export request's `filter`/`orderBy`, the hub's
  second `Subscribe` argument, the client's `ChangeNotification.OldData`); this project uses none of
  them, and the live MorphDB suite passes unchanged against the `0.12.0` image (310/310).
- `M3L.Native` to 0.10.0. Both releases since 0.8.0 are additive: 0.9.0 adds
  `@unique(..., nulls: "not_distinct")`, and 0.10.0 lets a registered attribute target an enum
  value and checks such usages against the registry. The hint adapter reads neither and does not
  surface M3L diagnostics, so the hints it derives are unchanged.
- `xunit.v3` to 4.0.1 (tests only).

### Internal

- The CI and release workflows run the Node.js 24 majors of the actions they use (`actions/checkout`
  v7, `actions/setup-dotnet` v6, `actions/cache` v6, `docker/build-push-action` v7,
  `docker/login-action` v4, `docker/setup-buildx-action` v4). The Node.js 20 majors ran only because
  the runner forced them onto Node.js 24; no input any step passes changed meaning.

## 0.10.0

Pairs with MorphDB `0.11.x`, unchanged — the bundle and the docs now name `0.11.1`, the newest
member of that line. One port breaks: `IProjectionState.SetProjectedAsync` now takes the run's
skips, so an implementation outside this repository gains a parameter and a `GetSkipsAsync`; the
HTTP surface only grows (`/health/live`, `/health/ready`, `GET /formtypes/{type}/projection/skips`).
One wire value changes: an array or object arriving for a `Text` column is now a recorded
`ProjectionSkip` instead of a silently stringified value.

### Added

- **Liveness and readiness probes, and a healthcheck that can actually run them.** The host is
  published as an image and answered nothing an orchestrator asks; its own compose bundle waited on
  postgres and on MorphDB's `/health`, then started this service blind. `/health/live` says the
  process is serving, `/health/ready` says the stores it was composed with answered — separated
  because a deployment that conflates them restarts this host in response to an outage somewhere
  else. Readiness performs a read rather than re-stating configuration, so it cannot be green
  through an outage. The runtime image carries neither curl nor wget, so the container healthcheck
  asks the host with the host: `dotnet Formbase.Host.dll --health-check` reads `/health/ready` over
  the loopback port and exits 0 or 1. None of these are in `docs/API.md` — that page is the
  consumer's surface, and these are the operator's.

- **The skips of the last projection are kept, not just returned.** `CONSTITUTION.md` calls a
  mapping failure a `ProjectionSkip` *record*, and until now it was a return value: the run that
  produced it handed it back to whoever called it and nothing stored it, so a host driving
  projection on a timer generated the reasons every tick and dropped them every tick. A completed
  projection now records its skips next to its stamp, and `IProjectionState.GetSkipsAsync` reads
  them back in the order the run produced them. They stay a derivative — raw is the truth and
  re-projecting regenerates them — which is why only the last run's are kept and why clearing a
  form type forgets them with the stamp.
- **`GET /formtypes/{type}/projection/skips`** answers which documents the last projection could not
  map, and why. The status endpoint reports that a projection caught up with raw; a run that
  inserted 2 of 150 documents is `projected` and current there, and this is where the other 148
  reasons are. Unlike `lastRun` on the status response — this host instance's own memory — it is
  read from the recorded state, so it survives a restart and answers for a run another instance
  performed. `count: 0` means the same for "mapped everything" and "never projected"; the status
  endpoint separates those.

### Changed

- **A body the host cannot read now says what is wrong with it.** Binding failures were answered
  with the framework's own message, which named the parameter and the type it was binding to
  (`Failed to read parameter "…Request request" …`) and nothing a caller could act on. The response
  now names the offending value by its JSON path in the document they sent — `the value at
  '$.fields[0].type' is not one this field accepts (line 1, position 79)` — and points at the page
  listing what is accepted. A body that never parsed says so instead of pointing at a field. No
  problem response carries an internal identifier, and a test sweeps the wrong-request surface to
  keep it that way.
- **The MorphDB the bundle and the docs name is now `0.11.1`.** The compatibility pair is unchanged
  — `Formbase.*` still pairs with MorphDB `0.11.x` — but the concrete tag the install
  instructions, the compose bundle, and the live-suite fixtures reach for had stayed at `0.11.0`
  after `0.11.1` was published. A pair names a line; the tag beside it should name the newest
  published member of that line, because that is the one a reader will actually run.

### Breaking

- **`IProjectionState.SetProjectedAsync` takes the run's skips.** The signature gained an
  `IReadOnlyList<ProjectionSkip>` parameter rather than offering a second method, so there is no way
  to record that a projection completed while staying silent about what it dropped — which is the
  gap the entry above closes. An implementation outside this repository adds the parameter and a
  `GetSkipsAsync`; passing an empty list preserves the previous behaviour exactly.

### Fixed

- **An array or object arriving for a `Text` column was projected as its JSON string, skip-free.**
  Every other column type records a `ProjectionSkip` when the value does not fit the declaration;
  `Text` alone turned a structural mismatch into a plausible value — `'["ITA"]'` in a column
  declared to hold a country — with nothing reported, so a query for the declared value found
  nothing and no one learned why. A structured value in a `Text` column is now a skip like any
  other mismatch, with a reason that names the column and points at the declaration that keeps
  the structure (`Jsonb`). Scalars are unchanged: a number or a boolean still lands as its text
  form. **Wire change**: rows that used to carry such a JSON string are skipped instead; a
  re-projection after upgrading reports them in `skipped`, and the projection status's last-run
  counts show the difference.

### Dependencies

- The .NET 10.0.12 servicing line (`Microsoft.Extensions.DependencyInjection*`,
  `Microsoft.AspNetCore.OpenApi`, `Microsoft.AspNetCore.Mvc.Testing`), `Microsoft.Extensions.AI.*`
  10.10.0, `M3L.Native` 0.8.0 and `Microsoft.NET.Test.Sdk` 18.10.0. Patch and minor only.
  `M3L.Native` 0.8.0 parses the entries of a `### Relations` section into `direction`, `name` /
  `target` and `cardinality` instead of passing the line on as text; the adapter still records
  those entries as a vocabulary gap, so the gap's construct text now shows the parsed object
  rather than the bare line — the count is unchanged. `Microsoft.OpenApi` stays on 2.x on purpose
  (see the comment in `Directory.Packages.props`).

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
