using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.Determinations;

/// <summary>
/// Determination service (SCRUM-23): submit pass/fail for a batch,
/// supersede an existing determination, lock panels + sensory.
/// </summary>
public interface IDeterminationService
{
    Task<Determination> SubmitAsync(string batchCode, SubmitDeterminationRequest request, string userId, CancellationToken ct);
    Task<Determination?> GetAsync(string batchCode, CancellationToken ct);
    Task<Determination> SupersedeAsync(string batchCode, SubmitDeterminationRequest request, string userId, CancellationToken ct);
    Task<IReadOnlyList<Determination>> GetHistoryAsync(string batchCode, CancellationToken ct);
}

public sealed class SubmitDeterminationRequest
{
    public DeterminationResult Result { get; set; }
    public List<string> ReasonCodes { get; set; } = new();
    public string? OverrideReason { get; set; }
    public string? Notes { get; set; }
}
