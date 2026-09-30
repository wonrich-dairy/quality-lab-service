using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;
using Wonrich.QualityPanel;

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

        // Use the shared evaluator from Wonrich.QualityPanel
        var limits = new SpecLimits
        {
            MinFatPercent = spec.MinFatPercent,
            MaxFatPercent = spec.MaxFatPercent,
            MinPh = spec.MinPh,
            MaxPh = spec.MaxPh,
            MinSnf = spec.MinSnf,
            MinCorrectedClr = spec.MinCorrectedClr
        };

        var specFlags = SpecEvaluator.Evaluate(panel.FatPercent, panel.Ph, panel.Snf, panel.CorrectedClr, limits);
        var flags = specFlags.Select(f => new OutOfSpecFlag
        {
            Parameter = f.Parameter,
            ActualValue = f.ActualValue,
            Limit = f.Limit,
            LimitValue = f.LimitValue
        }).ToList();

        var hasFlags = flags.Count > 0;

        // Update the panel with out-of-spec info (snapshot the limit at evaluation time)
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
