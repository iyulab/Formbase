# Fixture provenance

`eu-procurement-cn-standard-sample.json` — 25 real public-sector procurement notices (`cn-standard`
notice type), sampled from the EU's public Tenders Electronic Daily (TED) notice repository via its
public search API (`api.ted.europa.eu`, no authentication required).

`eu-procurement-can-standard-sample.json` — 25 real contract award notices (`can-standard` notice
type), same source and API. A contract award notice is published after a contract is signed, so its
`totalValue` is the awarded amount rather than the pre-award estimate `cn-standard` carries — a
different real population, not a copy of the same shape. It also surfaces a corpus quirk worth
keeping on record: 15 of the 25 buyer names in this sample have no English translation in the
source's multi-language name map, so `buyerName` is legitimately absent for most of the sample —
not a fixture bug.

Both fixtures keep the same small subset of fields: a notice identifier, notice type, publication
date, buyer name, and total contract value with its currency. Field values are unmodified from the
source.

**License**: TED notice data is made available for reuse under Commission Decision 2011/833/EU on
the reuse of Commission documents. This fixture is a small derived sample kept for offline,
deterministic testing — not a redistribution of the corpus itself.

**Why it is offline**: the source API rate-limits automated requests, so the test suite never fetches
it live. Regenerating the sample (a different query, a larger one, a different notice type) is a
manual, occasional step — not something a build performs.
