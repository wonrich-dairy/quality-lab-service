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
/// Consumer group: <c>quality-lab-stage-events</c>.
/// </summary>
public sealed class ProcessingCompletedConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProcessingCompletedConsumer> _logger;

    private const string Topic = "wonrich.processing.stage-events.v1";
    private const string GroupId = "quality-lab-stage-events";

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

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };

        // SASL config for Azure VM broker
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

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(Topic);

        _logger.LogInformation("ProcessingCompleted consumer started on topic {Topic}, group {GroupId}", Topic, GroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = consumer.Consume(stoppingToken);
                if (cr?.Message?.Value == null) continue;

                await HandleMessage(cr.Message.Key, cr.Message.Value, stoppingToken);
                consumer.Commit(cr);
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
                _logger.LogError(ex, "Error processing Kafka message");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        consumer.Close();
    }

    private async Task HandleMessage(string key, string value, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<StageEventEnvelope>(value, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (envelope == null)
            {
                _logger.LogWarning("Null event envelope from topic {Topic}", Topic);
                return;
            }

            // Only process "Completed" stage events
            if (!string.Equals(envelope.EventType, "ProcessingCompleted", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(envelope.Stage, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Ignoring event type {EventType} / stage {Stage}", envelope.EventType, envelope.Stage);
                return;
            }

            if (string.IsNullOrWhiteSpace(envelope.BatchCode))
            {
                _logger.LogWarning("ProcessingCompleted event missing BatchCode");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();

            // Idempotency: skip if batch already exists
            var exists = await db.BatchWorkItems.AnyAsync(b => b.BatchCode == envelope.BatchCode, ct);
            if (exists)
            {
                _logger.LogDebug("Batch {BatchCode} already in work queue — skipping", envelope.BatchCode);
                return;
            }

            if (!Enum.TryParse<ProductLine>(envelope.ProductLine, true, out var productLine))
            {
                _logger.LogWarning("Unknown product line {ProductLine} in event for batch {BatchCode}",
                    envelope.ProductLine, envelope.BatchCode);
                return;
            }

            var now = DateTime.UtcNow;
            var batch = new BatchWorkItem
            {
                Id = Guid.NewGuid(),
                BatchCode = envelope.BatchCode,
                DispatchNumber = envelope.DispatchNumber ?? $"DSP-{envelope.BatchCode}",
                ProductLine = productLine,
                StoringTankCode = envelope.StoringTankCode,
                CompletionTimeUtc = envelope.CompletedAtUtc ?? now,
                Status = BatchStatus.AwaitingPanel,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            db.BatchWorkItems.Add(batch);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Batch {BatchCode} ({ProductLine}) added to work queue from ProcessingCompleted event",
                envelope.BatchCode, productLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle ProcessingCompleted event: {Value}", value);
        }
    }

    /// <summary>
    /// Envelope for processing stage events. Flexible deserialization
    /// to handle varying schemas from the Processing Service.
    /// </summary>
    private sealed class StageEventEnvelope
    {
        public string? EventType { get; set; }
        public string? Stage { get; set; }
        public string? BatchCode { get; set; }
        public string? DispatchNumber { get; set; }
        public string? ProductLine { get; set; }
        public string? StoringTankCode { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }
}
