using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QualityLab.Api.Domain.Entities;
using QualityLab.Api.Infrastructure.Persistence;

namespace QualityLab.Api.Application.LabResults;

public interface ILabResultsService
{
    /// <summary>One page of matching rows, newest first.</summary>
    Task<LabResultsPage> SearchAsync(LabResultsFilter filter, int page, CancellationToken ct);

    /// <summary>Pass / fail / pending totals and per-reason counts for the filtered set.</summary>
    Task<LabResultsSummary> SummarizeAsync(LabResultsFilter filter, CancellationToken ct);

    /// <summary>Every matching row (not only one page), newest first, for CSV export.</summary>
    Task<IReadOnlyList<LabResultRow>> ExportAsync(LabResultsFilter filter, CancellationToken ct);
}

/// <summary>
/// Read side of the Quality Lab (SCRUM-25). Reads only this service's own database and exposes
/// no way to change anything.
///
/// A row is one batch: its latest chemical panel version, its sensory evaluation and its active
/// (non-superseded) determination. Filtering on a reason code uses an EXISTS over
/// <c>determination_reason_codes</c>, so a batch carrying several reasons is returned once.
/// </summary>
public sealed class LabResultsService : ILabResultsService
{
    public const int PageSize = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly QualityLabDbContext _db;

    public LabResultsService(QualityLabDbContext db)
    {
        _db = db;
    }

