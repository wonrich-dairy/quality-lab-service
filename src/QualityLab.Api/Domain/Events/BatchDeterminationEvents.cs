namespace QualityLab.Api.Domain.Events;

/// <summary>
/// Versioned event contracts published by Quality Lab on
/// <c>wonrich.quality-lab.batch-determinations.v1</c> (SCRUM-24).
/// Same envelope conventions as Processing Service (SCRUM-68): camelCase JSON, key = batch code,
/// headers x-correlation-id / eventId / eventType / schemaVersion, at-least-once delivery
/// (consumers dedupe on eventId). Breaking change = new topic .v2.
/// </summary>
public abstract record BatchDeterminationEventBase
{
    /// <summary>Unique ID for consumer-side deduplication (relay is at-least-once).</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Batch code e.g. 265-FM-A - also the Kafka message key (per-batch ordering).</summary>
    public string BatchId { get; init; } = string.Empty;

    public string DispatchNumber { get; init; } = string.Empty;

    public string ProductLine { get; init; } = string.Empty;

    public DateTime TimestampUtc { get; init; }

    /// <summary>True when the result was reached over an out-of-spec flag / failed sensory attribute (override used).</summary>
    public bool IsDeviation { get; init; }

    public string SchemaVersion { get; init; } = "v1";

    public string CorrelationId { get; init; } = string.Empty;

    public Guid DeterminationId { get; init; }

    /// <summary>Set when this determination replaced an earlier one (supersede).</summary>
    public Guid? SupersedesDeterminationId { get; init; }

    public string DeterminedBy { get; init; } = string.Empty;

    public DateTime DeterminedAtUtc { get; init; }

    public string? OverrideReason { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Published when a batch passes lab determination (status Cleared).</summary>
public sealed record BatchClearedEvent : BatchDeterminationEventBase;

/// <summary>Published when a batch fails lab determination (status Failed). Carries coded reasons.</summary>
public sealed record BatchFailedEvent : BatchDeterminationEventBase
{
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();
}
