using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

public class DeterminationServiceTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly DeterminationService _service;

    public DeterminationServiceTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"DetTest_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new DeterminationService(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private async Task<(BatchWorkItem Batch, ChemicalPanel Panel, SensoryEvaluation Sensory)> SeedFull(
        string batchCode = "B-001", ProductLine line = ProductLine.FM, bool outOfSpec = false)
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(), BatchCode = batchCode, DispatchNumber = $"D-{batchCode}",
            ProductLine = line, Status = BatchStatus.PanelRecorded,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);

        var panel = new ChemicalPanel
        {
            Id = Guid.NewGuid(), BatchWorkItemId = batch.Id, BatchCode = batchCode,
            DispatchNumber = batch.DispatchNumber, ProductLine = line,
            FatPercent = 3.8m, Ph = 6.7m, Version = 1, TestedBy = "tech",
            TestedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow,
            HasOutOfSpecFlags = outOfSpec
        };
        _db.ChemicalPanels.Add(panel);

        var sensory = new SensoryEvaluation
        {
            Id = Guid.NewGuid(), BatchWorkItemId = batch.Id,
            Taste = SensoryGrade.Acceptable,
            Smell = SensoryGrade.Acceptable, Colour = SensoryGrade.Acceptable,
            Appearance = SensoryGrade.Acceptable, EvaluatedBy = "tech",
            EvaluatedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        };
        _db.SensoryEvaluations.Add(sensory);

        await _db.SaveChangesAsync();
        return (batch, panel, sensory);
    }

    // ── AC: Submit pass determination ───────────────────────────────────────

    [Fact]
    public async Task Submit_Pass_Succeeds()
    {
        await SeedFull();
        var det = await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        Assert.Equal(DeterminationResult.Pass, det.Result);
        Assert.Equal("analyst-1", det.DeterminedBy);
    }

    // ── AC: Pass updates batch status to Cleared ────────────────────────────

    [Fact]
    public async Task Submit_Pass_SetsBatchCleared()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        var batch = await _db.BatchWorkItems.FirstAsync(b => b.BatchCode == "B-001");
        Assert.Equal(BatchStatus.Cleared, batch.Status);
    }

    // ── AC: Fail requires reason codes ──────────────────────────────────────

    [Fact]
    public async Task Submit_Fail_NoReasonCodes_Throws()
    {
        await SeedFull();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitAsync("B-001", new SubmitDeterminationRequest
            {
                Result = DeterminationResult.Fail,
                ReasonCodes = new()
            }, "analyst-1", CancellationToken.None));
        Assert.Contains("reason code", ex.Message);
    }

    // ── AC: Fail with valid reason codes ────────────────────────────────────

    [Fact]
    public async Task Submit_Fail_WithReasonCodes_Succeeds()
    {
        await SeedFull();
        var det = await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = new() { "LOW_FAT", "HIGH_PH" }
        }, "analyst-1", CancellationToken.None);

        Assert.Equal(DeterminationResult.Fail, det.Result);
        Assert.Contains("LOW_FAT", det.ReasonCodesJson!);
    }

    // ── AC: Fail updates batch status to Failed ─────────────────────────────

    [Fact]
    public async Task Submit_Fail_SetsBatchFailed()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = new() { "LOW_FAT" }
        }, "analyst-1", CancellationToken.None);

        var batch = await _db.BatchWorkItems.FirstAsync(b => b.BatchCode == "B-001");
        Assert.Equal(BatchStatus.Failed, batch.Status);
    }

    // ── AC: Invalid reason code rejected ────────────────────────────────────

    [Fact]
    public async Task Submit_InvalidReasonCode_Throws()
    {
        await SeedFull();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitAsync("B-001", new SubmitDeterminationRequest
            {
                Result = DeterminationResult.Fail,
                ReasonCodes = new() { "INVALID_CODE" }
            }, "analyst-1", CancellationToken.None));
        Assert.Contains("Invalid reason code", ex.Message);
    }

    // ── AC: Pass with out-of-spec requires override ─────────────────────────

    [Fact]
    public async Task Submit_Pass_OutOfSpec_NoOverride_Throws()
    {
        await SeedFull(outOfSpec: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitAsync("B-001", new SubmitDeterminationRequest
            {
                Result = DeterminationResult.Pass
            }, "analyst-1", CancellationToken.None));
        Assert.Contains("override reason", ex.Message);
    }

    // ── AC: Pass with out-of-spec + override succeeds ───────────────────────

    [Fact]
    public async Task Submit_Pass_OutOfSpec_WithOverride_Succeeds()
    {
        await SeedFull(outOfSpec: true);
        var det = await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass,
            OverrideReason = "Manager approved — within tolerance"
        }, "analyst-1", CancellationToken.None);

        Assert.Equal(DeterminationResult.Pass, det.Result);
        Assert.Equal("Manager approved — within tolerance", det.OverrideReason);
    }

    // ── AC: Locks sensory after determination ───────────────────────────────

    [Fact]
    public async Task Submit_LocksSensory()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        var sensory = await _db.SensoryEvaluations.FirstAsync();
        Assert.True(sensory.IsLocked);
    }

    // ── AC: Duplicate determination rejected ────────────────────────────────

    [Fact]
    public async Task Submit_Duplicate_Throws()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitAsync("B-001", new SubmitDeterminationRequest
            {
                Result = DeterminationResult.Fail,
                ReasonCodes = new() { "LOW_FAT" }
            }, "analyst-2", CancellationToken.None));
        Assert.Contains("already has an active determination", ex.Message);
    }

    // ── AC: No panel → rejected ─────────────────────────────────────────────

    [Fact]
    public async Task Submit_NoPanel_Throws()
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(), BatchCode = "NO-PANEL", DispatchNumber = "D-NP",
            ProductLine = ProductLine.FM, Status = BatchStatus.AwaitingPanel,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitAsync("NO-PANEL", new SubmitDeterminationRequest
            {
                Result = DeterminationResult.Pass
            }, "analyst-1", CancellationToken.None));
        Assert.Contains("No chemical panel", ex.Message);
    }

    // ── AC: Supersede creates new, chains old ───────────────────────────────

    [Fact]
    public async Task Supersede_ChainsOldDetermination()
    {
        await SeedFull();
        var first = await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        var second = await _service.SupersedeAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = new() { "LOW_FAT" }
        }, "analyst-2", CancellationToken.None);

        Assert.Equal(first.Id, second.SupersededDeterminationId);

        // Old one is now superseded
        var old = await _db.Determinations.FindAsync(first.Id);
        Assert.Equal(second.Id, old!.SupersededById);
    }

    // ── AC: Get returns active only ─────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsActiveOnly()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        await _service.SupersedeAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = new() { "LOW_FAT" }
        }, "analyst-2", CancellationToken.None);

        var active = await _service.GetAsync("B-001", CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal(DeterminationResult.Fail, active!.Result);
        Assert.Null(active.SupersededById); // Active = not superseded
    }

    // ── AC: History returns all versions ─────────────────────────────────────

    [Fact]
    public async Task GetHistory_ReturnsAllVersions()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        await _service.SupersedeAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = new() { "LOW_FAT" }
        }, "analyst-2", CancellationToken.None);

        var history = await _service.GetHistoryAsync("B-001", CancellationToken.None);
        Assert.Equal(2, history.Count);
    }

    // ── AC: Audit trail created ─────────────────────────────────────────────

    [Fact]
    public async Task Submit_CreatesAuditEntry()
    {
        await SeedFull();
        await _service.SubmitAsync("B-001", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Pass
        }, "analyst-1", CancellationToken.None);

        var audits = await _db.AuditEntries.Where(a => a.EntityType == "Determination").ToListAsync();
        Assert.Single(audits);
        Assert.Equal("Submit", audits[0].Action);
        Assert.Equal("analyst-1", audits[0].UserId);
    }
}
