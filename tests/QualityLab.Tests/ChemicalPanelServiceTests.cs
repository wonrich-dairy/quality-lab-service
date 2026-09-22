using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Application.Panels;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

/// <summary>
/// Unit tests for ChemicalPanelService covering all SCRUM-20 acceptance criteria:
/// - Panel shape per product line (liquid vs fermented)
/// - Derived values (CLR, SNF, TS) on liquid lines only
/// - Rejection of lactometer on fermented lines
/// - Rejection of direct SNF/TS submission
/// - Validation of physical ranges
/// - Re-test creates new version, not overwrite
/// - Required fields validation per product line
/// </summary>
public class ChemicalPanelServiceTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly ChemicalPanelService _service;

    public ChemicalPanelServiceTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"QualityLabTest_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new ChemicalPanelService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task<BatchWorkItem> SeedBatch(ProductLine productLine, string batchCode = "265-FM-A", bool completed = true)
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = batchCode,
            DispatchNumber = $"DSP-{batchCode}",
            ProductLine = productLine,
            StoringTankCode = "ST-01",
            Status = BatchStatus.AwaitingPanel,
            CompletionTimeUtc = completed ? DateTime.UtcNow.AddMinutes(-10) : null,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);
        await _db.SaveChangesAsync();
        return batch;
    }

    // ── SCRUM-20 AC: Panel form matches the product line ─────────────────────

    [Theory]
    [InlineData(ProductLine.FM)]
    [InlineData(ProductLine.FLM)]
    public async Task RecordPanel_LiquidLine_AcceptsFatLactometerTemperaturePh(ProductLine productLine)
    {
        await SeedBatch(productLine, $"LIQ-{productLine}");

        var panel = await _service.RecordPanelAsync($"LIQ-{productLine}", new RecordPanelRequest
        {
            FatPercent = 3.8m,
            LactometerReading = 29.0m,
            TemperatureCelsius = 27.0m,
            Ph = 6.7m
        }, "tech-1", CancellationToken.None);

        Assert.Equal(productLine, panel.ProductLine);
        Assert.Equal(3.8m, panel.FatPercent);
        Assert.Equal(29.0m, panel.LactometerReading);
        Assert.Equal(27.0m, panel.TemperatureCelsius);
        Assert.Equal(6.7m, panel.Ph);
    }

    [Theory]
    [InlineData(ProductLine.SY)]
    [InlineData(ProductLine.DY)]
    [InlineData(ProductLine.SK)]
    [InlineData(ProductLine.CD)]
    public async Task RecordPanel_FermentedLine_AcceptsFatAndPhOnly(ProductLine productLine)
    {
        await SeedBatch(productLine, $"FER-{productLine}");

        var panel = await _service.RecordPanelAsync($"FER-{productLine}", new RecordPanelRequest
        {
            FatPercent = 3.1m,
            Ph = 4.4m
        }, "tech-1", CancellationToken.None);

        Assert.Equal(productLine, panel.ProductLine);
        Assert.Equal(3.1m, panel.FatPercent);
        Assert.Equal(4.4m, panel.Ph);
        Assert.Null(panel.LactometerReading);
        Assert.Null(panel.TemperatureCelsius);
        Assert.Null(panel.CorrectedClr);
        Assert.Null(panel.Snf);
        Assert.Null(panel.Ts);
    }

    // ── SCRUM-20 AC: Derived values (CLR, SNF, TS) on liquid lines ──────────

    [Fact]
    public async Task RecordPanel_LiquidLine_DerivesCorrectedClrSnfTs()
    {
        await SeedBatch(ProductLine.FM, "DERIVE-FM");

        var panel = await _service.RecordPanelAsync("DERIVE-FM", new RecordPanelRequest
        {
            FatPercent = 3.8m,
            LactometerReading = 29.0m,
            TemperatureCelsius = 27.0m,
            Ph = 6.7m
        }, "tech-1", CancellationToken.None);

        Assert.NotNull(panel.CorrectedClr);
        Assert.NotNull(panel.Snf);
        Assert.NotNull(panel.Ts);

        // CLR = 29.0 + 0.2*(27.0 - 27.5) = 29.0 - 0.1 = 28.9
        Assert.Equal(28.90m, panel.CorrectedClr!.Value);

        // SNF = (28.9 * 0.25) + (3.8 * 0.22) + 0.72 = 7.225 + 0.836 + 0.72 = 8.781 → 8.78
        Assert.Equal(8.78m, panel.Snf!.Value);

        // TS = SNF + Fat = 8.78 + 3.8 = 12.58
        Assert.Equal(12.58m, panel.Ts!.Value);
    }

    [Fact]
    public async Task RecordPanel_FermentedLine_NoCLRSnfTs()
    {
        await SeedBatch(ProductLine.SY, "NODERIVE-SY");

        var panel = await _service.RecordPanelAsync("NODERIVE-SY", new RecordPanelRequest
        {
            FatPercent = 3.1m,
            Ph = 4.4m
        }, "tech-1", CancellationToken.None);

        Assert.Null(panel.CorrectedClr);
        Assert.Null(panel.Snf);
        Assert.Null(panel.Ts);
    }

    // ── SCRUM-20 AC: Lactometer rejected on fermented line ──────────────────

    [Fact]
    public async Task RecordPanel_FermentedLine_RejectsLactometerReading()
    {
        await SeedBatch(ProductLine.SY, "REJECT-SY");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("REJECT-SY", new RecordPanelRequest
            {
                FatPercent = 3.1m,
                LactometerReading = 29.0m,
                Ph = 4.4m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("lactometer reading does not apply", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── SCRUM-20 AC: Derived values cannot be entered by hand ───────────────

    [Fact]
    public async Task RecordPanel_RejectsDirectSnfSubmission()
    {
        await SeedBatch(ProductLine.FM, "REJECT-SNF");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("REJECT-SNF", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m,
                Snf = 8.5m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("SNF is calculated", ex.Message);
    }

    [Fact]
    public async Task RecordPanel_RejectsDirectTsSubmission()
    {
        await SeedBatch(ProductLine.FM, "REJECT-TS");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("REJECT-TS", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m,
                Ts = 12.5m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("TS is calculated", ex.Message);
    }

    // ── SCRUM-20 AC: Values outside physical range are rejected ──────────────

    [Theory]
    [InlineData(18.0)]  // fat > 15
    [InlineData(-1.0)]  // fat < 0
    public async Task RecordPanel_RejectsFatOutOfRange(decimal fatPercent)
    {
        await SeedBatch(ProductLine.FM, $"FAT-{fatPercent}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync($"FAT-{fatPercent}", new RecordPanelRequest
            {
                FatPercent = fatPercent,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Fat percent", ex.Message);
    }

    [Theory]
    [InlineData(9.5)]  // pH > 9.0
    [InlineData(2.0)]  // pH < 2.5
    public async Task RecordPanel_RejectsPhOutOfRange(decimal ph)
    {
        await SeedBatch(ProductLine.FM, $"PH-{ph}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync($"PH-{ph}", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = ph
            }, "tech-1", CancellationToken.None));

        Assert.Contains("pH", ex.Message);
    }

    [Fact]
    public async Task RecordPanel_RejectsTemperatureOutOfRange()
    {
        await SeedBatch(ProductLine.FM, "TEMP-BAD");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("TEMP-BAD", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = -5.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Temperature", ex.Message);
    }

    // ── SCRUM-20 AC: Required fields validated per product line ──────────────

    [Fact]
    public async Task RecordPanel_LiquidLine_RejectsMissingLactometer()
    {
        await SeedBatch(ProductLine.FM, "MISSING-LACTO");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("MISSING-LACTO", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Lactometer reading is required", ex.Message);
    }

    [Fact]
    public async Task RecordPanel_LiquidLine_RejectsMissingTemperature()
    {
        await SeedBatch(ProductLine.FM, "MISSING-TEMP");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("MISSING-TEMP", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Temperature is required", ex.Message);
    }

    // ── SCRUM-20 AC: Re-test is a new panel, not overwrite ──────────────────

    [Fact]
    public async Task RecordPanel_ReTest_CreatesNewVersion()
    {
        await SeedBatch(ProductLine.FM, "RETEST-FM");

        var panel1 = await _service.RecordPanelAsync("RETEST-FM", new RecordPanelRequest
        {
            FatPercent = 3.5m,
            LactometerReading = 28.0m,
            TemperatureCelsius = 27.0m,
            Ph = 6.5m
        }, "tech-1", CancellationToken.None);

        var panel2 = await _service.RecordPanelAsync("RETEST-FM", new RecordPanelRequest
        {
            FatPercent = 3.8m,
            LactometerReading = 29.0m,
            TemperatureCelsius = 27.5m,
            Ph = 6.7m
        }, "tech-2", CancellationToken.None);

        Assert.Equal(1, panel1.Version);
        Assert.Equal(2, panel2.Version);
        Assert.NotEqual(panel1.Id, panel2.Id);

        // First panel retained unchanged
        var versions = await _service.GetPanelVersionsAsync("RETEST-FM", CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.Equal(3.5m, versions[1].FatPercent);  // v1 still 3.5
        Assert.Equal(3.8m, versions[0].FatPercent);  // v2 is 3.8
    }

    // ── SCRUM-20 AC: Batch not found ────────────────────────────────────────

    [Fact]
    public async Task RecordPanel_BatchNotFound_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("DOES-NOT-EXIST", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("not found", ex.Message);
    }

    // ── SCRUM-20 AC: Panel is linked to the batch ───────────────────────────

    [Fact]
    public async Task RecordPanel_LinksToCorrectBatch()
    {
        var batch = await SeedBatch(ProductLine.FM, "LINKED-FM");

        var panel = await _service.RecordPanelAsync("LINKED-FM", new RecordPanelRequest
        {
            FatPercent = 3.8m,
            LactometerReading = 29.0m,
            TemperatureCelsius = 27.0m,
            Ph = 6.7m
        }, "tech-1", CancellationToken.None);

        Assert.Equal(batch.Id, panel.BatchWorkItemId);
        Assert.Equal("LINKED-FM", panel.BatchCode);
        Assert.Equal(batch.DispatchNumber, panel.DispatchNumber);
    }

    // ── SCRUM-20 AC: Identity and timestamp recorded ────────────────────────

    [Fact]
    public async Task RecordPanel_RecordsUserAndTimestamp()
    {
        await SeedBatch(ProductLine.SY, "AUDIT-SY");

        var panel = await _service.RecordPanelAsync("AUDIT-SY", new RecordPanelRequest
        {
            FatPercent = 3.1m,
            Ph = 4.4m
        }, "lab-tech-42", CancellationToken.None);

        Assert.Equal("lab-tech-42", panel.TestedBy);
        Assert.True(panel.TestedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    // ── SCRUM-20 AC: Batch status update ────────────────────────────────────

    [Fact]
    public async Task RecordPanel_UpdatesBatchStatus()
    {
        await SeedBatch(ProductLine.CD, "STATUS-CD");

        await _service.RecordPanelAsync("STATUS-CD", new RecordPanelRequest
        {
            FatPercent = 3.1m,
            Ph = 4.4m
        }, "tech-1", CancellationToken.None);

        var batch = await _service.GetBatchAsync("STATUS-CD", CancellationToken.None);
        Assert.Equal(BatchStatus.PanelRecorded, batch!.Status);
    }

    // ── SCRUM-20 AC: Work queue ─────────────────────────────────────────────

    [Fact]
    public async Task GetWorkQueue_ReturnsAllBatches()
    {
        await SeedBatch(ProductLine.FM, "WQ-1");
        await SeedBatch(ProductLine.SY, "WQ-2");

        var items = await _service.GetWorkQueueAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
    }

    // ── Formula unit tests ──────────────────────────────────────────────────

    [Fact]
    public void CalculateCorrectedClr_StandardTemperature()
    {
        // At 27.5°C (reference), correction is 0
        var result = ChemicalPanelService.CalculateCorrectedClr(29.0m, 27.5m);
        Assert.Equal(29.0m, result);
    }

    [Fact]
    public void CalculateCorrectedClr_BelowReference()
    {
        // At 25°C: 29 + 0.2*(25-27.5) = 29 - 0.5 = 28.5
        var result = ChemicalPanelService.CalculateCorrectedClr(29.0m, 25.0m);
        Assert.Equal(28.5m, result);
    }

    [Fact]
    public void CalculateSnf_CorrectFormula()
    {
        // SNF = (CLR * 0.25) + (Fat * 0.22) + 0.72
        // SNF = (29 * 0.25) + (3.8 * 0.22) + 0.72 = 7.25 + 0.836 + 0.72 = 8.806
        var result = ChemicalPanelService.CalculateSnf(3.8m, 29.0m);
        Assert.Equal(8.806m, result);
    }

    [Fact]
    public void CalculateTs_SumOfSnfAndFat()
    {
        var result = ChemicalPanelService.CalculateTs(8.5m, 3.8m);
        Assert.Equal(12.3m, result);
    }

    // ── CreateBatch tests ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateBatch_Valid_CreatesSuccessfully()
    {
        var batch = await _service.CreateBatchAsync(new CreateBatchRequest
        {
            BatchCode = "NEW-FM-A",
            DispatchNumber = "DSP-NEW-FM-A",
            ProductLine = "FM"
        }, "user-1", CancellationToken.None);

        Assert.Equal("NEW-FM-A", batch.BatchCode);
        Assert.Equal(ProductLine.FM, batch.ProductLine);
        Assert.Equal(BatchStatus.AwaitingPanel, batch.Status);
    }

    [Fact]
    public async Task CreateBatch_InvalidProductLine_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateBatchAsync(new CreateBatchRequest
            {
                BatchCode = "BAD-XX",
                DispatchNumber = "DSP-BAD-XX",
                ProductLine = "XX"
            }, "user-1", CancellationToken.None));

        Assert.Contains("Invalid product line", ex.Message);
    }

    [Fact]
    public async Task CreateBatch_Duplicate_Throws()
    {
        await _service.CreateBatchAsync(new CreateBatchRequest
        {
            BatchCode = "DUP-FM",
            DispatchNumber = "DSP-DUP-FM",
            ProductLine = "FM"
        }, "user-1", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateBatchAsync(new CreateBatchRequest
            {
                BatchCode = "DUP-FM",
                DispatchNumber = "DSP-DUP-FM2",
                ProductLine = "FM"
            }, "user-1", CancellationToken.None));

        Assert.Contains("already exists", ex.Message);
    }

    // ── QA-20-02: Panel blocked before run completes ────────────────────────

    [Fact]
    public async Task RecordPanel_RunNotCompleted_Throws()
    {
        await SeedBatch(ProductLine.FM, "INCOMPLETE-FM", completed: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("INCOMPLETE-FM", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("not completed", ex.Message);
    }

    // ── QA-20-03: Omitting fat% must not silently save 0 ────────────────────

    [Fact]
    public async Task RecordPanel_ZeroFat_Rejected()
    {
        await SeedBatch(ProductLine.FM, "ZEROFAT-FM");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("ZEROFAT-FM", new RecordPanelRequest
            {
                FatPercent = 0,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Fat percent is required", ex.Message);
    }

    [Fact]
    public async Task RecordPanel_FermentedLine_ZeroFat_Rejected()
    {
        await SeedBatch(ProductLine.SY, "ZEROFAT-SY");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("ZEROFAT-SY", new RecordPanelRequest
            {
                FatPercent = 0,
                Ph = 4.4m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Fat percent is required", ex.Message);
    }

    // ── QA-20-06: Temperature -3.0 boundary must be rejected ────────────────

    [Fact]
    public async Task RecordPanel_TemperatureMinus3_Rejected()
    {
        await SeedBatch(ProductLine.FM, "TEMP-FM");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("TEMP-FM", new RecordPanelRequest
            {
                FatPercent = 3.8m,
                LactometerReading = 29.0m,
                TemperatureCelsius = -3.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Temperature", ex.Message);
    }

    [Fact]
    public async Task RecordPanel_TemperatureAboveMinus3_Accepted()
    {
        await SeedBatch(ProductLine.FM, "TEMPOK-FM");

        var panel = await _service.RecordPanelAsync("TEMPOK-FM", new RecordPanelRequest
        {
            FatPercent = 3.8m,
            LactometerReading = 29.0m,
            TemperatureCelsius = -2.9m,
            Ph = 6.7m
        }, "tech-1", CancellationToken.None);

        Assert.Equal(-2.9m, panel.TemperatureCelsius);
    }

    // ── QA-20-09: Fat 18 rejected ───────────────────────────────────────────

    [Fact]
    public async Task RecordPanel_Fat18_Rejected()
    {
        await SeedBatch(ProductLine.FM, "FAT18-FM");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync("FAT18-FM", new RecordPanelRequest
            {
                FatPercent = 18.0m,
                LactometerReading = 29.0m,
                TemperatureCelsius = 27.0m,
                Ph = 6.7m
            }, "tech-1", CancellationToken.None));

        Assert.Contains("Fat percent", ex.Message);
    }

    // ── QA-20-09: pH 9.5 and pH 2.0 rejected ───────────────────────────────

    [Theory]
    [InlineData(9.5)]
    [InlineData(2.0)]
    public async Task RecordPanel_PhOutOfRange_Rejected(decimal ph)
    {
        var code = $"PH-{ph}";
        await SeedBatch(ProductLine.SY, code);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RecordPanelAsync(code, new RecordPanelRequest
            {
                FatPercent = 3.1m,
                Ph = ph
            }, "tech-1", CancellationToken.None));

        Assert.Contains("pH", ex.Message);
    }
}
