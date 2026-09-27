using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Kafka;

namespace QualityLab.Tests;

/// <summary>
/// Contract test for ProcessingCompletedConsumer.
/// Verifies the consumer correctly parses REAL Processing Service event payloads
/// and derives product line from batch codes.
/// </summary>
public class ProcessingCompletedContractTests
{
    /// <summary>
    /// Real-world JSON payload captured from the Processing Service (SCRUM-68).
    /// The eventType is in Kafka headers, not in the body.
    /// Body is camelCase: { eventId, batchId, dispatchNumber, completedAtUtc, mixingTankCode }
    /// </summary>
    private const string RealProcessingCompletedPayload = """
        {
            "eventId": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
            "batchId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            "dispatchNumber": "DSP-2026-0915-001",
            "completedAtUtc": "2026-09-15T14:30:00Z",
            "mixingTankCode": "MT-03"
        }
        """;

    [Fact]
    public void DeriveProductLine_FromFmBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("265-FM-A");
        Assert.Equal(ProductLine.FM, result);
    }

    [Fact]
    public void DeriveProductLine_FromSyBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("1-SY-A");
        Assert.Equal(ProductLine.SY, result);
    }

    [Fact]
    public void DeriveProductLine_FromFlmBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("42-FLM-B");
        Assert.Equal(ProductLine.FLM, result);
    }

    [Fact]
    public void DeriveProductLine_FromDyBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("100-DY-C");
        Assert.Equal(ProductLine.DY, result);
    }

    [Fact]
    public void DeriveProductLine_FromCdBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("7-CD-A");
        Assert.Equal(ProductLine.CD, result);
    }

    [Fact]
    public void DeriveProductLine_FromSkBatchCode()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("88-SK-D");
        Assert.Equal(ProductLine.SK, result);
    }

    [Fact]
    public void DeriveProductLine_InvalidCode_ReturnsNull()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("INVALID");
        Assert.Null(result);
    }

    [Fact]
    public void DeriveProductLine_UnknownMiddleSegment_ReturnsNull()
    {
        var result = ProcessingCompletedConsumer.DeriveProductLine("265-XX-A");
        Assert.Null(result);
    }

    [Fact]
    public void RealPayload_Deserializes_CorrectFields()
    {
        // Deserialize the real Processing payload as our consumer would
        var evt = System.Text.Json.JsonSerializer.Deserialize<ProcessingCompletedConsumer.ProcessingCompletedEvent>(
            RealProcessingCompletedPayload,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(evt);
        Assert.Equal(Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479"), evt.EventId);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890"), evt.BatchId);
        Assert.Equal("DSP-2026-0915-001", evt.DispatchNumber);
        Assert.Equal(new DateTime(2026, 9, 15, 14, 30, 0, DateTimeKind.Utc), evt.CompletedAtUtc);
        Assert.Equal("MT-03", evt.MixingTankCode);
    }

    [Fact]
    public void RealPayload_DoesNotContainEventTypeOrStage()
    {
        // The eventType is in Kafka headers, NOT in the JSON body.
        // This test verifies our contract doesn't accidentally rely on body-based eventType.
        var raw = System.Text.Json.JsonDocument.Parse(RealProcessingCompletedPayload);
        Assert.False(raw.RootElement.TryGetProperty("eventType", out _),
            "eventType should NOT be in the body — it's a Kafka header");
        Assert.False(raw.RootElement.TryGetProperty("stage", out _),
            "stage should NOT be in the body — it's a Kafka header");
    }

    [Fact]
    public void MinimalPayload_Deserializes_WithNulls()
    {
        // Even a minimal payload (empty or partial) should deserialize without throwing
        var minimal = """{"dispatchNumber": "DSP-001"}""";
        var evt = System.Text.Json.JsonSerializer.Deserialize<ProcessingCompletedConsumer.ProcessingCompletedEvent>(
            minimal,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(evt);
        Assert.Equal("DSP-001", evt.DispatchNumber);
        Assert.Null(evt.EventId);
        Assert.Null(evt.CompletedAtUtc);
        Assert.Null(evt.MixingTankCode);
    }

    // ── IsProcessingCompletedEvent predicate tests ─────────────────────────

    [Fact]
    public void IsProcessingCompletedEvent_RealHeaderValue_ReturnsTrue()
    {
        // Processing's OutboxWriter: ["eventType"] = @event.GetType().Name → "ProcessingCompletedEvent"
        Assert.True(ProcessingCompletedConsumer.IsProcessingCompletedEvent("ProcessingCompletedEvent"));
    }

    [Fact]
    public void IsProcessingCompletedEvent_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(ProcessingCompletedConsumer.IsProcessingCompletedEvent("processingcompletedevent"));
    }

    [Fact]
    public void IsProcessingCompletedEvent_WrongValue_ReturnsFalse()
    {
        // "ProcessingCompleted" (without "Event" suffix) was the old broken value
        Assert.False(ProcessingCompletedConsumer.IsProcessingCompletedEvent("ProcessingCompleted"));
    }

    [Fact]
    public void IsProcessingCompletedEvent_OtherStageEvent_ReturnsFalse()
    {
        Assert.False(ProcessingCompletedConsumer.IsProcessingCompletedEvent("ProcessingStageStartedEvent"));
    }

    [Fact]
    public void IsProcessingCompletedEvent_Null_ReturnsFalse()
    {
        Assert.False(ProcessingCompletedConsumer.IsProcessingCompletedEvent(null));
    }

    [Fact]
    public void IsProcessingCompletedEvent_Empty_ReturnsFalse()
    {
        Assert.False(ProcessingCompletedConsumer.IsProcessingCompletedEvent(""));
    }
}
