using Microsoft.EntityFrameworkCore;

namespace QualityLab.Api.Data;

public class QualityLabDbContext(DbContextOptions<QualityLabDbContext> options) : DbContext(options)
{
    // Entities are added here by the developer, e.g.:
    // public DbSet<LabTest> LabTests => Set<LabTest>();
}
