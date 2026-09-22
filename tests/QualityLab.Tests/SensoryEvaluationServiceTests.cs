using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Application.Sensory;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

/// <summary>
/// Unit tests for SensoryEvaluationService covering SCRUM-21 acceptance criteria:
/// - Fixed scale (Acceptable, Borderline, Unacceptable) for taste, smell, colour, appearance
/// - Texture required on fermented lines only
/// - Notes required for non-Acceptable grades
/// - Editable until determination locks it
/// - Audit trail on edits
/// </summary>
public class SensoryEvaluationServiceTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly SensoryEvaluationService _service;

    public SensoryEvaluationServiceTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"SensoryTest_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new SensoryEvaluationService(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private async Task<BatchWorkItem> SeedBatch(ProductLine line, string code)
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = code,
            DispatchNumber = $"DSP-{code}",
            ProductLine = line,
            Status = BatchStatus.PanelRecorded,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);
        await _db.SaveChangesAsync();
        return batch;
    }

    private static RecordSensoryRequest AllAcceptable(string? texture = null) => new()
    {
        Taste = "Acceptable",
        Smell = "Acceptable",
        Colour = "Acceptable",
        Appearance = "Acceptable",
        Texture = texture
    };

    // ── AC: Fixed scale enforced ────────────────────────────────────────────

    [Fact]
    public async Task RecordEvaluation_AllAcceptable_Success()
    {
        await SeedBatch(ProductLine.FM, "SENSE-FM");
        var result = await _service.RecordEvaluationAsync("SENSE-FM", AllAcceptable(), "tech-1", CancellationToken.None);

        Assert.Equal(SensoryGrade.Acceptable, result.Taste);
        Assert.Equal(SensoryGrade.Acceptable, result.Smell);
        Assert.Equal(SensoryGrade.Acceptable, result.Colour);
        Assert.Equal(SensoryGrade.Acceptable, result.Appearance);
        Assert.Null(result.Texture); // liquid line — no texture
    }

    [Fact]
    public async Task RecordEvaluation_InvalidGrade_Throws()
    {
        await SeedBatch(ProductLine.FM, "BAD-GRADE");
        var req = AllAcceptable();
        req.Taste = "Excellent"; // not a valid grade

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync("BAD-GRADE", req, "tech-1", CancellationToken.None));

        Assert.Contains("not a valid grade", ex.Message);
    }

    // ── AC: Notes required for non-Acceptable ───────────────────────────────

    [Fact]
    public async Task RecordEvaluation_Borderline_WithNote_Success()
    {
        await SeedBatch(ProductLine.FM, "BORDER-OK");
        var req = AllAcceptable();
        req.Taste = "Borderline";
        req.TasteNote = "Slightly sour aftertaste";

        var result = await _service.RecordEvaluationAsync("BORDER-OK", req, "tech-1", CancellationToken.None);
        Assert.Equal(SensoryGrade.Borderline, result.Taste);
        Assert.Equal("Slightly sour aftertaste", result.TasteNote);
    }

    [Fact]
    public async Task RecordEvaluation_Borderline_WithoutNote_Throws()
    {
        await SeedBatch(ProductLine.FM, "BORDER-BAD");
        var req = AllAcceptable();
        req.Smell = "Borderline";
        // No note!

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync("BORDER-BAD", req, "tech-1", CancellationToken.None));

        Assert.Contains("note is required", ex.Message);
    }

    [Fact]
    public async Task RecordEvaluation_Unacceptable_WithoutNote_Throws()
    {
        await SeedBatch(ProductLine.FM, "UNACC-BAD");
        var req = AllAcceptable();
        req.Colour = "Unacceptable";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync("UNACC-BAD", req, "tech-1", CancellationToken.None));

        Assert.Contains("note is required", ex.Message);
    }

    // ── AC: Texture required on fermented lines ────────────────────────────

    [Theory]
    [InlineData(ProductLine.SY)]
    [InlineData(ProductLine.DY)]
    [InlineData(ProductLine.SK)]
    [InlineData(ProductLine.CD)]
    public async Task RecordEvaluation_FermentedLine_TextureRequired(ProductLine line)
    {
        await SeedBatch(line, $"TEX-{line}");

        // Without texture → should fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync($"TEX-{line}", AllAcceptable(), "tech-1", CancellationToken.None));

        Assert.Contains("Texture is required", ex.Message);
    }

    [Fact]
    public async Task RecordEvaluation_FermentedLine_WithTexture_Success()
    {
        await SeedBatch(ProductLine.SY, "TEX-OK");
        var result = await _service.RecordEvaluationAsync("TEX-OK", AllAcceptable("Acceptable"), "tech-1", CancellationToken.None);

        Assert.Equal(SensoryGrade.Acceptable, result.Texture);
    }

    [Theory]
    [InlineData(ProductLine.FM)]
    [InlineData(ProductLine.FLM)]
    public async Task RecordEvaluation_LiquidLine_NoTexture(ProductLine line)
    {
        await SeedBatch(line, $"NOTEX-{line}");
        var result = await _service.RecordEvaluationAsync($"NOTEX-{line}", AllAcceptable(), "tech-1", CancellationToken.None);

        Assert.Null(result.Texture);
    }

    // ── AC: Duplicate evaluation rejected ───────────────────────────────────

    [Fact]
    public async Task RecordEvaluation_Duplicate_Throws()
    {
        await SeedBatch(ProductLine.FM, "DUP-SENSE");
        await _service.RecordEvaluationAsync("DUP-SENSE", AllAcceptable(), "tech-1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync("DUP-SENSE", AllAcceptable(), "tech-1", CancellationToken.None));

        Assert.Contains("already exists", ex.Message);
    }

    // ── AC: Editable before determination ──────────────────────────────────

    [Fact]
    public async Task UpdateEvaluation_BeforeDetermination_Success()
    {
        await SeedBatch(ProductLine.FM, "UPD-OK");
        await _service.RecordEvaluationAsync("UPD-OK", AllAcceptable(), "tech-1", CancellationToken.None);

        var req = AllAcceptable();
        req.Smell = "Borderline";
        req.SmellNote = "Slight off-odour";

        var updated = await _service.UpdateEvaluationAsync("UPD-OK", req, "tech-2", CancellationToken.None);
        Assert.Equal(SensoryGrade.Borderline, updated.Smell);
        Assert.Equal("Slight off-odour", updated.SmellNote);
        Assert.Equal("tech-2", updated.EvaluatedBy);
    }

    // ── AC: Locked after determination ─────────────────────────────────────

    [Fact]
    public async Task UpdateEvaluation_AfterDetermination_Locked()
    {
        var batch = await SeedBatch(ProductLine.FM, "LOCKED");
        await _service.RecordEvaluationAsync("LOCKED", AllAcceptable(), "tech-1", CancellationToken.None);

        // Simulate locking by marking evaluation as locked
        var eval = await _db.SensoryEvaluations.FirstAsync(e => e.BatchWorkItemId == batch.Id);
        eval.IsLocked = true;
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UpdateEvaluationAsync("LOCKED", AllAcceptable(), "tech-2", CancellationToken.None));

        Assert.Contains("locked", ex.Message);
    }

    // ── AC: Audit trail on update ──────────────────────────────────────────

    [Fact]
    public async Task UpdateEvaluation_CreatesAuditEntries()
    {
        await SeedBatch(ProductLine.FM, "AUDIT-SENSE");
        await _service.RecordEvaluationAsync("AUDIT-SENSE", AllAcceptable(), "tech-1", CancellationToken.None);

        var req = AllAcceptable();
        req.Taste = "Borderline";
        req.TasteNote = "Changed taste";

        await _service.UpdateEvaluationAsync("AUDIT-SENSE", req, "tech-2", CancellationToken.None);

        var audits = await _db.AuditEntries
            .Where(a => a.EntityType == "SensoryEvaluation" && a.Parameter == "Taste")
            .ToListAsync();

        Assert.Single(audits);
        Assert.Equal("Acceptable", audits[0].OldValue);
        Assert.Equal("Borderline", audits[0].NewValue);
        Assert.Equal("tech-2", audits[0].UserId);
    }

    // ── AC: Batch not found ────────────────────────────────────────────────

    [Fact]
    public async Task RecordEvaluation_BatchNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordEvaluationAsync("GHOST", AllAcceptable(), "tech-1", CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    // ── AC: Identity and timestamp recorded ────────────────────────────────

    [Fact]
    public async Task RecordEvaluation_RecordsUserAndTimestamp()
    {
        await SeedBatch(ProductLine.FM, "WHO-WHEN");
        var result = await _service.RecordEvaluationAsync("WHO-WHEN", AllAcceptable(), "analyst-7", CancellationToken.None);

        Assert.Equal("analyst-7", result.EvaluatedBy);
        Assert.True(result.EvaluatedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    // ── Formula unit tests (ValidateAttribute) ─────────────────────────────

    [Theory]
    [InlineData("Acceptable")]
    [InlineData("Borderline")]
    [InlineData("Unacceptable")]
    public void ParseGrade_ValidValues(string grade)
    {
        var result = SensoryEvaluationService.ParseGrade(grade);
        Assert.True(Enum.IsDefined(result));
    }
}
