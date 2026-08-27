using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;

namespace GrafanaToCx.Core.Assessment;

/// <summary>
/// How a dashboard is expected to fare, worst first.
/// </summary>
public enum AssessmentVerdict
{
    /// <summary>Converts with nothing lost.</summary>
    Clean,

    /// <summary>Converts and would upload, but something does not survive the trip.</summary>
    Degraded,

    /// <summary>Converts, but Coralogix would refuse it — nothing would land.</summary>
    Rejected,

    /// <summary>Could not be converted at all.</summary>
    Failed
}

/// <summary>
/// One problem worth telling the user about, in their terms rather than the converter's.
/// </summary>
public sealed record AssessmentFinding(string Category, string Detail, int Count)
{
    public override string ToString() => Count > 1 ? $"{Detail} (x{Count})" : Detail;
}

public sealed record DashboardAssessment
{
    public required string Source { get; init; }
    public required string Title { get; init; }
    public int PanelCount { get; init; }
    public int WidgetCount { get; init; }

    /// <summary>Set when conversion threw; the dashboard cannot be migrated at all.</summary>
    public string? ConversionError { get; init; }

    /// <summary>Errors the Coralogix API would return. Empty when the check did not run.</summary>
    public IReadOnlyList<CxCheckIssue> ValidationErrors { get; init; } = [];

    /// <summary>False when the cx CLI was unavailable, so "would upload" is unverified.</summary>
    public bool ValidationRan { get; init; }

    public IReadOnlyList<AssessmentFinding> Findings { get; init; } = [];

    public AssessmentVerdict Verdict =>
        ConversionError is not null ? AssessmentVerdict.Failed
        : ValidationErrors.Count > 0 ? AssessmentVerdict.Rejected
        : Findings.Count > 0 ? AssessmentVerdict.Degraded
        : AssessmentVerdict.Clean;

    /// <summary>Total individual problems, for ranking the worst dashboards first.</summary>
    public int Weight => Findings.Sum(f => f.Count) + ValidationErrors.Count * 100;
}
