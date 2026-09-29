using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.Sensory;

/// <summary>
/// Shared mapper for SensoryEvaluation → DTO. Used by both SensoryController
/// and PanelsController (composite batch response, DOD 3).
/// </summary>
public static class SensoryMappers
{
    public static object ToDto(SensoryEvaluation e) => new
    {
        id = e.Id,
        batchWorkItemId = e.BatchWorkItemId,
        taste = e.Taste.ToString(),
        tasteNote = e.TasteNote,
        smell = e.Smell.ToString(),
        smellNote = e.SmellNote,
        colour = e.Colour.ToString(),
        colourNote = e.ColourNote,
        appearance = e.Appearance.ToString(),
        appearanceNote = e.AppearanceNote,
        texture = e.Texture?.ToString(),
        textureNote = e.TextureNote,
        evaluatedBy = e.EvaluatedBy,
        evaluatedAtUtc = e.EvaluatedAtUtc,
        isLocked = e.IsLocked
    };
}
