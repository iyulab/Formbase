#!/usr/bin/env python3
"""automation.md Section 6 pilot: "given a form's declared Status and preconditions, deduce what
is currently possible" -- Section 6 is explicitly marked unverified there ("아직 계산해 본 기록이
없습니다"). This script runs the smallest real version of that computation: sample real result-
stage (`can-standard`) TED notices, follow the TED Search API's own `procedure-identifier` field
(a UUID TED itself assigns and reuses across every notice belonging to the same real procurement
procedure) back to see whether a competition-stage (`cn-standard`) notice -- the precondition the
eForms SDK's `formType` flow (planning -> competition -> result -> ...) declares -- actually
exists for it.

This is a manual, occasional research tool for P3-h -- never run by CI or the regression suite,
same posture as fetch-ted-corpus-sample.py / eforms-isomorphism.py / eforms-semantic-judge.py.

Usage:
    python3 scripts/eforms-procedure-chain.py --limit 20 --min-date 20250101 --out chain-result.json

The API (api.ted.europa.eu/v3/notices/search, no authentication) rate-limits automated requests --
this samples a bounded number of procedures per run, matching the offline-corpus posture the
sibling scripts already follow (see Fixtures/README.md "Why it is offline").
"""
import argparse
import json
import subprocess
import sys
import time

API_URL = "https://api.ted.europa.eu/v3/notices/search"


def query(q, fields, limit=25, page=1, retries=2):
    body = json.dumps({"query": q, "fields": fields, "limit": limit, "page": page})
    last_err = None
    for attempt in range(retries + 1):
        result = subprocess.run(
            ["curl", "-s", "-m", "30", API_URL, "-X", "POST", "-H", "Content-Type: application/json", "-d", body],
            capture_output=True, check=True,
        )
        raw = result.stdout.decode("utf-8")
        try:
            return json.loads(raw)
        except json.JSONDecodeError as exc:
            # Observed: an occasional empty/non-JSON body on this API, unrelated to the query
            # itself (no message, no error field -- looks like a transient upstream hiccup, not a
            # malformed request, since the exact same query succeeds on retry).
            last_err = exc
            time.sleep(2)
    raise RuntimeError(f"query failed after {retries + 1} attempts: {last_err} (query={q!r})")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--result-type", default="can-standard",
                     help="Result-stage notice type to sample (default: can-standard)")
    ap.add_argument("--precondition-type", default="cn-standard",
                     help="Competition-stage notice type the flow declares as a precondition "
                          "(default: cn-standard)")
    ap.add_argument("--limit", type=int, default=20)
    ap.add_argument("--min-date", default="20250101")
    ap.add_argument("--out", default="chain-result.json")
    args = ap.parse_args()

    sample = query(
        f"notice-type={args.result_type} AND publication-date>={args.min_date}",
        ["ND", "notice-type", "publication-date", "procedure-identifier"],
        limit=args.limit)
    notices = sample.get("notices", [])
    if not notices:
        print(f"error: {sample.get('message', sample)}", file=sys.stderr)
        sys.exit(1)
    print(f"sampled {len(notices)} of {sample.get('totalNoticeCount')} real {args.result_type} notices "
          f"(publication-date >= {args.min_date})", file=sys.stderr)

    procedures = []
    for n in notices:
        pid = n.get("procedure-identifier")
        if not pid:
            procedures.append({"resultNoticeId": n.get("ND"), "procedureId": None,
                                "classification": "no-procedure-identifier"})
            continue

        chain = query(f"procedure-identifier={pid}",
                       ["ND", "notice-type", "publication-date"], limit=25)
        chain_notices = chain.get("notices", [])
        types_in_chain = [c.get("notice-type") for c in chain_notices]

        has_precondition = args.precondition_type in types_in_chain
        total = chain.get("totalNoticeCount", len(chain_notices))

        if total > 20:
            # A single procedure-identifier fanning out to dozens/hundreds of notices is not a
            # one-competition-one-result procedure at all -- observed real case: a utility's
            # continuous Dynamic Purchasing System / framework, where one competition covers many
            # individual award notices over months. The simple "does a precondition notice exist"
            # question doesn't fit this shape; flag it rather than silently counting it either way.
            classification = "high-fanout-framework"
        elif has_precondition:
            classification = "precondition-satisfied"
        else:
            classification = "precondition-missing"

        procedures.append({
            "resultNoticeId": n.get("ND"),
            "procedureId": pid,
            "chainSize": total,
            "chainTypes": types_in_chain,
            "classification": classification,
        })

    counts = {}
    for p in procedures:
        counts[p["classification"]] = counts.get(p["classification"], 0) + 1

    result = {
        "result_type": args.result_type,
        "precondition_type": args.precondition_type,
        "n_sampled": len(notices),
        "classification_counts": counts,
        "procedures": procedures,
    }
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=2)

    print(f"classification counts: {counts}")
    print(f"written: {args.out}")


if __name__ == "__main__":
    main()
