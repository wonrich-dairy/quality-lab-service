using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Application.Specs;

/// <summary>
/// Manages specification thresholds per product type (SCRUM-22).
/// Evaluates a panel's measured values against the thresholds to produce out-of-spec flags.
/// Audit trail on every threshold change.
/// </summary>
public sealed class SpecThresholdService : ISpecThresholdService
{
    private readonly QualityLabDbContext _db;
    private readonly TimeProvider _time;

    public SpecThresholdService(QualityLabDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<IReadOnlyList<SpecThreshold>> GetAllAsync(CancellationToken ct)
    {
        return await _db.SpecThresholds
            .OrderBy(s => s.ProductLine)
            .ToListAsync(ct);
    }

    public async Task<SpecThreshold?> GetByProductLineAsync(ProductLine productLine, CancellationToken ct)
    {
        return await _db.SpecThresholds
            .FirstOrDefaultAsync(s => s.ProductLine == productLine, ct);
    }

    public async Task<SpecThreshold> UpdateAsync(ProductLine productLine, UpdateSpecRequest request, string userId, CancellationToken ct)
    {
        var spec = await _db.SpecThresholds
            .FirstOrDefaultAsync(s => s.ProductLine == productLine, ct)
            ?? throw new InvalidOperationException($"No spec threshold found for product line '{productLine}'.");

        var now = _time.GetUtcNow().UtcDateTime;

        // Audit each changed field
        AuditIfChanged(spec.Id, "MinFatPercent", spec.MinFatPercent, request.MinFatPercent, userId, now);
        AuditIfChanged(spec.Id, "MaxFatPercent", spec.MaxFatPercent, request.MaxFatPercent, userId, now);
        AuditIfChanged(spec.Id, "MinPh", spec.MinPh, request.MinPh, userId, now);
        AuditIfChanged(spec.Id, "MaxPh", spec.MaxPh, request.MaxPh, userId, now);
        AuditIfChanged(spec.Id, "MinSnf", spec.MinSnf, request.MinSnf, userId, now);
        AuditIfChanged(spec.Id, "MinCorrectedClr", spec.MinCorrectedClr, request.MinCorrectedClr, userId, now);

        // Apply changes
        spec.MinFatPercent = request.MinFatPercent;
        spec.MaxFatPercent = request.MaxFatPercent;
        spec.MinPh = request.MinPh;
        spec.MaxPh = request.MaxPh;
        spec.MinSnf = request.MinSnf;
        spec.MinCorrectedClr = request.MinCorrectedClr;
        spec.UpdatedBy = userId;
        spec.UpdatedAtUtc = now;

        await _db.SaveChangesAsync(ct);
        return spec;
    }

    public async Task<SpecEvaluationResult> EvaluatePanelAsync(string batchCode, CancellationToken ct)
    {
        var panel = await _db.ChemicalPanels
            .Where(p => p.BatchCode == batchCode)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No chemical panel found for batch '{batchCode}'.");

        var spec = await _db.SpecThresholds
            .FirstOrDefaultAsync(s => s.ProductLine == panel.ProductLine, ct)
            ?? throw new InvalidOperationException($"No spec threshold configured for product line '{panel.ProductLine}'.");

        var flags = new List<OutOfSpecFlag>();

        // Fat
        if (spec.MinFatPercent.HasValue && panel.FatPercent < spec.MinFatPercent.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "FatPercent", ActualValue = panel.FatPercent, Limit = "Min", LimitValue = spec.MinFatPercent.Value });
        if (spec.MaxFatPercent.HasValue && panel.FatPercent > spec.MaxFatPercent.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "FatPercent", ActualValue = panel.FatPercent, Limit = "Max", LimitValue = spec.MaxFatPercent.Value });

        // pH
        if (spec.MinPh.HasValue && panel.Ph < spec.MinPh.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "Ph", ActualValue = panel.Ph, Limit = "Min", LimitValue = spec.MinPh.Value });
        if (spec.MaxPh.HasValue && panel.Ph > spec.MaxPh.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "Ph", ActualValue = panel.Ph, Limit = "Max", LimitValue = spec.MaxPh.Value });

        // SNF (liquid lines only)
        if (spec.MinSnf.HasValue && panel.Snf.HasValue && panel.Snf.Value < spec.MinSnf.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "SNF", ActualValue = panel.Snf.Value, Limit = "Min", LimitValue = spec.MinSnf.Value });

        // CLR (liquid lines only)
        if (spec.MinCorrectedClr.HasValue && panel.CorrectedClr.HasValue && panel.CorrectedClr.Value < spec.MinCorrectedClr.Value)
            flags.Add(new OutOfSpecFlag { Parameter = "CorrectedCLR", ActualValue = panel.CorrectedClr.Value, Limit = "Min", LimitValue = spec.MinCorrectedClr.Value });

        var hasFlags = flags.Count > 0;

        // Update the panel with out-of-spec info
        panel.HasOutOfSpecFlags = hasFlags;
        panel.OutOfSpecFlagsJson = hasFlags ? JsonSerializer.Serialize(flags) : null;
        await _db.SaveChangesAsync(ct);

        return new SpecEvaluationResult
        {
            BatchCode = batchCode,
            HasOutOfSpecFlags = hasFlags,
            Flags = flags
        };
    }

    private void AuditIfChanged(Guid entityId, string parameter, decimal? oldValue, decimal? newValue, string userId, DateTime now)
    {
        if (oldValue == newValue) return;

        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "SpecThreshold",
            EntityId = entityId,
            Action = "Update",
            Parameter = parameter,
            OldValue = oldValue?.ToString(),
            NewValue = newValue?.ToString(),
            UserId = userId,
            TimestampUtc = now
        });
    }
}
