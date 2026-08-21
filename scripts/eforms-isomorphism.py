#!/usr/bin/env python3
"""Reproduce and extend automation.md Section 4's structural precondition (composition match)
over the eForms SDK's declared notice-type visualizations.

This is a manual, occasional research tool for P3-h (ontology layer dogfooding) — regression tests
never call it, and CI never runs it, the same posture as fetch-ted-corpus-sample.py. Run it only
when re-verifying or extending the Section 4 pilot. See
tests/Formbase.Core.Tests/Regression/Fixtures/README.md for the sibling TED-instance corpus this
one complements: that one samples filled *documents*, this one reads the eForms SDK's *declared
structure* (notice-types/*.json + fields/fields.json) — the two halves Formology's philosophy
requires to be kept separate (declared structure vs. filled document).

The corpus (fields.json + 51 notice-type declarations, ~2.5MB) is not committed — regenerate it
with `fetch`, same "derived fixtures only, raw corpus stays local" rule the TED sample fetcher
follows.

Usage:
    python3 scripts/eforms-isomorphism.py fetch --out-dir /tmp/eforms-sdk
    python3 scripts/eforms-isomorphism.py analyze --corpus-dir /tmp/eforms-sdk

`fetch` targets a specific SDK release tag (default: latest non-prerelease release) via GitHub's
raw content CDN, no authentication required. `analyze` walks every notice-type's declared content
tree and reports:
  - the automation.md Section 4 structural census (distinct sections/fields, recurrence)
  - POSITIVE candidates: the same declared group id with a *different* field-composition variant
    across the forms it appears in (same section, different optional-field subset per form) —
    Section 4's real "similar-but-not-identical, still one shared type" case
  - NEGATIVE controls: different group ids with *different* declared businessEntityId whose field
    composition happens to overlap by chance — Section 4's "looks similar, is not" case, the one
    automation.md warns must not be auto-merged
"""
import argparse
import glob
import json
import os
import subprocess
import sys
from collections import defaultdict

RAW_BASE = "https://raw.githubusercontent.com/OP-TED/eForms-SDK"


def latest_release_tag():
    result = subprocess.run(
        ["gh", "api", "repos/OP-TED/eForms-SDK/releases", "--jq",
         '[.[] | select(.prerelease == false)][0].tag_name'],
        capture_output=True, check=True, text=True,
    )
    return result.stdout.strip()


def curl(url, out_path):
    subprocess.run(["curl", "-sL", "-o", out_path, url], check=True)


def cmd_fetch(args):
    tag = args.tag or latest_release_tag()
    os.makedirs(os.path.join(args.out_dir, "notice-types"), exist_ok=True)

    print(f"fetching eForms SDK {tag} into {args.out_dir}", file=sys.stderr)
    curl(f"{RAW_BASE}/{tag}/fields/fields.json", os.path.join(args.out_dir, "fields.json"))

    listing = subprocess.run(
        ["gh", "api", f"repos/OP-TED/eForms-SDK/contents/notice-types?ref={tag}", "--jq", ".[].name"],
        capture_output=True, check=True, text=True,
    )
    # The 51 forms this project tracks are every file in the directory except the notice-types.json
    # index itself (52 files total at 1.15.1 — 40 numeric + CEI/E1-E6/T01-T02/X01-X02).
    names = [n for n in listing.stdout.splitlines() if n and n != "notice-types.json"]
    for name in names:
        curl(f"{RAW_BASE}/{tag}/notice-types/{name}", os.path.join(args.out_dir, "notice-types", name))
    print(f"wrote fields.json + {len(names)} notice-type files (tag {tag})", file=sys.stderr)


def walk(node, form_id, group_fields_per_form, group_be):
    ctype = node.get("contentType")
    if ctype == "group":
        gid = node["id"]
        be = node.get("businessEntityId")
        if be:
            group_be[gid] = be
        own_fields = set()
        for child in node.get("content", []):
            if child.get("contentType") == "field":
                own_fields.add(child["id"])
            elif child.get("contentType") == "group":
                walk(child, form_id, group_fields_per_form, group_be)
                own_fields |= group_fields_per_form[(child["id"], form_id)]
        group_fields_per_form[(gid, form_id)] = own_fields


def load_corpus(corpus_dir):
    group_fields_per_form = {}
    group_be = {}
    forms_by_field = defaultdict(set)
    for path in sorted(glob.glob(os.path.join(corpus_dir, "notice-types", "*.json"))):
        form_id = os.path.splitext(os.path.basename(path))[0]
        doc = json.load(open(path, encoding="utf-8"))

        def walk_fields(node):
            if node.get("contentType") == "field":
                forms_by_field[node["id"]].add(form_id)
            for child in node.get("content", []):
                walk_fields(child)

        for top in doc.get("content", []):
            walk(top, form_id, group_fields_per_form, group_be)
            walk_fields(top)

    by_group = defaultdict(dict)
    for (gid, form_id), fields in group_fields_per_form.items():
        by_group[gid][form_id] = fields
    return by_group, group_be, forms_by_field


