using System.Net;
using System.Text.Json;

namespace QualityLab.IntegrationTests;

public class HealthEndpointTests(QualityLabApiFactory factory) : IClassFixture<QualityLabApiFactory>
{
    [Fact]
    public async Task Health_returns_200_and_database_check_is_healthy()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();

        var mysql = checks.Single(c => c.GetProperty("name").GetString() == "mysql");
        Assert.Equal("Healthy", mysql.GetProperty("status").GetString());

        // Kafka has no broker in CI, so it is Degraded by design and must not fail the request.
        var kafka = checks.Single(c => c.GetProperty("name").GetString() == "kafka");
        Assert.Equal("Degraded", kafka.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Version_endpoint_reports_the_build_commit()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/version");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sha", body);
    }
}
