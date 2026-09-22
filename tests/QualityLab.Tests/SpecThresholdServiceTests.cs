using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Application.Specs;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

/// <summary>
/// Unit tests for SpecThresholdService covering SCRUM-22 acceptance criteria:
/// - Get thresholds per product line
/// - Update thresholds with audit trail
/// - Evaluate panel against thresholds (flag out-of-spec)
/// - Liquid vs fermented threshold fields
/// </summary>
public class SpecThresholdServiceTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly SpecThresholdService _service;

    public SpecThresholdServiceTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"SpecTest_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new SpecThresholdService(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedSpec(ProductLine line, decimal minFat = 3.0m, decimal maxFat = 8.0m, decimal minPh = 4.2m, decimal maxPh = 6.8m, decimal? minSnf = null, decimal? minClr = null)
    {
        _db.SpecThresholds.Add(new SpecThreshold
        {
            Id = Guid.NewGuid(),
            ProductLine = line,
            MinFatPercent = minFat,
            MaxFatPercent = maxFat,
            MinPh = minPh,
            MaxPh = maxPh,
            MinSnf = minSnf,
            MinCorrectedClr = minClr,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedBy = "seed"
        });
        await _db.SaveChangesAsync();
    }

    private async Task<ChemicalPanel> SeedPanel(ProductLine line, string batchCode, decimal fat, decimal ph, decimal? snf = null, decimal? clr = null)
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = batchCode,
            DispatchNumber = $"DSP-{batchCode}",
            ProductLine = line,
            Status = BatchStatus.PanelRecorded,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);

        var panel = new ChemicalPanel
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batchCode,
            DispatchNumber = batch.DispatchNumber,
            ProductLine = line,
            FatPercent = fat,
            Ph = ph,
            Snf = snf,
            CorrectedClr = clr,
            Version = 1,
            TestedBy = "tech-1",
            TestedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };
        _db.ChemicalPanels.Add(panel);
        await _db.SaveChangesAsync();
        return panel;
    }

    // ── AC: Get all thresholds ──────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsAllSeeded()
    {
        await SeedSpec(ProductLine.FM, minSnf: 8.5m, minClr: 26.0m);
        await SeedSpec(ProductLine.SY);

        var result = await _service.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, result.Count);
    }

    // ── AC: Get by product line ─────────────────────────────────────────────

    [Fact]
    public async Task GetByProductLine_ReturnsCorrectSpec()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.5m, minSnf: 8.5m, minClr: 26.0m);

        var spec = await _service.GetByProductLineAsync(ProductLine.FM, CancellationToken.None);

        Assert.NotNull(spec);
        Assert.Equal(3.5m, spec!.MinFatPercent);
        Assert.Equal(8.5m, spec.MinSnf);
    }

    // ── AC: Update threshold with audit ─────────────────────────────────────

    [Fact]
    public async Task Update_ChangesValues_AndCreatesAudit()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.0m, maxFat: 8.0m);

        var result = await _service.UpdateAsync(ProductLine.FM, new UpdateSpecRequest
        {
            MinFatPercent = 3.5m,
            MaxFatPercent = 7.0m,
            MinPh = 6.5m,
            MaxPh = 6.8m
        }, "manager-1", CancellationToken.None);

        Assert.Equal(3.5m, result.MinFatPercent);
        Assert.Equal(7.0m, result.MaxFatPercent);
        Assert.Equal("manager-1", result.UpdatedBy);

        // Check audit entries
        var audits = await _db.AuditEntries.Where(a => a.EntityType == "SpecThreshold").ToListAsync();
        Assert.True(audits.Count >= 2); // MinFat and MaxFat changed
    }

    // ── AC: Evaluate panel — all in spec ────────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_AllInSpec_NoFlags()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.0m, maxFat: 8.0m, minPh: 6.5m, maxPh: 6.8m, minSnf: 8.5m, minClr: 26.0m);
        await SeedPanel(ProductLine.FM, "INSPEC-FM", fat: 4.0m, ph: 6.7m, snf: 9.0m, clr: 28.0m);

        var result = await _service.EvaluatePanelAsync("INSPEC-FM", CancellationToken.None);

        Assert.False(result.HasOutOfSpecFlags);
        Assert.Empty(result.Flags);
    }

    // ── AC: Evaluate panel — fat below min ──────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_FatBelowMin_Flagged()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.5m, maxFat: 8.0m, minPh: 6.5m, maxPh: 6.8m);
        await SeedPanel(ProductLine.FM, "LOWFAT-FM", fat: 2.8m, ph: 6.7m);

        var result = await _service.EvaluatePanelAsync("LOWFAT-FM", CancellationToken.None);

        Assert.True(result.HasOutOfSpecFlags);
        Assert.Contains(result.Flags, f => f.Parameter == "FatPercent" && f.Limit == "Min");
    }

    // ── AC: Evaluate panel — pH out of range ────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_PhAboveMax_Flagged()
    {
        await SeedSpec(ProductLine.SY, minFat: 3.0m, maxFat: 8.0m, minPh: 4.2m, maxPh: 4.6m);
        await SeedPanel(ProductLine.SY, "HIGHPH-SY", fat: 3.5m, ph: 5.0m);

        var result = await _service.EvaluatePanelAsync("HIGHPH-SY", CancellationToken.None);

        Assert.True(result.HasOutOfSpecFlags);
        Assert.Contains(result.Flags, f => f.Parameter == "Ph" && f.Limit == "Max");
    }

    // ── AC: Evaluate panel — SNF below min (liquid) ─────────────────────────

    [Fact]
    public async Task EvaluatePanel_SnfBelowMin_Flagged()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.0m, maxFat: 8.0m, minPh: 6.5m, maxPh: 6.8m, minSnf: 8.5m);
        await SeedPanel(ProductLine.FM, "LOWSNF-FM", fat: 3.8m, ph: 6.7m, snf: 7.5m);

        var result = await _service.EvaluatePanelAsync("LOWSNF-FM", CancellationToken.None);

        Assert.True(result.HasOutOfSpecFlags);
        Assert.Contains(result.Flags, f => f.Parameter == "SNF" && f.Limit == "Min");
    }

    // ── AC: Multiple flags at once ──────────────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_MultipleFlags()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.5m, maxFat: 8.0m, minPh: 6.5m, maxPh: 6.8m, minSnf: 8.5m);
        await SeedPanel(ProductLine.FM, "MULTI-FM", fat: 2.0m, ph: 7.0m, snf: 7.0m);

        var result = await _service.EvaluatePanelAsync("MULTI-FM", CancellationToken.None);

        Assert.True(result.HasOutOfSpecFlags);
        Assert.True(result.Flags.Count >= 3);
    }

    // ── AC: Panel not found ─────────────────────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_NoPanelFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.EvaluatePanelAsync("GHOST-BATCH", CancellationToken.None));
        Assert.Contains("No chemical panel", ex.Message);
    }

    // ── AC: Flags stored on panel ───────────────────────────────────────────

    [Fact]
    public async Task EvaluatePanel_StoresFlagsOnPanel()
    {
        await SeedSpec(ProductLine.FM, minFat: 3.5m, maxFat: 8.0m, minPh: 6.5m, maxPh: 6.8m);
        await SeedPanel(ProductLine.FM, "STORE-FM", fat: 2.0m, ph: 6.7m);

        await _service.EvaluatePanelAsync("STORE-FM", CancellationToken.None);

        var panel = await _db.ChemicalPanels.FirstAsync(p => p.BatchCode == "STORE-FM");
        Assert.True(panel.HasOutOfSpecFlags);
        Assert.NotNull(panel.OutOfSpecFlagsJson);
        Assert.Contains("FatPercent", panel.OutOfSpecFlagsJson);
    }
}
