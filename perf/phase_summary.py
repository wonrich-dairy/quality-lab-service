#!/usr/bin/env python3
"""Per-phase results of a JMeter run (SCRUM-43, SE3112).

    python3 perf/phase_summary.py results/results.jtl <profile>

JMeter's HTML report aggregates the whole run. A stress or spike profile is only meaningful
phase by phase (where did p95 or the error rate break?), so this prints one row per phase:
peak users, requests, throughput, p50 / p95 / max latency and error rate. Only the measured
samplers ('read:' / 'write:') count. The table also goes to the GitHub run summary.
"""
import csv
import math
import os
import sys
from collections import OrderedDict

STEPS = {
    "load": ["steady load"],
    "stress": ["step 1", "step 2", "step 3"],
    "spike": ["baseline", "spike", "recovery"],
}


def percentile(sorted_values, pct):
    if not sorted_values:
        return 0
    k = max(0, math.ceil(pct / 100 * len(sorted_values)) - 1)
    return sorted_values[k]


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: phase_summary.py <results.jtl> [profile]")
    jtl, profile = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "load")

    phases = OrderedDict()
    with open(jtl, newline="") as f:
        for row in csv.DictReader(f):
            if not row["label"].startswith(("read:", "write:")):
                continue
            # threadName = "phase 2: quality analysts 2-17" -> "phase 2"
            phase = row["threadName"].split(":")[0]
            p = phases.setdefault(phase, {"elapsed": [], "errors": 0, "first": None, "last": 0, "users": 0})
            ts, elapsed = int(row["timeStamp"]), int(row["elapsed"])
            p["elapsed"].append(elapsed)
            p["errors"] += row["success"] != "true"
            p["first"] = ts if p["first"] is None else min(p["first"], ts)
            p["last"] = max(p["last"], ts + elapsed)
            p["users"] = max(p["users"], int(row.get("grpThreads") or 0))

    names = STEPS.get(profile, [])
    lines = [
        f"### Load profile: {profile}",
        "",
        "| Phase | Peak users | Requests | Req/s | p50 (ms) | p95 (ms) | Max (ms) | Errors |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for i, (phase, p) in enumerate(sorted(phases.items())):
        values = sorted(p["elapsed"])
        seconds = max((p["last"] - p["first"]) / 1000, 1)
        label = f"{phase} ({names[i]})" if i < len(names) else phase
        lines.append(
            f"| {label} | {p['users']} | {len(values)} | {len(values) / seconds:.1f} | "
            f"{percentile(values, 50)} | {percentile(values, 95)} | {values[-1] if values else 0} | "
            f"{100 * p['errors'] / max(len(values), 1):.1f}% |"
        )
    if not phases:
        lines.append("| no measured requests | | | | | | | |")
    text = "\n".join(lines) + "\n"
    print(text)

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as f:
            f.write(text + "\n")


if __name__ == "__main__":
    main()
