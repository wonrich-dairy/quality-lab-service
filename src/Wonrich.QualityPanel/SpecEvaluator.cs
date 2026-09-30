namespace Wonrich.QualityPanel;

/// <summary>
/// Specification limits for a product type.
/// Pure data — no database dependency.
/// </summary>
public sealed class SpecLimits
{
    public decimal? MinFatPercent { get; init; }
    public decimal? MaxFatPercent { get; init; }
    public decimal? MinPh { get; init; }
    public decimal? MaxPh { get; init; }
    public decimal? MinSnf { get; init; }
    public decimal? MinCorrectedClr { get; init; }
}

/// <summary>
/// Result of evaluating one measured parameter against spec limits.
/// </summary>
public sealed class SpecFlag
{
    public required string Parameter { get; init; }
    public required decimal ActualValue { get; init; }
    /// <summary>"Min" or "Max" — which bound was violated.</summary>
    public required string Limit { get; init; }
    public required decimal LimitValue { get; init; }
}

/// <summary>
/// Evaluates measured panel values against product-specific specification limits.
/// Centralised so every service uses identical comparison logic.
/// </summary>
public static class SpecEvaluator
{
    /// <summary>
    /// Compare measured values against the given limits and return all violations.
    /// Returns an empty list when everything is within spec.
    /// </summary>
    public static IReadOnlyList<SpecFlag> Evaluate(
        decimal fatPercent,
        decimal ph,
        decimal? snf,
        decimal? correctedClr,
        SpecLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var flags = new List<SpecFlag>();

        // Fat
        if (limits.MinFatPercent.HasValue && fatPercent < limits.MinFatPercent.Value)
            flags.Add(new SpecFlag { Parameter = "FatPercent", ActualValue = fatPercent, Limit = "Min", LimitValue = limits.MinFatPercent.Value });
        if (limits.MaxFatPercent.HasValue && fatPercent > limits.MaxFatPercent.Value)
            flags.Add(new SpecFlag { Parameter = "FatPercent", ActualValue = fatPercent, Limit = "Max", LimitValue = limits.MaxFatPercent.Value });

        // pH
        if (limits.MinPh.HasValue && ph < limits.MinPh.Value)
            flags.Add(new SpecFlag { Parameter = "Ph", ActualValue = ph, Limit = "Min", LimitValue = limits.MinPh.Value });
        if (limits.MaxPh.HasValue && ph > limits.MaxPh.Value)
            flags.Add(new SpecFlag { Parameter = "Ph", ActualValue = ph, Limit = "Max", LimitValue = limits.MaxPh.Value });

        // SNF (liquid lines only)
        if (limits.MinSnf.HasValue && snf.HasValue && snf.Value < limits.MinSnf.Value)
            flags.Add(new SpecFlag { Parameter = "SNF", ActualValue = snf.Value, Limit = "Min", LimitValue = limits.MinSnf.Value });

        // Corrected CLR (liquid lines only)
        if (limits.MinCorrectedClr.HasValue && correctedClr.HasValue && correctedClr.Value < limits.MinCorrectedClr.Value)
            flags.Add(new SpecFlag { Parameter = "CorrectedCLR", ActualValue = correctedClr.Value, Limit = "Min", LimitValue = limits.MinCorrectedClr.Value });

        return flags;
    }
}