def jaccard(a, b):
    if not a or not b:
        return 0.0
    return len(a & b) / len(a | b)


def cmd_analyze(args):
    by_group, group_be, forms_by_field = load_corpus(args.corpus_dir)
    n_forms = len(set(f for per_form in by_group.values() for f in per_form))

    recurrence = defaultdict(int)
    for per_form in by_group.values():
        n = len(per_form)
        if n == 1:
            recurrence["unique to 1"] += 1
        elif n >= n_forms:
            recurrence["all forms"] += 1
        elif n >= n_forms / 2:
            recurrence[">=half"] += 1
        else:
            recurrence[">1"] += 1

    positives = []
    for gid, per_form in by_group.items():
        variants = defaultdict(list)
        for form_id, fields in per_form.items():
            variants[frozenset(fields)].append(form_id)
        distinct = list(variants.keys())
        for i in range(len(distinct)):
            for j in range(i + 1, len(distinct)):
                a, b = set(distinct[i]), set(distinct[j])
                if len(a) < args.min_fields or len(b) < args.min_fields:
                    continue
                score = jaccard(a, b)
                if 0 < score < 1:
                    positives.append({
                        "groupId": gid, "businessEntityId": group_be.get(gid),
                        "fieldsA": sorted(a), "fieldsB": sorted(b), "jaccard": round(score, 3),
                    })

    union_fields = {gid: set().union(*per_form.values()) for gid, per_form in by_group.items()}
    gids = sorted(g for g in union_fields if len(union_fields[g]) >= args.min_fields)
    negatives = []
    for i in range(len(gids)):
        for j in range(i + 1, len(gids)):
            a, b = gids[i], gids[j]
            be_a, be_b = group_be.get(a), group_be.get(b)
            # Both sides must carry a declared businessEntityId — a group with none is a purely
            # structural container, not "labelled different" (see eforms-semantic-judge.py for the
            # pilot run that found this the hard way: mixing in None-vs-labelled pairs let several
            # identical-field-set pairs masquerade as negative controls).
            if be_a is None or be_b is None or be_a == be_b:
                continue
            score = jaccard(union_fields[a], union_fields[b])
            if score >= args.negative_threshold:
                negatives.append({
                    "groupIdA": a, "groupIdB": b,
                    "businessEntityIdA": group_be.get(a), "businessEntityIdB": group_be.get(b),
                    "fieldsA": sorted(union_fields[a]), "fieldsB": sorted(union_fields[b]),
                    "jaccard": round(score, 3),
                })

    positives.sort(key=lambda c: -c["jaccard"])
    negatives.sort(key=lambda c: -c["jaccard"])

    result = {
        "forms_analysed": n_forms,
        "distinct_declared_sections": len(by_group),
        "section_recurrence": dict(recurrence),
        "distinct_declared_fields_used": len(forms_by_field),
        "fields_appearing_in_gt1_form": sum(1 for forms in forms_by_field.values() if len(forms) > 1),
        "positive_candidates_same_group_different_variant": len(positives),
        "negative_controls_different_businessEntity_high_overlap": len(negatives),
        "positives": positives,
        "negatives": negatives,
    }

    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=2)

    print(f"forms analysed: {n_forms}")
    print(f"distinct declared sections (groups): {len(by_group)}")
    print(f"section recurrence: {dict(recurrence)}")
    print(f"distinct declared fields used: {len(forms_by_field)}   "
          f"appearing in >1 form: {result['fields_appearing_in_gt1_form']}")
    print(f"positive candidates (same group id, different composition variant): {len(positives)}")
    print(f"negative controls (different businessEntity, >= {args.negative_threshold:.0%} overlap): {len(negatives)}")
    print(f"written: {args.out}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_fetch = sub.add_parser("fetch", help="Download fields.json + all notice-type declarations")
    p_fetch.add_argument("--out-dir", required=True)
    p_fetch.add_argument("--tag", help="SDK release tag, e.g. 1.15.1 (default: latest non-prerelease)")
    p_fetch.set_defaults(func=cmd_fetch)

    p_analyze = sub.add_parser("analyze", help="Compute the composition census + candidate pairs")
    p_analyze.add_argument("--corpus-dir", required=True)
    p_analyze.add_argument("--out", default="isomorphism-result.json")
    p_analyze.add_argument("--min-fields", type=int, default=4,
                            help="Ignore groups/pairs with fewer fields than this (default: 4 — "
                                 "a 1-2 field group's overlap is not a meaningful judgment case)")
    p_analyze.add_argument("--negative-threshold", type=float, default=0.5,
                            help="Minimum Jaccard overlap for a cross-businessEntity pair to count "
                                 "as a negative control (default: 0.5)")
    p_analyze.set_defaults(func=cmd_analyze)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
