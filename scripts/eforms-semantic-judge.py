#!/usr/bin/env python3
"""automation.md Section 4 pilot: can a live LLM, seeing ONLY two blind lists of human-readable
field labels (no group id, no businessEntityId, no BT-code), correctly judge whether they describe
the same underlying concept?

Ground truth comes from the eForms SDK's OWN declarations, which this script never shows the
model — see eforms-isomorphism.py's docstring for how the positive/negative pools are built:
  - POSITIVE pairs: two field-composition *variants* of the exact same declared group id (so same
    businessEntityId too) as it appears in different forms. Section 4's real "similar but not
    identical, still one shared type" case.
  - NEGATIVE pairs: two different group ids with different businessEntityId whose composition
    happens to overlap by chance. Section 4's "looks similar, actually different" case — the one
    the methodology warns must not be auto-merged.

This is a manual, occasional research tool for P3-h — never run by CI or the regression suite, the
same posture as fetch-ted-corpus-sample.py and eforms-isomorphism.py.

Requires a corpus directory already populated by:
    python3 scripts/eforms-isomorphism.py fetch --out-dir <dir>
and a reachable OpenAI-compatible endpoint via FORMBASE_LLM_ENDPOINT / FORMBASE_LLM_API_KEY /
FORMBASE_LLM_MODEL (read from the environment, or from --env-file — this repo keeps them in the
umbrella's .env.local, gitignored, see HD-05 / D-C17 in claudedocs/HANDOFF.md).

Usage:
    python3 scripts/eforms-semantic-judge.py --corpus-dir /tmp/eforms-sdk \\
        --env-file ../.env.local --n-positive 10 --n-negative 10
"""
import argparse
import glob
import json
import os
import random
import re
import time
import urllib.request
from collections import defaultdict

PROMPT_TEMPLATE = """You are reviewing two sections from possibly different business forms. Each \
section is described only by the human-readable labels of the data fields it contains (the field \
order below is randomized and carries no meaning).

Section A fields:
{a_list}

Section B fields:
{b_list}

Question: based only on these field labels, do Section A and Section B represent the SAME \
underlying real-world concept (e.g. the same kind of section, possibly with a different subset of \
optional fields present), or DIFFERENT concepts that merely share some field labels by coincidence?

Respond with ONLY a JSON object, no other text, in this exact shape:
{{"same_concept": true or false, "confidence": a number from 0 to 1, "reason": "one short sentence"}}
"""


def load_env_file(path):
    if not path or not os.path.exists(path):
        return {}
    raw = {}
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            raw[key.strip()] = value.strip()
    return {k: re.sub(r"\$\{(\w+)\}", lambda m: raw.get(m.group(1), ""), v) for k, v in raw.items()}


def get_llm_config(env_file):
    env = dict(load_env_file(env_file))
    env.update(os.environ)  # real environment wins over the file
    endpoint, api_key, model = (env.get(k) for k in
                                 ("FORMBASE_LLM_ENDPOINT", "FORMBASE_LLM_API_KEY", "FORMBASE_LLM_MODEL"))
    if not (endpoint and api_key and model):
        raise SystemExit("FORMBASE_LLM_ENDPOINT/_API_KEY/_MODEL not found in the environment or --env-file")
    return endpoint.rstrip("/"), api_key, model


def walk(node, form_id, group_fields_per_form, group_be):
    if node.get("contentType") != "group":
        return
    gid = node["id"]
    if node.get("businessEntityId"):
        group_be[gid] = node["businessEntityId"]
    own_fields = set()
    for child in node.get("content", []):
        if child.get("contentType") == "field":
            own_fields.add(child["id"])
        elif child.get("contentType") == "group":
            walk(child, form_id, group_fields_per_form, group_be)
            own_fields |= group_fields_per_form[(child["id"], form_id)]
    group_fields_per_form[(gid, form_id)] = own_fields


