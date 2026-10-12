# Wonrich.QualityLab.Contracts

Event contracts the Quality Lab service publishes on the Kafka topic
`wonrich.quality-lab.batch-determinations.v1`.

| Type | `eventType` header / field | When |
|---|---|---|
| `BatchClearedEvent` | `BatchCleared` | A batch passes lab determination |
| `BatchFailedEvent` | `BatchFailed` | A batch fails; carries `ReasonCodes` and `OutOfSpecDetails` |

Namespace: `Wonrich.QualityLab.Contracts.Events`.

## Wire format

- JSON, camelCase property names. Deserialize with `JsonNamingPolicy.CamelCase` (or `JsonSerializerDefaults.Web`).
- Message key is the batch code (`batchId`), so events for one batch stay in order.
- Headers: `x-correlation-id`, `eventId`, `eventType`, `schemaVersion`, `timestamp`.
- Delivery is at-least-once. Deduplicate on `eventId`.
- Pick the type from the `eventType` header before deserializing.

```csharp
using System.Text.Json;
using Wonrich.QualityLab.Contracts.Events;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
BatchDeterminationEventBase? evt = eventTypeHeader switch
{
    "BatchCleared" => JsonSerializer.Deserialize<BatchClearedEvent>(payload, json),
    "BatchFailed"  => JsonSerializer.Deserialize<BatchFailedEvent>(payload, json),
    _ => null // unknown type on a v1 topic: ignore, do not dead-letter
};
```

## Versioning

The package's major version follows the topic's `schemaVersion`: `1.x.y` is `v1`.
An added optional field is a minor release on the same topic. A breaking change ships as `2.0.0` with a new `.v2` topic.
