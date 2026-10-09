using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;
using QualityLab.Api.Observability;

namespace QualityLab.Api.Infrastructure.Kafka;

/// <summary>
/// Polls outbox_messages and publishes to Kafka (SCRUM-24; same algorithm as Processing Service SCRUM-68).
/// - Rows claimed with SELECT ... FOR UPDATE SKIP LOCKED in a READ COMMITTED transaction (no double publish across instances)
/// - Batch stops at the first failure to preserve per-key ordering
/// - Retries up to Outbox:MaxRetries (default 20) with exponential backoff (5s .. 5m), then row is Poisoned (human review)
/// - At-least-once: consumers dedupe on eventId
/// Also deletes Processed rows older than Outbox:ProcessedRetentionDays (default 7) once a day.
/// </summary>
public sealed class OutboxRelayService : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<OutboxRelayService> _logger;
    private readonly int _maxRetries;
    private readonly int _retentionDays;
    private readonly string _dlqTopic;
    private int _consecutiveFailedBatches;
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    public OutboxRelayService(IServiceProvider services, ILogger<OutboxRelayService> logger, IConfiguration configuration)
    {
        _services = services;
        _logger = logger;
        _maxRetries = configuration.GetValue("Outbox:MaxRetries", 20);
        _retentionDays = Math.Max(1, configuration.GetValue("Outbox:ProcessedRetentionDays", 7));
        _dlqTopic = configuration["Kafka:Topics:DeterminationsDeadLetter"] is { Length: > 0 } d ? d : "wonrich.dlq.quality-lab-determinations.v1";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxRelayService started - batch {BatchSize}, max retries {MaxRetries}", BatchSize, _maxRetries);
        await Task.Delay(Interval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = false;
            try
            {
                ok = await ProcessBatchAsync(stoppingToken);
                await CleanupIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing outbox batch");
            }

            TimeSpan delay;
            if (ok)
            {
                _consecutiveFailedBatches = 0;
                delay = Interval;
            }
            else
            {
                _consecutiveFailedBatches++;
                var seconds = Interval.TotalSeconds * Math.Pow(2, Math.Min(_consecutiveFailedBatches - 1, 6));
                delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <returns>false when a whole non-empty batch failed (caller backs off).</returns>
    private async Task<bool> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();
        var producer = scope.ServiceProvider.GetRequiredService<IKafkaProducer>();

        if (!db.Database.IsRelational())
            return true; // in-memory test provider: no raw SQL / row locking available

        List<OutboxMessage> pending;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx;
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", ct);
            tx = await db.Database.BeginTransactionAsync(ct);

            pending = await db.OutboxMessages.FromSqlInterpolated($@"
SELECT Id, Topic, `Key`, EventType, Payload, HeadersJson, CreatedAtUtc, ProcessedAtUtc, RetryCount, LastError, Status, CorrelationId
FROM outbox_messages
WHERE Status = {"Pending"}
ORDER BY CreatedAtUtc
LIMIT {BatchSize}
FOR UPDATE SKIP LOCKED").ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not claim outbox batch (DB unavailable or migration pending) - will retry");
            return true;
        }

        if (pending.Count == 0)
        {
            await tx.RollbackAsync(CancellationToken.None);
            QualityLabMetrics.OutboxBacklog.Set(await CountBacklogAsync(db, ct));
            return true;
        }

        foreach (var message in pending)
        {
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = message.CorrelationId, ["EventType"] = message.EventType, ["Topic"] = message.Topic
            });

            var success = false;
            try
            {
                success = await producer.PublishAsync(message.Topic, message.Key, message.Payload,
                    message.HeadersJson, message.CorrelationId, ct);
                if (!success) message.LastError = $"Publish failed, retry {message.RetryCount + 1}/{_maxRetries}";
            }
            catch (Exception ex)
            {
                message.LastError = ex.Message;
            }

            if (success)
            {
                message.Status = "Processed";
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.LastError = null;
                continue;
            }

            message.RetryCount++;
            if (message.RetryCount >= _maxRetries)
            {
                message.Status = "Poisoned";
                message.ProcessedAtUtc = DateTime.UtcNow;
                _logger.LogError("Outbox message {Id} key={Key} failed {MaxRetries} times - POISONED, needs human review. Requeue: Status='Pending', RetryCount=0. LastError: {LastError}",
                    message.Id, message.Key, _maxRetries, message.LastError);

                // Retries exhausted: park a copy on the dead-letter topic. Best effort - if the broker is still down
                // this fails too, which is why the row stays Poisoned in the outbox as the durable failure record.
                await ParkOnDlqAsync(producer, message, ct);
            }

            break; // stop at first failure - preserves per-key ordering
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _logger.LogWarning(ex, "Outbox batch rolled back - claimed rows stay Pending");
            return false;
        }

        QualityLabMetrics.OutboxBacklog.Set(await CountBacklogAsync(db, ct));
        return pending.Any(m => m.Status == "Processed");
    }

    private async Task ParkOnDlqAsync(IKafkaProducer producer, OutboxMessage message, CancellationToken ct)
    {
        try
        {
            var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(message.HeadersJson) ?? new();
            headers["x-original-topic"] = message.Topic;
            headers["x-failure-reason"] = message.LastError ?? "retries exhausted";
            headers["x-failure-timestamp"] = DateTime.UtcNow.ToString("O");

            var parked = await producer.PublishAsync(_dlqTopic, message.Key, message.Payload,
                System.Text.Json.JsonSerializer.Serialize(headers), message.CorrelationId, ct);
            if (parked)
                _logger.LogWarning("Outbox message {Id} key={Key} parked on DLQ {DlqTopic}", message.Id, message.Key, _dlqTopic);
            else
                _logger.LogError("Outbox message {Id} key={Key} could not be parked on DLQ {DlqTopic} - remains Poisoned in outbox_messages", message.Id, message.Key, _dlqTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLQ publish failed for outbox message {Id} - remains Poisoned in outbox_messages", message.Id);
        }
    }

    private static async Task<int> CountBacklogAsync(QualityLabDbContext db, CancellationToken ct) =>
        await db.OutboxMessages.CountAsync(m => m.Status == "Pending" || m.Status == "Poisoned", ct);

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastCleanupUtc < TimeSpan.FromHours(24)) return;
        _lastCleanupUtc = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();
        if (!db.Database.IsRelational()) return;

        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        // Poisoned rows are never auto-deleted - they are the failure record for human review
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM outbox_messages WHERE Status = {"Processed"} AND ProcessedAtUtc IS NOT NULL AND ProcessedAtUtc < {cutoff}", ct);
        if (deleted > 0)
            _logger.LogInformation("Outbox cleanup: deleted {Count} Processed rows older than {Days}d", deleted, _retentionDays);
    }
}
