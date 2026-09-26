using System.Text;
using Confluent.Kafka;

namespace QualityLab.Api.Observability;

/// <summary>
/// Passive listener on Processing's stage events (SCRUM-111).
///
/// Logs every event with the correlation ID from its x-correlation-id header, so one ID in Loki
/// shows a request's whole path: the HTTP call into Processing, the event it published, and its
/// arrival here. It does no business processing.
///
/// It NEVER commits offsets. The real consumer, which will act on these events, is later work; when
/// it arrives it uses the same consumer group and still receives every retained event, because
/// nothing here has marked any of them as handled. The cost: after a restart, this listener logs
/// again from the group's last committed position.
///
/// Disable with Kafka:StageEventListener:Enabled = false (the integration tests do).
/// </summary>
public sealed class StageEventListener(IConfiguration configuration, ILogger<StageEventListener> logger)
    : BackgroundService
{
    private const string CorrelationHeader = "x-correlation-id";
    private const string EventTypeHeader = "eventType";

    // Consume() blocks, so the loop gets its own thread rather than holding a thread-pool one.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Listen(stoppingToken), stoppingToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Listen(CancellationToken stoppingToken)
    {
        // An exception escaping a BackgroundService stops the whole app, so nothing may leave here.
        try
        {
            if (!configuration.GetValue("Kafka:StageEventListener:Enabled", true))
            {
                logger.LogInformation("Stage event listener disabled by configuration");
                return;
            }

            var bootstrap = configuration["Kafka:BootstrapServers"];
            var topic = configuration["Kafka:Topics:StageEvents"];
            var group = configuration["Kafka:ConsumerGroups:StageEvents"] ?? "quality-lab-stage-events";
            if (string.IsNullOrWhiteSpace(bootstrap) || string.IsNullOrWhiteSpace(topic))
            {
                logger.LogWarning("Stage event listener not started: Kafka:BootstrapServers or Kafka:Topics:StageEvents is not configured");
                return;
            }

            var config = new ConsumerConfig
            {
                BootstrapServers = bootstrap,
                GroupId = group,
                ClientId = "quality-lab-stage-event-listener",
                EnableAutoCommit = false,            // passive: see class comment
                EnableAutoOffsetStore = false,
                AutoOffsetReset = AutoOffsetReset.Latest
            };
            ApplySecurity(config);

            using var consumer = new ConsumerBuilder<string, string>(config)
                .SetErrorHandler((_, e) => logger.LogWarning("Stage event listener: Kafka {Code} {Reason}", e.Code, e.Reason))
                .Build();

            consumer.Subscribe(topic);
            logger.LogInformation("Stage event listener started on {Topic} as group {Group} (passive, no offset commits)", topic, group);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = consumer.Consume(stoppingToken);
                        if (result?.Message is not null)
                            LogEvent(result);
                    }
                    catch (ConsumeException ex)
                    {
                        logger.LogWarning("Stage event listener: {Code} {Reason}", ex.Error.Code, ex.Error.Reason);
                        stoppingToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); // do not spin on a persistent error
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            finally
            {
                consumer.Close(); // leave the group cleanly; commits nothing
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stage event listener stopped unexpectedly; the service keeps running without it");
        }
    }

    private void LogEvent(ConsumeResult<string, string> result)
    {
        var headers = result.Message.Headers;
        var correlationId = Header(headers, CorrelationHeader) ?? "none";
        var eventType = Header(headers, EventTypeHeader) ?? "unknown";

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            logger.LogInformation("Received {EventType} for batch {BatchId} from {Topic} partition {Partition} offset {Offset}",
                eventType, result.Message.Key, result.Topic, result.Partition.Value, result.Offset.Value);
        }

        // Bounded label: the producer sends a handful of event type names; anything odd is grouped.
        var label = eventType.Length <= 64 ? eventType : "other";
        QualityLabMetrics.StageEventsReceived.WithLabels(label).Inc();
    }

    private static string? Header(Headers? headers, string name) =>
        headers is not null && headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Same settings as KafkaHealthCheck: plaintext locally, SASL on the staging broker.</summary>
    private void ApplySecurity(ClientConfig config)
    {
        if (Enum.TryParse<SecurityProtocol>(configuration["Kafka:SecurityProtocol"], true, out var protocol))
            config.SecurityProtocol = protocol;
        if (Enum.TryParse<SaslMechanism>(configuration["Kafka:SaslMechanism"], true, out var mechanism))
            config.SaslMechanism = mechanism;
        if (!string.IsNullOrWhiteSpace(configuration["Kafka:SaslUsername"]))
        {
            config.SaslUsername = configuration["Kafka:SaslUsername"];
            config.SaslPassword = configuration["Kafka:SaslPassword"];
        }
    }
}
