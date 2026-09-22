using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Application.Panels;

/// <summary>
/// Chemical panel recording — validates per product line, derives CLR/SNF/TS for liquid lines,
/// rejects lactometer on fermented lines, rejects direct SNF/TS submission.
/// A re-test creates a new version, not an overwrite.
/// </summary>
public sealed class ChemicalPanelService : IChemicalPanelService
{
    private readonly QualityLabDbContext _db;
    private readonly TimeProvider _time;

    public ChemicalPanelService(QualityLabDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<BatchWorkItem?> GetBatchAsync(string batchCode, CancellationToken ct)
    {
        return await _db.BatchWorkItems
            .Include(b => b.Panels.OrderByDescending(p => p.Version))
            .Include(b => b.SensoryEvaluation)
            .Include(b => b.Determinations.OrderByDescending(d => d.DeterminedAtUtc))
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct);
    }

    public async Task<IReadOnlyList<BatchWorkItem>> GetWorkQueueAsync(CancellationToken ct)
    {
        return await _db.BatchWorkItems
            .Include(b => b.Panels)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<BatchWorkItem> CreateBatchAsync(CreateBatchRequest request, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BatchCode))
            throw new InvalidOperationException("Batch code is required.");
        if (string.IsNullOrWhiteSpace(request.DispatchNumber))
            throw new InvalidOperationException("Dispatch number is required.");

        if (!Enum.TryParse<ProductLine>(request.ProductLine, true, out var productLine))
            throw new InvalidOperationException($"Invalid product line '{request.ProductLine}'. Must be one of: FM, FLM, SY, SK, DY, CD.");

        var existing = await _db.BatchWorkItems.FirstOrDefaultAsync(b => b.BatchCode == request.BatchCode, ct);
        if (existing != null)
            throw new InvalidOperationException($"Batch '{request.BatchCode}' already exists.");

        var now = _time.GetUtcNow().UtcDateTime;
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = request.BatchCode,
            DispatchNumber = request.DispatchNumber,
            ProductLine = productLine,
            StoringTankCode = request.StoringTankCode,
            Status = BatchStatus.AwaitingPanel,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.BatchWorkItems.Add(batch);
        await _db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<ChemicalPanel> RecordPanelAsync(string batchCode, RecordPanelRequest request, string userId, CancellationToken ct)
    {
        var batch = await _db.BatchWorkItems
            .Include(b => b.Panels)
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct);

        if (batch == null)
            throw new InvalidOperationException($"Batch '{batchCode}' not found in the work queue.");

        // Validate per product line
        ValidateForProductLine(batch.ProductLine, request);

        // Derive values for liquid lines
        decimal? correctedClr = null;
        decimal? snf = null;
        decimal? ts = null;

        if (batch.ProductLine.IsLiquid())
        {
            correctedClr = CalculateCorrectedClr(request.LactometerReading!.Value, request.TemperatureCelsius!.Value);
            snf = CalculateSnf(request.FatPercent, correctedClr.Value);
            ts = CalculateTs(snf.Value, request.FatPercent);
        }

        // Determine version (re-test creates a new version)
        var currentMaxVersion = batch.Panels.Any() ? batch.Panels.Max(p => p.Version) : 0;

        var now = _time.GetUtcNow().UtcDateTime;
        var panel = new ChemicalPanel
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batchCode,
            DispatchNumber = batch.DispatchNumber,
            ProductLine = batch.ProductLine,
            FatPercent = Math.Round(request.FatPercent, 2),
            LactometerReading = request.LactometerReading.HasValue ? Math.Round(request.LactometerReading.Value, 2) : null,
            TemperatureCelsius = request.TemperatureCelsius.HasValue ? Math.Round(request.TemperatureCelsius.Value, 2) : null,
            Ph = Math.Round(request.Ph, 2),
            CorrectedClr = correctedClr.HasValue ? Math.Round(correctedClr.Value, 2) : null,
            Snf = snf.HasValue ? Math.Round(snf.Value, 2) : null,
            Ts = ts.HasValue ? Math.Round(ts.Value, 2) : null,
            Version = currentMaxVersion + 1,
            TestedBy = userId,
            TestedAtUtc = now,
            CreatedAtUtc = now
        };

        _db.ChemicalPanels.Add(panel);

        // Update batch status
        if (batch.Status == BatchStatus.AwaitingPanel)
        {
            batch.Status = BatchStatus.PanelRecorded;
            batch.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
        return panel;
    }

    public async Task<ChemicalPanel?> GetLatestPanelAsync(string batchCode, CancellationToken ct)
    {
        return await _db.ChemicalPanels
            .Where(p => p.BatchCode == batchCode)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ChemicalPanel>> GetPanelVersionsAsync(string batchCode, CancellationToken ct)
    {
        return await _db.ChemicalPanels
            .Where(p => p.BatchCode == batchCode)
            .OrderByDescending(p => p.Version)
            .ToListAsync(ct);
    }

    private static void ValidateForProductLine(ProductLine productLine, RecordPanelRequest request)
    {
        // Reject direct SNF/TS submission — these are calculated
        if (request.Snf.HasValue)
            throw new InvalidOperationException("SNF is calculated from the lactometer reading and fat — do not supply it directly.");
        if (request.Ts.HasValue)
            throw new InvalidOperationException("TS is calculated from SNF and fat — do not supply it directly.");

        // Validate physical ranges
        if (request.FatPercent < 0 || request.FatPercent > 15)
            throw new InvalidOperationException("Fat percent must be between 0 and 15.");
        if (request.Ph < 2.5m || request.Ph > 9.0m)
            throw new InvalidOperationException("pH must be between 2.5 and 9.0.");

        if (productLine.IsLiquid())
        {
            // Liquid lines require lactometer and temperature
            if (!request.LactometerReading.HasValue)
                throw new InvalidOperationException("Lactometer reading is required for liquid product lines (FM, FLM).");
            if (!request.TemperatureCelsius.HasValue)
                throw new InvalidOperationException("Temperature is required for liquid product lines (FM, FLM).");
            if (request.TemperatureCelsius < -3 || request.TemperatureCelsius > 50)
                throw new InvalidOperationException("Temperature must be between -3 and 50 °C.");
        }
        else
        {
            // Fermented lines must NOT supply lactometer/temperature
            if (request.LactometerReading.HasValue)
                throw new InvalidOperationException($"A lactometer reading does not apply to product line {productLine}. Only fat and pH are measured on fermented lines.");
            if (request.TemperatureCelsius.HasValue)
                throw new InvalidOperationException($"Temperature does not apply to product line {productLine}. Only fat and pH are measured on fermented lines.");
        }
    }

    /// <summary>
    /// Corrected CLR = Raw CLR + 0.2 × (Temperature - 27.5)
    /// Standard correction factor used by dairy industry.
    /// </summary>
    internal static decimal CalculateCorrectedClr(decimal rawClr, decimal temperatureCelsius)
    {
        return rawClr + 0.2m * (temperatureCelsius - 27.5m);
    }

    /// <summary>
    /// SNF = (CLR × 0.25) + (Fat × 0.22) + 0.72
    /// Same formula as processing service's MockQualityTestClient.
    /// </summary>
    internal static decimal CalculateSnf(decimal fatPercent, decimal correctedClr)
    {
        return (correctedClr * 0.25m) + (fatPercent * 0.22m) + 0.72m;
    }

    /// <summary>TS = SNF + Fat</summary>
    internal static decimal CalculateTs(decimal snf, decimal fatPercent)
    {
        return snf + fatPercent;
    }
}
