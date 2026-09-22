using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QualityLab.Api.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations. Reads connection string from the
/// QLS_DB_CONNECTION environment variable. No fallback — credentials must never
/// be hardcoded (SCRUM-107 AC2).
/// </summary>
public class QualityLabDbContextFactory : IDesignTimeDbContextFactory<QualityLabDbContext>
{
    public QualityLabDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("QLS_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "QLS_DB_CONNECTION environment variable is not set. "
                + "Set it before running EF migrations: "
                + "export QLS_DB_CONNECTION='Server=...;Database=quality_lab;User=qls_app;Password=...;SslMode=Required;'");

        var serverVersion = new MySqlServerVersion(new Version(8, 0, 21));
        var optionsBuilder = new DbContextOptionsBuilder<QualityLabDbContext>();
        optionsBuilder.UseMySql(connectionString, serverVersion);

        return new QualityLabDbContext(optionsBuilder.Options);
    }
}
