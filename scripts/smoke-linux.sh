#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

FAKE=.smoke-hosts
COMPOSE=smoke-linux.compose.yml
ALIAS=smoke-target.local

cleanup() {
  docker compose -f "$COMPOSE" down -v --remove-orphans >/dev/null 2>&1 || true
  rm -f "$FAKE"
}
trap cleanup EXIT

echo "127.0.0.1 localhost" > "$FAKE"

echo "==> Building and starting stack"
docker compose -f "$COMPOSE" up -d --build

echo "==> Waiting for whw to write '$ALIAS' to fake hosts file"
for i in $(seq 1 30); do
  if grep -q "$ALIAS" "$FAKE"; then
    echo "PASS: entry written after ${i}s"
    break
  fi
  sleep 1
done

if ! grep -q "$ALIAS" "$FAKE"; then
  echo "FAIL: entry never written"
  echo "--- whw logs ---"
  docker compose -f "$COMPOSE" logs whw
  echo "--- fake hosts ---"
  cat "$FAKE"
  exit 1
fi

echo "==> Stopping target container"
docker compose -f "$COMPOSE" stop target

echo "==> Waiting for whw to remove '$ALIAS' from fake hosts file"
for i in $(seq 1 30); do
  if ! grep -q "$ALIAS" "$FAKE"; then
    echo "PASS: entry removed after ${i}s"
    break
  fi
  sleep 1
done

if grep -q "$ALIAS" "$FAKE"; then
  echo "FAIL: entry not cleaned up"
  docker compose -f "$COMPOSE" logs whw
  exit 1
fi

echo "==> Smoke test passed"
