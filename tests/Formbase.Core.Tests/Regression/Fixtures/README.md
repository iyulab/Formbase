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

`eu-procurement-pin-only-sample.json` — 25 real prior information notices (`pin-only` notice
type, published as advance market intelligence rather than a call for competition), same source
and API. A third real population: none of the 25 sampled notices carry a contract value at all —
`total-value` is absent from the source JSON entirely, not present-as-null, so this fixture omits
the `totalValue`/`totalValueCurrency` keys outright rather than writing them as `null`. Writing
them as `null` would misrepresent what the source actually said (a field a document never had is a
different fact from an explicit null — see `DocumentMapper.cs`'s `absent` handling). This is the
only one of the three fixtures that exercises the absent-field path with real data end to end; the
other two only exercise the explicit-null path.

`eu-procurement-cn-social-sample.json` — 25 real light-regime social and specific services notices
(`cn-social` notice type), same source and API. A fourth real population, and the first where
presence of `totalValue` is inconsistent *within* the sample rather than uniform across it: 7 of the
25 notices carry a real numeric value, the other 18 lack the key entirely. A light-regime notice is
not required to declare a contract value the way a standard contract notice is, so the corpus itself
answers the field for some notices and never asks the question for others — this is the only one of
the four fixtures where the absent and present cases coexist in one homogeneous notice type instead
of one case dominating the whole sample.

`eu-procurement-veat-sample.json` — 25 real voluntary ex ante transparency notices (`veat` notice
type), same source and API. A fifth real population, and structurally unlike the other four: this
notice type does not announce or award a competitive tender — it is published when a buyer intends
to award a contract without prior competition (a direct award) and voluntarily opens a waiting
period before signing. It shares the `cn-social` fixture's trait of mixed `totalValue` presence
within one type, but at the opposite ratio (18 of 25 present here, versus 7 of 25 present there) and
for a different real-world reason — not a lighter reporting regime, but a value that is sometimes not
finalized or not disclosed before signature.

`eu-procurement-multilot-notice-sample.json` + `eu-procurement-multilot-lot-sample.json` — a paired
sample exercising entity repetition instead of scalar field shape: 7 real 2025 contract notices
(`cn-standard`) and the 31 real lots they carry between them (2 to 8 lots each), each lot's
`noticeId` pointing back at its real parent notice. The other five fixtures sample 2016-era notices,
which predate eForms becoming the mandatory publication format (October 2023) and so never carry the
structured per-lot fields (`BT-137-Lot`, `BT-27-Lot`) this pair needs — this is the first pair in the
family to sample 2025 notices instead. Lot value and currency are unmodified from the source; notices
were filtered to those where the lot-identifier count matched the lot-value count exactly, so no
value had to be paired with a lot by inference.

All five single-notice fixtures keep the same small subset of fields: a notice identifier, notice
type, publication date, buyer name, and (where the source carries it) total contract value with its
currency. Field values are unmodified from the source.

**License**: TED notice data is made available for reuse under Commission Decision 2011/833/EU on
the reuse of Commission documents. This fixture is a small derived sample kept for offline,
deterministic testing — not a redistribution of the corpus itself.

**Why it is offline**: the source API rate-limits automated requests, so the test suite never fetches
it live. Regenerating the sample (a different query, a larger one, a different notice type) is a
manual, occasional step — not something a build performs.
