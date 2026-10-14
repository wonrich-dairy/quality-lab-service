using System.Globalization;
using System.Text;

namespace QualityLab.Api.Application.LabResults;

/// <summary>
/// CSV export of the historical view (SCRUM-25). Same columns as the on-screen list.
/// A column the row's product line does not measure carries <see cref="NotApplicable"/>,
/// never an empty cell, because a blank in an audit export is indistinguishable from a test
/// someone forgot to run.
/// </summary>
public static class LabResultsCsv
{
    public const string NotApplicable = "N/A";
    public const string NotRecorded = "Not recorded";

    public static readonly IReadOnlyList<string> Header =
    [
        "Batch code",
        "Dispatch number",
        "Product line",
        "Tested at (UTC)",
        "Panel version",
        "Fat %",
        "pH",
        "SNF",
        "TS",
        "Taste",
        "Smell",
        "Colour",
        "Appearance",
        "Texture",
        "Determination",
        "Reason codes",
        "Out of spec (limit in force)"
    ];

    public static string Write(IEnumerable<LabResultRow> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, Header);

        foreach (var row in rows)
            AppendLine(sb, ToCells(row));

        return sb.ToString();
    }

    public static IReadOnlyList<string> ToCells(LabResultRow row)
    {
        var na = new HashSet<string>(row.NotApplicable, StringComparer.OrdinalIgnoreCase);

        return
        [
            row.BatchCode,
            row.DispatchNumber,
            row.ProductLine,
            row.TestedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            row.PanelVersion.ToString(CultureInfo.InvariantCulture),
            Number(row.FatPercent),
            Number(row.Ph),
            na.Contains("snf") ? NotApplicable : Number(row.Snf),
            na.Contains("ts") ? NotApplicable : Number(row.Ts),
            Grade(row, row.Taste),
            Grade(row, row.Smell),
            Grade(row, row.Colour),
            Grade(row, row.Appearance),
            na.Contains("texture") ? NotApplicable : Grade(row, row.Texture),
            row.Determination,
            row.ReasonCodes.Count == 0 ? (row.Determination == "Fail" ? NotRecorded : NotApplicable) : string.Join("; ", row.ReasonCodes),
            row.OutOfSpec.Count == 0 ? "None" : string.Join("; ", row.OutOfSpec.Select(DescribeFlag))
        ];
    }

    /// <summary>e.g. "Fat % 3.20 below min 3.50".</summary>
    public static string DescribeFlag(LabResultOutOfSpec flag)
    {
        var direction = string.Equals(flag.Limit, "Max", StringComparison.OrdinalIgnoreCase) ? "above max" : "below min";
        return $"{ParameterLabel(flag.Parameter)} {Number(flag.Value)} {direction} {Number(flag.LimitValue)}";
    }

    public static string ParameterLabel(string parameter) => parameter switch
    {
        "FatPercent" => "Fat %",
        "Ph" => "pH",
        "SNF" => "SNF",
        "CorrectedCLR" => "CLR",
        _ => parameter
    };

    /// <summary>
    /// File name carrying the active filters and the export date, e.g.
    /// <c>lab-results_SY_2026-09-01_to_2026-09-30_Fail_2026-10-10.csv</c>.
    /// </summary>
    public static string FileName(LabResultsFilter filter, DateOnly exportDate)
    {
        var parts = new List<string> { "lab-results" };

        if (filter.IsEmpty)
        {
            parts.Add("all");
        }
        else
        {
            if (filter.ProductLine is { } line) parts.Add(line.ToString());
            if (filter.From is { } from && filter.To is { } to)
                parts.Add($"{Date(from)}_to_{Date(to)}");
            else if (filter.From is { } onlyFrom)
                parts.Add($"from_{Date(onlyFrom)}");
            else if (filter.To is { } onlyTo)
                parts.Add($"to_{Date(onlyTo)}");
            if (filter.Result is { } result) parts.Add(result.ToString());
            if (filter.ReasonCode is { } code) parts.Add(Sanitise(code));
        }

        parts.Add($"exported_{Date(exportDate)}");
        return string.Join("_", parts) + ".csv";
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string Grade(LabResultRow row, string? grade) =>
        !row.SensoryRecorded ? NotRecorded : grade ?? NotRecorded;

    private static string Number(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : NotRecorded;

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Sanitise(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());

    private static void AppendLine(StringBuilder sb, IEnumerable<string> cells)
    {
        sb.AppendJoin(',', cells.Select(Escape));
        sb.Append("\r\n");
    }

    /// <summary>RFC 4180 quoting, plus a guard against spreadsheet formula injection.</summary>
    private static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '\t' or '\r'
            || (value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1])))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
