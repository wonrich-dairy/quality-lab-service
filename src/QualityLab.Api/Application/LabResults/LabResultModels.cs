namespace QualityLab.Api.Application.LabResults;

/// <summary>
/// One row of the historical view: the batch's latest chemical panel, its sensory grades and
/// its active determination (SCRUM-25). Columns the product line does not measure are listed in
/// <see cref="NotApplicable"/> so every consumer (UI, CSV) renders them as "not applicable"
/// rather than as a blank that looks like a forgotten test.
/// </summary>
public sealed record LabResultRow
{
    public string BatchCode { get; init; } = string.Empty;
    public string DispatchNumber { get; init; } = string.Empty;
    public string ProductLine { get; init; } = string.Empty;

    public int PanelVersion { get; init; }
    public DateTime TestedAtUtc { get; init; }
    public string TestedBy { get; init; } = string.Empty;

    public decimal FatPercent { get; init; }
    public decimal Ph { get; init; }
    /// <summary>Liquid lines only. Null and listed in <see cref="NotApplicable"/> on fermented lines.</summary>
    public decimal? Snf { get; init; }
    /// <summary>Liquid lines only. Null and listed in <see cref="NotApplicable"/> on fermented lines.</summary>
    public decimal? Ts { get; init; }

    /// <summary>False when no sensory evaluation has been recorded yet.</summary>
    public bool SensoryRecorded { get; init; }
    public string? Taste { get; init; }
    public string? Smell { get; init; }
    public string? Colour { get; init; }
    public string? Appearance { get; init; }
    /// <summary>Fermented lines only. Listed in <see cref="NotApplicable"/> on liquid lines.</summary>
    public string? Texture { get; init; }

    /// <summary>"Pass", "Fail" or "Pending" (no active determination).</summary>
    public string Determination { get; init; } = "Pending";
    public Guid? DeterminationId { get; init; }
    public DateTime? DeterminedAtUtc { get; init; }
    public string? DeterminedBy { get; init; }
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();

    /// <summary>Out-of-spec flags with the limit that was in force when the panel was saved.</summary>
    public IReadOnlyList<LabResultOutOfSpec> OutOfSpec { get; init; } = Array.Empty<LabResultOutOfSpec>();

    /// <summary>Column keys that do not apply to this row's product line: "snf", "ts", "texture".</summary>
    public IReadOnlyList<string> NotApplicable { get; init; } = Array.Empty<string>();
}

/// <summary>Out-of-spec parameter as snapshotted on the panel (SCRUM-22).</summary>
/// <param name="Parameter">"FatPercent", "Ph", "SNF" or "CorrectedCLR".</param>
/// <param name="Value">The measured value.</param>
/// <param name="Limit">"Min" or "Max".</param>
/// <param name="LimitValue">The limit in force at the time the panel was saved.</param>
public sealed record LabResultOutOfSpec(string Parameter, decimal Value, string Limit, decimal LimitValue);

public sealed record LabResultsPage
{
    public IReadOnlyList<LabResultRow> Items { get; init; } = Array.Empty<LabResultRow>();
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
}

public sealed record LabResultsSummary
{
    /// <summary>Batches matching the filters.</summary>
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Pending { get; init; }

    /// <summary>Failed batches per reason code, highest first. A batch with two reasons counts once under each.</summary>
    public IReadOnlyList<ReasonCodeCount> ReasonCounts { get; init; } = Array.Empty<ReasonCodeCount>();
}

public sealed record ReasonCodeCount(string Code, int Count);
