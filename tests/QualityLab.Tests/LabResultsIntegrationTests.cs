using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using QualityLab.Api.Infrastructure.Persistence;
using QualityLab.Api.Shared.Authorization;

namespace QualityLab.Tests;

/// <summary>
/// SCRUM-25 over HTTP: role scope, each filter, empty result, CSV export, read-only surface,
/// and the 2-second budget on a 1000-panel dataset spread over every product line.
/// Uses its own in-memory database so other test classes' batches don't leak into the counts.
/// </summary>
public class LabResultsIntegrationTests : IClassFixture<LabResultsIntegrationTests.Factory>
{
    private const string TestSigningKey = "ThisIsATestSigningKeyForIntegrationTestsOnly2026!";
    private const string Issuer = "wonrich-auth";
    private const string Audience = "wonrich-services";
    private const int DatasetSize = 1000;

    private readonly Factory _factory;

    public LabResultsIntegrationTests(Factory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(role));
        return client;
    }

    private static string Jwt(string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, "u-" + role), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    // ── Access ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WonrichRoles.IntakeOfficer)]
    [InlineData(WonrichRoles.MccManager)]
    [InlineData(WonrichRoles.ProcessingTechnician)]
    public async Task OutOfScopeRole_Gets403(string role)
    {
        using var client = Client(role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/lab-results")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/lab-results/export")).StatusCode);
    }

    [Theory]
    [InlineData(WonrichRoles.QualityAnalyst)]
    [InlineData(WonrichRoles.ProductionManager)]
    [InlineData(WonrichRoles.SystemAdministrator)]
    public async Task InScopeRole_Gets200(string role)
    {
        using var client = Client(role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/lab-results")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_Gets401()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/lab-results")).StatusCode);
    }

    // ── Read-only ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("POST", "/api/lab-results")]
    [InlineData("PUT", "/api/lab-results")]
    [InlineData("PATCH", "/api/lab-results")]
    [InlineData("DELETE", "/api/lab-results")]
    [InlineData("POST", "/api/lab-results/export")]
    [InlineData("DELETE", "/api/lab-results/summary")]
    public async Task NoMutationEndpoint_IsReachable(string method, string path)
    {
        using var client = Client(WonrichRoles.SystemAdministrator);
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { })
        });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ── Filters ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("productLine=SY")]
    [InlineData("from=2026-07-01&to=2026-07-15")]
    [InlineData("result=Fail")]
    [InlineData("result=Pending")]
    [InlineData("reasonCode=LOW_FAT")]
    [InlineData("productLine=CD&result=Fail&reasonCode=SENSORY_TEXTURE")]
    public async Task EachFilter_ReturnsOnlyMatchingRows(string query)
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        var page = await Json(await client.GetAsync($"/api/lab-results?{query}"));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        var args = query.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);

        Assert.NotEmpty(items);
        foreach (var row in items)
        {
            if (args.TryGetValue("productLine", out var line))
                Assert.Equal(line, row.GetProperty("productLine").GetString());
            if (args.TryGetValue("result", out var result))
                Assert.Equal(result, row.GetProperty("determination").GetString());
            if (args.TryGetValue("reasonCode", out var code))
                Assert.Contains(code, row.GetProperty("reasonCodes").EnumerateArray().Select(c => c.GetString()));
            if (args.TryGetValue("from", out var from))
            {
                var tested = row.GetProperty("testedAtUtc").GetDateTime();
                Assert.InRange(tested, DateTime.Parse(from), DateTime.Parse(args["to"]).AddDays(1));
            }
        }
    }

    [Fact]
    public async Task NoMatch_Returns200WithEmptyItems()
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        var page = await Json(await client.GetAsync("/api/lab-results?from=2020-01-01&to=2020-01-31"));

        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Empty(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task InvalidFilter_Returns400WithMessage()
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        var response = await client.GetAsync("/api/lab-results?productLine=ZZ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("FM, FLM, SY, SK, DY, CD", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FermentedRow_ListsSnfAndTsAsNotApplicable()
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        var page = await Json(await client.GetAsync("/api/lab-results?productLine=CD"));
        var row = page.GetProperty("items")[0];

        var na = row.GetProperty("notApplicable").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("snf", na);
        Assert.Contains("ts", na);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("snf").ValueKind);
    }

    [Fact]
    public async Task Paging_50PerPage_WithTotal()
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        var page = await Json(await client.GetAsync("/api/lab-results"));

        Assert.Equal(50, page.GetProperty("items").GetArrayLength());
        Assert.Equal(DatasetSize, page.GetProperty("total").GetInt32());
        Assert.Equal(20, page.GetProperty("totalPages").GetInt32());
    }

    [Fact]
    public async Task Summary_ReasonCounts_HighestFirst_AndAddUp()
    {
        using var client = Client(WonrichRoles.ProductionManager);
        var summary = await Json(await client.GetAsync("/api/lab-results/summary"));

        var counts = summary.GetProperty("reasonCounts").EnumerateArray()
            .Select(e => e.GetProperty("count").GetInt32()).ToList();
        Assert.NotEmpty(counts);
        Assert.Equal(counts.OrderByDescending(c => c), counts);
        Assert.Equal(DatasetSize,
            summary.GetProperty("passed").GetInt32() + summary.GetProperty("failed").GetInt32() + summary.GetProperty("pending").GetInt32());
    }

    // ── CSV export ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ReturnsCsv_WithEveryRow_AndFilterInFileName()
    {
        using var client = Client(WonrichRoles.QualityAnalyst);

        var list = await Json(await client.GetAsync("/api/lab-results?productLine=SY"));
        var total = list.GetProperty("total").GetInt32();
        Assert.True(total > 50, "dataset should give SY more than one page");

        var response = await client.GetAsync("/api/lab-results/export?productLine=SY");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.StartsWith("lab-results_SY_exported_", fileName);
        Assert.EndsWith(".csv", fileName);

        var csv = (await response.Content.ReadAsStringAsync()).TrimStart('\uFEFF');
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(total + 1, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.Contains(",N/A,N/A,", l)); // SNF, TS on a fermented line
    }

    // ── Performance (NFR1 budget, in-process proxy for the JMeter run) ──────

    [Theory]
    [InlineData("")]
    [InlineData("productLine=FM")]
    [InlineData("result=Fail&reasonCode=COLOUR_OFF")]
    [InlineData("productLine=SY&from=2026-06-01&to=2026-09-30&result=Fail")]
    [InlineData("result=Pending&page=3")]
    public async Task AnyFilterCombination_On1000Panels_RespondsWithin2Seconds(string query)
    {
        using var client = Client(WonrichRoles.QualityAnalyst);
        await client.GetAsync("/api/lab-results"); // warm-up

        var sw = Stopwatch.StartNew();
        var list = await client.GetAsync($"/api/lab-results?{query}");
        var summary = await client.GetAsync($"/api/lab-results/summary?{query}");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = $"LabResultsIntegration_{Guid.NewGuid()}";
        private readonly object _gate = new();
        private bool _seeded;

        public void EnsureSeeded()
        {
            lock (_gate)
            {
                if (_seeded) return;
                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<QualityLabDbContext>();
                LabHistorySeed.AddPerformanceDataset(db, DatasetSize);
                db.SaveChanges();
                _seeded = true;
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:QualityLabDb", "Server=unused;Database=unused;");
            builder.UseSetting("Auth:SigningKey", TestSigningKey);
            builder.UseSetting("Auth:Issuer", Issuer);
            builder.UseSetting("Auth:Audience", Audience);
            builder.UseSetting("Kafka:BootstrapServers", "");
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
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

                services.AddDbContext<QualityLabDbContext>(o => o.UseInMemoryDatabase(_dbName));

                foreach (var hosted in services.Where(d =>
                             d.ImplementationType?.Name is "ProcessingCompletedConsumer" or "ProcessingDbSyncService" or "StageEventListener").ToList())
                    services.Remove(hosted);
            });
        }
    }
}
