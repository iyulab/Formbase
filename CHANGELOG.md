# Changelog

## Unreleased

### Added

- **Host access keys.** `Formbase__Access__Keys__<n>` configures one or more keys; when set, every request
  except the health probes must send `Authorization: Bearer <key>` and is otherwise refused with `401`
  `/problems/access-key-required`. A key names a calling service, not a person. Unset, the host behaves as
  before and logs a warning at startup that it answers every caller that can reach it. Keys shorter than 32
  characters stop the host as a configuration error.
- `QueryResult.SkippedDocuments` and `AggregateResult.SkippedDocuments` (and `skippedDocuments` on the
  host's record query response): how many accepted documents the tables an answer read could not project —
  the form type's own table and each child filter's relation. Those documents are in no row, so a
  `ChildQuantifier.None` filter keeps a parent whose section could not be read (a lone value where a list
  belongs) and an `Any` filter cannot find it; the count says the answer is not over every document. Zero
  when every document stands.
- **`Formbase.Formdown`: a field a condition hid or disabled is left out of the document.**
  `FormdownAdapter.AdaptData` takes the form's condition states (formdown `0.25.0`'s `getConditionStates()`
  or `conditionStates(schema, data)`, as JSON) as an optional third argument. A form keeps what was written
  in a field its conditions later hid, and hands a hidden field over as the same empty string as one left
  blank; with the states, a field that was hidden or disabled is not in the document — it projects as
  absent (`AbsentFieldCounts`), while a visible field left blank stays `null`. Without the states nothing
  changes.

### Changed

- **`Formbase.Formdown`: a field a condition can hide or disable is declared nullable**, even when it is
  required (`*`) — every document filled while it was hidden lacks it, and a non-nullable column skipped
  that whole document. Its condition (`visible-if result=fail`) is reported as a `Constraint` gap instead
  of being dropped silently. Re-declare forms that use `visible-if`, `hidden-if`, `enabled-if` or
  `disabled-if`.

- README: `ChildQuantifier.None` keeps the documents with no row of the relation — those that answered with
  none, those that never carried the field, and those whose section could not be read — not only "no option
  checked".

### Fixed

- **A record key or document id shaped like a number or an id no longer fails a projection over MorphDB.**
  The MorphDB store finds the rows a projection replaces by an equality filter on the record key; MorphDB
  `0.32.x` read a value shaped like a number, a boolean, a date or a GUID as that type and failed the read
  against the text column (`503` `projection-unavailable` on the next run). `MorphDB.Client` `0.33.0` sends a
  string filter value quoted, which every MorphDB server reads as text; MorphDB `0.33.0` also reads a filter
  value as its column's type.
- **A host failure answered with a `5xx` problem now logs its cause.** The framework does not log an exception a
  handler answers, so a `503` `projection-unavailable` named the store and nothing else; the host now logs the
  request and the exception at `Error`. A problem the caller can fix (`4xx`) is not logged as a failure.

## 0.34.0

A minor: a child filter can ask for the parents without such a child row (`ChildQuantifier.None`), and
its condition is optional — so a section of values answers «no option checked». One source-compatible
contract change (`ChildFilter.Condition` is nullable). Pairs with MorphDB `0.32.x`, whose `notexists`
the MorphDB store sends.

### Added