def load_corpus(corpus_dir):
    field_names = {f["id"]: f.get("name", f["id"])
                   for f in json.load(open(os.path.join(corpus_dir, "fields.json"), encoding="utf-8"))["fields"]}
    group_fields_per_form, group_be = {}, {}
    for path in sorted(glob.glob(os.path.join(corpus_dir, "notice-types", "*.json"))):
        form_id = os.path.splitext(os.path.basename(path))[0]
        doc = json.load(open(path, encoding="utf-8"))
        for top in doc.get("content", []):
            walk(top, form_id, group_fields_per_form, group_be)
    by_group = defaultdict(dict)
    for (gid, form_id), fields in group_fields_per_form.items():
        by_group[gid][form_id] = fields
    return field_names, by_group, group_be


def jaccard(a, b):
    return len(a & b) / len(a | b) if a and b else 0.0


def build_positive_pairs(by_group, group_be, min_fields, lo, hi):
    pairs = []
    for gid, per_form in by_group.items():
        variants = defaultdict(list)
        for form_id, fields in per_form.items():
            variants[frozenset(fields)].append(form_id)
        distinct = list(variants.keys())
        for i in range(len(distinct)):
            for j in range(i + 1, len(distinct)):
                a, b = set(distinct[i]), set(distinct[j])
                if len(a) < min_fields or len(b) < min_fields:
                    continue
                score = jaccard(a, b)
                if lo <= score <= hi:
                    pairs.append({"kind": "positive", "groupId": gid, "businessEntityId": group_be.get(gid),
                                  "fieldsA": sorted(a), "fieldsB": sorted(b), "jaccard": round(score, 3)})
    return pairs


def build_negative_pairs(by_group, group_be, min_fields, threshold):
    union_fields = {gid: set().union(*per_form.values()) for gid, per_form in by_group.items()}
    gids = sorted(g for g in union_fields if len(union_fields[g]) >= min_fields)
    pairs = []
    for i in range(len(gids)):
        for j in range(i + 1, len(gids)):
            a, b = gids[i], gids[j]
            be_a, be_b = group_be.get(a), group_be.get(b)
            # Requiring BOTH sides to carry a declared businessEntityId is not optional: a group
            # with none is a purely structural container, not "labelled different" — treating
            # `None != "LotResult"` as a semantic conflict was this script's own bug (found in the
            # cycle-165 pilot run: 11/20 sampled negatives were jaccard=1.0, i.e. IDENTICAL field
            # sets, mislabeled "different concept" only because one side had no businessEntityId at
            # all). Ground truth this weak cannot support any conclusion about the model.
            if be_a is None or be_b is None or be_a == be_b:
                continue
            score = jaccard(union_fields[a], union_fields[b])
            if score >= threshold:
                pairs.append({"kind": "negative", "groupIdA": a, "groupIdB": b,
                              "businessEntityIdA": group_be.get(a), "businessEntityIdB": group_be.get(b),
                              "fieldsA": sorted(union_fields[a]), "fieldsB": sorted(union_fields[b]),
                              "jaccard": round(score, 3)})
    return pairs


def anonymize(field_names, field_ids, rng):
    labels = [field_names.get(fid, fid) for fid in field_ids]
    rng.shuffle(labels)
    return labels


def ask_llm(endpoint, api_key, model, prompt, timeout):
    body = json.dumps({"model": model, "messages": [{"role": "user", "content": prompt}], "temperature": 0.1}).encode()
    req = urllib.request.Request(
        f"{endpoint}/v1/chat/completions", data=body, method="POST",
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))["choices"][0]["message"]["content"]


def parse_verdict(raw_text):
    match = re.search(r"\{.*\}", raw_text, re.DOTALL)
    if not match:
        return None
    try:
        obj = json.loads(match.group(0))
    except json.JSONDecodeError:
        return None
    return obj if "same_concept" in obj else None


