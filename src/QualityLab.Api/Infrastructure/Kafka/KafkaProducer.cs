using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace QualityLab.Api.Infrastructure.Kafka;

public interface IKafkaProducer
{
    /// <summary>True only when the broker acknowledged persistence. Never throws - false means "retry later".</summary>
    Task<bool> PublishAsync(string topic, string key, string payloadJson, string headersJson, string correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Confluent.Kafka producer, same settings as Processing Service's (SCRUM-68): acks=all, idempotent.
/// Only ever called from OutboxRelayService after the DB commit, so broker trouble cannot fail a determination save.
/// </summary>
public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducer> _logger;

    public KafkaProducer(IConfiguration config, ILogger<KafkaProducer> logger)
    {
        _logger = logger;

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            ClientId = "quality-lab-service",
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 10000,
            CompressionType = CompressionType.Snappy,
        };

        var securityProtocol = config["Kafka:SecurityProtocol"];
        if (!string.IsNullOrWhiteSpace(securityProtocol) &&
            Enum.TryParse<SecurityProtocol>(securityProtocol, true, out var protocol) &&
            protocol != SecurityProtocol.Plaintext)
        {
            producerConfig.SecurityProtocol = protocol;
            producerConfig.SaslMechanism = Enum.TryParse<SaslMechanism>(config["Kafka:SaslMechanism"], true, out var mechanism)
                ? mechanism
                : SaslMechanism.Plain;
            producerConfig.SaslUsername = config["Kafka:SaslUsername"];
            producerConfig.SaslPassword = config["Kafka:SaslPassword"];
        }

        _producer = new ProducerBuilder<string, string>(producerConfig)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka producer error: {Reason}", e.Reason))
            .Build();

        _logger.LogInformation("Kafka producer created - BootstrapServers: {BootstrapServers}", producerConfig.BootstrapServers);
    }

    public async Task<bool> PublishAsync(string topic, string key, string payloadJson, string headersJson, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var headers = new Headers { { "x-correlation-id", Encoding.UTF8.GetBytes(correlationId) } };

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        if (kv.Key is "x-correlation-id" or "correlationId") continue;
                        headers.Add(kv.Key, Encoding.UTF8.GetBytes(kv.Value ?? ""));
                    }
                }
            }
            catch (JsonException) { /* malformed headers must not block the event itself */ }

            var result = await _producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = key,
                Value = payloadJson,
                Headers = headers,
                Timestamp = new Timestamp(DateTime.UtcNow)
            }, cancellationToken);

            _logger.LogInformation("Published event to {Topic} key={Key} correlationId={CorrelationId} partition={Partition} offset={Offset}",
                topic, key, correlationId, result.Partition, result.Offset);

            return result.Status == PersistenceStatus.Persisted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish to {Topic} key={Key} correlationId={CorrelationId} - will retry",
                topic, key, correlationId);
            return false;
        }
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

/// <summary>
/// Used when Kafka:BootstrapServers is not configured. Returns false so outbox rows stay Pending
/// (never marked Processed) - events are delayed, not lost.
/// </summary>
public sealed class NoOpKafkaProducer(ILogger<NoOpKafkaProducer> logger) : IKafkaProducer
{
    public Task<bool> PublishAsync(string topic, string key, string payloadJson, string headersJson, string correlationId, CancellationToken cancellationToken)
    {
        logger.LogWarning("[NoOp] Kafka not configured - event for {Topic} key={Key} correlationId={CorrelationId} stays PENDING in outbox_messages",
            topic, key, correlationId);
        return Task.FromResult(false);
    }
}
