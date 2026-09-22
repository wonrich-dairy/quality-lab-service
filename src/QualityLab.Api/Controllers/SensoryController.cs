using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QualityLab.Api.Application.Sensory;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Controllers;

/// <summary>
/// Sensory evaluation endpoints (SCRUM-21): taste, smell, colour, appearance
/// on all lines, texture on fermented lines, on a fixed scale.
/// </summary>
[ApiController]
[Route("api/panels/{batchCode}/sensory")]
[Authorize]
public sealed class SensoryController : ControllerBase
{
    private readonly ISensoryEvaluationService _sensoryService;

    public SensoryController(ISensoryEvaluationService sensoryService)
    {
        _sensoryService = sensoryService;
    }

    /// <summary>Record a sensory evaluation for a batch.</summary>
    [HttpPost]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> RecordEvaluation(string batchCode, [FromBody] RecordSensoryRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var evaluation = await _sensoryService.RecordEvaluationAsync(batchCode, request, userId, ct);
            return Ok(ToDto(evaluation));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Update a sensory evaluation (editable until determination).</summary>
    [HttpPut]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> UpdateEvaluation(string batchCode, [FromBody] RecordSensoryRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var evaluation = await _sensoryService.UpdateEvaluationAsync(batchCode, request, userId, ct);
            return Ok(ToDto(evaluation));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Get the sensory evaluation for a batch.</summary>
    [HttpGet]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager},{WonrichRoles.ProcessingTechnician}")]
    public async Task<ActionResult<object>> GetEvaluation(string batchCode, CancellationToken ct)
    {
        var evaluation = await _sensoryService.GetEvaluationAsync(batchCode, ct);
        if (evaluation == null)
            return NotFound(new { message = $"No sensory evaluation for batch '{batchCode}'." });

        return Ok(ToDto(evaluation));
    }

    private static object ToDto(SensoryEvaluation e) => new
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
