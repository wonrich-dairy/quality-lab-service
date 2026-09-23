using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

public enum DeterminationResult
{
    Pass,
    Fail
}

/// <summary>
/// Pass/Fail determination for a batch (SCRUM-23). Written once; corrections
/// are made by superseding, not editing. Locks the panel and sensory evaluation.
/// </summary>
public class Determination
{
    public Guid Id { get; set; }

    public Guid BatchWorkItemId { get; set; }
    public BatchWorkItem BatchWorkItem { get; set; } = null!;

    [MaxLength(100)]
    public string BatchCode { get; set; } = string.Empty;

    /// <summary>Pass or Fail</summary>
    public DeterminationResult Result { get; set; }

    /// <summary>JSON array of coded reason codes for Fail: ["LOW_FAT","HIGH_PH"]</summary>
    [MaxLength(1000)]
    public string? ReasonCodesJson { get; set; }

    /// <summary>Required when passing over an out-of-spec flag or failed sensory attribute.</summary>
    [MaxLength(1000)]
    public string? OverrideReason { get; set; }

    /// <summary>Free-text notes.</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>ID of the determination this one supersedes (null if original).</summary>
    public Guid? SupersededDeterminationId { get; set; }

    /// <summary>ID of the determination that superseded this one (null if still active).</summary>
    public Guid? SupersededById { get; set; }

    [MaxLength(100)]
    public string DeterminedBy { get; set; } = string.Empty;

    public DateTime DeterminedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
