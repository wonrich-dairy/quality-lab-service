using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Controllers;

/// <summary>
/// Determination endpoints (SCRUM-23): submit pass/fail, get active,
/// supersede, view determination history for a batch.
/// </summary>
[ApiController]
[Route("api/panels/{batchCode}/determination")]
[Authorize]
public sealed class DeterminationsController : ControllerBase
{
    private readonly IDeterminationService _determinationService;

    public DeterminationsController(IDeterminationService determinationService)
    {
        _determinationService = determinationService;
    }

    /// <summary>Submit a pass/fail determination for a batch.</summary>
    [HttpPost]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> Submit(string batchCode, [FromBody] SubmitDeterminationRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var determination = await _determinationService.SubmitAsync(batchCode, request, userId, ct);
            return Ok(ToDto(determination));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Get the active (non-superseded) determination for a batch.</summary>
    [HttpGet]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> Get(string batchCode, CancellationToken ct)
    {
        var determination = await _determinationService.GetAsync(batchCode, ct);
        if (determination == null)
            return NotFound(new { message = $"No active determination for batch '{batchCode}'." });

        return Ok(ToDto(determination));
    }

    /// <summary>Supersede the current determination with a new one.</summary>
    [HttpPost("supersede")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> Supersede(string batchCode, [FromBody] SubmitDeterminationRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var determination = await _determinationService.SupersedeAsync(batchCode, request, userId, ct);
            return Ok(ToDto(determination));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Get full determination history (all versions) for a batch.</summary>
    [HttpGet("history")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> History(string batchCode, CancellationToken ct)
    {
        var history = await _determinationService.GetHistoryAsync(batchCode, ct);
        return Ok(history.Select(ToDto));
    }

    private static object ToDto(Determination d)
    {
        var reasonCodes = d.ReasonCodesJson != null
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(d.ReasonCodesJson) ?? new()
            : new List<string>();

        return new
        {
            id = d.Id,
            batchCode = d.BatchCode,
            result = d.Result.ToString(),
            reasonCodes,
            overrideReason = d.OverrideReason,
            notes = d.Notes,
            determinedBy = d.DeterminedBy,
            determinedAtUtc = d.DeterminedAtUtc,
            supersededDeterminationId = d.SupersededDeterminationId,
            supersededById = d.SupersededById,
            isActive = d.SupersededById == null
        };
    }
}