- **A child filter can ask for the parents without such a child row.** `ChildFilter` takes a quantifier:
  `ChildQuantifier.Any` (the default — at least one child row the condition keeps, OData's `any()`) or
  `ChildQuantifier.None` (no such row, `not any()`), and its condition is optional — without one every
  row of the relation counts. A section of values now answers what a JSON column of several values
  answered before 0.31: `new ChildFilter("options", Quantifier: ChildQuantifier.None)` keeps the documents
  with no option checked, and an aggregate with that filter counts them as their own group beside the
  per-option counts. Every store gives the same answer; a parent whose join column is empty has no child
  row. On the MorphDB store a `None` filter needs a MorphDB service that takes `notexists` (0.32.0 or
  later).

### Changed

- `ChildFilter.Condition` is nullable (`FieldFilter?`) and defaults to none. Code that reads it from a
  filter it did not build checks for null; constructing one as before is unchanged.

## 0.33.0

A minor: a repeated section's declaration says which parent form type its rows are cut from, so a
reader starting from the section can reach the parent documents over HTTP. No breaking change. Pairs
with MorphDB `0.31.x`.

### Added

- **A repeated section's declaration names where its rows come from.** `GET /formtypes/{type}/declaration`
  (and the reply to `PUT`) on a form type that is a repeated section of another carries `sectionOf` — the
  parent form type, the child relation naming the section, the array field holding the rows, and the
  parent field each row carries. A section's own declaration does not say it is one, so a reader that
  starts from the section — sampling its rows over HTTP — had no way to find the documents they are cut
  from. `null` on every other form type.

## 0.32.0

A minor, and a fix that matters for anyone who restricts a projected table with MorphDB row-level
security: a rebuild no longer drops the MorphDB table — it refreshes it in place, so the policies,
webhooks and subscriptions bound to it stay. Before, a redeclaration silently left the table readable by
anyone the project lets in. Pairs with MorphDB `0.31.x`.

### Fixed

- **A rebuild no longer removes what MorphDB binds to a projected table.** A rebuild — after a
  redeclaration, a failed run, or on the first run of a table built before marks — dropped the MorphDB
  table and created a new one, and with it went every row-level security policy, webhook and
  subscription bound to the old table: a table its policies had closed became readable by anyone the
  project lets in, without a word. The MorphDB store now refreshes the table in place, as PostgreSQL
  refreshes a materialized view — rows deleted, columns, declared indexes and declared relations
  brought to the declaration — so the table and what is bound to it stay. A column whose shape is
  unchanged is kept as it is; a changed one is replaced, and if MorphDB refuses because a policy depends
  on it, the rebuild fails rather than leave the table open. A table without an owner mark is still
  replaced (MorphDB cannot add the mark in place).

### Added

- `IProjectionStore.ResetTableAsync(schema)` — leaves a table with the schema's shape and no rows, what a
  rebuild refills. Its default drops and recreates the table; a store whose tables carry things the
  projection does not own keeps the table and reshapes it.

### Changed

- `MorphDbTypeMap` names MorphDB's canonical types (`biginteger`, `datetime`, `json`), the names MorphDB
  reports a column's type as, so a declared column and an existing one compare by name. MorphDB accepts
  both spellings, so tables are unaffected.
- Built against `MorphDB.Client 0.31.0`, whose client reports a table's relations — an in-place rebuild reconciles them.

## 0.31.0

A minor. Pairs with MorphDB `0.30.x`. A multiple choice becomes rows: a section that declares one field
beside its key takes values, the formdown adapter declares a checkbox group as one, and a query keeps the
records that have a child row meeting a condition (`ChildFilter`) — the inspections with a dent checked —
on every store. Also new: a calendar-date column type, declared indexes, computed fields, JSON Pointer
source keys, distinct counts, ranked and cut groups, `In` filters, and a durable host that presents a MorphDB
secret. Breaking: a formdown checkbox group is a section, not a JSON column; an equality, grouping or
distinct count on a JSON column is refused; a `sourceKey` starting with `/` is a JSON Pointer.

### Added

- **A query can filter on a child relation: `QuerySpec.Children` / `AggregateSpec.Children`** — a
  `ChildFilter(relation, condition)` keeps the rows with at least one row of the child relation's target
  that the condition keeps (existence, as OData's `any()`): the inspections with a dent checked, the
  orders with a line over 100. The condition is checked against the target's declared columns, the join is
  the relation's key field (or the parent's record, for a section whose parent declares none), and the
  answer is stale when the target's projection is. Every store answers it alike; on SQLite and MorphDB it is
  an `EXISTS` (MorphDB 0.30's `exists` filter). `RelationDef.Target` carries the target's form type.

- **A repeated section of values.** A section that declares one field beside its key field takes items
  that are values as well as objects — `"tags": ["rush", "fragile"]` projects two rows, each value in that
  field — so a multiple choice is a one-to-many table whose values filter, group and count like any column.
  A section projection counts, under the section field in `AbsentFieldCounts`, the parent documents that do
  not carry the section at all (absent or `null`); an empty array is an answer and is not counted.

- **A calendar-date column type: `ColumnType.Date`** (`date` on the wire). A day is not a point in time:
  `2026-10-21` is the same day in every offset, where a timestamp at midnight UTC is another day west of
  Greenwich. Values are `DateOnly`; a document carries one as `yyyy-MM-dd` (a timestamp string in a
  date field is skipped, not cut to a day), and a filter takes a `DateOnly` or that text. Equality, `In`,
  ranges, minimum and maximum, and grouping answer alike on every store; MorphDB stores it as its `date`
  type. The formdown adapter declares a `date` field with it, the M3L adapter a `date` field, and the
  LLM proposer a `"format": "date"` string.

- **The durable host presents a MorphDB secret: `Formbase:MorphDb:Secret`** (and the third argument of
  `AddMorphDbProjectionStore(url, projectId, secret)`), sent as `Authorization: Bearer` on every MorphDB
  request. A host could not run in front of a MorphDB that enforces secrets. A host whose secret MorphDB
  refuses — none, a wrong one, or one confined to another project — now refuses to start (exit `78`),
  as a mistyped project id does, instead of starting and failing its first projection.
- **A declaration can name the indexes its projected table is built with: `FormTypeHints.Indexes`**
  (`indexes` on the wire, each `{"fields": [...]}`) — declared field names in index order, one composite
  index per entry. The SQLite and MorphDB stores build them with the table, so a rebuilt projection has them again;
  the in-memory store keeps nothing to index, and a query answers the same with or without them. A field
  that is not declared, a reference field, a field named twice in one index and two indexes over the same
  fields in the same order are refused by every declaration writer (`ArgumentException`; `400` from the
  host). A declaration without indexes keeps its fingerprint; changing the set rebuilds the projection.
  The SQLite and PostgreSQL declaration stores add an `indexes` column to a declarations table written
  by an earlier version on first use.
- Computed fields: a declared field may carry a derivation (`FieldHint.Derivation`, `derivation` on the
  wire) — a Unix time as a timestamp (`FromEpoch`), a time rounded down to a unit (`TruncateTo`), or one
  part of a time as a number (`PartOf`), all in UTC. The value is computed at projection time and stored
  as an ordinary column, so every store filters, groups and indexes it the same way. The field's type
  must be what the derivation computes and its binding stored. A declaration without a derivation keeps
  its fingerprint; changing a derivation rebuilds the projection.
- **A field can read a nested value: `sourceKey` takes a JSON Pointer** (RFC 6901). A key that starts
  with `/` — `/commit/collection`, `/tags/0` — is resolved through objects and arrays; any other key is
  one top-level member, as before. A value the document does not reach is absent, as a missing
  top-level key is. The projection, a repeated section's parent key and every store read the key the
  same way. A malformed pointer is refused when the field is declared (`ArgumentException`; `400` from
  the host), and a repeated section's key field, which is written into each row, must stay a top-level
  key.
- **An aggregate can count distinct values: `AggregateMeasure.CountDistinct(column)`** — how many
  different values each group holds in a column, empty ones not counted, zero for a group with none.
  It applies to every column type but `Jsonb`, and tells values apart as an equality filter does (one
  decimal spelt two ways, one instant at two offsets, is one value).
- **An aggregate's groups can be ranked and cut: `AggregateSpec.OrderBy` and `AggregateSpec.Limit`.**
  `GroupOrder.ByCount`, `GroupOrder.ByKey(column)` and `GroupOrder.ByMeasure(index)` order the groups,
  each ascending or descending; groups still tied fall back to their key order, so the order is the
  same from every store. `Limit` keeps the first N — the top of a ranking. A key on a column the
  aggregate does not group by, or on a measure it does not ask for, is refused.
- **A filter can list values: `FieldFilter.In(column, values)` (`FilterOperator.In`)** — the column equals
  one of them; every column type but JSON (as for an equality), each value coerced to the column's type as an equality is; an empty
  list keeps no row, and an empty column is never one of the list (`IsNull` asks for that, and a list
  with an empty value in it is refused). Queries and aggregates read it alike. `Formbase.MorphDb` needs
  MorphDB 0.29, which reads a list filter.

### Changed

- **formdown: a checkbox group is a section, not a JSON column** (breaking). `FormdownAdapter.AdaptSchema`
  declares each checkbox group as a child relation to a section form type (`SchemaResult.Sections` —
  `<form>.<field>`, table `<table>_<field>`, fields `parent` and `value`), one row per checked option; the
  form's own declaration no longer has the column, and the "checkbox group" gap is gone. Declare
  `SchemaResult.Declarations` (the sections, then the form). To migrate a query that filtered or grouped the
  group's column: query the section's form type on `value`, and read the form's record from each row's
  `parent`. Documents already stored need no change — the array in them is what the section reads.

- **The SQLite projection store keeps planner statistics.** After writing rows it runs SQLite's
  `PRAGMA optimize`, which analyzes a table only when its row count has moved tenfold since it was last
  analyzed, under a bounded sample. Without statistics the planner could pick the wrong one of several
  indexes on a table; a projection now pays for a few analyses as its table grows.
- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.30.0** (was 0.28.0); the live MorphDB suite and the
  compose file run `ghcr.io/iyulab/morphdb:0.30.0`. A child filter needs a 0.30 server (`exists`), and a
  date filter value reaches it in ISO form.
- **The MorphDB projection store names the row MorphDB refuses.** A refused insert now fails with the
  document id and record key of the refused row and the table, with MorphDB's error inside; it used to
  carry only MorphDB's message, whose row position counts within one chunk of the write. Needs MorphDB
  0.30 (which names the row) — against an older server the error is MorphDB's, as before.
- Dependencies: Microsoft.Extensions.AI 10.11.0.
- **An equality filter on a JSON column is refused** (`InvalidQueryException`, `400` from the host), as
  a distinct count on one already was: the stores read two JSON values as equal differently, and a
  scalar never equals the list a checkbox group stores, so the filter matched nothing on one store and
  could match on another. `FilterOperator.Equal` and `In` now apply to every type but `Jsonb`. Grouping
  an aggregate by a JSON column is refused for the same reason (the in-memory and SQLite stores group
  the JSON as written, PostgreSQL `jsonb` as parsed); `InvalidQueryException.UngroupableColumns` names it.
- **`Formbase.Formdown`: a field left empty is no value.** A form can only hand over an empty string for
  an unchosen option or an unwritten box, so `AdaptData` now writes `null` for an empty text field as it
  already did for an empty number or date — an unanswered choice reads as `IsNull`, not as a group of its
  own. A checked list that is empty stays an empty list. A `date` field is declared as the new `Date`
  type (it was a timestamp at 00:00 UTC, so a day asked for at local midnight matched nothing); a
  `datetime-local` one stays a timestamp and is reported as a gap (`type datetime-local` — read as UTC).
- **A `sourceKey` that starts with `/` is now a JSON Pointer.** A declaration that used one to name a
  top-level key whose name starts with `/` writes it as `/~1…`; a field with no `sourceKey` whose own
  name starts with `/` keeps reading that name.

### Fixed

- **A query row's field has its column's type on every store.** The MorphDB store handed back the text its
  API speaks for a timestamp, a UUID or a date (`"2026-10-21T00:00:00Z"`), where the in-memory and SQLite
  stores give `DateTimeOffset`, `Guid` and `DateOnly`; `RecordRow.Fields` now reads each value as its
  column's type, by the rule a group key already followed. A host over MorphDB writes such a timestamp
  as the others do (`+00:00`).

## 0.30.1

A patch. Pairs with MorphDB `0.28.x`. `Formbase.MorphDb` builds on `MorphDB.Client` 0.28.0 and stops
reading a table where the server says no rows follow.

### Changed

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.28.0** (was 0.26.1); the live MorphDB suite and the
  compose file run `ghcr.io/iyulab/morphdb:0.28.0`. A MorphDB 0.28 list no longer counts its rows unless
  asked; the adapter never read the count.
- **A query or a re-projection that reads a table page by page stops when the server says no rows
  follow** (`Pagination.HasNext`). It used to stop on a page shorter than asked for, so a table whose
  rows filled the last page exactly cost one more request that read nothing.

## 0.30.0

A minor. Pairs with MorphDB `0.26.x`. The formdown adapter declares every number field `Decimal`, so a
document that reached the store without passing through its form keeps its fractions in ranges and
sums; a declaration made by the 0.28.x/0.29.x adapter from the same schema differs in those fields'
type, and declaring it again rebuilds the table as any type change does.

### Changed

- **`FormdownAdapter.AdaptSchema` declares every formdown `number` and `range` field `Decimal`.** It used
  to declare a field `Integer` when its step (or the step of 1 an input has without one) admitted only
  whole numbers, so a document that reached the store another way than through the form — an import, a
  file another tool wrote — had its fractions left out of ranges and sums (each reported as a
  projection field skip). `step`, `min` and `max` stay what they were: input rules, reported as
  constraint gaps. Migration: a declaration made by 0.28.x/0.29.x from the same schema now differs in
  those fields' type, so declaring it again rebuilds the table as any type change does; a host whose
  numbers are whole by its own guarantee sets `Type = ColumnType.Integer` on those hints.
- **`toggle` is no longer read as a formdown field type.** formdown 0.22.0 writes a switch as a
  `checkbox` (with `switch: true`), which is a `Boolean` as before; a schema from an older formdown that
  still says `toggle` degrades to `Text` with an unresolved gap, like any unknown type.
- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.26.1** (was 0.26.0); the live MorphDB suite and the
  compose file run `ghcr.io/iyulab/morphdb:0.26.1`.

## 0.29.0

A minor. Pairs with MorphDB `0.26.x`. A form type can declare the name a person reads it by
(`FormTypeHints.BusinessName`), as a field already could, and the M3L adapter now carries a model's
label there instead of dropping it. Source-compatible: `FormTypeHints` gains an optional last parameter,
so code compiled against 0.28.0 recompiles without edits (a positional deconstruction of `FormTypeHints`
takes one more variable). The SQLite and PostgreSQL declaration tables gain a nullable column on first
use; a bundle volume from 0.28.0 starts on the new images with no step to run, and no table is rebuilt.

### Added

- **A form type can declare the name a person reads it by**, as a field can. `FormTypeHints.BusinessName`
  (wire: top-level `businessName` on a declaration) — «설비 점검일지» for `inspection_log`. Declaration
  only: stored, read back and served, never part of the projected table, so naming or renaming it
  rebuilds nothing. Blank is refused (`400` over HTTP). The M3L adapter fills it from a model's label
  (`## Inspection(설비 점검일지)`), which it used to drop. The SQLite and PostgreSQL declaration tables
  gain a nullable `business_name` column on first use; declarations stored before read back with none.
  Source-compatible: `FormTypeHints` gains an optional last parameter (a positional deconstruction takes
  one more variable).

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.26.0** (was 0.25.0); the live MorphDB suite and the compose file run `ghcr.io/iyulab/morphdb:0.26.0`.
  Nothing Formbase uses changed: 0.26.0 adds OData atomic groups and per-subscriber real-time delivery.

## 0.28.0

A minor. Pairs with MorphDB `0.25.x`. Forms written in formdown come in through a new package,
`Formbase.Formdown`: a form's `getSchema()` result becomes its declaration and a filled form a document
typed by it. A field can declare the name a person reads it by (`BusinessName`). On MorphDB, the records
behind an aggregate group now come in the order — and under a cut, the selection — the other stores
give. Source-compatible: `FieldHint` gains an optional last parameter, so code compiled against 0.27.0
recompiles without edits (a positional deconstruction of `FieldHint` takes one more variable). A bundle
volume from 0.27.0 starts on the new images with no step to run, and no table is rebuilt.

### Added

- **A field can declare the name a person reads it by.** `FieldHint.BusinessName` (wire:
  `fields[].businessName`, ODCS v3.1's `businessName`) — «로트 번호» for `lot_no`. Declaration only: it
  is stored, read back and served, and never reaches the projected table, so naming or renaming it
  rebuilds nothing. Blank is refused (`400` over HTTP). Declarations stored before read back with none.
- **`Formbase.Formdown` — a new package for forms written in formdown.** `FormdownAdapter.AdaptSchema`
  turns the JSON formdown core's `getSchema()` returns into the form type's `FormTypeHints`: one field
  per data field in form order (buttons are not fields), required fields not nullable, types from the
  input — a number input is `Integer` unless its step or minimum admits fractions, a checkbox group is
  `Jsonb`, a single checkbox or toggle `Boolean`, a date `Timestamp`, a time, month or week `Text`.
  `FormdownAdapter.AdaptData` turns a filled form into a document typed by those hints: formdown hands
  values over as text, so a number field's text becomes a JSON number and an empty number or date field
  becomes null; text that is not a plain invariant number is kept as sent, so the projection reports it
  instead of storing a misread value. A label someone wrote becomes the field's `BusinessName`; what the
  hints cannot carry — an option list, a validation rule, a formula — is reported as a `VocabularyGap`. No package dependencies beyond
  `Formbase.Core`; trim- and AOT-compatible.
- **`VocabularyGap` and `VocabularyGapKind` are in `Formbase.Core.Schema`**, shared by every input
  adapter. They were part of the unpackaged M3L adapter; its property `Model` is now `Declaration`, and
  the M3L adapter now carries a field's label as its `BusinessName` instead of reporting it as a gap.

### Fixed

- **The records behind an aggregate group come in the same order on MorphDB as on the other stores,
  and a cut keeps the same ones.** A repeated section's rows share their parent's watermark; MorphDB
  ordered the records behind a group by the watermark alone, so among one parent's rows the order was
  the server's and a `RecordsPerGroup` cut among them kept whichever rows it reached first. They are
  now ordered by watermark and then row position on the server (MorphDB `0.25.0`'s multi-column
  `arrayAgg` order), as SQLite and in-memory order them, and the client no longer re-sorts.

## 0.27.0

A minor. Pairs with MorphDB `0.24.x`. A repeated block inside one form — the lines of an order, the
items of an inspection checklist — is declared as a form type of its own and named on the parent's
child relation as its section field; projecting it cuts each item of the parent's array into a row of
its own table, carrying the parent's key, and a correction or retirement of the parent replaces every
row it held, incrementally as a rebuild would. Before, an input adapter had to split each document and
append its rows under the child's form type itself. A section accepts no documents of its own (`409`).
Its rows are told apart by position wherever a row is named — `RecordRef.Row`, the records behind an
aggregate, the source of a reference — on every store. Two breaking changes, below: `IFieldHintSource`
gains a member and `IntakeService` takes the hint source. A bundle volume from 0.26.0 starts on the
new images with no step to run; tables that are not sections keep their shape and are not rebuilt.

### Added

- **A child relation can name a repeated section.** `RelationHint.SectionField` (wire:
  `relations[].sectionField`) names the array field of the parent's documents whose items are the
  target's rows. Declarations are checked the same way on every store: only a child relation may name
  one, the parent may not also declare the array as a field, a section has one parent and no sections
  of its own, and the section has to declare the relation's key field. `IFieldHintSource.GetSectionSourceAsync`
  answers which declaration a section's rows come from. Declarations stored before read back unchanged.
- **A repeated section projects as rows of its own table.** Projecting the section's form type reads
  the parent's documents: each array item is one row, with the relation's key field set to the
  parent's value of the same name (or, when the parent declares none, to the parent's record identity),
  `fb_doc_id` and `fb_record_key` the parent's, `fb_record` the parent's record identity, a slash and
  the item's position, and a new system column `fb_row` holding that position — present only on a
  section's table, so other tables keep their shape and are not rebuilt. Correcting or retiring the
  parent replaces every row it held, incrementally as a rebuild would. A parent with no section, or a
  null one, holds no rows; a section that is not an array of objects skips the parent document, and a
  row that cannot be mapped is a skip of the parent naming the row. The section's projection status and
  trigger follow the parent's stream, and changing the section field rebuilds the section's table.
- **A repeated section's form type accepts no documents of its own.** A document, retirement or batch
  sent under it is refused with `FormTypeIsASectionException` — `409` `/problems/form-type-is-a-section`
  — and nothing is stored: its rows arrive inside the parent's documents, and a document of its own
  would be stored and never projected. Documents it received before it became a section stay in its raw
  stream and are not projected. Intake otherwise still needs no declaration; when the declarations cannot
  be read, intake fails as `IntakeException` (`503`), as it does when the raw store cannot be written.
- `RecordRef.Row` (wire: `record.row`, absent otherwise) is a section row's position, so the rows one
  parent document holds are told apart — in a record query, in the records behind an aggregate, and in
  the source of a reference that reads a section row. Record queries over a section page by position
  after the watermark, and the records behind an aggregate come in the same order, on every store.
- **A reference can read a section's rows.** By record (`lookupRecord`, the row's identity `o-1/0`) or
  by field. When several rows of one parent match a field, the first by position is read — the same row
  on every store. `ReferenceDef.TargetIsSection` carries this to a store that fixes its lookups when the
  table is created; a reference whose target becomes a section reads as a new shape and rebuilds.

### Changed

- The skip reason for an array in a text column now points at a child relation's section field as the
  way to project its items as rows, instead of appending each item as a document of its own.

### Breaking

- `IFieldHintSource` gains `GetSectionSourceAsync`. An implementation outside this repository has to add
  it; a store that holds no section declarations answers null.
- `IntakeService` takes the `IFieldHintSource` as a second constructor argument. Registration through
  `AddFormbaseCore` is unchanged; code that constructs it directly passes the same hint source the engine
  reads.

### Dependencies

- `MorphDB.Client` 0.24.1, and the compose file and README pin the `0.24.1` server image. The client now
  reports a deleted MorphDB project on the first read rather than on the next table it creates; the host
  answers it as before (`503` `/problems/projection-store-missing`).

## 0.26.0

A minor. Pairs with MorphDB `0.24.x` (was `0.22.x`). Hosts sharing one MorphDB project no longer
replace each other's projected tables: a table carries the store id of the schema whose documents
built it, and a projection that would replace another schema's table answers `409`. A MorphDB project
deleted while a host runs, and a store that cannot be reached during a projection run, answer `503`
instead of an unhandled `500`. One breaking change: `AffixVocabulary` is removed, below. A bundle
volume from 0.25.0 starts on the new images with no step to run; existing projected tables carry no
mark and are marked on their next rebuild.

### Fixed

- **A host no longer replaces projected tables another host built in the same MorphDB project.** Two
  durable hosts over different PostgreSQL schemas given one MorphDB project — under the same namespace
  name or not — each rebuilt a table of the same name by dropping the other's, and the first host then
  served the second one's rows as its own, with no error on either side. Every projected table now
  carries the store id of the schema whose documents filled it (generated once with the schema's
  namespace record), and a projection that would replace a table built from another schema answers
  `409` `/problems/projection-table-owned-elsewhere` and leaves that table as it was. Tables built
  before this carry no mark and are replaced as before; the rebuild marks them. A namespace record
  written by an earlier version gains its store id on the next start, with no step to run.

- **A MorphDB project deleted while a host runs is reported, not a `500`.** The startup check refuses a
  missing project, but a running host's next projection read the project's `404` as "no such table",
  treated the drop as done, and failed creating the table with an unhandled `500` naming neither. Every
  projection-store call now answers that case as `503` `/problems/projection-store-missing` — retrying
  does not help; give the host a project again and restart, and the projections rebuild from the raw
  stream, which keeps accepting documents meanwhile.
- **A projection run against an unreachable store answers `503` `/problems/projection-unavailable`**, as
  queries already did, instead of an unhandled `500`. The run leaves the projection reporting
  `notProjected` (or `unverified`), so nothing stale is served.

### Removed

- **Breaking: `AffixVocabulary`.** It read a modality tag from a form type's name by its suffix, and
  nothing in the engine used it. Formology classifies forms by their purpose and by whether people
  fill them in or they are computed from other documents — a suffix is one language's way of saying
  that, not the test: the same suffix names a register people write and a history computed from
  other documents. A caller that needs the classification declares it rather than reading it from
  the name. The constitution's §1 says so.

### Added

- **`MorphDbProjectionStore` takes an owner** (`IProjectionTableOwnerSource`, `ProjectionTableOwner`)
  whose mark it stores with every table it creates, refusing to drop a table carrying another owner's
  mark (`ProjectionTableOwnedElsewhereException`). Without one it marks nothing and replaces any table
  of the requested name, as before — right only when nothing else writes into the MorphDB project.

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.24.0** (was 0.22.0), and the bundle and the live
  fixtures run the MorphDB `0.24.0` image. Formbase uses 0.24.0's table metadata for the mark above. The
  rest of 0.23.0 and 0.24.0 does not reach Formbase: their wire changes are to GraphQL, OData, values a
  fixed set of choices does not hold and rollups, none of which Formbase uses. A REST list read without
  an order now comes earliest first, so the paged reads Formbase makes to find records by a field's
  value hold every matching row once.

## 0.25.0

A minor. Pairs with MorphDB `0.22.x` (was `0.21.x`), which closes security defects of MorphDB `0.21.x`:
a caller of one project could reach another project's schema objects and webhook dead letters by id,
and any issued secret could read the service's diagnostics. The bundle runs the MorphDB `0.22.0` image —
upgrading a bundle picks those fixes up. Formbase's own behaviour and API are unchanged, and a bundle
volume from 0.24.0 starts on the new MorphDB image with no step to run.

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.22.0** (was 0.21.0). The client calls Formbase
  makes keep their shape; the client's breaking changes in 0.22.0 are to webhook models, xlsx export
  options and column routes, which Formbase does not use. The bundle and the live fixtures run the
  MorphDB `0.22.0` image.

## 0.24.0

A minor. Pairs with MorphDB `0.21.x` (was `0.19.x`). Formbase's own behaviour and API are unchanged, and a
bundle volume from 0.23.0 starts on the new MorphDB image with no step to run.

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.21.0** (was 0.19.0). The client calls Formbase
  makes keep their shape; the client's breaking changes in 0.20.0 and 0.21.0 are to views,
  transactions and JSON import options, which Formbase does not use. The bundle and the live
  fixtures run the MorphDB `0.21.0` image.

## 0.23.0

A minor. Still pairs with MorphDB `0.19.x` (`MorphDB.Client` 0.19.0, the bundle and the live
fixtures on the MorphDB `0.19.0` image). Aggregates measure columns, and a query can ask where each
reference value was read from; a `Formbase.MorphDb` projection built by an earlier release reports
every source as `TargetUnavailable` until it is rebuilt.

### Added

- **An aggregate measures columns, not only counts.** `AggregateSpec.Measures` takes
  `AggregateMeasure.Sum`/`Average`/`Min`/`Max` of a declared column — `Integer` or `Decimal`, and for
  `Min`/`Max` also `Timestamp`; a reference column is measured as its computed value. Each group
  carries one `MeasureValue` per measure, in the order asked: the value and `ValueCount`, how many of
  the group's records had a value in the column (an empty one takes no part). A sum or an average is a
  `decimal` from every store and exact — SQLite adds decimals as decimals, not as floating point; a
  group with no values gets `null`, not `0`. Read in the same read as the count, so the records behind
  a group (`RecordsPerGroup`, `RecordsOf`) are the records its measures are computed from.
- **A reference value says which record it was read from.** `QuerySpec.ReferenceSources` (HTTP
  `referenceSources=true`) adds `RecordRow.References`: for each reference field, a `ReferenceSource`
  — the target `RecordRef` the value is that record's field of, and a `ReferenceOutcome`: `Resolved`
  (found; the field may still be empty), `NoTarget` (the via field is empty or matches nothing — a
  retired record included) or `TargetUnavailable` (the target's projection is missing or not yet in
  the shape the reference reads). Read in the same statement as the value, so the value and its source
  are the same record; a second query to find it could meet a correction in between. Off by default.
  `Formbase.MorphDb` carries each reference's source as two more MorphDB lookups of the same target
  row, created with the table, and reads them only when a query asks for sources — a query that does
  not names its columns, so MorphDB does not compute the source lookups for it. A projection built by
  an earlier release reports every source as `TargetUnavailable` until it is rebuilt
  (`POST /formtypes/{type}/projection`).
- **`InvalidQueryException.InapplicableMeasures`.** A measure of a column its function does not apply
  to — a sum of text, a bound of a boolean — is refused with the measures named; an undeclared
  measured column is in `UnknownColumns`, as for filters. Text bounds are left out for the reason text
  ranges are: the stores order strings by different collations.

### Documentation

- **The API docs no longer say a reference is not resolved.** `FieldBinding`, `FieldBinding.Reference`,
  `ProjectionResult.UnresolvedReferences` and the HTTP projection contract still described the
  `Reference` binding as declared but not executed. Since 0.20.0 every projection store computes a
  reference when the row is read — in query rows, filters, ordering and aggregate groups — provided
  its `EntityRef` carries a lookup and a via field; `UnresolvedReferences` names only the reference
  columns declared without that pair. The docs now say so. Behaviour is unchanged.

### Internal

- **The M3L adapter builds on `M3L.Native` 0.24.0** (was 0.20.0). The adapter is not packaged.

## 0.22.0

A minor. Pairs with MorphDB `0.19.x` (was `0.18.x`): MorphDB enforces row-level security in
PostgreSQL now, and its database roles change with it. The bundle runs MorphDB `0.19.0` and creates
the second role MorphDB needs; **a bundle volume from an earlier release needs two statements before
it starts** (see Changed). Formbase's own behaviour and API are unchanged.

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.19.0** (was 0.18.1). The client's wire is
  unchanged; the new pairing is the server's. A `Formbase.MorphDb` host pointed at a MorphDB `0.18.x`
  server keeps working.

### Changed

- **The bundle's MorphDB login is a member of `morphdb_bypass`.** `scripts/init-databases.sql` creates
  that role (`NOLOGIN`, nothing else) and grants it to `morph`. MorphDB switches a request that
  bypasses row-level security — one made with its master secret — to it, and MorphDB `0.19.0`
  refuses to start without the membership; `0.18.x` ignores it. A bundle volume created before this
  change needs the two statements once, as the bootstrap superuser, before it is moved to a MorphDB
  that requires them:

  ```bash
  docker compose exec -T postgres psql -U postgres -d morphdb \
    -c "CREATE ROLE morphdb_bypass NOLOGIN" -c "GRANT morphdb_bypass TO morph"
  ```

  (A volume still on the superuser login `morph` from before 0.21.0 runs MorphDB's
  `scripts/migrate-to-service-role.sql` instead, which creates the role as well.)

## 0.21.0

A minor. Pairs with MorphDB `0.18.x`, now `0.18.1`. Restores again under audit warnings as errors:
`Formbase.MorphDb` 0.20.0 brought in a package with a high-severity advisory through
`MorphDB.Client` 0.18.0 (see Dependencies). M3L import declares a `@lookup` field as a computed
reference. The bundle's MorphDB logs in as a role of its own, ahead of MorphDB `0.19.0` refusing a
superuser — an existing bundle volume keeps working unchanged with `0.18.x`.

### Changed

- **The bundle's MorphDB logs in as a role of its own.** `docker-compose.yml` no longer makes the
  PostgreSQL image's bootstrap superuser (`POSTGRES_USER: morph`) MorphDB's login:
  `scripts/init-databases.sql` creates `morph` as a `NOSUPERUSER NOBYPASSRLS` role that owns the
  `morphdb` database, and the bootstrap superuser keeps the image default, `postgres`. MorphDB `0.19.0`
  refuses a superuser login, because PostgreSQL applies no row-level security to one.

  A bundle volume created before this change still starts as it is with the MorphDB this bundle pins
  today (`0.18.x`). Before moving it to a MorphDB that refuses a superuser, hand MorphDB's database to a
  service role with MorphDB's `scripts/migrate-to-service-role.sql` — it changes ownership only, and
  Formbase's own database is not touched:

  ```bash
  docker compose exec -T postgres psql -U morph -d morphdb \
    -v service_role=morphdb_service -v service_password='<password>' \
    < migrate-to-service-role.sql
  ```

  then set `MORPHDB_CONNECTION_STRING` (new — `.env`) to that role's connection string. The volume
  keeps the superuser it was created with (`morph`), and that is still the account you administer it
  with: the compose file's `POSTGRES_PASSWORD` is read only when a volume is first created.

### Documentation

- The README's second example runs the engine over `Formbase.Sqlite` and shows a keyed record, a
  reference with its lookup pair, and a correction to the target reaching the row that reads it. It is
  compiled against the packed packages like the first. "Reading a declaration back" no longer says
  every reference projects empty: one with a lookup pair is computed, one without is not.

### Dependencies

- **`Formbase.MorphDb` builds on `MorphDB.Client` 0.18.1** (was 0.18.0), and the Microsoft packages
  move from `10.0.12` to `10.0.13`. `MorphDB.Client` 0.18.0 brought in
  `Microsoft.AspNetCore.SignalR.Common` 10.0.12, which carries a high-severity denial-of-service
  advisory (CVE-2026-70159); a project that treats audit warnings as errors could no longer restore
  `Formbase.MorphDb` 0.20.0. The bundle runs the MorphDB `0.18.1` image, which also fixes two
  security defects of `0.18.0` (see MorphDB's changelog).
- `Formbase.M3L` builds on `M3L.Native` 0.20.0 (was 0.19.0). What the import reads from a model is
  unchanged; 0.20.0 moves an index directive's keyword arguments and a computed field's platform
  expressions to fields of their own, neither of which the import maps.

### Added

- **M3L import declares a lookup field as a computed reference.** `- inspector_name: string
  @lookup(inspector_id.name)` says both halves of the join — the field here that carries the key, and
  the target's field to read — so it is now imported as a `reference` field whose target carries the
  lookup pair (matched against the target model's `@pk`), and is computed when read. Before, every
  lookup was dropped as a `VocabularyGapKind.Derived` gap and no column was declared. A lookup the
  document does not say enough to compute stays a `Derived` gap whose detail says what is missing: a
  path that is not `fk.column`, a first segment that is not a `@reference` field, a target model that
  is not in the document or has no single `@pk`, a column the target does not store. Rollup and
  computed fields are still not carried.
- **M3L import names a soft binding it cannot compute.** A true-now binding names its target column but
  not which target record, so the reference is declared without a lookup pair and is stored, not
  computed. The import now reports each such field as a `VocabularyGapKind.ReferenceLookup` gap, before
  the first projection names it in `UnresolvedReferences`; the gap's detail points at the lookup form
  above, which is computed.

## 0.20.0

A minor. Pairs with MorphDB `0.18.x` (was `0.17.x`). A `reference` field is computed when it is read, in
every store: the target record's current value, found by a target field or by the record itself, and it
filters, orders and groups like any declared field. Every projected row now carries its record identity
(`fb_record`), so **every projection reads `Stale` once after upgrading and rebuilds on its next run**.
Breaking: `EntityRef`'s lookup is typed (`TargetLookup`) — see Changed for the one-line migration; a
stored declaration reads back unchanged.

### Added

- **A reference reads its target's current value.** A `reference` field whose target declares how to find
  the record — `EntityRef(target, ValueField, Lookup, ViaField)` — is computed when it is read: the
  value of `ValueField` on the target record that `ViaField`'s value identifies, the latest accepted
  when several match, null when none does. It filters, orders and groups like any declared field, and a
  result is `Stale` when a target it reads is not current. Every store computes it: the in-memory store
  when it reads, SQLite from each reference's definition kept beside the table's columns, MorphDB as a
  lookup column (which needs a `0.18.0` server). A reference declared without a
  lookup is still stored and still reported in `UnresolvedReferences`.
- **A target can be found by its record.** `TargetLookup.Record` matches the target record's identity —
  its record key, or the document id of a record appended without one — so a field carrying a record key
  (stable across corrections) or a document id can be a reference. Over HTTP: `"lookupRecord": true`.
  The identity is text and is matched exactly: a record key as written, a document id in its canonical
  form (`RecordRef.Document.ToString()`, lower case with hyphens). The via field is declared `Text`; a
  record lookup through a field of another type is refused when declared.
- A reference with a lookup is refused when declared if its pairs differ in type — the reference and the
  target field it reads, or the via field and the target field it is matched against — once the target
  form type is declared. The stores would otherwise answer it differently (one compares by type, one
  does not).
- **Every projected row carries its record identity** in a new system column, `fb_record` — the record
  key, else the document id in canonical form — the column a record lookup is matched against.
- A reference whose target is projected but not in the shape the reference reads — the target redeclared
  and not yet rebuilt, or built before `fb_record` existed — reads null, and the result is `Stale`, as
  for a target never projected. It used to fail the read in the SQLite store.

### Changed

- **Breaking: `EntityRef`'s lookup is typed.** `EntityRef.LookupKey` (a field name) is now
  `EntityRef.Lookup`, a `TargetLookup` — `TargetLookup.Field(name)` for the former meaning or
  `TargetLookup.Record`. A stored declaration keeps the form earlier releases wrote and reads back as
  `TargetLookup.Field`. Migrating: `new EntityRef(t, v, "key", via)` becomes
  `new EntityRef(t, v, TargetLookup.Field("key"), via)`.
- **Every projection reads `Stale` once after upgrading, and its next run rebuilds it.** A projection's
  fingerprint now covers the system columns every row carries as well as the declaration, so the
  `fb_record` column (above) reads as a shape change everywhere. Project each form type again; until a
  reference's target is rebuilt, the reference reads null (above).

## 0.19.0

A minor. Pairs with MorphDB `0.16.x` (was `0.15.x`). Breaking: a query row now says which record it is
(`RecordRow` — `Record` and `Fields`), an aggregate's evidence is records (`Records`, `RecordsPerGroup`), and
a record read over HTTP answers each row as `{ record, fields }` — see the migration paragraph below. The
MorphDB store reads a query window from its offset in one request and runs under Native AOT.

### Changed

- **Breaking: a query row says which record it is.** `QueryResult.Rows` is a list of `RecordRow` —
  `Record`, a `RecordRef` (the document the record stands on now, and its `RecordKey` when it has one),
  and `Fields`, exactly the declared columns as before. A keyed record keeps its key through every
  correction while its document moves to the correcting one, so the key is what identifies a record
  across reads; a document without a key is a record of its own (`Key` is null) and its document is its
  identity. Before, a row carried only its declared fields, so a row could not be tied to a document or
  to the same record in a later read.
- **Breaking: an aggregate's evidence is records, not documents.** `AggregateGroup.Documents` is
  `AggregateGroup.Records` (a list of `RecordRef`), and `AggregateSpec.DocumentsPerGroup` is
  `AggregateSpec.RecordsPerGroup`. Order (the order the documents were accepted) and the limit are
  unchanged; the records are the same identities the rows of `AggregateSpec.RecordsOf(group)` carry, so a
  count can be matched to the rows that make it up. Every store reads each record's key with its document
  in the same statement as the count — SQLite as one array of pairs, MorphDB as a second `ARRAY_AGG` in
  the same order beside the document ids.
- **Breaking (HTTP): a record read's rows are `{ "record": { "document", "key" }, "fields": { … } }`.**
  `GET /formtypes/{type}/records` used to answer each row as the declared fields alone; they are now under
  `fields`, beside `record`, whose `key` is `null` for a document sent without one (present, not omitted).
  The served OpenAPI document gains `RecordRowResponse` and `RecordRefResponse`.

  **Migrating.** Read a field as `row.Fields["name"]` instead of `row["name"]` (over HTTP,
  `row.fields.name` instead of `row.name`). Read an aggregate's evidence as `group.Records[i].Document`
  where it was `group.Documents[i]`, and ask for it with `RecordsPerGroup` instead of `DocumentsPerGroup`.
  To keep track of a record across reads, compare `Record.Key` when it is set and `Record.Document`
  otherwise. A store implementing `IProjectionStore` outside this repository returns `AggregateGroup.Records`
  — each record's key beside its document, null for a document without one — and keeps returning the
  `fb_doc_id` and `fb_record_key` system columns with its query rows, which the core now reads; a query
  row without a readable document id is refused as a broken store contract.

- **`Formbase.MorphDb` pairs with MorphDB `0.16.x`** (was `0.15.x`), and reads a query window from where it
  starts: one request with the window's offset, where it used to assemble an arbitrary offset from the
  pages that covered it (two requests in the common case). A window wider than the most rows MorphDB
  returns at once still takes several requests, each starting where the last ended. Needs a MorphDB server
  of `0.16.0` or later — an older one does not read the offset and answers from the first row.
- **`Formbase.MorphDb` runs under Native AOT and trimming.** Its MorphDB client is AOT-compatible from
  `0.16.0`, so the claim that every package but the host runs under Native AOT now holds on the MorphDB
  path too; the AOT publish smoke drives the MorphDB store (against a stub server) alongside SQLite.

## 0.18.0

A minor. Pairs with MorphDB `0.15.x` (was `0.14.x`). An aggregate can say which documents each count is
made of, read with the count, and every package but the host runs under Native AOT and trimming. Additive
for callers; a store implementing `IProjectionStore` outside this repository sees `AggregateSpec` gain
`DocumentsPerGroup` and answers it or leaves `AggregateGroup.Documents` null.

### Added

- **An aggregate says which documents each count is made of.** `AggregateSpec.DocumentsPerGroup` asks
  each group to carry up to that many of the documents behind its count (`AggregateGroup.Documents`), in
  the order they were accepted, read by the same read as the count — so a number and its evidence cannot
  come from two moments of the projection. Fewer documents than `Count` means the list stopped at the
  limit; `AggregateSpec.RecordsOf(group)` is the query that reads all of a group's records (the
  aggregate's filters plus each grouping column equal to the group's value, an empty value as `IsNull`).
  Every store answers it in one statement: in-memory, SQLite (a window over the group) and MorphDB (its
  `ARRAY_AGG` — `Formbase.MorphDb` with `DocumentsPerGroup` needs a MorphDB server of `0.15.0` or later).
  A non-positive `DocumentsPerGroup` is refused. `AggregateSpec` and `AggregateGroup` gain a trailing
  optional parameter each — a store implementing `IProjectionStore` outside this repository answers
  `DocumentsPerGroup` or leaves `Documents` null.
- **`DeclarationJson`** — the format a durable `IFieldHintSource` stores a declaration's fields and
  relations in, one for every store and usable by a store outside this repository; the SQLite and
  PostgreSQL stores use it.

### Fixed

- **Every package runs under Native AOT and trimming.** All packages but the host declare
  `IsAotCompatible`. The SQLite and PostgreSQL declaration stores wrote and read the stored fields and
  relations through reflection-based serialization, which a Native AOT host does not have; they now use
  compile-time metadata, and the schema proposer writes its sample documents without the serializer. The
  stored format is unchanged — written byte for byte as before, and declarations already on disk read
  back the same. CI publishes and runs a Native AOT executable over the SQLite path (intake, declaration,
  projection, query, aggregate).

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
