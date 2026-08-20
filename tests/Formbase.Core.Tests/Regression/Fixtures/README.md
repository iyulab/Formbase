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

`eu-procurement-pmc-sample.json` — 25 real prior market consultation notices (`pmc` notice type),
same source and API. The first fixture that is not a notice about a contract, an award, or a
modification at all: it announces an informal consultation with the market before any procurement
procedure has been launched (Directive 2014/24/EU Article 40). Every other fixture, even `pin-only`'s
advance intelligence about an undefined contract, is still a step inside a procedure that will
produce one — this one precedes the procedure entirely. Real ratio: 22 of 25 absent, 3 present, a
genuine minority rather than a uniform absence.

`eu-procurement-cn-desg-sample.json` — 25 real design contest notices (`cn-desg` notice type), same
source and API. The first fixture representing a fundamentally different procurement mechanism —
selecting a design through a jury-judged competition (Directive 2014/24/EU Articles 78-82) rather
than a priced bid — instead of a different point in the same contract-award lifecycle every prior
fixture shares. Not the same shape as the contest's own results notice (`can-desg`, evaluated and
rejected during an earlier expansion for being 100% absent): this notice type's real sample is
genuinely mixed, 7 of 25 present, 18 absent.

`eu-procurement-pin-rtl-sample.json` — 25 real prior information notices used to reduce time limits
(`pin-rtl` notice type), same source and API. A third distinct procedural role for the PIN instrument
alongside `pin-only` (advance intelligence, a separate contract notice must still follow) and
`pin-cfc-standard` (the PIN is itself the invitation): publishing this type shortens the minimum time
limits a subsequent tender must allow, if published far enough in advance. Real ratio 16 of 25
present, 9 absent — close to but distinct from `pin-cfc-standard`'s 12/13.

`eu-procurement-corr-sample.json` — 25 real corrigenda (`corr` notice type), same source and API. The
most procedurally different type in the family yet: every other fixture, even `pmc`'s pre-procedure
consultation, announces or reports on some procurement act, while a corrigendum amends the text of a
notice already published — correcting an error rather than advancing a procedure. Whether a given
corrigendum carries `totalValue` depends entirely on what it happens to correct, not any systematic
rule, which is why this population's real ratio swings sharply between sampling windows (a same-day
2025 sample showed 23 of 23 present; a broader mid-2024-onward sample, the one actually committed,
shows 10 of 25) — correcting a buyer's name and correcting a contract value are unrelated events.

`eu-procurement-pin-tran-sample.json` — 25 real prior information notices for public transport
services (`pin-tran` notice type), same source and API. The first fixture published under a
Regulation rather than either procurement Directive every prior fixture carries — confirmed against
the raw source's `legal-basis` field (`32007R1370`, Regulation (EC) No 1370/2007 on public passenger
transport services by rail and road, on every sampled notice), sitting entirely outside the classic/
utilities directive family `qu-sy` already distinguished from. A third all-absent population in the
family (alongside `pin-only` and `qu-sy`), each for a different reason: here, a transport service
concession is compensated and structured differently from a priced contract award.

`eu-business-register-eeig-sample.json` — 25 real registration notices for a European Economic
Interest Grouping (`brin-eeig` notice type), same source and API, but the first fixture in this family
that is not a procurement notice at all. It reports the formation or completion of the liquidation of
an EEIG (Council Regulation (EEC) No 2137/85), published to a business register — a company-law event
with no contract to describe, not a procurement stage this family's other populations merely disclose
less about. `buyerName` here carries the registered grouping's name, reusing this family's field label
across notice families rather than implying a contracting authority exists. All 25 lack `totalValue` —
a fourth all-absent population in the family (alongside `pin-only`, `qu-sy`, `pin-tran`), each absent
for a different reason; here, there is no contract for the field to describe. A related type under the
same `BRIN` document family, `brin-ecs`, was evaluated and rejected earlier for having only a single
real notice ever published; `brin-eeig` carries 661.

`eu-procurement-can-tran-sample.json` — 25 real result notices for public passenger transport services
(`can-tran` notice type), same source and API. The award-stage sibling of `eu-procurement-pin-tran-sample.json`,
sharing its legal basis (confirmed against the raw source: `32007R1370` on every sampled notice). The
planning-stage fixture found `totalValue` absent on all 25 sampled notices; this result-stage sample
shows the absence is not total at this later stage — 6 of 25 carry a real value, 19 do not — the same
lifecycle-stage relationship `cn-standard`/`can-standard` already established for the classic
directive, reproduced here under a different legal instrument entirely.

