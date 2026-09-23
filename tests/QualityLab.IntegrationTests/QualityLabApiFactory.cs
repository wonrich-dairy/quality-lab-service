using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Testcontainers.MySql;

namespace QualityLab.IntegrationTests;

/// <summary>
/// Starts a throwaway MySQL container and runs the real API against it.
/// EF Core migrations are applied by the app on startup, so every integration test
/// also proves the migrations run cleanly against an empty database.
/// </summary>
public class QualityLabApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithDatabase("quality_lab")
        .WithUsername("qls_app")
        .WithPassword("qls_test_pwd")
        .Build();

    public async Task InitializeAsync() => await _mysql.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _mysql.DisposeAsync().AsTask();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QualityLabDb"] = _mysql.GetConnectionString(),
                // No broker in CI: the Kafka check reports Degraded, which is expected.
                ["Kafka:BootstrapServers"] = "localhost:1",
                ["Kafka:Topics:BatchDeterminations"] = "wonrich.quality-lab.batch-determinations.v1"
            }));

        return base.CreateHost(builder);
    }
}
