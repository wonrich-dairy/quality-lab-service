# Performance testing (JMeter)

A JMeter load test runs against staging after every deployment, so NFR1 ("the system responds
within 2 seconds") is measured on each release rather than asserted (SCRUM-43).

| | |
|---|---|
| Test plan | `perf/quality-lab.jmx` |
| Workflow | `.github/workflows/performance.yml` |
| Runs | after every successful staging deploy (the `performance` job in `ci-cd.yml`), and by hand |
| Result | pass / fail on p95 latency and error rate, plus an HTML report artifact |

---

## What it tests

Each simulated quality analyst creates its own batch once, then repeats this loop, with a pause of
1.0–1.5 s before each request:

| Request | Endpoint | Kind |
|---|---|---|
| `read: work queue` | `GET /api/panels/work-queue` | read |
| `read: batch` | `GET /api/panels/batch/{batch}` | read |
| `write: record chemical panel` | `POST /api/panels/{batch}` | write |
| `read: latest panel` | `GET /api/panels/{batch}` | read |

`{batch}` is `PERF-<run>-<attempt>-T<user>`, for example `PERF-57-1-T3`: one batch per virtual user
per run. The write comes before the latest-panel read because a new batch has no panel until the
first write.

Three reads to one write: analysts look far more than they record.

Every request carries a bearer token from the auth service, obtained once at the start, and an
`X-Correlation-ID` of `jmeter-<uuid>`, so load-test traffic is easy to find, or exclude, in Loki.

**Setup, not measured:** a warm-up request to `/health` (Free-tier App Services cold-start), the
sign-in, and each user creating its batch (product line DY). They are labelled `setup:` and
filtered out of the report, so they never count towards the thresholds.

### Why one batch per user

Panel versions are numbered "highest existing version + 1", with a unique index on
(batch, version) (`ux_chemical_panels_batch_version`). When several users record a panel on the
**same** batch at the same moment, two requests can read the same highest version and one of them
fails with a 500. That is a service defect, tracked in **SCRUM-<bug>**, not a latency problem.
Giving each user its own batch keeps this test measuring response time. Once the defect is fixed,
a shared-batch scenario can be added to check it under load.

### Test data it leaves on staging

- **One batch per virtual user per run** (10 with the defaults), codes starting `PERF-`, dispatch
  number `PERF-JMETER`.
- **One chemical-panel version per write request**, on those batches only. Real batches are not touched.

Do not record determinations against `PERF-` batches. Filter them out of demos with the dispatch
number `PERF-JMETER`.

---

## Load profile

| Setting | Default | Input |
|---|---|---|
| Concurrent users | 10 | `users` |
| Ramp-up | 30 s | `ramp_seconds` |
| Duration | 120 s | `duration_seconds` |
| Pause between a user's requests | 1000 ms + up to 500 ms random | `think_ms` |

About 7 requests per second at full load. That is several times what a single factory's QA lab
generates, on a Free-tier App Service, so it finds a regression without load-testing Azure itself.

> **Agreed with QA:** _<name>, <date>_

## Thresholds

| Metric | Fails when | Input |
|---|---|---|
| p95 latency, all measured requests | above **2000 ms** (NFR1) | `p95_ms` |
| Error rate (any non-2xx response) | above **1%** | `max_error_pct` |

p95 rather than the average, because an average hides the slow tail users actually notice. A breach
fails the job with an error annotation on the workflow run naming the metric, the value and the threshold.

All defaults live in `performance.yml`. Change them there, through review, rather than in the plan.

---

## Running it by hand

**Actions → Performance test (JMeter) → Run workflow.** Every input above can be overridden for that run.

### Configuration (staging environment)

| Name | Kind | Value |
|---|---|---|
| `PERF_USERNAME` | secret | a staging user with the **QualityAnalyst** role |
| `PERF_PASSWORD` | secret | its password |
| `APP_HOST` | variable | already set for deployment |
| `AUTH_HOST` | variable | the auth service's staging host, without `https://` |

### Locally, against your own machine

Install JMeter 5.6.3, start the auth service and this service, then:

```bash
jmeter -n -t perf/quality-lab.jmx -l results.jtl -e -o report \
  -Jprotocol=http -Jhost=localhost -Jport=5003 \
  -Jauth_protocol=http -Jauth_host=localhost -Jauth_port=5189 \
  -Jusername=<user> -Jpassword=<password> \
  -Jusers=5 -Jduration=60 \
  "-Jjmeter.reportgenerator.sample_filter=^(read|write): .*"
bash perf/check-thresholds.sh report/statistics.json 2000 1
```

`jmeter -t perf/quality-lab.jmx` without `-n` opens the plan in the JMeter GUI to inspect or edit it.

---

## Reading the report

**Quick look:** the run's summary page shows a table of every request with its sample count, p95 and
error rate, and the thresholds it was judged against.

**Full report:** download the **`jmeter-report`** artifact from the run, unzip it and open
`report/index.html`:

| Where | What to look at |
|---|---|
| **Dashboard → Statistics** | One row per request. **95th pct** is the column the threshold uses; **Error %** the other |
| **Dashboard → Errors** | What failed and how, by response code. Start here when the error rate fails |
| **Dashboard → APDEX** | 1.0 is every request satisfactory; below 0.85 is worth investigating |
| **Charts → Over Time → Response Times Over Time** | Latency during the run. A climb during ramp-up means the service struggles as users increase; a spike at the start only is a cold start |
| **Charts → Throughput → Transactions Per Second** | Should plateau once all users are running |

The artifact also contains `results.jtl` (every request) and `jmeter.log`, which explains a run that
produced no report. A failed sign-in is the usual cause.

To find the load test's requests in Loki: `{service="quality-lab-service"} |= "jmeter-"`.

---

## Proving the gate works (Definition of Done)

Run the workflow by hand with **`p95_ms` = `100`**. A real service cannot answer over the internet in
100 ms at p95, so the job fails with *"p95 latency … is above the 100 ms threshold"*. Nothing needs
reverting; the next run uses the default again.

| Item | Evidence |
|---|---|
| Green run on staging, report attached | _run link_ |
| Deliberately tightened threshold fails the job | _run link (p95_ms = 100)_ |
