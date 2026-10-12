using System.Text.Json;
using Wonrich.QualityLab.Contracts.Events;

namespace QualityLab.Tests;

/// <summary>
/// The determination events are a published contract (Wonrich.QualityLab.Contracts, schemaVersion v1).
/// Processing and Traceability deserialize them from the package, so the wire shape must not drift.
/// </summary>
public class BatchDeterminationContractsTests
{
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void Events_come_from_the_contracts_package_not_the_api()
    {
        var assembly = typeof(BatchFailedEvent).Assembly.GetName();

        Assert.Equal("Wonrich.QualityLab.Contracts", assembly.Name);
        Assert.Equal(1, assembly.Version?.Major); // major version == schemaVersion v1
        Assert.Same(typeof(BatchFailedEvent).Assembly, typeof(BatchClearedEvent).Assembly);
    }

    [Fact]
    public void BatchFailed_serializes_to_the_v1_wire_shape()
    {
        var evt = new BatchFailedEvent
        {
            BatchId = "265-FM-A",
            DispatchNumber = "DN-0142",
            ProductLine = "FM",
            ReasonCodes = ["LOW_FAT"],
            OutOfSpecDetails = [new OutOfSpecDetail("FatPercent", 3.2m, "Min", 3.5m)]
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(evt, Wire));
        var root = doc.RootElement;

        Assert.Equal("BatchFailed", root.GetProperty("eventType").GetString());
        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("265-FM-A", root.GetProperty("batchId").GetString());
        Assert.Equal("LOW_FAT", root.GetProperty("reasonCodes")[0].GetString());

        var detail = root.GetProperty("outOfSpecDetails")[0];
        Assert.Equal("FatPercent", detail.GetProperty("parameter").GetString());
        Assert.Equal(3.2m, detail.GetProperty("measuredValue").GetDecimal());
        Assert.Equal("Min", detail.GetProperty("limit").GetString());
        Assert.Equal(3.5m, detail.GetProperty("limitValue").GetDecimal());

        foreach (var field in new[] { "eventId", "dispatchNumber", "productLine", "timestampUtc", "isDeviation",
                     "correlationId", "determinationId", "supersedesDeterminationId", "determinedBy",
                     "determinedAtUtc", "overrideReason", "notes" })
        {
            Assert.True(root.TryGetProperty(field, out _), $"v1 field '{field}' is missing");
        }
    }

    [Fact]
    public void A_consumer_can_round_trip_both_events_with_web_defaults()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var cleared = JsonSerializer.Deserialize<BatchClearedEvent>(
            JsonSerializer.Serialize(new BatchClearedEvent { BatchId = "265-FM-A" }, Wire), web);
        var failed = JsonSerializer.Deserialize<BatchFailedEvent>(
            JsonSerializer.Serialize(new BatchFailedEvent { BatchId = "265-FM-B", ReasonCodes = ["TASTE_OFF"] }, Wire), web);

        Assert.Equal("265-FM-A", cleared!.BatchId);
        Assert.Equal("BatchCleared", cleared.EventType);
        Assert.Equal(["TASTE_OFF"], failed!.ReasonCodes);
    }
}
