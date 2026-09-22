using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Audit trail entry for changes to spec thresholds, sensory evaluations,
/// determinations and overrides.
/// </summary>
public class AuditEntry
{
    public Guid Id { get; set; }

    [MaxLength(50)]
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Parameter { get; set; }

    [MaxLength(500)]
    public string? OldValue { get; set; }

    [MaxLength(500)]
    public string? NewValue { get; set; }

    [MaxLength(100)]
    public string UserId { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; }
}
