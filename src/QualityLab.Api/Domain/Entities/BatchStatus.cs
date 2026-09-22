namespace QualityLab.Api.Domain.Entities;

/// <summary>
/// Status of a batch in the lab work queue.
/// </summary>
public enum BatchStatus
{
    AwaitingPanel = 0,
    PanelRecorded = 1,
    Cleared = 2,
    Failed = 3
}
