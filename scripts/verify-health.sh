#!/usr/bin/env bash
# Post-deploy verification (SCRUM-109).
#
#   scripts/verify-health.sh <app-host> [expected-git-sha]
#
# Passes only when:
#   1. /version reports the expected commit (so we are not checking the old container), and
#   2. /health is not Unhealthy and its "mysql" check is Healthy.
# Kafka may be Degraded during a broker outage; that does not fail the deployment.
set -euo pipefail

HOST="${1:?usage: $0 <app-host> [expected-git-sha]}"
EXPECTED_SHA="${2:-}"
ATTEMPTS=20
DELAY=15

for i in $(seq 1 "$ATTEMPTS"); do
  if [ -n "$EXPECTED_SHA" ]; then
    running=$(curl -fsS "https://$HOST/version" 2>/dev/null | jq -r '.sha' 2>/dev/null || true)
    if [ "$running" != "$EXPECTED_SHA" ]; then
      echo "Attempt $i/$ATTEMPTS: running version '${running:-unreachable}', waiting for $EXPECTED_SHA"
      sleep "$DELAY"; continue
    fi
  fi

  body=$(curl -sS "https://$HOST/health" 2>/dev/null || true)
  if echo "$body" | jq -e '
        (.status != "Unhealthy") and
        ([.checks[] | select(.name == "mysql" and .status == "Healthy")] | length == 1)
      ' >/dev/null 2>&1; then
    echo "$body" | jq .
    echo "OK: version ${EXPECTED_SHA:-(not checked)} is running and the database check is Healthy"
    exit 0
  fi

  echo "Attempt $i/$ATTEMPTS: /health not healthy yet: ${body:-unreachable}"
  sleep "$DELAY"
done

echo "FAILED: /health did not report a healthy database within $((ATTEMPTS * DELAY))s" >&2
exit 1