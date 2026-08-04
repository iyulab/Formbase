# HTTP API

The host serves the engine over HTTP so a consumer does not have to be a .NET process in the same
container. The library stays what it is: embedding the engine directly remains supported, and this
surface is a packaging of it rather than a replacement.

Everything here follows from one property of the engine — **the raw store is the source of truth and
a declaration is never required to accept a document.** Intake and raw reads therefore work
unconditionally; everything about projections is a state a caller can read rather than a
precondition they must satisfy.

```yaml
POST   /formtypes/{type}/documents     # Accept a document
GET    /documents/{id}                 # Read a stored document
GET    /formtypes/{type}/declaration   # Read the declaration in force
PUT    /formtypes/{type}/declaration   # Put a declaration in force
POST   /formtypes/{type}/projection    # Rebuild the projected table
GET    /formtypes/{type}/projection    # Read projection state
GET    /formtypes/{type}/records       # Query projected records
GET    /openapi/v1.json                # The generated OpenAPI document
```

The OpenAPI document is generated from the endpoints themselves, so it is the machine-readable form
of this page and cannot drift from what is served.

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
schema holding the raw stream, and the MorphDB project holding the projected tables. A host running
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

Deleting a declaration is not on this surface yet.

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
  "unresolvedReferences": ["customerName"]
}
```

**Safe to repeat and safe to retry.** The table is rebuilt from the raw stream rather than updated
in place, so two runs over an unchanged stream leave the same table. A run is bounded to the raw
head it saw when it started, so documents arriving mid-run are left for the next run rather than
landing under a watermark that does not cover them.

`projected: false` means no declaration proposed a schema. That is not an error — documents are
accepted without one — and nothing about the recorded state changes.

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
{ "state": "stale", "projectedWatermark": 8, "rawHead": 12 }
```

**Branch on all four states.**

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
- **`orderBy`** — comma-separated column list; a leading `-` reverses that column.
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
  "detail": "Form type 'orders' has no projection; declare field hints or trigger a projection."
}
```

| Status | `type` | When |
|---|---|---|
| 400 | `/problems/invalid-form-type` | The path named something that cannot be a form type |
| 400 | `/problems/invalid-request` | The body was not JSON, or the idempotency key was not a UUID |
| 400 | `/problems/invalid-query` | A filter or ordering key could not be read |
| 400 | `/problems/invalid-declaration` | A declaration named no table, carried no fields, or declared one twice |
| 404 | `/problems/no-such-document` | No document has that id |
| 404 | `/problems/no-declaration` | The form type has no declaration |
| 404 | `/problems/unknown-namespace` | The request named a namespace this host does not serve |
| 409 | `/problems/not-projected` | Records were queried before any projection was built |
| 409 | `/problems/projection-unverified` | A failed rebuild left the projection's integrity unconfirmed |
| 409 | `/problems/declaration-version-conflict` | The declaration in force is not the one the request expected |
| 503 | `/problems/intake-failed` | The document could not be written to the raw store |
| 503 | `/problems/projection-unavailable` | The projection store is not reachable right now |

The three refusals a query can meet are deliberately separate, because their remedies are: run a
projection, rebuild it, or retry later.

**A caller's mistake is never a `5xx`.** A form type is validated where it is constructed, so every
route that takes one from the path can meet the same refusal; it is translated in one place rather
than guarded at each endpoint, because the route most likely to be missing a guard is the one added
after the guards were written.

`type` is a relative reference. It identifies the problem stably and resolves against whatever host
is serving, rather than naming a site that may not exist.
