using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Infrastructure.Persistence;

/// <summary>
/// EF Core context for Quality Lab Service.
/// Precision 2 decimal places, UTC datetime(6), varchar enums, per processing-service conventions.
/// </summary>
public class QualityLabDbContext : DbContext
{
    public QualityLabDbContext(DbContextOptions<QualityLabDbContext> options) : base(options)
    {
    }

    public DbSet<BatchWorkItem> BatchWorkItems => Set<BatchWorkItem>();
    public DbSet<ChemicalPanel> ChemicalPanels => Set<ChemicalPanel>();
    public DbSet<SensoryEvaluation> SensoryEvaluations => Set<SensoryEvaluation>();
    public DbSet<SpecThreshold> SpecThresholds => Set<SpecThreshold>();
    public DbSet<Determination> Determinations => Set<Determination>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // BatchWorkItem
        modelBuilder.Entity<BatchWorkItem>(batch =>
        {
            batch.ToTable("batch_work_items");
            batch.HasKey(b => b.Id);
            batch.Property(b => b.BatchCode).HasMaxLength(30).IsRequired();
            batch.HasIndex(b => b.BatchCode).IsUnique().HasDatabaseName("ux_batch_work_items_code");
            batch.Property(b => b.DispatchNumber).HasMaxLength(50).IsRequired();
            batch.HasIndex(b => b.DispatchNumber).HasDatabaseName("ix_batch_work_items_dispatch");
            batch.Property(b => b.ProductLine).HasConversion<string>().HasMaxLength(10).IsRequired();
            batch.HasIndex(b => b.ProductLine).HasDatabaseName("ix_batch_work_items_product_line");
            batch.Property(b => b.StoringTankCode).HasMaxLength(20);
            batch.Property(b => b.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            batch.HasIndex(b => b.Status).HasDatabaseName("ix_batch_work_items_status");
            batch.Property(b => b.CompletionTimeUtc).HasColumnType("datetime(6)");
            batch.Property(b => b.CreatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            batch.Property(b => b.UpdatedAtUtc).HasColumnType("datetime(6)").IsRequired();
        });

        // ChemicalPanel
        modelBuilder.Entity<ChemicalPanel>(panel =>
        {
            panel.ToTable("chemical_panels");
            panel.HasKey(p => p.Id);
            panel.Property(p => p.BatchCode).HasMaxLength(30).IsRequired();
            panel.HasIndex(p => p.BatchCode).HasDatabaseName("ix_chemical_panels_batch");
            panel.Property(p => p.DispatchNumber).HasMaxLength(50).IsRequired();
            panel.Property(p => p.ProductLine).HasConversion<string>().HasMaxLength(10).IsRequired();
            panel.Property(p => p.FatPercent).HasPrecision(10, 2).IsRequired();
            panel.Property(p => p.LactometerReading).HasPrecision(10, 2);
            panel.Property(p => p.TemperatureCelsius).HasPrecision(10, 2);
            panel.Property(p => p.Ph).HasPrecision(10, 2).IsRequired();
            panel.Property(p => p.CorrectedClr).HasPrecision(10, 2);
            panel.Property(p => p.Snf).HasPrecision(10, 2);
            panel.Property(p => p.Ts).HasPrecision(10, 2);
            panel.Property(p => p.OutOfSpecFlagsJson).HasMaxLength(2000);
            panel.Property(p => p.TestedBy).HasMaxLength(100).IsRequired();
            panel.Property(p => p.TestedAtUtc).HasColumnType("datetime(6)").IsRequired();
            panel.Property(p => p.CreatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            panel.HasOne(p => p.BatchWorkItem).WithMany(b => b.Panels).HasForeignKey(p => p.BatchWorkItemId).OnDelete(DeleteBehavior.Cascade);
            panel.HasIndex(p => new { p.BatchWorkItemId, p.Version }).IsUnique().HasDatabaseName("ux_chemical_panels_batch_version");
        });

        // SensoryEvaluation — one per batch
        modelBuilder.Entity<SensoryEvaluation>(sensory =>
        {
            sensory.ToTable("sensory_evaluations");
            sensory.HasKey(s => s.Id);
            sensory.Property(s => s.Taste).HasConversion<string>().HasMaxLength(20).IsRequired();
            sensory.Property(s => s.TasteNote).HasMaxLength(500);
            sensory.Property(s => s.Smell).HasConversion<string>().HasMaxLength(20).IsRequired();
            sensory.Property(s => s.SmellNote).HasMaxLength(500);
            sensory.Property(s => s.Colour).HasConversion<string>().HasMaxLength(20).IsRequired();
            sensory.Property(s => s.ColourNote).HasMaxLength(500);
            sensory.Property(s => s.Appearance).HasConversion<string>().HasMaxLength(20).IsRequired();
            sensory.Property(s => s.AppearanceNote).HasMaxLength(500);
            sensory.Property(s => s.Texture).HasConversion<string?>().HasMaxLength(20);
            sensory.Property(s => s.TextureNote).HasMaxLength(500);
            sensory.Property(s => s.EvaluatedBy).HasMaxLength(100).IsRequired();
            sensory.Property(s => s.EvaluatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            sensory.Property(s => s.CreatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            sensory.Property(s => s.UpdatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            sensory.HasOne(s => s.BatchWorkItem).WithOne(b => b.SensoryEvaluation).HasForeignKey<SensoryEvaluation>(s => s.BatchWorkItemId).OnDelete(DeleteBehavior.Cascade);
            sensory.HasIndex(s => s.BatchWorkItemId).IsUnique().HasDatabaseName("ux_sensory_evaluations_batch");
        });

        // SpecThreshold — one per product line
        modelBuilder.Entity<SpecThreshold>(spec =>
        {
            spec.ToTable("spec_thresholds");
            spec.HasKey(s => s.Id);
            spec.Property(s => s.ProductLine).HasConversion<string>().HasMaxLength(10).IsRequired();
            spec.HasIndex(s => s.ProductLine).IsUnique().HasDatabaseName("ux_spec_thresholds_product_line");
            spec.Property(s => s.MinFatPercent).HasPrecision(10, 2);
            spec.Property(s => s.MaxFatPercent).HasPrecision(10, 2);
            spec.Property(s => s.MinPh).HasPrecision(10, 2);
            spec.Property(s => s.MaxPh).HasPrecision(10, 2);
            spec.Property(s => s.MinSnf).HasPrecision(10, 2);
            spec.Property(s => s.MinCorrectedClr).HasPrecision(10, 2);
            spec.Property(s => s.CreatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            spec.Property(s => s.UpdatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            spec.Property(s => s.UpdatedBy).HasMaxLength(100).IsRequired();

            // Seed defaults for all 6 product lines
            var seedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            spec.HasData(
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000001"), ProductLine = ProductLine.FM,  MinFatPercent = 3.5m, MaxFatPercent = 8.0m, MinPh = 6.5m, MaxPh = 6.8m, MinSnf = 8.5m, MinCorrectedClr = 26.0m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" },
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000002"), ProductLine = ProductLine.FLM, MinFatPercent = 3.0m, MaxFatPercent = 8.0m, MinPh = 6.5m, MaxPh = 6.8m, MinSnf = 8.0m, MinCorrectedClr = 26.0m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" },
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000003"), ProductLine = ProductLine.SY,  MinFatPercent = 3.0m, MaxFatPercent = 8.0m, MinPh = 4.2m, MaxPh = 4.6m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" },
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000004"), ProductLine = ProductLine.SK,  MinFatPercent = 3.0m, MaxFatPercent = 8.0m, MinPh = 4.2m, MaxPh = 4.6m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" },
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000005"), ProductLine = ProductLine.DY,  MinFatPercent = 2.0m, MaxFatPercent = 6.0m, MinPh = 4.0m, MaxPh = 4.5m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" },
                new SpecThreshold { Id = Guid.Parse("a0000001-0000-0000-0000-000000000006"), ProductLine = ProductLine.CD,  MinFatPercent = 3.0m, MaxFatPercent = 8.0m, MinPh = 4.3m, MaxPh = 4.7m, CreatedAtUtc = seedTime, UpdatedAtUtc = seedTime, UpdatedBy = "seed" }
            );
        });

        // Determination
        modelBuilder.Entity<Determination>(det =>
        {
            det.ToTable("determinations");
            det.HasKey(d => d.Id);
            det.Property(d => d.Result).HasMaxLength(10).IsRequired();
            det.Property(d => d.ReasonCodesJson).HasMaxLength(1000);
            det.Property(d => d.OverrideReason).HasMaxLength(1000);
            det.Property(d => d.CorrectionNote).HasMaxLength(1000);
            det.Property(d => d.DeterminedBy).HasMaxLength(100).IsRequired();
            det.Property(d => d.DeterminedAtUtc).HasColumnType("datetime(6)").IsRequired();
            det.Property(d => d.CreatedAtUtc).HasColumnType("datetime(6)").IsRequired();
            det.HasOne(d => d.BatchWorkItem).WithMany(b => b.Determinations).HasForeignKey(d => d.BatchWorkItemId).OnDelete(DeleteBehavior.Cascade);
            det.HasIndex(d => d.BatchWorkItemId).HasDatabaseName("ix_determinations_batch");
        });

        // AuditEntry
        modelBuilder.Entity<AuditEntry>(audit =>
        {
            audit.ToTable("audit_entries");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.EntityType).HasMaxLength(50).IsRequired();
            audit.Property(a => a.Action).HasMaxLength(50).IsRequired();
            audit.Property(a => a.Parameter).HasMaxLength(100);
            audit.Property(a => a.OldValue).HasMaxLength(500);
            audit.Property(a => a.NewValue).HasMaxLength(500);
            audit.Property(a => a.UserId).HasMaxLength(100).IsRequired();
            audit.Property(a => a.TimestampUtc).HasColumnType("datetime(6)").IsRequired();
            audit.HasIndex(a => new { a.EntityType, a.EntityId }).HasDatabaseName("ix_audit_entity");
        });

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(QualityLabDbContext).Assembly);
    }
}
