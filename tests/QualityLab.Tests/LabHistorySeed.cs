using System.Text.Json;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Tests;

/// <summary>
/// Builds history rows straight into the DbContext for SCRUM-25 tests: a batch with a panel,
/// optionally a sensory evaluation and a determination. Mirrors scripts/seed-lab-history.sql,
/// which builds the same shape in MySQL for the JMeter run.
/// </summary>
internal static class LabHistorySeed
{
    public static readonly ProductLine[] AllLines =
        [ProductLine.FM, ProductLine.FLM, ProductLine.SY, ProductLine.SK, ProductLine.DY, ProductLine.CD];

    public sealed record Flag(string Parameter, decimal ActualValue, string Limit, decimal LimitValue);

    public static BatchWorkItem Add(
        QualityLabDbContext db,
        string batchCode,
        ProductLine line,
        DateTime testedAtUtc,
        DeterminationResult? result = null,
        string[]? reasons = null,
        bool sensory = true,
        SensoryGrade colour = SensoryGrade.Acceptable,
        Flag[]? flags = null,
        decimal fat = 3.80m,
        decimal? ph = null)
    {
        var liquid = line.IsLiquid();
        var batch = new BatchWorkItem
        {
            Id = Guid.NewGuid(),
            BatchCode = batchCode,
            DispatchNumber = $"DSP-{batchCode}",
            ProductLine = line,
            Status = result switch
            {
                DeterminationResult.Pass => BatchStatus.Cleared,
                DeterminationResult.Fail => BatchStatus.Failed,
                _ => BatchStatus.PanelRecorded
            },
            CompletionTimeUtc = testedAtUtc.AddHours(-1),
            CreatedAtUtc = testedAtUtc.AddHours(-1),
            UpdatedAtUtc = testedAtUtc
        };
        db.BatchWorkItems.Add(batch);

        db.ChemicalPanels.Add(new ChemicalPanel
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batchCode,
            DispatchNumber = batch.DispatchNumber,
            ProductLine = line,
            FatPercent = fat,
            Ph = ph ?? (liquid ? 6.70m : 4.40m),
            LactometerReading = liquid ? 29.0m : null,
            TemperatureCelsius = liquid ? 27.0m : null,
            CorrectedClr = liquid ? 29.6m : null,
            Snf = liquid ? 8.55m : null,
            Ts = liquid ? 12.35m : null,
            Version = 1,
            HasOutOfSpecFlags = flags is { Length: > 0 },
            OutOfSpecFlagsJson = flags is { Length: > 0 } ? JsonSerializer.Serialize(flags) : null,
            TestedBy = "tech-1",
            TestedAtUtc = testedAtUtc,
            CreatedAtUtc = testedAtUtc
        });

        if (sensory)
        {
            db.SensoryEvaluations.Add(new SensoryEvaluation
            {
                Id = Guid.NewGuid(),
                BatchWorkItemId = batch.Id,
                Taste = SensoryGrade.Acceptable,
                Smell = SensoryGrade.Acceptable,
                Colour = colour,
                ColourNote = colour == SensoryGrade.Acceptable ? null : "off shade",
                Appearance = SensoryGrade.Acceptable,
                Texture = line.HasTexture() ? SensoryGrade.Acceptable : null,
                EvaluatedBy = "tech-1",
                EvaluatedAtUtc = testedAtUtc,
                CreatedAtUtc = testedAtUtc,
                UpdatedAtUtc = testedAtUtc
            });
        }

        if (result is { } r)
            AddDetermination(db, batch, r, reasons ?? [], testedAtUtc.AddMinutes(30));

        return batch;
    }

    public static Determination AddDetermination(
        QualityLabDbContext db, BatchWorkItem batch, DeterminationResult result, string[] reasons, DateTime at,
        Guid? supersedes = null)
    {
        var det = new Determination
        {
            Id = Guid.NewGuid(),
            BatchWorkItemId = batch.Id,
            BatchCode = batch.BatchCode,
            Result = result,
            ReasonCodesJson = reasons.Length > 0 ? JsonSerializer.Serialize(reasons) : null,
            SupersededDeterminationId = supersedes,
            DeterminedBy = "analyst-1",
            DeterminedAtUtc = at,
            CreatedAtUtc = at
        };
        foreach (var code in reasons.Distinct())
            det.ReasonCodes.Add(new DeterminationReasonCode { Id = Guid.NewGuid(), DeterminationId = det.Id, Code = code });

        db.Determinations.Add(det);
        return det;
    }

    /// <summary>
    /// The SCRUM-25 performance dataset: <paramref name="count"/> panels spread over every product
    /// line and the last 120 days, about 60% passed, 25% failed (with one or two reasons) and 15% pending.
    /// Deterministic, so a failing run can be reproduced.
    /// </summary>
    public static void AddPerformanceDataset(QualityLabDbContext db, int count = 1000, string prefix = "SEED")
    {
        var rng = new Random(25);
        var start = new DateTime(2026, 6, 1, 6, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < count; i++)
        {
            var line = AllLines[i % AllLines.Length];
            var testedAt = start.AddHours(i * 2.9);
            var roll = rng.Next(100);

            DeterminationResult? result = roll < 60 ? DeterminationResult.Pass
                : roll < 85 ? DeterminationResult.Fail
                : null;

            string[]? reasons = null;
            if (result == DeterminationResult.Fail)
            {
                var pool = line.IsLiquid()
                    ? new[] { "LOW_FAT", "LOW_SNF", "HIGH_PH", "SENSORY_TASTE", "SENSORY_COLOUR" }
                    : new[] { "LOW_FAT", "LOW_PH", "SENSORY_SMELL", "SENSORY_COLOUR", "SENSORY_TEXTURE" };
                var first = pool[rng.Next(pool.Length)];
                reasons = rng.Next(3) == 0
                    ? new[] { first, pool.First(c => c != first) }
                    : new[] { first };
            }

            Add(db, $"{prefix}-{line}-{i:D4}", line, testedAt, result, reasons);
        }
    }
}
