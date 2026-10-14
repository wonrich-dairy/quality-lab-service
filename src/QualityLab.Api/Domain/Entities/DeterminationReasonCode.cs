using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// One coded failure reason on a determination (SCRUM-25).
/// The codes are also kept in <see cref="Determination.ReasonCodesJson"/> for the existing API
/// shape; this row-per-code table exists so the historical view can filter and count by reason
/// with an index instead of scanning JSON text.
/// </summary>
public class DeterminationReasonCode
{
    public Guid Id { get; set; }

    public Guid DeterminationId { get; set; }
    public Determination Determination { get; set; } = null!;

    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;
}