def ask_llm_with_retry(endpoint, api_key, model, prompt, timeout, retries=1):
    """One retry on a timeout or an unparseable response — a 27B model's latency is variable
    enough (observed: single calls from 5s to 116s) that a lone slow response should not cost the
    whole sample point, and a retry is cheap next to the pilot's total wall-clock budget."""
    last_error = None
    for attempt in range(retries + 1):
        try:
            raw = ask_llm(endpoint, api_key, model, prompt, timeout)
        except Exception as exc:  # noqa: BLE001 - surfaced to the caller if every attempt fails
            last_error = exc
            continue
        verdict = parse_verdict(raw)
        if verdict is not None:
            return raw, verdict, None
        last_error = ValueError(f"unparseable response: {raw[:200]!r}")
    return None, None, last_error


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--corpus-dir", required=True)
    ap.add_argument("--env-file")
    ap.add_argument("--n-positive", type=int, default=10)
    ap.add_argument("--n-negative", type=int, default=10)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--min-fields", type=int, default=4)
    ap.add_argument("--positive-jaccard-range", type=float, nargs=2, default=[0.3, 0.9])
    ap.add_argument("--negative-threshold", type=float, default=0.5)
    ap.add_argument("--timeout", type=int, default=180)
    ap.add_argument("--out", default="judgment-result.json")
    args = ap.parse_args()

    endpoint, api_key, model = get_llm_config(args.env_file)
    print(f"LLM: {endpoint} model={model}", flush=True)

    field_names, by_group, group_be = load_corpus(args.corpus_dir)
    lo, hi = args.positive_jaccard_range
    positives = build_positive_pairs(by_group, group_be, args.min_fields, lo, hi)
    negatives = build_negative_pairs(by_group, group_be, args.min_fields, args.negative_threshold)
    print(f"positive pool: {len(positives)}   negative pool: {len(negatives)}", flush=True)

    rng = random.Random(args.seed)
    rng.shuffle(positives)
    rng.shuffle(negatives)
    sample = positives[:args.n_positive] + negatives[:args.n_negative]
    rng.shuffle(sample)

    results = []
    for idx, pair in enumerate(sample, start=1):
        a_labels = anonymize(field_names, pair["fieldsA"], rng)
        b_labels = anonymize(field_names, pair["fieldsB"], rng)
        prompt = PROMPT_TEMPLATE.format(
            a_list="\n".join(f"- {l}" for l in a_labels),
            b_list="\n".join(f"- {l}" for l in b_labels))
        t0 = time.time()
        raw, verdict, error = ask_llm_with_retry(endpoint, api_key, model, prompt, args.timeout)
        if error is not None:
            print(f"[{idx}/{len(sample)}] ERROR: {error}", flush=True)
        elapsed = round(time.time() - t0, 1)

        expected_same = pair["kind"] == "positive"
        predicted_same = verdict.get("same_concept") if verdict else None
        correct = (predicted_same == expected_same) if verdict else None

        results.append({**pair, "expected_same_concept": expected_same, "raw_response": raw,
                         "verdict": verdict, "correct": correct, "elapsed_sec": elapsed})
        status = "?" if correct is None else ("OK" if correct else "WRONG")
        print(f"[{idx}/{len(sample)}] {pair['kind']:8s} jaccard={pair['jaccard']:.2f} "
              f"expected={expected_same} predicted={predicted_same} [{status}] ({elapsed}s)", flush=True)

    scored = [r for r in results if r["correct"] is not None]
    pos_scored = [r for r in scored if r["kind"] == "positive"]
    neg_scored = [r for r in scored if r["kind"] == "negative"]

    def rate(rs):
        return sum(1 for r in rs if r["correct"]) / len(rs) if rs else None

    summary = {
        "model": model, "n_sampled": len(sample), "n_scored": len(scored),
        "n_errors": len(sample) - len(scored),
        "overall_accuracy": rate(scored),
        "positive_class_accuracy_recall": rate(pos_scored),
        "negative_class_accuracy_specificity": rate(neg_scored),
        "results": results,
    }
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(summary, fh, indent=2)

    print(flush=True)
    print(f"overall accuracy: {summary['overall_accuracy']}")
    print(f"positive-class accuracy (recall on 'same concept'): {summary['positive_class_accuracy_recall']}")
    print(f"negative-class accuracy (specificity on 'different concept'): {summary['negative_class_accuracy_specificity']}")
    print(f"written: {args.out}")


if __name__ == "__main__":
    main()
