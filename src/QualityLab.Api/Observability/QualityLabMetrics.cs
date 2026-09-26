using Prometheus;

namespace QualityLab.Api.Observability;

/// <summary>
/// Prometheus metrics for Quality Lab Service (SCRUM-111), served on GET /metrics.
///
/// Naming follows the Wonrich convention &lt;service&gt;_http_*, with the same labels Processing
/// Service uses (method, endpoint, status), so the shared dashboard and the HighErrorRate alert
/// pick this service up by metric-name pattern without per-service queries.
/// The service and environment labels are added by Prometheus from the scrape job, not here.
/// </summary>
public static class QualityLabMetrics
{
    private static readonly string[] RequestLabels = { "method", "endpoint", "status" };

    public static readonly Counter HttpRequests = Metrics.CreateCounter(
        "quality_lab_http_requests_total",
        "HTTP requests handled, by endpoint and status code.",
        new CounterConfiguration { LabelNames = RequestLabels });

    /// <summary>Server errors (5xx) only. A 4xx is the caller's mistake, not the service failing.</summary>
    public static readonly Counter HttpRequestErrors = Metrics.CreateCounter(
        "quality_lab_http_request_errors_total",
        "HTTP requests that ended in a server error (5xx), by endpoint.",
        new CounterConfiguration { LabelNames = RequestLabels });

    public static readonly Histogram HttpRequestDuration = Metrics.CreateHistogram(
        "quality_lab_http_request_duration_seconds",
        "HTTP request duration in seconds, by endpoint and status code.",
        new HistogramConfiguration
        {
            LabelNames = RequestLabels,
            // 5 ms to ~10 s
            Buckets = Histogram.ExponentialBuckets(start: 0.005, factor: 2, count: 12)
        });

    /// <summary>Final lab determinations: result = "pass" (batch cleared) or "fail" (batch failed).</summary>
    public static readonly Counter Determinations = Metrics.CreateCounter(
        "quality_lab_determinations_total",
        "Final lab determinations, by result (pass = batch cleared, fail = batch failed).",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Counter StageEventsReceived = Metrics.CreateCounter(
        "quality_lab_stage_events_received_total",
        "Processing stage events received from Kafka, by event type.",
        new CounterConfiguration { LabelNames = new[] { "event_type" } });

    /// <summary>
    /// Publishes both determination results at zero, so the dashboard shows 0 rather than
    /// "No data" before the first determination. Call once at startup.
    /// </summary>
    public static void Initialise()
    {
        Determinations.WithLabels("pass");
        Determinations.WithLabels("fail");
    }

    /// <summary>Call from the determination code (SCRUM-23) when a batch is cleared or failed.</summary>
    public static void RecordDetermination(bool passed) =>
        Determinations.WithLabels(passed ? "pass" : "fail").Inc();
}
