using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Application.Determinations;

/// <summary>
/// Manages pass/fail determinations for batches (SCRUM-23).
/// - Submit: requires panel + sensory to exist; validates reason codes for Fail
/// - Pass with out-of-spec flags requires an override reason
/// - Lock: sets sensory.Locked and prevents further edits
/// - Supersede: never edit in place; create a new determination and chain
/// </summary>
public sealed class DeterminationService : IDeterminationService
{
    private readonly QualityLabDbContext _db;
    private readonly TimeProvider _time;

    public DeterminationService(QualityLabDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    // Standard reason codes used when failing a batch
    private static readonly HashSet<string> ValidReasonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOW_FAT", "HIGH_FAT", "LOW_PH", "HIGH_PH", "LOW_SNF", "LOW_CLR",
        "SENSORY_TASTE", "SENSORY_SMELL", "SENSORY_COLOUR", "SENSORY_APPEARANCE", "SENSORY_TEXTURE",
        "OUT_OF_SPEC", "CONTAMINATION", "OTHER"
    };

    public async Task<Determination> SubmitAsync(string batchCode, SubmitDeterminationRequest request, string userId, CancellationToken ct)
    {
        // Validate batch exists
        var batch = await _db.BatchWorkItems
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct)
            ?? throw new InvalidOperationException($"Batch '{batchCode}' not found.");

        // Check panel exists
        var panel = await _db.ChemicalPanels
            .Where(p => p.BatchCode == batchCode)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No chemical panel recorded for batch '{batchCode}'. Record a panel before submitting determination.");

        // Check sensory exists
        var sensory = await _db.SensoryEvaluations
            .FirstOrDefaultAsync(s => s.BatchWorkItemId == batch.Id, ct)
            ?? throw new InvalidOperationException($"No sensory evaluation recorded for batch '{batchCode}'. Record a sensory evaluation before submitting determination.");

        // Check no existing active determination
        var existing = await _db.Determinations
            .Where(d => d.BatchCode == batchCode && d.SupersededById == null)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
            throw new InvalidOperationException($"Batch '{batchCode}' already has an active determination. Use supersede to change the result.");

        // Validate the request
        ValidateRequest(request, panel);

        var now = _time.GetUtcNow().UtcDateTime;

        var determination = new Determination
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batchCode,
            Result = request.Result,
            ReasonCodesJson = request.ReasonCodes.Count > 0 ? JsonSerializer.Serialize(request.ReasonCodes) : null,
            OverrideReason = request.OverrideReason,
            Notes = request.Notes,
            DeterminedBy = userId,
            DeterminedAtUtc = now,
            CreatedAtUtc = now
        };

        _db.Determinations.Add(determination);

        // Lock sensory evaluation
        sensory.IsLocked = true;

        // Update batch status
        batch.Status = request.Result == DeterminationResult.Pass
            ? BatchStatus.Cleared
            : BatchStatus.Failed;
        batch.UpdatedAtUtc = now;

        // Audit
        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Determination",
            EntityId = determination.Id,
            Action = "Submit",
            Parameter = "Result",
            NewValue = request.Result.ToString(),
            UserId = userId,
            TimestampUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return determination;
    }

    public async Task<Determination?> GetAsync(string batchCode, CancellationToken ct)
    {
        return await _db.Determinations
            .Where(d => d.BatchCode == batchCode && d.SupersededById == null)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Determination> SupersedeAsync(string batchCode, SubmitDeterminationRequest request, string userId, CancellationToken ct)
    {
        var batch = await _db.BatchWorkItems
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct)
            ?? throw new InvalidOperationException($"Batch '{batchCode}' not found.");

        var panel = await _db.ChemicalPanels
            .Where(p => p.BatchCode == batchCode)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No chemical panel recorded for batch '{batchCode}'.");

        var existing = await _db.Determinations
            .Where(d => d.BatchCode == batchCode && d.SupersededById == null)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"No active determination found for batch '{batchCode}' to supersede.");

        ValidateRequest(request, panel);

        var now = _time.GetUtcNow().UtcDateTime;

        var newDetermination = new Determination
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batchCode,
            Result = request.Result,
            ReasonCodesJson = request.ReasonCodes.Count > 0 ? JsonSerializer.Serialize(request.ReasonCodes) : null,
            OverrideReason = request.OverrideReason,
            Notes = request.Notes,
            SupersededDeterminationId = existing.Id,
            DeterminedBy = userId,
            DeterminedAtUtc = now,
            CreatedAtUtc = now
        };

        _db.Determinations.Add(newDetermination);

        // Chain: mark old as superseded
        existing.SupersededById = newDetermination.Id;

        // Update batch status
        batch.Status = request.Result == DeterminationResult.Pass
            ? BatchStatus.Cleared
            : BatchStatus.Failed;
        batch.UpdatedAtUtc = now;

        // Audit
        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "Determination",
            EntityId = newDetermination.Id,
            Action = "Supersede",
            Parameter = "Result",
            OldValue = existing.Result.ToString(),
            NewValue = request.Result.ToString(),
            UserId = userId,
            TimestampUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return newDetermination;
    }

    public async Task<IReadOnlyList<Determination>> GetHistoryAsync(string batchCode, CancellationToken ct)
    {
        return await _db.Determinations
            .Where(d => d.BatchCode == batchCode)
            .OrderByDescending(d => d.DeterminedAtUtc)
            .ToListAsync(ct);
    }

    private static void ValidateRequest(SubmitDeterminationRequest request, ChemicalPanel panel)
    {
        // Fail must have at least one reason code
        if (request.Result == DeterminationResult.Fail && request.ReasonCodes.Count == 0)
            throw new InvalidOperationException("At least one reason code is required when failing a batch.");

        // Validate reason codes
        foreach (var code in request.ReasonCodes)
        {
            if (!ValidReasonCodes.Contains(code))
                throw new InvalidOperationException($"Invalid reason code '{code}'. Valid codes: {string.Join(", ", ValidReasonCodes)}");
        }

        // Pass with out-of-spec flags requires override reason
        if (request.Result == DeterminationResult.Pass && panel.HasOutOfSpecFlags && string.IsNullOrWhiteSpace(request.OverrideReason))
            throw new InvalidOperationException("An override reason is required when passing a batch with out-of-spec flags.");
    }
}
