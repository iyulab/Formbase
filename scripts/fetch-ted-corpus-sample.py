#!/usr/bin/env python3
"""Fetch a small real sample from the EU's public TED notice search API and shape it into the
fixture format used by tests/Formbase.Core.Tests/Regression/Fixtures/.

This is a manual, occasional tool — regression tests never call it, and CI never runs it. Run it
only when adding a new notice-type fixture to that directory. See Fixtures/README.md for the
fixture family this feeds and why the harness stays offline otherwise.

Usage:
    python3 scripts/fetch-ted-corpus-sample.py cn-standard fixtures/eu-procurement-cn-standard-sample.json
    python3 scripts/fetch-ted-corpus-sample.py cn-social out.json --limit 25 --page 1
    python3 scripts/fetch-ted-corpus-sample.py cn-standard out.json --min-date 20250101  # eForms-era

The API (api.ted.europa.eu/v3/notices/search, no authentication) does not publish a field list —
an unsupported field VALUE returns a clear "not supported" error, but an unsupported field NAME is
silently ignored. Confirm a candidate notice-type code first with --probe before trusting a fetch.
"""

import argparse
import json
import subprocess
import sys

API_URL = "https://api.ted.europa.eu/v3/notices/search"

# The five scalar fields every fixture in this family carries. Do not add fields speculatively —
# each addition is another thing that can come back None for a given notice-type and silently
# skew a sample; add one only when a new fixture actually needs it.
DEFAULT_FIELDS = ["ND", "notice-type", "publication-date", "buyer-name", "total-value", "total-value-cur"]


def fetch(notice_type, fields, limit, page, min_date=None):
    """Shells out to curl rather than using urllib: this API is HTTPS-only, and curl resolves the
    OS certificate store correctly on every platform this is likely to run on, where Python's own
    ssl module sometimes cannot (observed on a stock MSYS2/UCRT64 Python with no CA bundle wired
    up — the fetch fails with CERTIFICATE_VERIFY_FAILED there even though curl works fine)."""
    query = f"notice-type={notice_type}"
    if min_date:
        query += f" AND publication-date>={min_date}"
    body = json.dumps({"query": query, "fields": fields, "limit": limit, "page": page})
    result = subprocess.run(
        ["curl", "-s", "-m", "30", API_URL, "-X", "POST", "-H", "Content-Type: application/json", "-d", body],
        capture_output=True, check=True,
    )
    # Decode explicitly as UTF-8: the response carries non-ASCII buyer names, and some platforms
    # (observed: Windows with a non-UTF-8 console code page) default subprocess text decoding to
    # something narrower that then fails on them.
    return json.loads(result.stdout.decode("utf-8"))


def buyer_name(notice):
    """The source gives buyer-name as {language: [names]} — one representative language's first
    name, matching every existing fixture's convention (see Fixtures/README.md)."""
    names_by_lang = notice.get("buyer-name")
    if not names_by_lang:
        return None
    for names in names_by_lang.values():
        if names:
            return names[0]
    return None


def publication_date(notice):
    """The source gives a bare date + offset ('2016-08-19+02:00') — every fixture stores full
    ISO-8601 with a zeroed time component instead, so pin this transform in one place."""
    raw = notice.get("publication-date")
    if raw is None or "+" not in raw:
        return raw
    date_part, offset = raw.split("+", 1)
    return f"{date_part}T00:00:00+{offset}"


def to_fixture_record(notice):
    record = {
        "noticeId": notice.get("ND"),
        "noticeType": notice.get("notice-type"),
        "publicationDate": publication_date(notice),
        "buyerName": buyer_name(notice),
    }
    # Omit totalValue/totalValueCurrency outright when the source never carried the key — do not
    # write JSON null for a field the document never had. See DocumentMapper.cs's absent-vs-null
    # distinction and Fixtures/README.md's pin-only note for why this matters.
    if "total-value" in notice:
        record["totalValue"] = notice.get("total-value")
    if "total-value-cur" in notice:
        record["totalValueCurrency"] = notice.get("total-value-cur")
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("notice_type", help="TED notice-type code, e.g. cn-standard, pin-only, veat")
    parser.add_argument("output", nargs="?", help="Path to write the fixture JSON to (omit with --probe)")
    parser.add_argument("--limit", type=int, default=25, help="Sample size (default: 25, matching every existing fixture)")
    parser.add_argument("--page", type=int, default=1, help="Page of results to fetch (default: 1)")
    parser.add_argument("--min-date", help="YYYYMMDD floor, e.g. 20250101 — needed for lot-level BT fields, which pre-eForms (before 2023-10) notices never carry")
    parser.add_argument("--probe", action="store_true", help="Just fetch and print a presence/absence summary; do not write a fixture file")
    args = parser.parse_args()

    result = fetch(args.notice_type, DEFAULT_FIELDS, args.limit, args.page, args.min_date)
    if "notices" not in result:
        print(f"error: {result.get('message', result)}", file=sys.stderr)
        sys.exit(1)

    notices = result["notices"]
    present = sum(1 for n in notices if "total-value" in n)
    print(f"{args.notice_type}: fetched {len(notices)} of {result.get('totalNoticeCount')} total "
          f"-- totalValue present {present}, absent {len(notices) - present}", file=sys.stderr)

    if args.probe:
        return

    if not args.output:
        parser.error("output path is required unless --probe is given")

    records = [to_fixture_record(n) for n in notices]
    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(records, f, indent=2, ensure_ascii=False)
    print(f"wrote {len(records)} records to {args.output}", file=sys.stderr)


if __name__ == "__main__":
    main()
