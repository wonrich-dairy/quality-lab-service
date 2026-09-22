using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace QualityLab.Api.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations. Uses the connection string from environment
/// or a local fallback so `dotnet ef migrations add` works without the full host.
/// </summary>
public class QualityLabDbContextFactory : IDesignTimeDbContextFactory<QualityLabDbContext>
{
    public QualityLabDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("QLS_DB_CONNECTION")
            ?? "Server=mcc-db.mysql.database.azure.com;Port=3306;Database=quality_lab;User Id=mccadmin;Password=mccAdmin@123;SslMode=Required;";

        var serverVersion = new MySqlServerVersion(new Version(8, 4, 0));
        var optionsBuilder = new DbContextOptionsBuilder<QualityLabDbContext>();
        optionsBuilder.UseMySql(connectionString, serverVersion);

        return new QualityLabDbContext(optionsBuilder.Options);
    }
}
