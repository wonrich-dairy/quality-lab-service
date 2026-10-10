using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Application.LabResults;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;
using static QualityLab.Tests.LabHistorySeed;

namespace QualityLab.Tests;

/// <summary>SCRUM-25: historical lab results — filters, counts, N/A rendering, paging, CSV.</summary>
public class LabResultsServiceTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly LabResultsService _service;

    private static readonly DateTime Sep1 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep15 = new(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep30 = new(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

    public LabResultsServiceTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"LabResults_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new LabResultsService(_db);
    }

    public void Dispose() => _db.Dispose();

    private static LabResultsFilter F(string? line = null, DateOnly? from = null, DateOnly? to = null,
        string? result = null, string? reason = null) =>
        LabResultsFilter.Parse(line, from, to, result, reason);

    private Task<LabResultsPage> Search(LabResultsFilter filter, int page = 1) =>
        _service.SearchAsync(filter, page, CancellationToken.None);

    // ── Filter by product line ──────────────────────────────────────────────

    [Fact]
    public async Task FilterByProductLine_ReturnsOnlyThatLine()
    {
        Add(_db, "B-FM", ProductLine.FM, Sep1);
        Add(_db, "B-SY", ProductLine.SY, Sep1);
        Add(_db, "B-CD", ProductLine.CD, Sep1);
        await _db.SaveChangesAsync();

        var page = await Search(F(line: "FM"));

        Assert.Single(page.Items);
        Assert.All(page.Items, r => Assert.Equal("FM", r.ProductLine));
    }

    // ── Filter by date range ────────────────────────────────────────────────

    [Fact]
    public async Task FilterByDateRange_ReturnsOnlyPanelsInside()
    {
        Add(_db, "B-01", ProductLine.FM, Sep1);
        Add(_db, "B-15", ProductLine.FM, Sep15);
        Add(_db, "B-30", ProductLine.FM, Sep30);
        await _db.SaveChangesAsync();

        var page = await Search(F(from: new DateOnly(2026, 9, 10), to: new DateOnly(2026, 9, 20)));

        Assert.Equal(["B-15"], page.Items.Select(r => r.BatchCode));
    }

    [Fact]
    public async Task FilterByDateRange_EndDateIsInclusive()
    {
        Add(_db, "B-LATE", ProductLine.FM, new DateTime(2026, 9, 20, 23, 59, 0, DateTimeKind.Utc));
        Add(_db, "B-NEXT", ProductLine.FM, new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));
        await _db.SaveChangesAsync();

        var page = await Search(F(from: new DateOnly(2026, 9, 20), to: new DateOnly(2026, 9, 20)));

        Assert.Equal(["B-LATE"], page.Items.Select(r => r.BatchCode));
    }

    // ── Filter by result ────────────────────────────────────────────────────

    [Fact]
    public async Task FilterByResult_FailAndPending()
    {
        Add(_db, "B-PASS", ProductLine.FM, Sep1, DeterminationResult.Pass);
        Add(_db, "B-FAIL", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "B-NONE", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();

        Assert.Equal(["B-FAIL"], (await Search(F(result: "Fail"))).Items.Select(r => r.BatchCode));
        Assert.Equal(["B-PASS"], (await Search(F(result: "pass"))).Items.Select(r => r.BatchCode));

        var pending = await Search(F(result: "Pending"));
        Assert.Equal(["B-NONE"], pending.Items.Select(r => r.BatchCode));
        Assert.Equal("Pending", pending.Items[0].Determination);
    }

    // ── Filter by reason code ───────────────────────────────────────────────

    [Theory]
    [InlineData("LOW_FAT", "B-FAT")]
    [InlineData("SENSORY_COLOUR", "B-COL")]
    [InlineData("SENSORY_TEXTURE", "B-TEX")]
    public async Task FilterByReasonCode_ReturnsOnlyBatchesCarryingIt(string code, string expected)
    {
        Add(_db, "B-FAT", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "B-COL", ProductLine.FM, Sep1, DeterminationResult.Fail, ["SENSORY_COLOUR"]);
        Add(_db, "B-TEX", ProductLine.SY, Sep1, DeterminationResult.Fail, ["SENSORY_TEXTURE"]);
        await _db.SaveChangesAsync();

        var page = await Search(F(reason: code));

        Assert.Equal([expected], page.Items.Select(r => r.BatchCode));
    }

    [Fact]
    public async Task FilterByReasonCode_IsCaseInsensitive()
    {
        Add(_db, "B-FAT", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        await _db.SaveChangesAsync();

        Assert.Single((await Search(F(reason: "low_fat"))).Items);
    }

    [Theory]
    [InlineData("HIGH_PH")]
    [InlineData("SENSORY_COLOUR")]
    public async Task BatchWithSeveralReasons_MatchesEach_AndAppearsOnce(string code)
    {
        Add(_db, "B-MULTI", ProductLine.FM, Sep1, DeterminationResult.Fail, ["HIGH_PH", "SENSORY_COLOUR"]);
        await _db.SaveChangesAsync();

        var page = await Search(F(reason: code));

        Assert.Single(page.Items);
        Assert.Equal(1, page.Total);
        Assert.Equal(["HIGH_PH", "SENSORY_COLOUR"], page.Items[0].ReasonCodes);
    }

    [Fact]
    public async Task SupersededDetermination_IsIgnored()
    {
        var batch = Add(_db, "B-CORR", ProductLine.FM, Sep1);
        var first = AddDetermination(_db, batch, DeterminationResult.Pass, [], Sep1.AddHours(1));
        var second = AddDetermination(_db, batch, DeterminationResult.Fail, ["SENSORY_SMELL"], Sep1.AddHours(2), first.Id);
        first.SupersededById = second.Id;
        await _db.SaveChangesAsync();

        Assert.Empty((await Search(F(result: "Pass"))).Items);
        var fail = await Search(F(result: "Fail"));
        Assert.Single(fail.Items);
        Assert.Equal(second.Id, fail.Items[0].DeterminationId);
    }

    // ── Summary: count failures by reason ───────────────────────────────────

    [Fact]
    public async Task Summary_CountsPerReason_HighestFirst()
    {
        Add(_db, "B-1", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "B-2", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT", "SENSORY_COLOUR"]);
        Add(_db, "B-3", ProductLine.SY, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "B-4", ProductLine.SY, Sep1, DeterminationResult.Fail, ["SENSORY_TEXTURE"]);
        Add(_db, "B-5", ProductLine.FM, Sep1, DeterminationResult.Pass);
        Add(_db, "B-6", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();

        var summary = await _service.SummarizeAsync(F(), CancellationToken.None);

        Assert.Equal(6, summary.Total);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(4, summary.Failed);
        Assert.Equal(1, summary.Pending);
        Assert.Equal("LOW_FAT", summary.ReasonCounts[0].Code);
        Assert.Equal(3, summary.ReasonCounts[0].Count);
        Assert.Equal(summary.ReasonCounts.OrderByDescending(r => r.Count).Select(r => r.Count),
                     summary.ReasonCounts.Select(r => r.Count));
    }

    [Fact]
    public async Task Summary_ReflectsActiveFilters()
    {
        Add(_db, "B-1", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "B-2", ProductLine.SY, Sep1, DeterminationResult.Fail, ["SENSORY_TEXTURE"]);
        Add(_db, "B-3", ProductLine.SY, Sep1, DeterminationResult.Fail, ["SENSORY_TEXTURE", "LOW_FAT"]);
        await _db.SaveChangesAsync();

        var summary = await _service.SummarizeAsync(F(line: "SY"), CancellationToken.None);

        Assert.Equal(2, summary.Total);
        Assert.Equal(new ReasonCodeCount("SENSORY_TEXTURE", 2), summary.ReasonCounts[0]);
        Assert.Equal(new ReasonCodeCount("LOW_FAT", 1), summary.ReasonCounts[1]);
    }

    // ── Filters combine ─────────────────────────────────────────────────────

    [Fact]
    public async Task Filters_Combine()
    {
        var inRange = new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc);
        Add(_db, "MATCH", ProductLine.SY, inRange, DeterminationResult.Fail, ["LOW_PH"]);
        Add(_db, "WRONG-LINE", ProductLine.FM, inRange, DeterminationResult.Fail, ["LOW_FAT"]);
        Add(_db, "WRONG-DATE", ProductLine.SY, new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc), DeterminationResult.Fail, ["LOW_PH"]);
        Add(_db, "WRONG-RESULT", ProductLine.SY, inRange, DeterminationResult.Pass);
        await _db.SaveChangesAsync();

        var page = await Search(F(line: "SY", from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), result: "Fail"));

        Assert.Equal(["MATCH"], page.Items.Select(r => r.BatchCode));
    }

    [Fact]
    public async Task NoMatch_ReturnsEmptyPage_NotError()
    {
        Add(_db, "B-1", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();

        var page = await Search(F(line: "CD"));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Equal(0, page.TotalPages);
    }

    // ── Row content and not-applicable columns ──────────────────────────────

    [Theory]
    [InlineData(ProductLine.SY)]
    [InlineData(ProductLine.CD)]
    public async Task FermentedRow_SnfAndTs_AreNotApplicable(ProductLine line)
    {
        Add(_db, "B-F", line, Sep1);
        await _db.SaveChangesAsync();

        var row = (await Search(F())).Items.Single();

        Assert.Contains("snf", row.NotApplicable);
        Assert.Contains("ts", row.NotApplicable);
        Assert.DoesNotContain("texture", row.NotApplicable);
        Assert.Null(row.Snf);
        Assert.Null(row.Ts);
        Assert.Equal("Acceptable", row.Texture);
    }

    [Fact]
    public async Task LiquidRow_Texture_IsNotApplicable()
    {
        Add(_db, "B-L", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();

        var row = (await Search(F())).Items.Single();

        Assert.Equal(["texture"], row.NotApplicable);
        Assert.Null(row.Texture);
        Assert.Equal(8.55m, row.Snf);
        Assert.Equal(12.35m, row.Ts);
    }

    [Fact]
    public async Task Row_ShowsBatchAtAGlance_WithOutOfSpecLimitInForce()
    {
        Add(_db, "WR-2609-0142", ProductLine.FM, Sep1, DeterminationResult.Fail, ["LOW_FAT"],
            colour: SensoryGrade.Borderline, fat: 3.20m,
            flags: [new Flag("FatPercent", 3.20m, "Min", 3.50m)]);
        await _db.SaveChangesAsync();

        var row = (await Search(F())).Items.Single();

        Assert.Equal("WR-2609-0142", row.BatchCode);
        Assert.Equal("DSP-WR-2609-0142", row.DispatchNumber);
        Assert.Equal(Sep1, row.TestedAtUtc);
        Assert.Equal(3.20m, row.FatPercent);
        Assert.Equal(6.70m, row.Ph);
        Assert.Equal("Borderline", row.Colour);
        Assert.Equal("Acceptable", row.Taste);
        Assert.Equal("Fail", row.Determination);
        var flag = Assert.Single(row.OutOfSpec);
        Assert.Equal(new LabResultOutOfSpec("FatPercent", 3.20m, "Min", 3.50m), flag);
    }

    [Fact]
    public async Task Row_UsesLatestPanelVersion_OneRowPerBatch()
    {
        var batch = Add(_db, "B-RETEST", ProductLine.FM, Sep1, fat: 3.10m);
        _db.ChemicalPanels.Add(new ChemicalPanel
        {
            Id = Guid.NewGuid(), BatchWorkItemId = batch.Id, BatchCode = batch.BatchCode,
            DispatchNumber = batch.DispatchNumber, ProductLine = ProductLine.FM,
            FatPercent = 3.90m, Ph = 6.70m, Snf = 8.6m, Ts = 12.5m, Version = 2,
            TestedBy = "tech-2", TestedAtUtc = Sep15, CreatedAtUtc = Sep15
        });
        await _db.SaveChangesAsync();

        var page = await Search(F());

        var row = Assert.Single(page.Items);
        Assert.Equal(2, row.PanelVersion);
        Assert.Equal(3.90m, row.FatPercent);
    }

    [Fact]
    public async Task Row_WithoutSensory_ReportsNotRecorded()
    {
        Add(_db, "B-NOSENS", ProductLine.FM, Sep1, sensory: false);
        await _db.SaveChangesAsync();

        var row = (await Search(F())).Items.Single();

        Assert.False(row.SensoryRecorded);
        Assert.Equal(LabResultsCsv.NotRecorded, LabResultsCsv.ToCells(row)[9]);
    }

    // ── Paging and ordering ─────────────────────────────────────────────────

    [Fact]
    public async Task Results_Paged50_NewestFirst_WithTotal()
    {
        for (var i = 0; i < 60; i++)
            Add(_db, $"B-{i:D2}", ProductLine.FM, Sep1.AddHours(i));
        await _db.SaveChangesAsync();

        var first = await Search(F());
        var second = await Search(F(), page: 2);

        Assert.Equal(50, first.Items.Count);
        Assert.Equal(10, second.Items.Count);
        Assert.Equal(60, first.Total);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal("B-59", first.Items[0].BatchCode);
        Assert.Equal(first.Items.OrderByDescending(r => r.TestedAtUtc).Select(r => r.BatchCode),
                     first.Items.Select(r => r.BatchCode));
        Assert.Empty(first.Items.Select(r => r.BatchCode).Intersect(second.Items.Select(r => r.BatchCode)));
    }

    // ── CSV export ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ContainsEveryRow_NotOnlyCurrentPage()
    {
        for (var i = 0; i < 55; i++)
            Add(_db, $"B-{i:D2}", ProductLine.FM, Sep1.AddHours(i));
        await _db.SaveChangesAsync();

        var rows = await _service.ExportAsync(F(), CancellationToken.None);
        var lines = LabResultsCsv.Write(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(55, rows.Count);
        Assert.Equal(56, lines.Length); // header + 55
        Assert.Equal(string.Join(",", LabResultsCsv.Header), lines[0]);
    }

    [Fact]
    public async Task Export_NotApplicableCells_CarryExplicitMarker()
    {
        Add(_db, "B-SY", ProductLine.SY, Sep1);
        Add(_db, "B-FM", ProductLine.FM, Sep15);
        await _db.SaveChangesAsync();

        var rows = await _service.ExportAsync(F(), CancellationToken.None);
        var fm = LabResultsCsv.ToCells(rows.Single(r => r.ProductLine == "FM"));
        var sy = LabResultsCsv.ToCells(rows.Single(r => r.ProductLine == "SY"));

        int Col(string name) => LabResultsCsv.Header.ToList().IndexOf(name);

        Assert.Equal("N/A", sy[Col("SNF")]);
        Assert.Equal("N/A", sy[Col("TS")]);
        Assert.Equal("Acceptable", sy[Col("Texture")]);
        Assert.Equal("N/A", fm[Col("Texture")]);
        Assert.Equal("8.55", fm[Col("SNF")]);
        Assert.All(fm.Concat(sy), cell => Assert.False(string.IsNullOrEmpty(cell)));
    }

    [Fact]
    public void Export_FileName_CarriesFiltersAndDate()
    {
        var name = LabResultsCsv.FileName(
            F(line: "SY", from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), result: "Fail", reason: "low_fat"),
            new DateOnly(2026, 10, 10));

        Assert.Equal("lab-results_SY_2026-09-01_to_2026-09-30_Fail_LOW_FAT_exported_2026-10-10.csv", name);
        Assert.Equal("lab-results_all_exported_2026-10-10.csv", LabResultsCsv.FileName(F(), new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Export_EscapesCommasAndFormulaInjection()
    {
        var row = new LabResultRow
        {
            BatchCode = "=HYPERLINK(\"x\")", DispatchNumber = "D,1", ProductLine = "FM",
            TestedAtUtc = Sep1, NotApplicable = ["texture"], Determination = "Pending"
        };

        var line = LabResultsCsv.Write([row]).Split("\r\n")[1];

        Assert.StartsWith("\"'=HYPERLINK(\"\"x\"\")\",\"D,1\",FM", line);
    }

    // ── Filter validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("XX", null, null, null, null)]
    [InlineData(null, "2026-09-20", "2026-09-10", null, null)]
    [InlineData(null, null, null, "Maybe", null)]
    [InlineData(null, null, null, "Pass", "LOW_FAT")]
    public void Filter_InvalidValues_Rejected(string? line, string? from, string? to, string? result, string? reason)
    {
        Assert.Throws<ArgumentException>(() => LabResultsFilter.Parse(
            line, from is null ? null : DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to), result, reason));
    }

    // ── Reason codes are indexed when a determination is written ────────────

    [Fact]
    public async Task DeterminationService_WritesOneReasonRowPerDistinctCode()
    {
        Add(_db, "B-DET", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();
        var determinations = new DeterminationService(_db, TimeProvider.System);

        var det = await determinations.SubmitAsync("B-DET", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail,
            ReasonCodes = ["LOW_FAT", "low_fat", "SENSORY_COLOUR"]
        }, "analyst-1", CancellationToken.None);

        var codes = await _db.DeterminationReasonCodes.Where(r => r.DeterminationId == det.Id)
            .Select(r => r.Code).OrderBy(c => c).ToListAsync();
        Assert.Equal(["LOW_FAT", "SENSORY_COLOUR"], codes);

        Assert.Single((await Search(F(reason: "SENSORY_COLOUR"))).Items);
    }

    [Fact]
    public async Task DeterminationService_Supersede_NewReasonsAreFilterable_OldAreNot()
    {
        Add(_db, "B-SUP", ProductLine.FM, Sep1);
        await _db.SaveChangesAsync();
        var determinations = new DeterminationService(_db, TimeProvider.System);

        await determinations.SubmitAsync("B-SUP", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail, ReasonCodes = ["LOW_FAT"]
        }, "analyst-1", CancellationToken.None);
        await determinations.SupersedeAsync("B-SUP", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail, ReasonCodes = ["SENSORY_SMELL"], Notes = "correction"
        }, "analyst-1", CancellationToken.None);

        Assert.Empty((await Search(F(reason: "LOW_FAT"))).Items);
        Assert.Single((await Search(F(reason: "SENSORY_SMELL"))).Items);
    }
}
