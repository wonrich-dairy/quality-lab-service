using QualityLab.Api.Domain.Entities;

namespace QualityLab.Api.Application.LabResults;

/// <summary>Result filter values for the historical view. "Pending" means no active determination.</summary>
public enum LabResultFilter
{
    Pass,
    Fail,
    Pending
}

/// <summary>
/// Validated filter set for the historical lab results view (SCRUM-25).
/// Every filter is optional; supplied filters are combined with AND.
/// Dates are whole UTC days and both ends are inclusive.
/// </summary>
public sealed record LabResultsFilter
{
    public ProductLine? ProductLine { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public LabResultFilter? Result { get; init; }
    public string? ReasonCode { get; init; }

    /// <summary>
    /// Parses raw query-string values. Throws <see cref="ArgumentException"/> with a message
    /// that can be shown to the user when a value is not recognised.
    /// </summary>
    public static LabResultsFilter Parse(string? productLine, DateOnly? from, DateOnly? to, string? result, string? reasonCode)
    {
        ProductLine? line = null;
        if (!string.IsNullOrWhiteSpace(productLine))
        {
            if (!Enum.TryParse<ProductLine>(productLine.Trim(), ignoreCase: true, out var parsedLine)
                || !Enum.IsDefined(parsedLine))
            {
                throw new ArgumentException($"Unknown product line '{productLine}'. Use one of: FM, FLM, SY, SK, DY, CD.");
            }
            line = parsedLine;
        }

        LabResultFilter? parsedResult = null;
        if (!string.IsNullOrWhiteSpace(result))
        {
            if (!Enum.TryParse<LabResultFilter>(result.Trim(), ignoreCase: true, out var r) || !Enum.IsDefined(r))
                throw new ArgumentException($"Unknown result '{result}'. Use one of: Pass, Fail, Pending.");
            parsedResult = r;
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
            throw new ArgumentException("The 'from' date must not be after the 'to' date.");

        string? code = null;
        if (!string.IsNullOrWhiteSpace(reasonCode))
        {
            code = reasonCode.Trim().ToUpperInvariant();
            if (code.Length > 50)
                throw new ArgumentException("Reason code is too long.");
            // A reason code only exists on a failed determination.
            if (parsedResult is LabResultFilter.Pass or LabResultFilter.Pending)
                throw new ArgumentException("A reason code filter can only be combined with result 'Fail' (or no result filter).");
        }

        return new LabResultsFilter
        {
            ProductLine = line,
            From = from,
            To = to,
            Result = parsedResult,
            ReasonCode = code
        };
    }

    public bool IsEmpty =>
        ProductLine is null && From is null && To is null && Result is null && ReasonCode is null;
}
