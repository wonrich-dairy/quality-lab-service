using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;
using System.Text.Json;

namespace QualityLab.Api.Infrastructure.Sync;

/// <summary>
/// Background service that polls the Processing Service database directly for completed batches
/// with allocations, and creates corresponding BatchWorkItems in the quality lab work queue.
/// This provides real-time batch availability without requiring Kafka infrastructure.
/// </summary>
public sealed class ProcessingDbSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<ProcessingDbSyncService> _logger;
    private readonly TimeSpan _pollInterval;

    public ProcessingDbSyncService(
        IServiceProvider services,
        IConfiguration config,
        ILogger<ProcessingDbSyncService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;

        // Poll every 30 seconds by default, configurable
        var seconds = config.GetValue("ProcessingSync:PollIntervalSeconds", 30);
        _pollInterval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processingConnectionString = _config.GetConnectionString("ProcessingDb");

        if (string.IsNullOrWhiteSpace(processingConnectionString))
        {
            _logger.LogWarning(
                "ConnectionStrings:ProcessingDb is not configured — processing sync is disabled. " +
                "Batches can still be created manually via POST /api/panels/batch.");
            return;
        }

        _logger.LogInformation(
            "Processing DB sync started, polling every {Interval}s for completed batches",
            _pollInterval.TotalSeconds);

        // Wait a bit for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncCompletedBatchesAsync(processingConnectionString, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error during processing DB sync — will retry in {Interval}s", _pollInterval.TotalSeconds);
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task SyncCompletedBatchesAsync(string processingConnectionString, CancellationToken ct)
    {
        // 1. Read completed batches from the processing database
        var processingBatches = await ReadProcessingBatchesAsync(processingConnectionString, ct);

        if (processingBatches.Count == 0)
            return;

        // 2. Create missing BatchWorkItems in the quality lab database
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();

        // Get existing batch codes to avoid duplicates
        var existingCodes = await db.BatchWorkItems
            .Select(b => b.BatchCode)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(existingCodes, StringComparer.OrdinalIgnoreCase);

        var newBatches = processingBatches
            .Where(b => !existingSet.Contains(b.BatchCode))
            .ToList();

        if (newBatches.Count == 0)
            return;

        var now = DateTime.UtcNow;

        foreach (var batch in newBatches)
        {
            db.BatchWorkItems.Add(new BatchWorkItem
            {
                Id = Guid.NewGuid(),
                BatchCode = batch.BatchCode,
                DispatchNumber = batch.DispatchNumber,
                ProductLine = batch.ProductLine,
                StoringTankCode = batch.StoringTankCode,
                CompletionTimeUtc = batch.CompletionTimeUtc,
                Status = BatchStatus.AwaitingPanel,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Synced {Count} new batch(es) from processing DB: {Codes}",
            newBatches.Count,
            string.Join(", ", newBatches.Select(b => b.BatchCode)));
    }

    private static async Task<List<ProcessingBatchInfo>> ReadProcessingBatchesAsync(
        string connectionString, CancellationToken ct)
    {
        var batches = new List<ProcessingBatchInfo>();

        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Query tank_allocations joined with processing_runs to get batch codes
        // with their dispatch numbers, product types, and storing tank codes.
        // Tank allocations have the batch code; processing_runs have the dispatch number and storing tank.
        await using var cmd = new MySqlCommand(@"
            SELECT 
                a.BatchCode,
                r.DispatchNumber,
                a.ProductType,
                st.Code AS StoringTankCode,
                a.AllocatedAtUtc AS CompletionTimeUtc
            FROM tank_allocations a
            INNER JOIN processing_runs r ON a.ProcessingRunId = r.Id
            INNER JOIN tanks st ON r.StoringTankId = st.Id
            WHERE a.BatchCode IS NOT NULL 
              AND a.BatchCode != ''
            ORDER BY a.AllocatedAtUtc DESC", conn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var productTypeStr = reader.GetString("ProductType");
            var productLine = MapProductType(productTypeStr);

            batches.Add(new ProcessingBatchInfo
            {
                BatchCode = reader.GetString("BatchCode"),
                DispatchNumber = reader.GetString("DispatchNumber"),
                ProductLine = productLine,
                StoringTankCode = reader.IsDBNull(reader.GetOrdinal("StoringTankCode"))
                    ? null
                    : reader.GetString("StoringTankCode"),
                CompletionTimeUtc = reader.GetDateTime("CompletionTimeUtc")
            });
        }

        return batches;
    }

    /// <summary>
    /// Maps the processing service's ProductType enum values to the quality lab ProductLine enum.
    /// Processing uses: FM, FLM, SY, SK, DY, CD — same as quality lab.
    /// The DB stores these as integer enum values, so we read the string name.
    /// </summary>
    private static ProductLine MapProductType(string productType)
    {
        // Processing stores the enum as int in MySQL, but we read it as string
        // Try parsing as enum name first
        if (Enum.TryParse<ProductLine>(productType, ignoreCase: true, out var line))
            return line;

        // Try parsing as integer (enum ordinal)
        if (int.TryParse(productType, out var intVal) && Enum.IsDefined(typeof(ProductLine), intVal))
            return (ProductLine)intVal;

        // Fallback mappings for known processing ProductType values
        return productType.ToUpperInvariant() switch
        {
            "FRESHMILK" => ProductLine.FM,
            "FLAVOUREDMILK" => ProductLine.FLM,
            "SETYOGURT" => ProductLine.SY,
            "SETKIRI" => ProductLine.SK,
            "DRINKINGYOGURT" => ProductLine.DY,
            "CURD" => ProductLine.CD,
            _ => ProductLine.FM // default fallback
        };
    }

    private sealed class ProcessingBatchInfo
    {
        public string BatchCode { get; init; } = string.Empty;
        public string DispatchNumber { get; init; } = string.Empty;
        public ProductLine ProductLine { get; init; }
        public string? StoringTankCode { get; init; }
        public DateTime? CompletionTimeUtc { get; init; }
    }
}
