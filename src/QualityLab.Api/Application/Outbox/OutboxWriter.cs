using System.Text.Json;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Domain.Events;
using QualityLab.Api.Infrastructure.Persistence;
using QualityLab.Api.Observability;

namespace QualityLab.Api.Application.Outbox;

public interface IOutboxWriter
{
    /// <summary>
    /// Stages an event in outbox_messages. Does NOT call SaveChanges and does NOT touch Kafka -
    /// the caller saves it in the same transaction as the business record (publish-after-commit).
    /// </summary>
    void Write<TEvent>(string topic, string key, TEvent @event) where TEvent : BatchDeterminationEventBase;
}

/// <summary>
/// Mirrors Processing Service's OutboxWriter (SCRUM-68): same headers, same camelCase payload.
/// </summary>
public sealed class OutboxWriter(QualityLabDbContext db, TimeProvider time, IHttpContextAccessor? http = null) : IOutboxWriter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Write<TEvent>(string topic, string key, TEvent @event) where TEvent : BatchDeterminationEventBase
    {
        var correlationId = !string.IsNullOrWhiteSpace(@event.CorrelationId)
            ? @event.CorrelationId
            : http?.HttpContext?.Items[CorrelationIdMiddleware.HeaderName] as string ?? Guid.NewGuid().ToString();

        // Stamp the resolved ID into the payload too (contract field)
        var stamped = @event with { CorrelationId = correlationId };

        var headers = new Dictionary<string, string>
        {
            ["x-correlation-id"] = correlationId,
            ["eventId"] = stamped.EventId.ToString(),
            ["eventType"] = typeof(TEvent).Name,
            ["schemaVersion"] = stamped.SchemaVersion,
            ["timestamp"] = stamped.TimestampUtc.ToString("o")
        };

        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            Key = key,
            EventType = typeof(TEvent).Name,
            Payload = JsonSerializer.Serialize(stamped, stamped.GetType(), Json),
            HeadersJson = JsonSerializer.Serialize(headers),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            Status = "Pending",
            CorrelationId = correlationId
        });
    }
}
