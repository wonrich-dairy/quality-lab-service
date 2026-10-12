# Kafka Events - Quality Lab Service - SCRUM-24

Quality Lab publishes every official batch determination so Processing and Traceability can react without querying the lab.

Topic: `wonrich.quality-lab.batch-determinations.v1` (3 partitions, 7 days) - key = batch code (per-batch ordering). Registered in `wonrich-infra/kafka/topics.env` on `develop`.
Dead-letter: `wonrich.dlq.quality-lab-determinations.v1` (30 days, 7 on staging). Registered in `wonrich-infra/kafka/topics.env` by wonrich-infra PR #3 (`feature/SCRUM-24-determination-topics`). **Until that PR is merged and `create-topics.sh` has been re-run against each broker, the broker does not have this topic, and a dead-letter copy of an event that exhausted its retries fails to send.** The `Poisoned` outbox row is still kept as the durable record (see Delivery, step 4).

## Contracts package

The event classes live in the class library `src/Wonrich.QualityLab.Contracts`, published as a NuGet package. Consumers reference the package and must not copy the classes.

| | |
|---|---|
| Package | `Wonrich.QualityLab.Contracts` |
| Version | `1.0.0` (major version = `schemaVersion` `v1`) |
| Namespace | `Wonrich.QualityLab.Contracts.Events` (was `QualityLab.Api.Domain.Events`) |
| Feed | `https://nuget.pkg.github.com/wonrich-dairy/index.json` (GitHub Packages, org `wonrich-dairy`) |
| Dependencies | none (System types only), `net10.0` |

Published by `.github/workflows/publish-contracts.yml` when a tag `contracts-v<Version>` is pushed. The tag must equal `<Version>` in the `.csproj` and point at a commit on `develop` or `main`, so each version maps to one commit. GitHub Packages refuses to overwrite a published version.

Versioning: an added optional field is a minor release (`1.1.0`) on the same topic. A breaking change is `2.0.0` together with a new `.v2` topic.

Consuming (Processing, Traceability):

```xml
<!-- nuget.config next to the solution -->
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="wonrich" value="https://nuget.pkg.github.com/wonrich-dairy/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
    <packageSource key="wonrich"><package pattern="Wonrich.*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

```bash
# GitHub Packages needs a token even for reads: a PAT with read:packages locally, or in CI
# GITHUB_TOKEN with "packages: read" (grant the consuming repo access in the package settings).
dotnet nuget update source wonrich --username <github-user> --password <token> --store-password-in-clear-text
dotnet add package Wonrich.QualityLab.Contracts --version 1.0.0
```

Inside this repo `QualityLab.Api` references the library as a project, so the publisher and the package always compile from the same source.

## Events

| Determination | Event |
|---|---|
| Pass | `BatchCleared` |
| Fail | `BatchFailed` (adds `reasonCodes`, `outOfSpecDetails`) |
| Supersede | New `BatchCleared` / `BatchFailed` with `supersedesDeterminationId` set |

Common fields (camelCase JSON): `eventType`, `eventId`, `batchId` (batch code), `dispatchNumber`, `productLine`, `timestampUtc`, `isDeviation`, `schemaVersion`, `correlationId`, `determinationId`, `supersedesDeterminationId`, `determinedBy`, `determinedAtUtc`, `overrideReason`, `notes`.

```json
{
  "eventType": "BatchFailed",
  "batchId": "WR-2609-0143",
  "dispatchNumber": "DSP-2609-0143",
  "productLine": "FM",
  "determinationId": "...",
  "supersedesDeterminationId": null,
  "determinedBy": "analyst-1",
  "reasonCodes": ["LOW_FAT"],
  "outOfSpecDetails": [
    { "parameter": "FatPercent", "measuredValue": 3.2, "limit": "Min", "limitValue": 3.5 }
  ]
}
```

Headers: `x-correlation-id` (taken from the request's `X-Correlation-ID`), `eventId`, `eventType`, `schemaVersion`, `timestamp`.

## Delivery (transactional outbox)

1. `DeterminationService` writes the determination and an `outbox_messages` row in the **same `SaveChanges`** - rollback publishes nothing.
2. `OutboxRelayService` polls every 5s (batch 20, `FOR UPDATE SKIP LOCKED`), publishes with acks=all + idempotence, marks `Processed`.
3. Broker down: the save already succeeded; the relay retries with backoff (5s to 5m). The batch stops at the first failure to keep per-key order.
4. After `Outbox:MaxRetries` (default 20) the row is marked `Poisoned` **and** a copy is sent to the dead-letter topic with `x-original-topic`, `x-failure-reason`, `x-failure-timestamp`. If the broker is still down the DLQ publish fails too, so the `Poisoned` row stays as the durable record (requeue: `Status='Pending', RetryCount=0`).
5. Delivery is at-least-once: consumers dedupe on `eventId`.
6. Processed rows are deleted after `Outbox:ProcessedRetentionDays` (7). Metric `quality_lab_outbox_backlog` = Pending + Poisoned.

`Kafka:BootstrapServers` empty -> a no-op producer keeps rows Pending (delayed, never lost).

## Consumers

| Consumer group | Service | Dead-letter topic | Registered |
|---|---|---|---|
| `processing-batch-determinations` | Processing | `wonrich.dlq.processing-batch-determinations.v1` | wonrich-infra PR #3 (pending merge) |
| `traceability-batch-determinations` | Traceability (SCRUM-126) | `wonrich.dlq.traceability-batch-determinations.v1` | wonrich-infra `develop` |

Pick the event type from the `eventType` header, dedupe on `eventId`, and replace superseded determinations by `supersedesDeterminationId`.

## Verifying locally

```bash
docker exec wonrich-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 \
  --topic wonrich.quality-lab.batch-determinations.v1 --from-beginning --property print.key=true --property print.headers=true
# broker outage: docker compose stop kafka -> submit determination (still succeeds) -> start kafka -> row becomes Processed
# SELECT Status, RetryCount FROM outbox_messages ORDER BY CreatedAtUtc DESC;
```