    public async Task<LabResultsPage> SearchAsync(LabResultsFilter filter, int page, CancellationToken ct)
    {
        if (page < 1) page = 1;

        var query = ApplyFilter(filter);
        var total = await query.CountAsync(ct);
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)PageSize);

        var rows = await LoadRowsAsync(Order(query).Skip((page - 1) * PageSize).Take(PageSize), ct);

        return new LabResultsPage
        {
            Items = rows.Select(ToRow).ToList(),
            Total = total,
            Page = page,
            PageSize = PageSize,
            TotalPages = totalPages
        };
    }

    public async Task<IReadOnlyList<LabResultRow>> ExportAsync(LabResultsFilter filter, CancellationToken ct)
    {
        var rows = await LoadRowsAsync(Order(ApplyFilter(filter)), ct);
        return rows.Select(ToRow).ToList();
    }

    public async Task<LabResultsSummary> SummarizeAsync(LabResultsFilter filter, CancellationToken ct)
    {
        var query = ApplyFilter(filter);

        var total = await query.CountAsync(ct);

        var resultCounts = await query
            .Join(ActiveDeterminations(), p => p.BatchWorkItemId, d => d.BatchWorkItemId, (p, d) => d.Result)
            .GroupBy(r => r)
            .Select(g => new { Result = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var passed = resultCounts.Where(r => r.Result == DeterminationResult.Pass).Sum(r => r.Count);
        var failed = resultCounts.Where(r => r.Result == DeterminationResult.Fail).Sum(r => r.Count);

        var reasonCounts = await query
            .Join(ActiveFailedReasons(), p => p.BatchWorkItemId, r => r.BatchWorkItemId, (p, r) => r.Code)
            .GroupBy(c => c)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new LabResultsSummary
        {
            Total = total,
            Passed = passed,
            Failed = failed,
            Pending = Math.Max(0, total - passed - failed),
            ReasonCounts = reasonCounts
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Code, StringComparer.Ordinal)
                .Select(r => new ReasonCodeCount(r.Code, r.Count))
                .ToList()
        };
    }

    // ── Query building ──────────────────────────────────────────────────────

    private IQueryable<Determination> ActiveDeterminations() =>
        _db.Determinations.AsNoTracking().Where(d => d.SupersededById == null);

    private IQueryable<ReasonRef> ActiveFailedReasons() =>
        _db.DeterminationReasonCodes.AsNoTracking()
            .Where(r => r.Determination.SupersededById == null
                        && r.Determination.Result == DeterminationResult.Fail)
            .Select(r => new ReasonRef { BatchWorkItemId = r.Determination.BatchWorkItemId, Code = r.Code });

    private sealed class ReasonRef
    {
        public Guid BatchWorkItemId { get; init; }
        public string Code { get; init; } = string.Empty;
    }

    /// <summary>Latest panel version per batch, narrowed by every supplied filter.</summary>
    private IQueryable<ChemicalPanel> ApplyFilter(LabResultsFilter filter)
    {
        var panels = _db.ChemicalPanels.AsNoTracking();

        var latest = panels
            .GroupBy(x => x.BatchWorkItemId)
            .Select(g => new { BatchWorkItemId = g.Key, Version = g.Max(x => x.Version) });

        IQueryable<ChemicalPanel> query = panels.Join(
            latest,
            p => new { p.BatchWorkItemId, p.Version },
            l => new { l.BatchWorkItemId, l.Version },
            (p, l) => p);

        if (filter.ProductLine is { } line)
            query = query.Where(p => p.ProductLine == line);

        if (filter.From is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(p => p.TestedAtUtc >= fromUtc);
        }

        if (filter.To is { } to)
        {
            // Inclusive end date: everything before the start of the following day.
            var toExclusiveUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(p => p.TestedAtUtc < toExclusiveUtc);
        }

        var active = ActiveDeterminations();

        switch (filter.Result)
        {
            case LabResultFilter.Pass:
                query = query.Join(
                    active.Where(d => d.Result == DeterminationResult.Pass).Select(d => d.BatchWorkItemId).Distinct(),
                    p => p.BatchWorkItemId, id => id, (p, id) => p);
                break;
            case LabResultFilter.Fail:
                query = query.Join(
                    active.Where(d => d.Result == DeterminationResult.Fail).Select(d => d.BatchWorkItemId).Distinct(),
                    p => p.BatchWorkItemId, id => id, (p, id) => p);
                break;
            case LabResultFilter.Pending:
                query = query
                    .GroupJoin(active.Select(d => d.BatchWorkItemId).Distinct(),
                        p => p.BatchWorkItemId, id => id, (p, ids) => new { p, ids })
                    .Where(x => !x.ids.Any())
                    .Select(x => x.p);
                break;
        }

        if (filter.ReasonCode is { } code)
        {
            query = query.Join(
                ActiveFailedReasons().Where(r => r.Code == code).Select(r => r.BatchWorkItemId).Distinct(),
                p => p.BatchWorkItemId, id => id, (p, id) => p);
        }

        return query;
    }

    private static IQueryable<ChemicalPanel> Order(IQueryable<ChemicalPanel> query) =>
        query.OrderByDescending(p => p.TestedAtUtc).ThenBy(p => p.BatchCode);

    /// <summary>Loads the page's panels with their sensory evaluation, then the active determinations in one batched lookup.</summary>
    private async Task<List<RowSource>> LoadRowsAsync(IQueryable<ChemicalPanel> query, CancellationToken ct)
    {
        var panels = await query
            .Select(p => new { Panel = p, Sensory = p.BatchWorkItem.SensoryEvaluation })
            .ToListAsync(ct);

        var ids = panels.Select(x => x.Panel.BatchWorkItemId).Distinct().ToList();
        var determinations = ids.Count == 0
            ? new List<Determination>()
            : await ActiveDeterminations().Where(d => ids.Contains(d.BatchWorkItemId)).ToListAsync(ct);

        var byBatch = determinations
            .GroupBy(d => d.BatchWorkItemId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DeterminedAtUtc).First());

        return panels.Select(x => new RowSource
        {
            Panel = x.Panel,
            Sensory = x.Sensory,
            Determination = byBatch.GetValueOrDefault(x.Panel.BatchWorkItemId)
        }).ToList();
    }

    private sealed class RowSource
    {
        public ChemicalPanel Panel { get; init; } = null!;
        public SensoryEvaluation? Sensory { get; init; }
        public Determination? Determination { get; init; }
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    private static LabResultRow ToRow(RowSource src)
    {
        var p = src.Panel;
        var s = src.Sensory;
        var d = src.Determination;
        var liquid = p.ProductLine.IsLiquid();

        var notApplicable = liquid
            ? new[] { "texture" }
            : new[] { "snf", "ts" };

        return new LabResultRow
        {
            BatchCode = p.BatchCode,
            DispatchNumber = p.DispatchNumber,
            ProductLine = p.ProductLine.ToString(),
            PanelVersion = p.Version,
            TestedAtUtc = DateTime.SpecifyKind(p.TestedAtUtc, DateTimeKind.Utc),
            TestedBy = p.TestedBy,
            FatPercent = p.FatPercent,
            Ph = p.Ph,
            Snf = liquid ? p.Snf : null,
            Ts = liquid ? p.Ts : null,
            SensoryRecorded = s != null,
            Taste = s?.Taste.ToString(),
            Smell = s?.Smell.ToString(),
            Colour = s?.Colour.ToString(),
            Appearance = s?.Appearance.ToString(),
            Texture = liquid ? null : s?.Texture?.ToString(),
            Determination = d?.Result.ToString() ?? "Pending",
            DeterminationId = d?.Id,
            DeterminedAtUtc = d is null ? null : DateTime.SpecifyKind(d.DeterminedAtUtc, DateTimeKind.Utc),
            DeterminedBy = d?.DeterminedBy,
            ReasonCodes = ParseReasonCodes(d?.ReasonCodesJson),
            OutOfSpec = ParseOutOfSpec(p.OutOfSpecFlagsJson),
            NotApplicable = notApplicable
        };
    }

    private static IReadOnlyList<string> ParseReasonCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToUpperInvariant())
                .Distinct()
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<LabResultOutOfSpec> ParseOutOfSpec(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<LabResultOutOfSpec>();
        try
        {
            return (JsonSerializer.Deserialize<List<StoredFlag>>(json, JsonOptions) ?? new List<StoredFlag>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Parameter))
                .Select(f => new LabResultOutOfSpec(f.Parameter!, f.ActualValue, f.Limit ?? string.Empty, f.LimitValue))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<LabResultOutOfSpec>();
        }
    }

    /// <summary>Shape written by ChemicalPanelService: {Parameter, ActualValue, Limit, LimitValue}.</summary>
    private sealed class StoredFlag
    {
        public string? Parameter { get; set; }
        public decimal ActualValue { get; set; }
        public string? Limit { get; set; }
        public decimal LimitValue { get; set; }
    }
}
