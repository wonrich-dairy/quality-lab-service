using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QualityLab.Api.Application.Specs;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Controllers;

/// <summary>
/// Spec threshold endpoints (SCRUM-22): configure per product type,
/// evaluate a panel against the thresholds.
/// </summary>
[ApiController]
[Route("api/specs")]
[Authorize]
public sealed class SpecsController : ControllerBase
{
    private readonly ISpecThresholdService _specService;

    public SpecsController(ISpecThresholdService specService)
    {
        _specService = specService;
    }

    /// <summary>Get all spec thresholds (one per product line).</summary>
    [HttpGet]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> GetAll(CancellationToken ct)
    {
        var specs = await _specService.GetAllAsync(ct);
        return Ok(specs.Select(ToDto));
    }

    /// <summary>Get spec threshold for a product line.</summary>
    [HttpGet("{productLine}")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> GetByProductLine(string productLine, CancellationToken ct)
    {
        if (!Enum.TryParse<ProductLine>(productLine, true, out var pl))
            return BadRequest(new { message = $"Invalid product line '{productLine}'." });

        var spec = await _specService.GetByProductLineAsync(pl, ct);
        if (spec == null)
            return NotFound(new { message = $"No spec threshold for '{productLine}'." });

        return Ok(ToDto(spec));
    }

    /// <summary>Update spec threshold for a product line (ProductionManager only).</summary>
    [HttpPut("{productLine}")]
    [Authorize(Roles = $"{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> Update(string productLine, [FromBody] UpdateSpecRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<ProductLine>(productLine, true, out var pl))
            return BadRequest(new { message = $"Invalid product line '{productLine}'." });

        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var spec = await _specService.UpdateAsync(pl, request, userId, ct);
            return Ok(ToDto(spec));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Evaluate a panel against spec thresholds and return out-of-spec flags.</summary>
    [HttpPost("evaluate/{batchCode}")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> EvaluatePanel(string batchCode, CancellationToken ct)
    {
        try
        {
            var result = await _specService.EvaluatePanelAsync(batchCode, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private static object ToDto(SpecThreshold s) => new
    {
        id = s.Id,
        productLine = s.ProductLine.ToString(),
        minFatPercent = s.MinFatPercent,
        maxFatPercent = s.MaxFatPercent,
        minPh = s.MinPh,
        maxPh = s.MaxPh,
        minSnf = s.MinSnf,
        minCorrectedClr = s.MinCorrectedClr,
        updatedBy = s.UpdatedBy,
        updatedAtUtc = s.UpdatedAtUtc
    };
}
