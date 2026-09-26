namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Sensory evaluation grade — fixed scale so results are comparable across technicians.
/// Numeric values are part of the stored contract and must never be renumbered.
/// </summary>
public enum SensoryGrade
{
    Acceptable = 0,
    Borderline = 1,
    Unacceptable = 2
}
