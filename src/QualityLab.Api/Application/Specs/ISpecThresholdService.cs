using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.Specs;

/// <summary>
/// Spec threshold service (SCRUM-22): CRUD thresholds per product type,
/// evaluate a panel against thresholds to flag out-of-spec parameters.
/// </summary>
public interface ISpecThresholdService
{
    Task<IReadOnlyList<SpecThreshold>> GetAllAsync(CancellationToken ct);
    Task<SpecThreshold?> GetByProductLineAsync(ProductLine productLine, CancellationToken ct);
    Task<SpecThreshold> UpdateAsync(ProductLine productLine, UpdateSpecRequest request, string userId, CancellationToken ct);
    Task<SpecEvaluationResult> EvaluatePanelAsync(string batchCode, CancellationToken ct);
}

public sealed class UpdateSpecRequest
{
    public decimal? MinFatPercent { get; set; }
    public decimal? MaxFatPercent { get; set; }
    public decimal? MinPh { get; set; }
    public decimal? MaxPh { get; set; }
    public decimal? MinSnf { get; set; }
    public decimal? MinCorrectedClr { get; set; }
}

public sealed class SpecEvaluationResult
{
    public string BatchCode { get; set; } = string.Empty;
    public bool HasOutOfSpecFlags { get; set; }
    public List<OutOfSpecFlag> Flags { get; set; } = new();
}

public sealed class OutOfSpecFlag
{
    public string Parameter { get; set; } = string.Empty;
    public decimal ActualValue { get; set; }
    public string Limit { get; set; } = string.Empty;
    public decimal LimitValue { get; set; }
}
