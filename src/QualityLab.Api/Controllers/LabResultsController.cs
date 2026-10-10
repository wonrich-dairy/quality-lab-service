using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QualityLab.Api.Application.LabResults;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Api.Controllers;

/// <summary>
/// Historical lab results by product line (SCRUM-25). Read-only: GET endpoints only, reading
/// this service's own database. Filters (all optional, combined with AND):
/// <c>productLine</c> (FM, FLM, SY, SK, DY, CD), <c>from</c> / <c>to</c> (inclusive UTC dates,
/// yyyy-MM-dd, on the panel's tested-at), <c>result</c> (Pass, Fail, Pending) and
/// <c>reasonCode</c> (e.g. LOW_FAT).
/// </summary>
[ApiController]
[Route("api/lab-results")]
[Authorize(Roles = $"{WonrichRoles.QualityAnalyst},{WonrichRoles.ProductionManager},{WonrichRoles.SystemAdministrator}")]
[Produces("application/json")]
public sealed class LabResultsController : ControllerBase
{
    private readonly ILabResultsService _service;
    private readonly TimeProvider _time;

    public LabResultsController(ILabResultsService service, TimeProvider time)
    {
        _service = service;
        _time = time;
    }

    /// <summary>Matching results, 50 per page, newest first, with the total count.</summary>
    /// <response code="200">A page of results. An empty <c>items</c> list means nothing matched.</response>
    /// <response code="400">A filter value was not recognised.</response>
    [HttpGet]
    [ProducesResponseType(typeof(LabResultsPage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LabResultsPage>> Search(
        [FromQuery] string? productLine,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? result,
        [FromQuery] string? reasonCode,
        [FromQuery] int page = 1,
        CancellationToken ct = default)
    {
        if (!TryParse(productLine, from, to, result, reasonCode, out var filter, out var error))
            return error!;

        return Ok(await _service.SearchAsync(filter!, page, ct));
    }

    /// <summary>Pass / fail / pending totals and failure counts per reason code (highest first) for the filtered set.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(LabResultsSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LabResultsSummary>> Summary(
        [FromQuery] string? productLine,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? result,
        [FromQuery] string? reasonCode,
        CancellationToken ct = default)
    {
        if (!TryParse(productLine, from, to, result, reasonCode, out var filter, out var error))
            return error!;

        return Ok(await _service.SummarizeAsync(filter!, ct));
    }

    /// <summary>
    /// CSV of every matching row (not only the current page). Columns a product line does not
    /// measure read "N/A". The file name carries the filters and the export date.
    /// </summary>
    [HttpGet("export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Export(
        [FromQuery] string? productLine,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? result,
        [FromQuery] string? reasonCode,
        CancellationToken ct = default)
    {
        if (!TryParse(productLine, from, to, result, reasonCode, out var filter, out var error))
            return error!;

        var rows = await _service.ExportAsync(filter!, ct);
        var csv = LabResultsCsv.Write(rows);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var fileName = LabResultsCsv.FileName(filter!, today);

        // UTF-8 with BOM so Excel opens it with the right encoding.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private bool TryParse(string? productLine, DateOnly? from, DateOnly? to, string? result, string? reasonCode,
        out LabResultsFilter? filter, out ActionResult? error)
    {
        try
        {
            filter = LabResultsFilter.Parse(productLine, from, to, result, reasonCode);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            filter = null;
            error = BadRequest(new { message = ex.Message });
            return false;
        }
    }
}
