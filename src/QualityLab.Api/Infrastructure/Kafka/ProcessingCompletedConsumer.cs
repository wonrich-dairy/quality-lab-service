using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Infrastructure.Kafka;

/// <summary>
/// Kafka consumer for ProcessingCompleted events (SCRUM-20 DOD 4).
/// When the Processing Service marks a run as Completed, it publishes to
/// <c>wonrich.processing.stage-events.v1</c>. This consumer picks up those
/// events and creates BatchWorkItem entries in the Quality Lab work queue.
///
/// Real Processing event contract (SCRUM-68):
///   Key   = batch code (e.g. "265-FM-A")
///   Header "eventType" = "ProcessingCompleted"
///   Payload (camelCase JSON):
///     { "eventId", "batchId", "dispatchNumber", "completedAtUtc", "mixingTankCode", ... }
///   Product line is derived from the batch code middle segment (265-FM-A → FM).
///
/// Consumer group: <c>quality-lab-stage-events</c>.
/// </summary>
public sealed class ProcessingCompletedConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProcessingCompletedConsumer> _logger;

    private const string Topic = "wonrich.processing.stage-events.v1";
    private const string DlqTopic = "wonrich.dlq.quality-lab-stage-events.v1";
    private const string GroupId = "quality-lab-stage-events";
    private const string EventTypeHeader = "eventType";
    private const int MaxRetries = 3;

    public ProcessingCompletedConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ProcessingCompletedConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the rest of the app time to start
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — ProcessingCompleted consumer disabled.");
            return;
        }

        var consumerConfig = BuildConsumerConfig(bootstrapServers);
        var producerConfig = BuildProducerConfig(bootstrapServers);

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        using var dlqProducer = new ProducerBuilder<string, string>(producerConfig).Build();

        consumer.Subscribe(Topic);
        _logger.LogInformation("ProcessingCompleted consumer started on topic {Topic}, group {GroupId}", Topic, GroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? cr = null;
            try
            {
                cr = consumer.Consume(stoppingToken);
                if (cr?.Message?.Value == null) continue;

                // Read eventType from Kafka headers (Processing puts it there, not in the body)
                var eventType = GetHeaderValue(cr.Message.Headers, EventTypeHeader);

                // Only process "ProcessingCompleted" events
                if (!string.Equals(eventType, "ProcessingCompleted", StringComparison.OrdinalIgnoreCase))
                {
                    // Not our event — commit and move on
                    consumer.Commit(cr);
                    continue;
                }

                // Batch code from the message key (Processing keys by batch code)
                var batchCode = cr.Message.Key;
                if (string.IsNullOrWhiteSpace(batchCode))
                {
                    _logger.LogWarning("ProcessingCompleted event has null/empty key — skipping");
                    consumer.Commit(cr);
                    continue;
                }

                // QA-20-12: Only commit offset AFTER successful DB write.
                // Retry on transient failures; park on DLQ after MaxRetries.
                var success = await HandleMessageWithRetry(batchCode, cr.Message.Value, stoppingToken);

                if (success)
                {
                    consumer.Commit(cr);
                }
                else
                {
                    // Route to DLQ — the batch will need manual intervention
                    await ParkOnDlq(dlqProducer, cr, stoppingToken);
                    consumer.Commit(cr); // commit after DLQ so we don't re-process forever
                }
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in consumer loop");
                // If we have a consumed result that was NOT committed, park it on DLQ
                if (cr != null)
                {
                    try
                    {
                        await ParkOnDlq(dlqProducer, cr, stoppingToken);
                        consumer.Commit(cr);
                    }
                    catch (Exception dlqEx)
                    {
                        _logger.LogError(dlqEx, "Failed to park message on DLQ — offset NOT committed, will retry on restart");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        consumer.Close();
    }

    /// <summary>
    /// Attempts to handle the message up to <see cref="MaxRetries"/> times.
    /// Returns true on success, false when all retries exhausted.
    /// </summary>
    private async Task<bool> HandleMessageWithRetry(string batchCode, string payload, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await HandleMessage(batchCode, payload, ct);
                return true;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                _logger.LogWarning(ex,
                    "HandleMessage failed for batch {BatchCode}, attempt {Attempt}/{Max} — retrying",
                    batchCode, attempt, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "HandleMessage failed for batch {BatchCode} after {Max} attempts — routing to DLQ",
                    batchCode, MaxRetries);
            }
        }
        return false;
    }

    private async Task HandleMessage(string batchCode, string payload, CancellationToken ct)
    {
        // Real Processing payload: { eventId, batchId, dispatchNumber, completedAtUtc, mixingTankCode, ... }
        var evt = JsonSerializer.Deserialize<ProcessingCompletedEvent>(payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();

        // Idempotency: skip if batch already exists
        var exists = await db.BatchWorkItems.AnyAsync(b => b.BatchCode == batchCode, ct);
        if (exists)
        {
            _logger.LogDebug("Batch {BatchCode} already in work queue — skipping", batchCode);
            return;
        }

        // Derive product line from batch code middle segment: "265-FM-A" → "FM"
        var productLine = DeriveProductLine(batchCode);
        if (productLine == null)
        {
            _logger.LogWarning("Cannot derive product line from batch code '{BatchCode}' — storing as FM fallback", batchCode);
            productLine = ProductLine.FM;
        }

        var now = DateTime.UtcNow;
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = batchCode,
            DispatchNumber = evt?.DispatchNumber ?? $"DSP-{batchCode}",
            ProductLine = productLine.Value,
            StoringTankCode = evt?.MixingTankCode,
            CompletionTimeUtc = evt?.CompletedAtUtc ?? now,
            Status = BatchStatus.AwaitingPanel,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        db.BatchWorkItems.Add(batch);
        // QA-20-12: If SaveChanges throws, the exception propagates to the retry loop.
        // The offset is NOT committed, so the message will be retried or sent to DLQ.
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Batch {BatchCode} ({ProductLine}) added to work queue from ProcessingCompleted event",
            batchCode, productLine);
    }

    /// <summary>
    /// Derives the <see cref="ProductLine"/> from a batch code.
    /// Batch codes follow the pattern "[day]-[ProductLine]-[letter]", e.g. "265-FM-A" → FM.
    /// </summary>
    internal static ProductLine? DeriveProductLine(string batchCode)
    {
        var parts = batchCode.Split('-');
        if (parts.Length < 2) return null;
        return Enum.TryParse<ProductLine>(parts[1], true, out var pl) ? pl : null;
    }

    private static string? GetHeaderValue(Headers? headers, string key)
    {
        if (headers == null) return null;
        try
        {
            var header = headers.FirstOrDefault(h =>
                string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));
            return header != null ? Encoding.UTF8.GetString(header.GetValueBytes()) : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ParkOnDlq(IProducer<string, string> producer, ConsumeResult<string, string> cr, CancellationToken ct)
    {
        try
        {
            var dlqMessage = new Message<string, string>
            {
                Key = cr.Message.Key,
                Value = cr.Message.Value,
                Headers = cr.Message.Headers ?? new Headers()
            };
            dlqMessage.Headers.Add("x-original-topic", Encoding.UTF8.GetBytes(cr.Topic));
            dlqMessage.Headers.Add("x-failure-timestamp", Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O")));

            await producer.ProduceAsync(DlqTopic, dlqMessage, ct);
            _logger.LogWarning("Message for key {Key} parked on DLQ {DlqTopic}", cr.Message.Key, DlqTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to produce to DLQ for key {Key}", cr.Message.Key);
            throw; // Let caller decide
        }
    }

    private ConsumerConfig BuildConsumerConfig(string bootstrapServers)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        ApplySasl(config);
        return config;
    }

    private ProducerConfig BuildProducerConfig(string bootstrapServers)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
        };

        ApplySasl(config);
        return config;
    }

    private void ApplySasl(ClientConfig config)
    {
        var securityProtocol = _configuration["Kafka:SecurityProtocol"];
        if (!string.IsNullOrWhiteSpace(securityProtocol) &&
            Enum.TryParse<SecurityProtocol>(securityProtocol, true, out var protocol) &&
            protocol != SecurityProtocol.Plaintext)
        {
            config.SecurityProtocol = protocol;
            config.SaslMechanism = Enum.TryParse<SaslMechanism>(_configuration["Kafka:SaslMechanism"], true, out var mechanism)
                ? mechanism
                : SaslMechanism.Plain;
            config.SaslUsername = _configuration["Kafka:SaslUsername"];
            config.SaslPassword = _configuration["Kafka:SaslPassword"];
        }
    }

    /// <summary>
    /// Matches the real Processing Service event payload (SCRUM-68 contract).
    /// camelCase JSON: { eventId, batchId, dispatchNumber, completedAtUtc, mixingTankCode, ... }
    /// </summary>
    internal sealed class ProcessingCompletedEvent
    {
        public Guid? EventId { get; set; }
        public Guid? BatchId { get; set; }
        public string? DispatchNumber { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string? MixingTankCode { get; set; }
    }
}
