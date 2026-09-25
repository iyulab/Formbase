# HTTP API

> The released version is **0.11.1**, and the documents that describe it are the tree at its tag —
> open a file at `v0.11.1` to read the reference for what you can install today.
>
> A `Since x.y.z` marker names a **Formbase** version. Statements about the MorphDB a deployment
> runs against are written in prose, because that version moves on its own line.

The host serves the engine over HTTP so a consumer does not have to be a .NET process in the same
container. The library stays what it is: embedding the engine directly remains supported, and this
surface is a packaging of it rather than a replacement.

Everything here follows from one property of the engine — **the raw store is the source of truth and
a declaration is never required to accept a document.** Intake and raw reads therefore work
unconditionally; everything about projections is a state a caller can read rather than a
precondition they must satisfy.

```yaml
POST   /formtypes/{type}/documents     # Accept a document
GET    /formtypes/{type}/documents     # Read a form type's stream, page by page
GET    /documents/{id}                 # Read a stored document
GET    /formtypes/{type}/declaration   # Read the declaration in force
PUT    /formtypes/{type}/declaration   # Put a declaration in force
DELETE /formtypes/{type}/declaration   # Remove it, and the projection it built
POST   /formtypes/{type}/projection    # Rebuild the projected table
GET    /formtypes/{type}/projection    # Read projection state
GET    /formtypes/{type}/projection/skips  # Read what the last run could not map
GET    /formtypes/{type}/records       # Query projected records
GET    /settings                       # What this instance was composed as
GET    /openapi/v1.json                # The generated OpenAPI document
```

The OpenAPI document is generated from the endpoints themselves, so it is the machine-readable form
of this page and cannot drift from what is served.

---

## What this instance is

```http
GET /settings
```

```json
{
  "namespace": "default",
  "storeProfile": "durable",
  "durable": true,
  "storage": { "schema": "formbase", "morphDbProjectId": "6f1a6f6e-0000-4000-8000-000000000001" },
  "schemaIntelligence": { "installed": true, "model": "gpt-4o-mini" }
}
```

Deployment choices, fixed when the process started — so this is a read. A settings write would be
offering to change what stores are behind the ports of a running host, and the answer to that is a
new process.

**`durable` is the one to check.** An in-process host answers every request exactly as a durable one
does and loses the documents at the next restart; nothing else about a response distinguishes them.

**`storage` is where a durable host keeps its data** — the PostgreSQL schema and the MorphDB project;
`null` for the in-process stores. It is reported apart from `namespace` because the two are not the
same thing: two hosts reporting the same `storage` read and write the same data, whatever namespace
each serves (see below). The connection itself is not reported.

**`schemaIntelligence` is an extension, not a requirement.** Supply a model endpoint, key and name
and the engine infers structure for fields nobody declared; supply none and every other capability is
unchanged — the host starts without model credentials, always. Supplying only some of the three is
the one thing refused at startup: a host with half of them looks like it has intelligence installed
and fails on the first proposal. Credentials are never reported back.

---

## Which data a request is addressed to

A host serves one namespace — the connection, schema and projection target it was composed with.

```http
Formbase-Namespace: orders-eu
```

**Omit the header and the request addresses the host's own namespace**, which is what makes a
single-namespace deployment usable without every caller knowing its name. A request naming a
namespace this host does not serve is refused with `404` rather than answered from the one it does:
answering would hand back someone else's rows under the name that was asked for.

The name comes from configuration (`Formbase:Namespace`, `default` when unset). The header is on the
surface even where the selection is degenerate, because one added later would mean rewriting every
request written without it.

The namespace's contents are the triple the host was composed with — the PostgreSQL connection and
schema holding the raw stream, and the MorphDB project holding the projected tables. **The name does
not choose them.** Two hosts configured with different namespaces and the same database, schema and
project serve the same data under two names, and the `404` above cannot tell — it checks which host a
request reached, not where that host's data lives. So several namespaces sharing one PostgreSQL and one
MorphDB need a different `Formbase__Schema` and `Formbase__MorphDb__ProjectId` each; `GET /settings`
reports both as `storage`, so two hosts can be compared. A host running
the in-process stores has that triple only notionally, and loses it on restart; see the README for
selecting the durable profile.

