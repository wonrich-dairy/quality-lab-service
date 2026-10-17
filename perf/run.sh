#!/usr/bin/env bash
# Run the Quality Lab JMeter plan with a named load profile (SCRUM-43, SE3112).
#
#   perf/run.sh <load|stress|spike> [extra JMeter -J options]
#
# The plan has three thread groups that run one after another. A profile sets how many users
# each phase has and for how long:
#
#   load    one steady phase           USERS (10) for DURATION (120 s), RAMP (30 s) ramp-up
#   stress  three rising steps         10 -> 25 -> 50 users, STEP_SECONDS (60 s) each
#   spike   baseline, spike, recovery  5 -> 50 -> 5 users, STEP_SECONDS (30 s) each, 2 s ramp into the spike
#
# Needs: HOST, AUTH_HOST (bare host names), PERF_USERNAME, PERF_PASSWORD.
# Optional: THINK_MS (1000), RUN_ID (timestamp), OUT (results), JMETER (jmeter on PATH),
#           P95_MS / MAX_ERROR_PCT to also check thresholds at the end.
#
# Against services on your own machine, pass the protocol and ports through:
#   HOST=localhost AUTH_HOST=localhost ... perf/run.sh load -Jprotocol=http -Jport=5003 -Jauth_protocol=http -Jauth_port=5189
#
# Example, a short stress run for a demo:
#   read -rs PERF_PASSWORD; export PERF_PASSWORD
#   HOST=<app host> AUTH_HOST=<auth host> PERF_USERNAME=perf-quality-lab STEP_SECONDS=20 perf/run.sh stress
set -euo pipefail
cd "$(dirname "$0")/.."

PROFILE="${1:-load}"
: "${HOST:?set HOST to the Quality Lab host name}"
: "${AUTH_HOST:?set AUTH_HOST to the auth service host name}"
: "${PERF_USERNAME:?set PERF_USERNAME}"
: "${PERF_PASSWORD:?set PERF_PASSWORD (read -rs PERF_PASSWORD; export PERF_PASSWORD)}"
JMETER="${JMETER:-jmeter}"
OUT="${OUT:-results}"
RUN_ID="${RUN_ID:-$(date +%y%m%d%H%M%S)}"
THINK_MS="${THINK_MS:-1000}"

# users ramp duration, per phase
case "$PROFILE" in
  load)
    P1="${USERS:-10} ${RAMP:-30} ${DURATION:-120}"; P2="0 1 1"; P3="0 1 1"
    DESC="load: ${USERS:-10} users for ${DURATION:-120}s" ;;
  stress)
    S="${STEP_SECONDS:-60}"
    P1="10 10 $S"; P2="25 10 $S"; P3="50 10 $S"
    DESC="stress: 10 -> 25 -> 50 users, ${S}s per step" ;;
  spike)
    S="${STEP_SECONDS:-30}"
    P1="5 5 $S"; P2="50 2 $S"; P3="5 2 $S"
    DESC="spike: 5 -> 50 -> 5 users, ${S}s per phase" ;;
  *)
    echo "unknown profile '$PROFILE' (use load, stress or spike)" >&2; exit 2 ;;
esac
for h in HOST AUTH_HOST; do
  [[ "${!h}" != *"://"* && "${!h}" != */ ]] || { echo "$h must be a bare host name, got '${!h}'" >&2; exit 2; }
done

read -r P1U P1R P1D <<<"$P1"
read -r P2U P2R P2D <<<"$P2"
read -r P3U P3R P3D <<<"$P3"

echo "== $DESC against https://$HOST (run $RUN_ID)"
rm -rf "$OUT"; mkdir -p "$OUT"

"$JMETER" -n -t perf/quality-lab.jmx \
  -l "$OUT/results.jtl" -j "$OUT/jmeter.log" -e -o "$OUT/report" \
  -Jhost="$HOST" -Jauth_host="$AUTH_HOST" \
  -Jusername="$PERF_USERNAME" -Jpassword="$PERF_PASSWORD" \
  -Jrun_id="$RUN_ID" -Jthink_ms="$THINK_MS" \
  -Jp1_users="$P1U" -Jp1_ramp="$P1R" -Jp1_duration="$P1D" \
  -Jp2_users="$P2U" -Jp2_ramp="$P2R" -Jp2_duration="$P2D" \
  -Jp3_users="$P3U" -Jp3_ramp="$P3R" -Jp3_duration="$P3D" \
  -Jaggregate_rpt_pct2=95 \
  "-Jjmeter.reportgenerator.sample_filter=^(read|write): .*" \
  "${@:2}"

python3 perf/phase_summary.py "$OUT/results.jtl" "$PROFILE"
echo "HTML report: $OUT/report/index.html"

if [ -n "${P95_MS:-}" ] && [ -n "${MAX_ERROR_PCT:-}" ]; then
  bash perf/check-thresholds.sh "$OUT/report/statistics.json" "$P95_MS" "$MAX_ERROR_PCT"
fi
