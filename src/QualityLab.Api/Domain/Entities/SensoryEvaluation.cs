using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Sensory evaluation for a finished-product batch (SCRUM-21).
/// Taste, smell, colour, appearance on all lines. Texture on fermented lines only.
/// Locked after determination exists. Edits before determination are audited.
/// </summary>
public class SensoryEvaluation
{
    public Guid Id { get; set; }

    public Guid BatchWorkItemId { get; set; }
    public BatchWorkItem BatchWorkItem { get; set; } = null!;

    public SensoryGrade Taste { get; set; }
    [MaxLength(500)]
    public string? TasteNote { get; set; }

    public SensoryGrade Smell { get; set; }
    [MaxLength(500)]
    public string? SmellNote { get; set; }

    public SensoryGrade Colour { get; set; }
    [MaxLength(500)]
    public string? ColourNote { get; set; }

    public SensoryGrade Appearance { get; set; }
    [MaxLength(500)]
    public string? AppearanceNote { get; set; }

    /// <summary>Texture — required on fermented lines (SY, SK, DY, CD) only.</summary>
    public SensoryGrade? Texture { get; set; }
    [MaxLength(500)]
    public string? TextureNote { get; set; }

    [MaxLength(100)]
    public string EvaluatedBy { get; set; } = string.Empty;

    public DateTime EvaluatedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Locked when a determination exists.</summary>
    public bool IsLocked { get; set; }
}
