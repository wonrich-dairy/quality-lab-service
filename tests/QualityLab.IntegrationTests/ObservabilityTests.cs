using System.Net;

namespace QualityLab.IntegrationTests;

/// <summary>SCRUM-111: /metrics and correlation IDs, through the real HTTP pipeline.</summary>
public class ObservabilityTests(QualityLabApiFactory factory) : IClassFixture<QualityLabApiFactory>
{
    [Fact]
    public async Task Metrics_endpoint_exposes_request_metrics_after_a_request()
    {
        var client = factory.CreateClient();
        await client.GetAsync("/health");

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("quality_lab_http_requests_total{", body);
        Assert.Contains("endpoint=\"/health\"", body);
        Assert.Contains("quality_lab_http_request_duration_seconds_bucket{", body);
    }

    [Fact]
    public async Task Metrics_endpoint_publishes_both_determination_results_from_zero()
    {
        var body = await factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("quality_lab_determinations_total{result=\"pass\"}", body);
        Assert.Contains("quality_lab_determinations_total{result=\"fail\"}", body);
    }

    [Fact]
    public async Task Scrapes_of_metrics_are_not_counted_as_traffic()
    {
        var client = factory.CreateClient();
        await client.GetAsync("/metrics");

        var body = await client.GetStringAsync("/metrics");

        Assert.DoesNotContain("endpoint=\"/metrics\"", body);
    }

    [Fact]
    public async Task Correlation_id_from_the_caller_is_echoed_back()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-ID", "scrum111-test-0001");

        var response = await client.SendAsync(request);

        Assert.Equal("scrum111-test-0001", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        var id = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task Malformed_correlation_id_is_replaced_not_trusted()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "abc def <script>");

        var response = await client.SendAsync(request);

        var id = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.Matches("^[0-9a-f]{32}$", id);
    }
}
