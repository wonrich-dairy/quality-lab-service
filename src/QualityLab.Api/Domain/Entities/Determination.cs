using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Pass/Fail determination for a batch (SCRUM-23). Written once; corrections
/// are made by superseding, not editing. Locks the panel and sensory evaluation.
/// </summary>
public class Determination
{
    public Guid Id { get; set; }

    public Guid BatchWorkItemId { get; set; }
    public BatchWorkItem BatchWorkItem { get; set; } = null!;

    /// <summary>Pass or Fail</summary>
    [MaxLength(10)]
    public string Result { get; set; } = string.Empty;

    /// <summary>JSON array of coded reason codes for Fail: ["LOW_FAT","PH_OUT_OF_SPEC"]</summary>
    [MaxLength(1000)]
    public string? ReasonCodesJson { get; set; }

    /// <summary>Required when passing over an out-of-spec flag or failed sensory attribute.</summary>
    [MaxLength(1000)]
    public string? OverrideReason { get; set; }

    /// <summary>Correction note when superseding a previous determination.</summary>
    [MaxLength(1000)]
    public string? CorrectionNote { get; set; }

    /// <summary>ID of the determination this one supersedes (null if original).</summary>
    public Guid? SupersededDeterminationId { get; set; }

    /// <summary>True if this determination has been superseded by a later one.</summary>
    public bool IsSuperseded { get; set; }

    [MaxLength(100)]
    public string DeterminedBy { get; set; } = string.Empty;

    public DateTime DeterminedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
