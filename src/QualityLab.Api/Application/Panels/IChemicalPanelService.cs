using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.Panels;

/// <summary>
/// Chemical panel recording service — records panels per product line,
/// derives CLR/SNF/TS for liquid lines, validates field applicability.
/// </summary>
public interface IChemicalPanelService
{
    Task<BatchWorkItem?> GetBatchAsync(string batchCode, CancellationToken ct);
    Task<IReadOnlyList<BatchWorkItem>> GetWorkQueueAsync(CancellationToken ct);
    Task<ChemicalPanel> RecordPanelAsync(string batchCode, RecordPanelRequest request, string userId, CancellationToken ct);
    Task<ChemicalPanel?> GetLatestPanelAsync(string batchCode, CancellationToken ct);
    Task<IReadOnlyList<ChemicalPanel>> GetPanelVersionsAsync(string batchCode, CancellationToken ct);
    Task<BatchWorkItem> CreateBatchAsync(CreateBatchRequest request, string userId, CancellationToken ct);
}

public sealed class RecordPanelRequest
{
    public decimal FatPercent { get; set; }
    public decimal? LactometerReading { get; set; }
    public decimal? TemperatureCelsius { get; set; }
    public decimal Ph { get; set; }

    /// <summary>Must not be supplied — derived server-side for liquid lines.</summary>
    public decimal? Snf { get; set; }
    /// <summary>Must not be supplied — derived server-side for liquid lines.</summary>
    public decimal? Ts { get; set; }
}

public sealed class CreateBatchRequest
{
    public string BatchCode { get; set; } = string.Empty;
    public string DispatchNumber { get; set; } = string.Empty;
    public string ProductLine { get; set; } = string.Empty;
    public string? StoringTankCode { get; set; }
}
