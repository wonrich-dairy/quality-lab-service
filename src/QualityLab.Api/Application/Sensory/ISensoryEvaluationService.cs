using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.Sensory;

/// <summary>
/// Sensory evaluation service (SCRUM-21): records taste, smell, colour, appearance
/// on all lines, plus texture on fermented lines. Notes required when below Acceptable.
/// </summary>
public interface ISensoryEvaluationService
{
    Task<SensoryEvaluation> RecordEvaluationAsync(string batchCode, RecordSensoryRequest request, string userId, CancellationToken ct);
    Task<SensoryEvaluation> UpdateEvaluationAsync(string batchCode, RecordSensoryRequest request, string userId, CancellationToken ct);
    Task<SensoryEvaluation?> GetEvaluationAsync(string batchCode, CancellationToken ct);
}

public sealed class RecordSensoryRequest
{
    public string Taste { get; set; } = string.Empty;
    public string? TasteNote { get; set; }
    public string Smell { get; set; } = string.Empty;
    public string? SmellNote { get; set; }
    public string Colour { get; set; } = string.Empty;
    public string? ColourNote { get; set; }
    public string Appearance { get; set; } = string.Empty;
    public string? AppearanceNote { get; set; }
    public string? Texture { get; set; }
    public string? TextureNote { get; set; }
}