`eu-procurement-compl-sample.json` — 25 real voluntary completion notices (`compl` notice type), same
source and API. The first fixture representing a procedural stage none of the prior seventeen do: every
sibling fixture is about a contract that does not yet exist, is being announced, awarded, or amended;
this one marks a contract's performance as concluded. Real ratio 24 of 25 present — the highest in the
family alongside `eu-procurement-can-social-sample.json`'s full 25 of 25. The eForms SDK declares this
type's legal basis as `other` only, but the raw source's live `legal-basis` field on the sampled
notices is broader — `32014L0024`, `32014L0025`, and `other` all appear — worth recording as a place
where the SDK's declared catalog and the live corpus's actual tagging disagree, rather than assuming
the catalog is authoritative on this point.

**Discovery method correction (2026-08-20)**: the first fifteen fixtures above were found by probing
individual candidate `notice-type` codes against the live search API — a method that could not tell
whether a rejected or unprobed code was genuinely exhausted, since no catalog was cross-referenced. The
three fixtures immediately above were found instead by reading the eForms SDK's own declared
notice-subtype catalog directly (`notice-types/notice-types.json` in
[OP-TED/eForms-SDK](https://github.com/OP-TED/eForms-SDK), fetched via `gh api
repos/OP-TED/eForms-SDK/contents/notice-types/notice-types.json?ref=<tag>` — the tag must omit a
leading `v`, e.g. `1.15.1` not `v1.15.1`, which 404s). That catalog declares **51 `subTypeId` entries
collapsing to 21 distinct `type` codes** — the "51" this project tracked was always the finer
legal-basis-variant count, not the number of distinct notice-type codes reachable through this
family's `notice-type=<code>` query shape. Cross-referencing this family's touched codes against those
21 accounts for all of them: seventeen adopted (the fifteen single-notice fixtures above, minus `corr`
— see below — plus `brin-eeig`/`can-tran`/`compl`), four rejected (`pin-buyer`, `can-desg`, `brin-ecs`,
and `subco` — see below). **The eForms SDK's declared notice-subtype catalog is therefore exhausted for
this family's discovery method** — not because thoroughness happened to land there, but because there
were only ever 21 codes to find. `corr` (corrigendum, already adopted as this family's 14th fixture)
does not appear anywhere in the SDK's 51-entry catalog at all — the search API accepts it, but it is a
TED-only code outside the eForms notice-subtype vocabulary the SDK declares, most likely because a
corrigendum is modelled in eForms as an amendment to an existing notice rather than as its own subtype.
Any further expansion of this family past its current eighteen fixtures needs a different catalog than
the one this correction relied on — the SDK's 51/21 no longer has anything unexplored in it.

**No broader catalog exists (2026-08-21)**: the open question left by the correction above — whether
the TED Search API's `notice-type` field draws from some published superset that would explain `corr`
— was checked directly rather than left open. The eForms SDK's own published `notice-type` codelist
(the human-readable table the SDK's `notice-types.json` collapses to) is the same 21 codes, `corr`
included nowhere. The Search API's own documentation publishes no field-value catalog and exposes no
machine-readable schema for it (no OpenAPI/Swagger document is served). The live API does validate the
field server-side — an unrecognized value returns a structured "not supported" error naming the field
— but that validation is not backed by any published enumeration. The one candidate source that could
have supplied one, a legacy Standard-Forms-to-eForms correspondence table TED itself publishes, is
explicitly captioned "indicative only, not a technical or reusable mapping" and does not list `corr`
either — corrigenda were never one of the numbered legacy forms. **Conclusion: no official catalog of
the Search API's full `notice-type` vocabulary exists publicly** — `corr` is confirmed to be a
genuinely undocumented, API-internal addition, not a gap in this project's research. Further expansion
of this fixture family by notice-type has no remaining discovery method and is closed; any additional
value beyond `corr` would have to come from a source outside official TED/eForms documentation
entirely, which this project does not pursue speculatively.

**Evaluated and rejected — `subco`** (`subco` notice type, subcontract notice, defence directive only,
documentType `CN`): probed alongside the three adopted types above and found to have only 13 real
notices ever published (no date floor), well short of this family's fixed 25-record sample — the same
insufficient-population reason `brin-ecs` was rejected for, just less extreme (13 real notices instead
of 1). Not adopted.

All eighteen single-notice fixtures keep the same small subset of fields: a notice identifier, notice
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