> Authentication and authorization are not on this surface. Namespacing says *which data*, not *who
> you are* — put the host behind a proxy that answers the second question.

---

## Accepting a document

```http
POST /formtypes/orders/documents
Idempotency-Key: 0f0e9a2c-3d4b-4c11-9a1e-6b8d5f2a7c33
Content-Type: application/json

{ "customer": "ada", "total": 42 }
```

```json
{ "documentId": "0f0e9a2c-3d4b-4c11-9a1e-6b8d5f2a7c33", "formType": "orders" }
```

The document is stored **verbatim**. Nothing interprets it at intake — not a declaration, not a
type check — so a form type with no declaration accepts documents exactly as one with a declaration
does, and they wait in raw until a shape is declared for them.

**`Idempotency-Key` makes a retry safe.** The key becomes the document's identity, so re-sending the
same request lands on the same document rather than a second copy, takes no new position in the
stream, and answers exactly as the first call did — a reply that revealed which attempt it was would
teach callers to tell them apart, which is the opposite of what the key is for.

**A key belongs to one form type.** Sent again for a document of another form type, it is refused
with `422` `/problems/idempotency-key-reused` — that is a second request wearing the first one's key,
not a retry of it, and answering it as a retry would report a document of the new type that was never
stored. The document already held under the key is unchanged. Under the same form type the key is
answered as a retry whatever the body says: the document keeps the body it was first stored with.

The key travels in a header rather than in the body, and that is forced rather than chosen: the body
is kept verbatim, so a key written into it would become part of the document. It must be a UUID; a
value that cannot be an identity is refused rather than ignored, since ignoring it would stop
deduplicating without saying so.

Without the key, each submission is a separate document.

---

## Reading a document

```http
GET /documents/0f0e9a2c-3d4b-4c11-9a1e-6b8d5f2a7c33
```

```json
{
  "documentId": "0f0e9a2c-3d4b-4c11-9a1e-6b8d5f2a7c33",
  "formType": "orders",
  "watermark": 12,
  "appendedAt": "2026-08-04T09:30:00+00:00",
  "body": { "customer": "ada", "total": 42 }
}
```

**This always works.** It reads the raw store, so it answers whether or not the form type has ever
been projected — the body comes back as it was sent, and `watermark` is the document's position in
that form type's append-only stream.

---

## Reading a form type's stream

```http
GET /formtypes/orders/documents?after=12&limit=100
```

```json
{
  "documents": [
    {
      "documentId": "5b1d0c7e-8a0f-4f5e-9c2d-3e4f5a6b7c8d",
      "formType": "orders",
      "watermark": 13,
      "appendedAt": "2026-08-04T09:31:00+00:00",
      "body": { "customer": "ada", "total": 42, "note": "rush" }
    }
  ],
  "rawHead": 13
}
```

**This always works, too** — it reads the raw store, so it answers before a declaration exists and
after one does, and it carries every field the documents were sent with. That is what it is for: a
declared projection answers only for its declared columns, and a single read needs an id you were
handed at intake, so this is the one place a caller that did not send the documents can see what
nothing has declared yet.

Each document has the shape a single read returns. They come oldest first.

**Page by watermark.** `after` is the last watermark you received (omit it, or send `0`, to start
from the beginning); the next page begins after it. `rawHead` is the form type's latest watermark,
read before the page, and the page never reaches past it — so you have read everything when the last
watermark you received equals `rawHead`, and a page read at that point comes back empty rather than
refused. Documents appended while you page wait for your next request.

- **`limit`** — defaults to `100`, at most `1000`. `limit=0` returns no documents and only
  `rawHead`: the cheap way to ask whether anything new has arrived.
- A `limit` outside `0`–`1000` or a negative `after` is refused with `400`
  `/problems/invalid-request` rather than adjusted. A page cut short to the server's maximum without
  saying so would read as the end of the stream.

A form type that has never received a document answers an empty page with `rawHead: 0`.

The stream is read-only and unfiltered by design. Selecting documents by their content is what a
declaration and its projection are for; the raw store is append-only, so nothing here changes it.

---

## Reading the declaration

```http
GET /formtypes/orders/declaration
```

