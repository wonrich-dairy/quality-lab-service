using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Specification threshold for a product type (SCRUM-22).
/// Carries only the limits its product line actually measures.
/// Seeded from defaults on first run.
/// </summary>
public class SpecThreshold
{
    public Guid Id { get; set; }

    public ProductLine ProductLine { get; set; }

    public decimal? MinFatPercent { get; set; }
    public decimal? MaxFatPercent { get; set; }

    public decimal? MinPh { get; set; }
    public decimal? MaxPh { get; set; }

    /// <summary>Liquid lines only</summary>
    public decimal? MinSnf { get; set; }

    /// <summary>Liquid lines only</summary>
    public decimal? MinCorrectedClr { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    [MaxLength(100)]
    public string UpdatedBy { get; set; } = "seed";
}
