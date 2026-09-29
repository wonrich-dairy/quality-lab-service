using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Tests;

/// <summary>
/// Integration tests using WebApplicationFactory:
/// - Work queue populated and retrievable
/// - 403 for ProductionManager on RecordPanel
/// </summary>
public class PanelsIntegrationTests : IClassFixture<PanelsIntegrationTests.QualityLabWebFactory>
{
    private readonly QualityLabWebFactory _factory;

    private const string TestSigningKey = "ThisIsATestSigningKeyForIntegrationTestsOnly2026!";
    private const string Issuer = "wonrich-auth";
    private const string Audience = "wonrich-services";

    public PanelsIntegrationTests(QualityLabWebFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthenticatedClient(string role)
    {
        var client = _factory.CreateClient();
        var token = GenerateJwt(role, "test-user-" + role.ToLower());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string GenerateJwt(string role, string userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, role),
            new Claim("userId", userId)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── Work-queue integration test ────────────────────────────────────────────

    [Fact]
    public async Task WorkQueue_CreateBatch_AppearsInList()
    {
        using var client = CreateAuthenticatedClient(WonrichRoles.QualityAnalyst);

        // Create a batch via POST
        var batchCode = $"INT-FM-{Guid.NewGuid().ToString()[..4]}";
        var createResponse = await client.PostAsJsonAsync("/api/panels/batch", new
        {
            batchCode,
            dispatchNumber = $"DSP-{batchCode}",
            productLine = "FM",
            storingTankCode = "ST-01"
        });
        var createBody = await createResponse.Content.ReadAsStringAsync();
        Assert.True(createResponse.StatusCode == HttpStatusCode.Created,
            $"Expected Created, got {createResponse.StatusCode}: {createBody}");

        // Retrieve work queue
        var queueResponse = await client.GetAsync("/api/panels/work-queue");
        Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);

        var body = await queueResponse.Content.ReadAsStringAsync();
        Assert.Contains(batchCode, body);
    }

    // ── 403 test: ProductionManager cannot RecordPanel ──────────────────────

    [Fact]
    public async Task RecordPanel_ProductionManager_Returns403()
    {
        // First, create a batch as QualityAnalyst
        using var analystClient = CreateAuthenticatedClient(WonrichRoles.QualityAnalyst);
        var batchCode = $"403-FM-{Guid.NewGuid().ToString()[..4]}";
        await analystClient.PostAsJsonAsync("/api/panels/batch", new
        {
            batchCode,
            dispatchNumber = $"DSP-{batchCode}",
            productLine = "FM",
            storingTankCode = "ST-01"
        });

        // Now try to record a panel as ProductionManager — should be 403
        using var pmClient = CreateAuthenticatedClient(WonrichRoles.ProductionManager);
        var response = await pmClient.PostAsJsonAsync($"/api/panels/{batchCode}", new
        {
            fatPercent = 3.8,
            lactometerReading = 29.0,
            temperatureCelsius = 27.0,
            ph = 6.7
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RecordPanel_QualityAnalyst_Succeeds()
    {
        using var client = CreateAuthenticatedClient(WonrichRoles.QualityAnalyst);
        var batchCode = $"OK-FM-{Guid.NewGuid().ToString()[..4]}";
        await client.PostAsJsonAsync("/api/panels/batch", new
        {
            batchCode,
            dispatchNumber = $"DSP-{batchCode}",
            productLine = "FM",
            storingTankCode = "ST-01"
        });

        var response = await client.PostAsJsonAsync($"/api/panels/{batchCode}", new
        {
            fatPercent = 3.8,
            lactometerReading = 29.0,
            temperatureCelsius = 27.0,
            ph = 6.7
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── QA-21-01: Composite batch endpoint (DOD 3) ────────────────────────

    [Fact]
    public async Task GetBatch_Empty_ReturnsNullPanelAndSensory()
    {
        using var client = CreateAuthenticatedClient(WonrichRoles.QualityAnalyst);
        var batchCode = $"COMP-FM-{Guid.NewGuid().ToString()[..4]}";
        await client.PostAsJsonAsync("/api/panels/batch", new
        {
            batchCode,
            dispatchNumber = $"DSP-{batchCode}",
            productLine = "FM",
            storingTankCode = "ST-01"
        });

        var response = await client.GetAsync($"/api/panels/batch/{batchCode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"latestPanel\":null", body);
        Assert.Contains("\"sensoryEvaluation\":null", body);
        Assert.Contains("\"hasPanels\":false", body);
        Assert.Contains("\"hasSensory\":false", body);
    }

    [Fact]
    public async Task GetBatch_WithPanelAndSensory_ReturnsComposite()
    {
        using var client = CreateAuthenticatedClient(WonrichRoles.QualityAnalyst);
        var batchCode = $"FULL-FM-{Guid.NewGuid().ToString()[..4]}";

        // Create batch
        await client.PostAsJsonAsync("/api/panels/batch", new
        {
            batchCode,
            dispatchNumber = $"DSP-{batchCode}",
            productLine = "FM",
            storingTankCode = "ST-01"
        });

        // Record chemical panel
        await client.PostAsJsonAsync($"/api/panels/{batchCode}", new
        {
            fatPercent = 3.8,
            lactometerReading = 29.0,
            temperatureCelsius = 27.0,
            ph = 6.7
        });

        // Record sensory evaluation
        await client.PostAsJsonAsync($"/api/panels/{batchCode}/sensory", new
        {
            taste = "Acceptable",
            smell = "Acceptable",
            colour = "Acceptable",
            appearance = "Acceptable"
        });

        // Get composite batch
        var response = await client.GetAsync($"/api/panels/batch/{batchCode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // Verify chemical panel present
        Assert.Contains("\"hasPanels\":true", body);
        Assert.Contains("\"snf\":", body);  // latestPanel includes derived values
        // Verify sensory present
        Assert.Contains("\"hasSensory\":true", body);
        Assert.Contains("\"taste\":\"Acceptable\"", body);
    }

    // ── Custom WebApplicationFactory ───────────────────────────────────────────


    public class QualityLabWebFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Config must be set BEFORE the host builds — Program.cs reads it eagerly
            builder.UseSetting("ConnectionStrings:QualityLabDb", "Server=unused;Database=unused;");
            builder.UseSetting("Auth:SigningKey", TestSigningKey);
            builder.UseSetting("Auth:Issuer", Issuer);
            builder.UseSetting("Auth:Audience", Audience);
            builder.UseSetting("Kafka:BootstrapServers", "");

            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove ALL EF Core + MySQL registrations to avoid dual-provider conflict
                var toRemove = services
                    .Where(d =>
                        d.ServiceType == typeof(DbContextOptions<QualityLabDbContext>)
                        || d.ServiceType == typeof(DbContextOptions)
                        || d.ServiceType == typeof(QualityLabDbContext)
                        || d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                        || d.ImplementationType?.FullName?.Contains("MySql") == true
                        || d.ImplementationType?.FullName?.Contains("Microting") == true)
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);

                // Re-register DbContext with InMemory only
                var dbName = "QualityLabIntegrationTest";
                services.AddDbContext<QualityLabDbContext>(options =>
                    options.UseInMemoryDatabase(dbName));

                // Remove the Kafka consumer (doesn't run in tests)
                var kafkaDescriptors = services.Where(
                    d => d.ImplementationType?.Name == "ProcessingCompletedConsumer").ToList();
                foreach (var kd in kafkaDescriptors) services.Remove(kd);
            });
        }
    }
}
