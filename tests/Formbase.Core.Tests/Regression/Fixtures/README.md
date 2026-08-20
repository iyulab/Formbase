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

`eu-procurement-can-social-sample.json` — 25 real light-regime social and specific services award
notices (`can-social` notice type), same source and API. The award-stage sibling of `cn-social`, and
the first fixture where `totalValue` is present in every single sampled notice — none of the five
prior fixtures ever exercises a full 25-of-25 presence. An award notice reports a concluded amount,
and the light regime does not excuse a buyer from disclosing what they actually paid, unlike the
lighter obligations that apply before any award exists.

`eu-procurement-pin-cfc-standard-sample.json` — 25 real prior information notices used as a call for
competition (`pin-cfc-standard` notice type), same source and API. Procedurally distinct from
`pin-only`: a plain PIN is advance market intelligence and a separate contract notice must still
follow, but a PIN used as a call for competition is itself the invitation — suppliers can respond to
it directly. That procedural difference shows up in `totalValue` presence too: 12 of 25 present, 13
absent, a near-even split unlike `pin-only`'s uniform absence or any sibling fixture's skewed ratio.

`eu-procurement-pin-cfc-social-sample.json` — 25 real light-regime prior information notices used as
a call for competition (`pin-cfc-social` notice type), same source and API. The first fixture to
combine two procedural traits the sibling fixtures only ever exercise separately — the lighter
disclosure regime `cn-social` exercises, and the direct-response procedure `pin-cfc-standard`
exercises. Its own real ratio (11 present, 14 absent) is close to but not identical to
`pin-cfc-standard`'s 12/13 — the combination was read from the corpus, not predicted by composing
the two individual traits' fixtures.

`eu-procurement-can-modif-sample.json` — 25 real contract modification notices (`can-modif` notice
type), same source and API. The first fixture in the family to represent a post-award act rather than
a pre-award announcement or a fresh award: every sibling fixture publishes a notice about a contract
that does not yet exist or has just been concluded, while this one amends a contract already awarded
— a real corpus check found it the single most common notice type published in mid-2025 after
`cn-standard` and `can-standard` themselves, ahead of every other type this family samples. Real ratio
18 of 25 present, 7 absent — the same ratio `eu-procurement-veat-sample.json` carries, but for a
different reason: a modification that leaves the contract's value unchanged has nothing to report
there, not an optional disclosure. `buyerName` is present and non-null in all 25, unlike several
sibling fixtures.

`eu-procurement-qu-sy-sample.json` — 25 real qualification system notices (`qu-sy` notice type),
same source and API. The first fixture published under the utilities directive (2014/25/EU) rather
than the classic directive (2014/24/EU) every prior fixture carries — confirmed against the raw
source's `legal-basis` field (`32014L0025`/`32004L0017` on every sampled notice), not inferred from
the notice-type code. A qualification system is not a notice about a specific contract at all: a
utilities buyer maintains an ongoing list of pre-qualified suppliers for future contracts, so none of
the 25 sampled notices carry `totalValue` — a categorically different reason for absence than
`pin-only` (a contract not yet defined) or `can-modif` (a modification that happens not to change the
value): here the concept the field names does not apply. `buyerName` is present and non-null in all
25.

All ten single-notice fixtures keep the same small subset of fields: a notice identifier, notice
type, publication date, buyer name, and (where the source carries it) total contract value with its
currency. Field values are unmodified from the source.

**Attribute pairing (P1's "속성 결합" axis)**: every fixture above already exercises this — value
and currency are declared as sibling fields on the same row, and `eu-procurement-cn-standard-sample.json`
alone carries several distinct currencies (`EUR`, `PLN`, and others) correctly paired per row across
its 25 notices. A dedicated search for the sharper case — different currencies for different lots
*within one notice* — turned up none across roughly 400 real 2025 `cn-standard` notices; the real
pattern is one buyer, one currency, for every lot in a given notice. No fixture invents that case,
since the source never presented it.

**License**: TED notice data is made available for reuse under Commission Decision 2011/833/EU on
the reuse of Commission documents. This fixture is a small derived sample kept for offline,
deterministic testing — not a redistribution of the corpus itself.

**Why it is offline**: the source API rate-limits automated requests, so the test suite never fetches
it live. Regenerating the sample (a different query, a larger one, a different notice type) is a
manual, occasional step — not something a build performs. Use `scripts/fetch-ted-corpus-sample.py`
(repo root) rather than hand-rolling the request each time; it encapsulates the query/field format
these fixtures depend on, including the `--min-date` filter the multi-lot pair needed (pre-eForms
notices, before October 2023, never carry structured per-lot fields).
