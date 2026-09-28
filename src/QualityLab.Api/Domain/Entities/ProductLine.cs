namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Product lines produced at the factory. The panel shape depends on the line:
/// Liquid lines (FM, FLM): fat, lactometer reading, temperature, pH → derived CLR, SNF, TS
/// Fermented lines (SY, DY, SK, CD): fat, pH only — no lactometer, no SNF/TS
/// </summary>
public enum ProductLine
{
    FM = 0,   // Fresh Milk
    FLM = 1,  // Flavoured Milk
    SY = 2,   // Set Yogurt
    SK = 3,   // Set Kiri
    DY = 4,   // Drinking Yogurt
    CD = 5    // Curd
}

public static class ProductLineExtensions
{
    private static readonly HashSet<ProductLine> LiquidLines = [ProductLine.FM, ProductLine.FLM];
    private static readonly HashSet<ProductLine> FermentedLines = [ProductLine.SY, ProductLine.SK, ProductLine.DY, ProductLine.CD];

    /// <summary>Liquid lines measure lactometer, temperature, and derive CLR/SNF/TS.</summary>
    public static bool IsLiquid(this ProductLine line) => LiquidLines.Contains(line);

    /// <summary>Fermented lines measure fat and pH only — no lactometer/SNF/TS.</summary>
    public static bool IsFermented(this ProductLine line) => FermentedLines.Contains(line);

    /// <summary>Fermented lines grade texture; liquid lines do not.</summary>
    public static bool HasTexture(this ProductLine line) => line.IsFermented();
}
