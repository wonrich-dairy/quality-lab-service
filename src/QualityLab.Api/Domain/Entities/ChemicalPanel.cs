using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Chemical test panel for a finished-product batch. Fields depend on the product line:
/// - Liquid lines (FM, FLM): fat, lactometer reading, temperature, pH → derived CLR, SNF, TS
/// - Fermented lines (SY, DY, SK, CD): fat, pH only
/// A re-test creates a new panel version, not an overwrite. Precision 2 decimal places.
/// Timestamps UTC datetime(6).
/// </summary>
public class ChemicalPanel
{
    public Guid Id { get; set; }

    public Guid BatchWorkItemId { get; set; }
    public BatchWorkItem BatchWorkItem { get; set; } = null!;

    [MaxLength(30)]
    public string BatchCode { get; set; } = string.Empty;

    [MaxLength(50)]
    public string DispatchNumber { get; set; } = string.Empty;

    public ProductLine ProductLine { get; set; }

    /// <summary>Fat % — required on all product lines. HasPrecision(10,2)</summary>
    public decimal FatPercent { get; set; }

    /// <summary>Raw lactometer reading — liquid lines only, null on fermented.</summary>
    public decimal? LactometerReading { get; set; }

    /// <summary>Sample temperature °C — liquid lines only, null on fermented.</summary>
    public decimal? TemperatureCelsius { get; set; }

    /// <summary>pH — required on all product lines.</summary>
    public decimal Ph { get; set; }

    /// <summary>Corrected CLR — derived by formula, liquid lines only.</summary>
    public decimal? CorrectedClr { get; set; }

    /// <summary>Solid Non-Fat % — derived from CLR and fat, liquid lines only.</summary>
    public decimal? Snf { get; set; }

    /// <summary>Total Solids % — derived SNF + fat, liquid lines only.</summary>
    public decimal? Ts { get; set; }

    /// <summary>Version number — 1 for first panel, increments on re-test.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Whether this panel has any out-of-spec flags (set by SCRUM-22 evaluation).</summary>
    public bool HasOutOfSpecFlags { get; set; }

    /// <summary>JSON array of out-of-spec flags [{parameter, value, limit}]</summary>
    [MaxLength(2000)]
    public string? OutOfSpecFlagsJson { get; set; }

    [MaxLength(100)]
    public string TestedBy { get; set; } = string.Empty;

    public DateTime TestedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Locked when a determination exists — no further edits.</summary>
    public bool IsLocked { get; set; }
}
