using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Application.Sensory;

/// <summary>
/// Sensory evaluation — records taste, smell, colour, appearance (all lines)
/// and texture (fermented lines only) on the fixed Acceptable / Borderline / Unacceptable scale.
/// Notes required for anything below Acceptable. Editable until determination, then locked.
/// </summary>
public sealed class SensoryEvaluationService : ISensoryEvaluationService
{
    private readonly QualityLabDbContext _db;
    private readonly TimeProvider _time;

    public SensoryEvaluationService(QualityLabDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<SensoryEvaluation> RecordEvaluationAsync(string batchCode, RecordSensoryRequest request, string userId, CancellationToken ct)
    {
        var batch = await _db.BatchWorkItems
            .Include(b => b.SensoryEvaluation)
            .Include(b => b.Determinations)
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct)
            ?? throw new InvalidOperationException($"Batch '{batchCode}' not found.");

        if (batch.SensoryEvaluation != null)
            throw new InvalidOperationException($"Sensory evaluation already exists for batch '{batchCode}'. Use update instead.");

        var evaluation = BuildEvaluation(batch, request, userId);
        _db.SensoryEvaluations.Add(evaluation);
        batch.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        return evaluation;
    }

    public async Task<SensoryEvaluation> UpdateEvaluationAsync(string batchCode, RecordSensoryRequest request, string userId, CancellationToken ct)
    {
        var batch = await _db.BatchWorkItems
            .Include(b => b.SensoryEvaluation)
            .Include(b => b.Determinations)
            .FirstOrDefaultAsync(b => b.BatchCode == batchCode, ct)
            ?? throw new InvalidOperationException($"Batch '{batchCode}' not found.");

        var existing = batch.SensoryEvaluation
            ?? throw new InvalidOperationException($"No sensory evaluation exists for batch '{batchCode}'.");

        if (existing.IsLocked)
            throw new InvalidOperationException("The sensory evaluation is locked because a determination has been submitted for this batch.");

        // Validate the new values
        ValidateRequest(batch.ProductLine, request);

        var now = _time.GetUtcNow().UtcDateTime;

        // Audit each changed attribute
        AuditChange(existing.Id, "Taste", existing.Taste.ToString(), request.Taste, userId, now);
        AuditChange(existing.Id, "Smell", existing.Smell.ToString(), request.Smell, userId, now);
        AuditChange(existing.Id, "Colour", existing.Colour.ToString(), request.Colour, userId, now);
        AuditChange(existing.Id, "Appearance", existing.Appearance.ToString(), request.Appearance, userId, now);
        if (batch.ProductLine.HasTexture())
            AuditChange(existing.Id, "Texture", existing.Texture?.ToString(), request.Texture, userId, now);

        // Update
        existing.Taste = ParseGrade(request.Taste);
        existing.TasteNote = request.TasteNote;
        existing.Smell = ParseGrade(request.Smell);
        existing.SmellNote = request.SmellNote;
        existing.Colour = ParseGrade(request.Colour);
        existing.ColourNote = request.ColourNote;
        existing.Appearance = ParseGrade(request.Appearance);
        existing.AppearanceNote = request.AppearanceNote;

        if (batch.ProductLine.HasTexture() && request.Texture != null)
        {
            existing.Texture = ParseGrade(request.Texture);
            existing.TextureNote = request.TextureNote;
        }

        existing.EvaluatedBy = userId;
        existing.EvaluatedAtUtc = now;
        existing.UpdatedAtUtc = now;

        batch.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<SensoryEvaluation?> GetEvaluationAsync(string batchCode, CancellationToken ct)
    {
        return await _db.SensoryEvaluations
            .Include(s => s.BatchWorkItem)
            .Where(s => s.BatchWorkItem.BatchCode == batchCode)
            .FirstOrDefaultAsync(ct);
    }

    private SensoryEvaluation BuildEvaluation(BatchWorkItem batch, RecordSensoryRequest request, string userId)
    {
        ValidateRequest(batch.ProductLine, request);

        var now = _time.GetUtcNow().UtcDateTime;
        var evaluation = new SensoryEvaluation
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            Taste = ParseGrade(request.Taste),
            TasteNote = request.TasteNote,
            Smell = ParseGrade(request.Smell),
            SmellNote = request.SmellNote,
            Colour = ParseGrade(request.Colour),
            ColourNote = request.ColourNote,
            Appearance = ParseGrade(request.Appearance),
            AppearanceNote = request.AppearanceNote,
            EvaluatedBy = userId,
            EvaluatedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        if (batch.ProductLine.HasTexture())
        {
            if (string.IsNullOrWhiteSpace(request.Texture))
                throw new InvalidOperationException("Texture is required for fermented product lines (SY, SK, DY, CD).");

            evaluation.Texture = ParseGrade(request.Texture);
            evaluation.TextureNote = request.TextureNote;
        }

        return evaluation;
    }

    internal static void ValidateRequest(ProductLine productLine, RecordSensoryRequest request)
    {
        // All base attributes required
        ValidateAttribute("taste", request.Taste, request.TasteNote);
        ValidateAttribute("smell", request.Smell, request.SmellNote);
        ValidateAttribute("colour", request.Colour, request.ColourNote);
        ValidateAttribute("appearance", request.Appearance, request.AppearanceNote);

        // Texture required on fermented lines only
        if (productLine.HasTexture())
        {
            if (string.IsNullOrWhiteSpace(request.Texture))
                throw new InvalidOperationException("Texture is required for fermented product lines (SY, SK, DY, CD).");
            ValidateAttribute("texture", request.Texture, request.TextureNote);
        }
    }

    internal static void ValidateAttribute(string name, string? grade, string? note)
    {
        if (string.IsNullOrWhiteSpace(grade))
            throw new InvalidOperationException($"The {name} attribute is required.");

        if (!Enum.TryParse<SensoryGrade>(grade, true, out var parsed))
            throw new InvalidOperationException($"'{grade}' is not a valid grade for {name}. Permitted grades: Acceptable, Borderline, Unacceptable.");

        // Note required for anything below Acceptable
        if (parsed != SensoryGrade.Acceptable && string.IsNullOrWhiteSpace(note))
            throw new InvalidOperationException($"A note is required when {name} is graded '{parsed}' (below Acceptable).");
    }

    internal static SensoryGrade ParseGrade(string value) =>
        Enum.Parse<SensoryGrade>(value, ignoreCase: true);

    private void AuditChange(Guid entityId, string parameter, string? oldValue, string? newValue, string userId, DateTime now)
    {
        if (string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
            return;

        _db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            EntityType = "SensoryEvaluation",
            EntityId = entityId,
            Action = "Update",
            Parameter = parameter,
            OldValue = oldValue,
            NewValue = newValue,
            UserId = userId,
            TimestampUtc = now
        });
    }
}
