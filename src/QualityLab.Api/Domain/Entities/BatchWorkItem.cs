using System.ComponentModel.DataAnnotations;

namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// A batch in the lab work queue. Created when ProcessingCompleted is consumed (or manually
/// when polling). Identified by batch code and dispatch number from the Processing Service.
/// </summary>
public class BatchWorkItem
{
    public Guid Id { get; set; }

    /// <summary>Batch code from processing: [day]-[product]-[letter] e.g. 265-FM-A</summary>
    [MaxLength(30)]
    public string BatchCode { get; set; } = string.Empty;

    /// <summary>Dispatch number for traceability back to MCC</summary>
    [MaxLength(50)]
    public string DispatchNumber { get; set; } = string.Empty;

    public ProductLine ProductLine { get; set; }

    [MaxLength(20)]
    public string? StoringTankCode { get; set; }

    public DateTime? CompletionTimeUtc { get; set; }

    public BatchStatus Status { get; set; } = BatchStatus.AwaitingPanel;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // Navigation
    public ICollection<ChemicalPanel> Panels { get; set; } = new List<ChemicalPanel>();
    public SensoryEvaluation? SensoryEvaluation { get; set; }
    public ICollection<Determination> Determinations { get; set; } = new List<Determination>();
}
