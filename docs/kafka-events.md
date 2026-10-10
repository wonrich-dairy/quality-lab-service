# Kafka Events - Quality Lab Service - SCRUM-24

Quality Lab publishes every official batch determination so Processing and Traceability can react without querying the lab.

Topic: `wonrich.quality-lab.batch-determinations.v1` (3 partitions, 7 days) - key = batch code (per-batch ordering).
Dead-letter: `wonrich.dlq.quality-lab-determinations.v1` (30 days). Both registered in `wonrich-infra/kafka/topics.env`.

Contracts: `src/QualityLab.Api/Domain/Events/BatchDeterminationEvents.cs` (v1; a breaking change gets a new `.v2` topic, an added optional field stays v1).

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

Processing is expected to consume with group `processing-batch-determinations` (DLQ `wonrich.dlq.processing-batch-determinations.v1`, registered in wonrich-infra). Replace superseded determinations by `supersedesDeterminationId`.

## Verifying locally

```bash
docker exec wonrich-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 \
  --topic wonrich.quality-lab.batch-determinations.v1 --from-beginning --property print.key=true --property print.headers=true
# broker outage: docker compose stop kafka -> submit determination (still succeeds) -> start kafka -> row becomes Processed
# SELECT Status, RetryCount FROM outbox_messages ORDER BY CreatedAtUtc DESC;
```
