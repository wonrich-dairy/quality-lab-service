using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QualityLab.Api.Application.Determinations;
using QualityLab.Api.Application.Outbox;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Kafka;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

/// <summary>SCRUM-24: BatchCleared / BatchFailed are staged in the outbox with the determination.</summary>
public class DeterminationOutboxTests : IDisposable
{
    private readonly QualityLabDbContext _db;
    private readonly DeterminationService _service;

    public DeterminationOutboxTests()
    {
        var options = new DbContextOptionsBuilder<QualityLabDbContext>()
            .UseInMemoryDatabase($"OutboxTest_{Guid.NewGuid()}")
            .Options;
        _db = new QualityLabDbContext(options);
        _service = new DeterminationService(_db, TimeProvider.System, new OutboxWriter(_db, TimeProvider.System));
    }

    public void Dispose() => _db.Dispose();

    private async Task Seed(string batchCode = "265-FM-A", ProductLine line = ProductLine.FM)
    {
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(), BatchCode = batchCode, DispatchNumber = "DN-1", ProductLine = line,
            Status = BatchStatus.PanelRecorded, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        _db.BatchWorkItems.Add(batch);
        _db.ChemicalPanels.Add(new ChemicalPanel
        {
            Id = Guid.NewGuid(), BatchWorkItemId = batch.Id, BatchCode = batchCode, DispatchNumber = "DN-1",
            ProductLine = line, FatPercent = 3.8m, Ph = 6.7m, Version = 1, TestedBy = "t",
            TestedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        });
        _db.SensoryEvaluations.Add(new SensoryEvaluation
        {
            Id = Guid.NewGuid(), BatchWorkItemId = batch.Id, Taste = SensoryGrade.Acceptable,
            Smell = SensoryGrade.Acceptable, Colour = SensoryGrade.Acceptable, Appearance = SensoryGrade.Acceptable,
            EvaluatedBy = "t", EvaluatedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Pass_StagesBatchClearedEvent_KeyedByBatchCode()
    {
        await Seed();
        var det = await _service.SubmitAsync("265-FM-A",
            new SubmitDeterminationRequest { Result = DeterminationResult.Pass }, "analyst", CancellationToken.None);

        var msg = Assert.Single(await _db.OutboxMessages.ToListAsync());
        Assert.Equal("wonrich.quality-lab.batch-determinations.v1", msg.Topic);
        Assert.Equal("265-FM-A", msg.Key);
        Assert.Equal("BatchClearedEvent", msg.EventType);
        Assert.Equal("Pending", msg.Status);

        using var payload = JsonDocument.Parse(msg.Payload);
        Assert.Equal("265-FM-A", payload.RootElement.GetProperty("batchId").GetString());
        Assert.Equal("DN-1", payload.RootElement.GetProperty("dispatchNumber").GetString());
        Assert.False(payload.RootElement.GetProperty("isDeviation").GetBoolean());
        Assert.Equal(det.Id, payload.RootElement.GetProperty("determinationId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("correlationId").GetString()));
    }

    [Fact]
    public async Task Fail_StagesBatchFailedEvent_WithReasonCodes()
    {
        await Seed();
        await _service.SubmitAsync("265-FM-A", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail, ReasonCodes = new List<string> { "LOW_FAT", "HIGH_PH" }
        }, "analyst", CancellationToken.None);

        var msg = Assert.Single(await _db.OutboxMessages.ToListAsync());
        Assert.Equal("BatchFailedEvent", msg.EventType);

        using var payload = JsonDocument.Parse(msg.Payload);
        var codes = payload.RootElement.GetProperty("reasonCodes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "LOW_FAT", "HIGH_PH" }, codes);

        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(msg.HeadersJson)!;
        Assert.Equal(msg.CorrelationId, headers["x-correlation-id"]);
        Assert.Equal("BatchFailedEvent", headers["eventType"]);
        Assert.True(Guid.TryParse(headers["eventId"], out _));
    }

    [Fact]
    public async Task RejectedSubmit_StagesNoEvent()
    {
        await Seed();
        // Fail without reason code is rejected - nothing may reach the outbox
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SubmitAsync("265-FM-A",
            new SubmitDeterminationRequest { Result = DeterminationResult.Fail }, "analyst", CancellationToken.None));

        Assert.Empty(await _db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Supersede_StagesNewEvent_LinkedToPreviousDetermination()
    {
        await Seed();
        var first = await _service.SubmitAsync("265-FM-A",
            new SubmitDeterminationRequest { Result = DeterminationResult.Pass }, "analyst", CancellationToken.None);
        await _service.SupersedeAsync("265-FM-A", new SubmitDeterminationRequest
        {
            Result = DeterminationResult.Fail, ReasonCodes = new List<string> { "CONTAMINATION" }
        }, "analyst", CancellationToken.None);

        var messages = await _db.OutboxMessages.OrderBy(m => m.CreatedAtUtc).ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Equal("BatchFailedEvent", messages[1].EventType);

        using var payload = JsonDocument.Parse(messages[1].Payload);
        Assert.Equal(first.Id, payload.RootElement.GetProperty("supersedesDeterminationId").GetGuid());
    }

    [Fact]
    public async Task NoOpProducer_ReturnsFalse_SoRowsStayPending()
    {
        var producer = new NoOpKafkaProducer(NullLogger<NoOpKafkaProducer>.Instance);
        var ok = await producer.PublishAsync("t", "k", "{}", "{}", "cid", CancellationToken.None);
        Assert.False(ok);
    }
}
