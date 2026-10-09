namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Transactional outbox row (SCRUM-24). Written in the SAME SaveChanges as the determination, then
/// relayed to Kafka by OutboxRelayService - so a rollback publishes nothing and a broker outage never
/// fails the lab technician's save. Same shape as Processing Service's outbox (SCRUM-68).
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string HeadersJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Pending, Processed, or Poisoned (past retry budget - needs human review).</summary>
    public string Status { get; set; } = "Pending";

    public string CorrelationId { get; set; } = string.Empty;
}
