namespace Wonrich.QualityPanel;

/// <summary>
/// Shared dairy quality panel derivation formulas. Used by MCC intake, Processing,
/// and Quality Lab services to ensure identical calculations from the same readings.
///
/// Formulas are standard dairy industry calculations:
///   Corrected CLR = Raw CLR + 0.2 × (Temperature − 27.5)
///   SNF = (CLR × 0.25) + (Fat × 0.22) + 0.72
///   TS  = SNF + Fat
/// </summary>
public static class QualityPanelCalculator
{
    /// <summary>
    /// Corrected CLR (corrected lactometer reading).
    /// Standard temperature correction: CLR + 0.2 × (T − 27.5).
    /// </summary>
    public static decimal CalculateCorrectedClr(decimal rawClr, decimal temperatureCelsius)
    {
        return rawClr + 0.2m * (temperatureCelsius - 27.5m);
    }

    /// <summary>
    /// SNF (solids-not-fat).
    /// Formula: (CLR × 0.25) + (Fat × 0.22) + 0.72.
    /// </summary>
    public static decimal CalculateSnf(decimal fatPercent, decimal correctedClr)
    {
        return (correctedClr * 0.25m) + (fatPercent * 0.22m) + 0.72m;
    }

    /// <summary>
    /// TS (total solids).
    /// Formula: SNF + Fat.
    /// </summary>
    public static decimal CalculateTs(decimal snf, decimal fatPercent)
    {
        return snf + fatPercent;
    }
}