```json
{
  "formType": "orders",
  "tableName": "orders",
  "declarationVersion": 1,
  "fields": [
    { "name": "total", "type": "integer", "nullable": false, "sourceKey": null,
      "binding": "stored", "target": null },
    { "name": "customerName", "type": "text", "nullable": true, "sourceKey": null,
      "binding": "reference", "target": { "formType": "customers", "keyField": "name" } }
  ],
  "relations": [
    { "name": "lines", "kind": "child", "target": "orderlines", "keyField": "orderId" }
  ]
}
```

This is the shape the next projection run will build. A form type with no declaration answers `404`
`/problems/no-declaration` — a state to read, not a failure to recover from.

- **`type`** — `text`, `integer`, `decimal`, `boolean`, `timestamp`, `uuid`, `jsonb`.
- **`sourceKey`** — the document key the field reads when it differs from the column name. A rename
  that keeps already-stored documents readable.
- **`binding`** — `stored` (the document's own value), `snapshot` (copied and fixed at write time),
  or `reference` (reads the target's current value). **A `reference` column is declared but not
  resolved by the engine today:** a run names it among `unresolvedReferences` and leaves it empty
  rather than filling it with the document's own fixed-then copy, which would be a different answer
  wearing the same column name.
- **`kind`** on a relation — `child` (an owned entity whose key field points back here) or
  `reference` (a link out).

### Putting a declaration in force

```http
PUT /formtypes/orders/declaration
{
  "tableName": "orders",
  "declarationVersion": 2,
  "expectedDeclarationVersion": 1,
  "fields": [ { "name": "total", "type": "integer", "nullable": false } ]
}
```

**Say which version you are replacing.** `expectedDeclarationVersion` is the version you believe is
in force; omit it only for a form type that has none. Two opposite mistakes answer `409`
`/problems/declaration-version-conflict`: expecting a version when none is in force, and omitting one
when there is. A blind overwrite is how one consumer silently discards another's declaration, so the
surface does not offer it.

The declaration's own `declarationVersion` is yours to choose — it is what the *next* writer will
have to expect.

A first declaration answers `201`; a replacement answers `200`. Both carry the declaration as it
will now read back, together with what the write did to the projection:

```json
{
  "declaration": { "formType": "orders", "declarationVersion": 2, "…": "…" },
  "projection": { "state": "stale", "projectedWatermark": 12, "rawHead": 12 }
}
```

**Nothing is rebuilt.** A declaration whose shape changed leaves an existing projection `stale`
without any document arriving — that is the shape axis of staleness, and the watermarks alone cannot
show it. Run a projection when you want the table to match; a rebuild drops and refills the whole
table, which is not a cost to spend on your behalf without being asked.

A declaration is refused with `400` `/problems/invalid-declaration` when it names no table, carries
no fields, or declares one field twice — each would otherwise land as a projected table nobody meant
to declare.

### Removing a declaration

```http
DELETE /formtypes/orders/declaration
```

`204` when it is gone, `404` when there was none.

**The projection goes with it** — the projected table is dropped and the projection state forgotten,
so the form type goes back to having documents and no shape. Leaving the table behind would be the
unsafe choice: the form type would read `notProjected` while its rows sat there.

**The raw stream is untouched**, which is what makes this safe. Declare again, project, and the table
comes back as it was — including any documents accepted in the meantime, because they were always in
raw. That is the difference between deleting a shape and deleting data, and only the first is on
offer here.

If dropping the table fails, the declaration is left in place so you can retry: removing it first
would leave a table nothing points at and no way to ask for it again.

---

## Projecting

```http
POST /formtypes/orders/projection
```

```json
{
  "projected": true,
  "inserted": 2,
  "projectedWatermark": 12,
  "skipped": [{ "documentId": "…", "reason": "…" }],
  "absentFieldCounts": { "total": 1 },
  "unresolvedReferences": ["customerName"],
  "notProjectedReason": null
}
```

**Safe to repeat and safe to retry.** The table is rebuilt from the raw stream rather than updated
in place, so two runs over an unchanged stream leave the same table. A run is bounded to the raw
head it saw when it started, so documents arriving mid-run are left for the next run rather than
landing under a watermark that does not cover them.

`projected: false` means no declaration proposed a schema **and** schema intelligence (see
[What this instance is](#what-this-instance-is)) had nothing to observe either. That is not an
error — documents are accepted without one — and nothing about the recorded state changes.
The three diagnostic fields are empty on such a run, because no run happened for them to describe;
**`notProjectedReason`** is what says so, and what would change the answer:

| `notProjectedReason` | Meaning | What to do |
|---|---|---|
| `noDeclaration` | Nothing is declared and schema intelligence is not installed — a declaration is the only thing that can give the form type a shape | Declare field hints; running the projection again changes nothing |
| `nothingToInfer` | Nothing is declared and schema intelligence is installed, but found nothing to infer from (no documents yet, or none with an object body) | Declare field hints, or append documents and run again |

It is `null` whenever `projected` is `true`.

**When schema intelligence is installed and a form type has no declaration, this endpoint asks the
model instead of answering `projected: false`.** A model failure is not silent: an unreadable or
unshaped proposal answers `400` `/problems/schema-proposal-invalid`, and a model that could not be
reached at all (connection, timeout, auth, rate limit) answers `503`
`/problems/schema-proposer-unavailable` — see [Errors](#errors).

Two fields exist so that silence stays visible:

- **`absentFieldCounts`** — per declared column, how many projected rows came from documents that did
  not carry the field at all. An explicit `null` in a document is an answer and is not counted here.
  The projected NULL conflates the two; this is what makes the conflation visible.
- **`unresolvedReferences`** — declared columns left empty because their binding could not be
  resolved. Named rather than silently blank: an empty column with no explanation reads as absent
  data.

`skipped` carries documents that could not be mapped into the declared shape. A mapping failure
skips one document and never touches raw.

---

## Projection state

```http
GET /formtypes/orders/projection
```

```json
{
  "state": "stale",
  "projectedWatermark": 8,
  "rawHead": 12,
  "lastRun": { "insertedCount": 8, "skippedCount": 2, "observedAt": "2026-08-19T10:00:00Z" }
}
```

**Branch on all four states.**

**`lastRun` is what this host instance itself observed, not a durable record.** It is `null` until
this host has run the projection at least once since it started — a different instance, or this one
after a restart, answers `null` for a form type it has genuinely projected before. This is what
turns "148 documents silently missing" into a number a caller can see instead of a fact only the raw
stream still knows: run the projection again to refresh it, the same way `state` is never a stale
answer you cannot correct.

| `state` | What it means | What to do |
|---|---|---|
| `notProjected` | No projected table exists for the current declaration | Declare, then run a projection |
| `projected` | The projection reflects the current raw head and declaration | Query it |
| `stale` | Raw documents arrived, or the declaration changed, after the run | Run a projection when freshness matters |
| `unverified` | A failed rebuild left the projection's integrity unconfirmed | Re-project; do not read it as fresh |

`unverified` is the one that is easy to miss. A projection exists and names the current table, so it
is not `notProjected` — but nothing vouches for its rows.

Staleness has two axes. **Data**: the raw stream advanced past what was projected. **Shape**: the
declaration was re-declared after the run. A redeclaration never moves the watermark, so the
watermarks alone cannot show the second one.

---

## What a projection skipped

```http
GET /formtypes/orders/projection/skips
```

```json
{
  "skipped": [
    { "documentId": "0f3c1e2a-1c4d-4e6a-9a11-2b7c9d0e4f55", "reason": "field 'deadline' is not convertible to Timestamp" }
  ],
  "count": 1
}
```

`state` answers whether the projection caught up with raw; this answers what it left behind getting
there. **A run that inserted 2 of 150 documents is `projected` and current** — the watermark reached
the head, because the 148 documents it could not map were read and recorded rather than skipped over
silently. Those 148 reasons are here, and nowhere else.

**Unlike `lastRun`, this is durable.** It is recorded with the projection itself, so it survives a
restart and answers for a run a different instance performed. A new run replaces it, because skips
describe one run: after a re-declaration that fixes the mismatch, a clean run leaves `count: 0`.

`count: 0` means the same thing for "the last run mapped everything" and "this form type was never
projected" — read `GET /formtypes/{type}/projection` to tell those apart (`projected` versus
`notProjected`).

---

## Querying records

```http
GET /formtypes/orders/records?filter=total:42&orderBy=-total&limit=20&offset=0
```

```json
{ "rows": [{ "total": 42 }], "stale": false }
```

Each row carries **exactly the declared columns** — the projection's own bookkeeping is not part of
what a caller reads, because an internal that leaked here would calcify into their contract.

- **`filter`** — repeatable, `column:value`, equality only. The **first** colon separates the two, so
  a value may contain colons. Values are compared as the declared column's type, so `total:42`
  matches a number. A filter that cannot be read is refused rather than dropped: dropping one widens
  the result, and the caller reads rows they asked to exclude.
- **`orderBy`** — comma-separated column list; a leading `-` reverses that column. An ordering key
  that cannot be read is refused for the same reason in the other direction: rows left in an order
  nobody asked for are indistinguishable from ordered ones until a second page disagrees with the
  first.

**The column and the value are refused differently, and the line is between the question and the
answer.** A name that is not a declared column — in a filter or in `orderBy` — is a question the
projection cannot be asked: `400` `/problems/invalid-query`. A value that does not fit the column it
names asks something answerable, and the answer is no rows: `200` with an empty page. Told "no rows"
for a mistyped column name, a caller would go looking at their data when what needs fixing is what
they sent.

The projection's bookkeeping columns are not declared columns, so they cannot be filtered or ordered
by either — rows never carry them, and a caller ordering by a name they can never read back would be
depending on an internal.
- **`limit` / `offset`** — paging is deterministic whether or not the caller orders, because the
  projection's watermark is appended as a final tie-breaker.

**`stale: true` still carries rows.** An answer a caller knows is from an earlier point in the stream
is more useful than a refusal; whether it is good enough is theirs to decide.

A form type with no projection **refuses** rather than reading empty — see below.

---

## Errors

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details. **Branch on `type`**,
which is stable; `title` and `detail` are prose.

```json
{
  "type": "/problems/not-projected",
  "title": "The form type has no projection yet",
  "status": 409,
  "detail": "Form type 'orders' has a declared shape that has not been projected yet; trigger a projection."
}
```

The `not-projected` detail names the one remedy that applies: *trigger a projection* when a shape is
declared but not built yet, *declare field hints first* when nothing is declared — a projection run
in that state projects nothing ([`notProjectedReason`](#projecting) says why).

| Status | `type` | When |
|---|---|---|
| 400 | `/problems/invalid-form-type` | The path named something that cannot be a form type |
| 400 | `/problems/invalid-request` | The request could not be read at all: the body was not JSON, the idempotency key was not a UUID, or a parameter did not bind |
| 400 | `/problems/invalid-query` | A filter or ordering key could not be read, or named a column the declaration does not have |
| 400 | `/problems/invalid-declaration` | A declaration named no table, carried no fields, or declared one twice |
| 400 | `/problems/schema-proposal-invalid` | Schema intelligence is installed and the model responded, but the proposal was not valid JSON, not the requested shape, or named a property never observed in the sampled documents |
| 404 | `/problems/no-such-document` | No document has that id |
| 404 | `/problems/no-declaration` | The form type has no declaration |
| 404 | `/problems/unknown-namespace` | The request named a namespace this host does not serve |
| 409 | `/problems/not-projected` | Records were queried before any projection was built |
| 409 | `/problems/projection-unverified` | A failed rebuild left the projection's integrity unconfirmed |
| 409 | `/problems/declaration-version-conflict` | The declaration in force is not the one the request expected |
| 422 | `/problems/idempotency-key-reused` | The idempotency key already identifies a document of another form type |
| 503 | `/problems/intake-failed` | The document could not be written to the raw store |
| 503 | `/problems/projection-unavailable` | The projection store is not reachable right now |
| 503 | `/problems/schema-proposer-unavailable` | Schema intelligence is installed but the model could not be reached (connection, timeout, auth, rate limit) |

The three refusals a query can meet are deliberately separate, because their remedies are: run a
projection, rebuild it, or retry later.

**A caller's mistake is never a `5xx`.** A form type is validated where it is constructed, so every
route that takes one from the path can meet the same refusal; it is translated in one place rather
than guarded at each endpoint, because the route most likely to be missing a guard is the one added
after the guards were written.

`type` is a relative reference. It identifies the problem stably and resolves against whatever host
is serving, rather than naming a site that may not exist.
