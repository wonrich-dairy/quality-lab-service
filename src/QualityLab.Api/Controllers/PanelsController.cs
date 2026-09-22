using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QualityLab.Api.Application.Panels;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Controllers;

/// <summary>
/// Chemical panel endpoints for SCRUM-20: record panels per product line,
/// dynamic fields based on liquid vs fermented, derived CLR/SNF/TS.
/// </summary>
[ApiController]
[Route("api/panels")]
[Authorize]
public sealed class PanelsController : ControllerBase
{
    private readonly IChemicalPanelService _panelService;

    public PanelsController(IChemicalPanelService panelService)
    {
        _panelService = panelService;
    }

    /// <summary>List all batches in the lab work queue.</summary>
    [HttpGet("work-queue")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager},{WonrichRoles.ProcessingTechnician}")]
    public async Task<ActionResult<object>> GetWorkQueue(CancellationToken ct)
    {
        var items = await _panelService.GetWorkQueueAsync(ct);
        return Ok(items.Select(ToBatchDto));
    }

    /// <summary>Get a batch by its batch code.</summary>
    [HttpGet("batch/{batchCode}")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager},{WonrichRoles.ProcessingTechnician}")]
    public async Task<ActionResult<object>> GetBatch(string batchCode, CancellationToken ct)
    {
        var batch = await _panelService.GetBatchAsync(batchCode, ct);
        if (batch == null)
            return NotFound(new { message = $"Batch '{batchCode}' not found." });

        return Ok(ToBatchDto(batch));
    }

    /// <summary>Create a batch in the work queue (normally from ProcessingCompleted event, or manual).</summary>
    [HttpPost("batch")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager}")]
    public async Task<ActionResult<object>> CreateBatch([FromBody] CreateBatchRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var batch = await _panelService.CreateBatchAsync(request, userId, ct);
            return CreatedAtAction(nameof(GetBatch), new { batchCode = batch.BatchCode }, ToBatchDto(batch));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Record a chemical panel for a batch. Fields depend on product line.</summary>
    [HttpPost("{batchCode}")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator}")]
    public async Task<ActionResult<object>> RecordPanel(string batchCode, [FromBody] RecordPanelRequest request, CancellationToken ct)
    {
        try
        {
            var userId = User.UserId() ?? User.Identity?.Name ?? "unknown";
            var panel = await _panelService.RecordPanelAsync(batchCode, request, userId, ct);
            return Ok(ToPanelDto(panel));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Get the latest chemical panel for a batch.</summary>
    [HttpGet("{batchCode}")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager},{WonrichRoles.ProcessingTechnician}")]
    public async Task<ActionResult<object>> GetLatestPanel(string batchCode, CancellationToken ct)
    {
        var panel = await _panelService.GetLatestPanelAsync(batchCode, ct);
        if (panel == null)
            return NotFound(new { message = $"No chemical panel recorded for batch '{batchCode}'." });

        return Ok(ToPanelDto(panel));
    }

    /// <summary>Get all panel versions for a batch (re-tests).</summary>
    [HttpGet("{batchCode}/versions")]
    [Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.SystemAdministrator},{WonrichRoles.ProductionManager}")]
    public async Task<ActionResult<object>> GetPanelVersions(string batchCode, CancellationToken ct)
    {
        var panels = await _panelService.GetPanelVersionsAsync(batchCode, ct);
        return Ok(panels.Select(ToPanelDto));
    }

    /// <summary>Get the fields required for a product line (for frontend dynamic form).</summary>
    [HttpGet("fields/{productLine}")]
    [AllowAnonymous]
    public ActionResult<object> GetFieldsForProductLine(string productLine)
    {
        if (!Enum.TryParse<ProductLine>(productLine, true, out var line))
            return BadRequest(new { message = $"Invalid product line '{productLine}'." });

        return Ok(new
        {
            productLine = line.ToString(),
            isLiquid = line.IsLiquid(),
            isFermented = line.IsFermented(),
            hasTexture = line.HasTexture(),
            fields = line.IsLiquid()
                ? new[] { "fatPercent", "lactometerReading", "temperatureCelsius", "ph" }
                : new[] { "fatPercent", "ph" },
            derivedFields = line.IsLiquid()
                ? new[] { "correctedClr", "snf", "ts" }
                : Array.Empty<string>()
        });
    }

    private static object ToBatchDto(BatchWorkItem batch) => new
    {
        id = batch.Id,
        batchCode = batch.BatchCode,
        dispatchNumber = batch.DispatchNumber,
        productLine = batch.ProductLine.ToString(),
        storingTankCode = batch.StoringTankCode,
        status = batch.Status.ToString(),
        completionTimeUtc = batch.CompletionTimeUtc,
        createdAtUtc = batch.CreatedAtUtc,
        updatedAtUtc = batch.UpdatedAtUtc,
        hasPanels = batch.Panels.Any(),
        latestPanelVersion = batch.Panels.Any() ? batch.Panels.Max(p => p.Version) : 0,
        hasSensory = batch.SensoryEvaluation != null,
        hasDetermination = batch.Determinations.Any(d => !d.IsSuperseded)
    };

    private static object ToPanelDto(ChemicalPanel panel) => new
    {
        id = panel.Id,
        batchWorkItemId = panel.BatchWorkItemId,
        batchCode = panel.BatchCode,
        dispatchNumber = panel.DispatchNumber,
        productLine = panel.ProductLine.ToString(),
        fatPercent = panel.FatPercent,
        lactometerReading = panel.LactometerReading,
        temperatureCelsius = panel.TemperatureCelsius,
        ph = panel.Ph,
        correctedClr = panel.CorrectedClr,
        snf = panel.Snf,
        ts = panel.Ts,
        version = panel.Version,
        hasOutOfSpecFlags = panel.HasOutOfSpecFlags,
        outOfSpecFlagsJson = panel.OutOfSpecFlagsJson,
        testedBy = panel.TestedBy,
        testedAtUtc = panel.TestedAtUtc,
        createdAtUtc = panel.CreatedAtUtc,
        isLocked = panel.IsLocked
    };
}
