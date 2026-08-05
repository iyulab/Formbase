#!/usr/bin/env bash
#
# Holds docker-compose.yml to the claim that makes it worth deploying: a document written to this
# instance is still there after the process that accepted it is gone.
#
# A liveness check cannot make that claim. Leave Formbase__Store out of the compose file and the
# host starts on its in-process stores, answers every request, reports 200 on the same endpoints —
# and loses every document at the next restart. The only difference visible from outside is what
# survives, so that is what this asks for.
set -euo pipefail

cd "$(dirname "$0")/.."

FORMBASE_PORT="${FORMBASE_PORT:-18080}"
MORPHDB_PORT="${MORPHDB_PORT:-18081}"
PROJECT_ID="${FORMBASE_MORPHDB_PROJECT_ID:-0197c0de-0000-4000-8000-000000000001}"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-formbase-durability-check}"
export FORMBASE_PORT MORPHDB_PORT FORMBASE_MORPHDB_PROJECT_ID="$PROJECT_ID" COMPOSE_PROJECT_NAME

BASE="http://127.0.0.1:${FORMBASE_PORT}"
FORM_TYPE="durability-check"

cleanup() {
  # Volumes too: a check that leaves its state behind passes the second time for the wrong reason.
  docker compose down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

wait_for_host() {
  local i=0
  until curl -sf -o /dev/null "${BASE}/settings"; do
    i=$((i + 1))
    [ "$i" -lt 90 ] || {
      docker compose logs --tail 50 formbase >&2 || true
      fail "the host did not answer at ${BASE} within 180s"
    }
    sleep 2
  done
}

echo "==> standing the instance up"
cleanup
docker compose up -d --build

echo "==> waiting for the host"
wait_for_host

echo "==> asserting the composed profile"
settings=$(curl -sf "${BASE}/settings")
case "$settings" in
  *'"durable":true'*) : ;;
  *) fail "the instance did not compose as durable — /settings answered: ${settings}" ;;
esac

echo "==> writing a document"
key=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || python -c 'import uuid;print(uuid.uuid4())')
accepted=$(curl -sf -X POST "${BASE}/formtypes/${FORM_TYPE}/documents" \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: ${key}" \
  -d '{"survives":"a restart"}')

# The idempotency key becomes the document's identity, so the id to read back is the key itself —
# no parsing of the response, and nothing to get wrong about which id was stored.
case "$accepted" in
  *"$key"*) : ;;
  *) fail "the accepted document did not answer with the id the key names: ${accepted}" ;;
esac

echo "==> restarting the host"
docker compose restart formbase
wait_for_host

echo "==> reading the document back"
stored=$(curl -sf "${BASE}/documents/${key}") \
  || fail "the document written before the restart is gone — this instance is not durable"

case "$stored" in
  *'"survives":"a restart"'*) : ;;
  *) fail "the document read back is not the one that was written: ${stored}" ;;
esac

echo "PASS: the document outlived the process that accepted it"
