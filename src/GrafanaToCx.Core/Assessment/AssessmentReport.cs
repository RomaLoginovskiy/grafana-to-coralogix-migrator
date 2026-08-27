using System.Text;

namespace GrafanaToCx.Core.Assessment;

/// <summary>
/// Renders assessments as something a person can act on: a verdict per dashboard, then the
/// problems grouped so the common ones are obvious.
/// </summary>
public static class AssessmentReport
{
    public static string Build(
        IReadOnlyList<DashboardAssessment> assessments,
        AssessmentReportFormat format = AssessmentReportFormat.Text)
    {
        var sb = new StringBuilder();

        if (format == AssessmentReportFormat.Markdown)
        {
            AppendMarkdown(sb, assessments);
            return sb.ToString();
        }

        AppendSummary(sb, assessments);
        AppendCommonProblems(sb, assessments);
        AppendPerDashboard(sb, assessments);
        return sb.ToString();
    }

    private static void AppendMarkdown(StringBuilder sb, IReadOnlyList<DashboardAssessment> assessments)
    {
        var byVerdict = assessments.GroupBy(a => a.Verdict).ToDictionary(g => g.Key, g => g.Count());
        int Count(AssessmentVerdict v) => byVerdict.GetValueOrDefault(v);

        sb.AppendLine("# Migration assessment");
        sb.AppendLine();
        sb.AppendLine($"{assessments.Count} dashboard(s) assessed. "
                      + $"{assessments.Sum(a => a.PanelCount)} panels in, "
                      + $"{assessments.Sum(a => a.WidgetCount)} widgets out.");
        sb.AppendLine();
        sb.AppendLine("| Verdict | Count | Meaning |");
        sb.AppendLine("|---|---:|---|");
        sb.AppendLine($"| Clean | {Count(AssessmentVerdict.Clean)} | migrate with nothing lost |");
        sb.AppendLine($"| Degraded | {Count(AssessmentVerdict.Degraded)} | migrate, but something does not survive |");
        sb.AppendLine($"| Rejected | {Count(AssessmentVerdict.Rejected)} | Coralogix would refuse these |");
        sb.AppendLine($"| Failed | {Count(AssessmentVerdict.Failed)} | could not be converted at all |");
        sb.AppendLine();

        if (assessments.Count > 0 && assessments.All(a => !a.ValidationRan))
        {
            sb.AppendLine("> The cx CLI was not available, so nothing was validated against the "
                          + "Coralogix API. Problems the API would reject are not included.");
            sb.AppendLine();
        }

        var findings = assessments
            .SelectMany(a => a.Findings)
            .GroupBy(f => f.Detail)
            .Select(g => (Detail: g.Key, Total: g.Sum(f => f.Count), Dashboards: g.Count()))
            .OrderByDescending(x => x.Total)
            .ToList();

        if (findings.Count > 0)
        {
            sb.AppendLine("## What gets lost");
            sb.AppendLine();
            sb.AppendLine("| Count | Dashboards | Problem |");
            sb.AppendLine("|---:|---:|---|");
            foreach (var (detail, total, dashboards) in findings)
                sb.AppendLine($"| {total} | {dashboards} | {detail} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Per dashboard");
        sb.AppendLine();
        sb.AppendLine("| Verdict | Dashboard | Panels | Widgets | Problems |");
        sb.AppendLine("|---|---|---:|---:|---|");

        foreach (var a in assessments.OrderByDescending(x => x.Weight).ThenBy(x => x.Title))
        {
            var problems = a.ConversionError is not null
                ? Escape(a.ConversionError)
                : string.Join("; ", a.ValidationErrors.Select(e => $"REJECTED {Escape(e.Message)}")
                    .Concat(a.Findings.OrderByDescending(f => f.Count).Select(f => Escape(f.ToString()))));

            sb.AppendLine($"| {a.Verdict} | {Escape(a.Title)} | {a.PanelCount} | {a.WidgetCount} | "
                          + $"{(problems.Length == 0 ? "—" : problems)} |");
        }

        sb.AppendLine();
    }

    /// <summary>A pipe in a title would split the markdown table cell.</summary>
    private static string Escape(string value) => value.Replace("|", "\\|");

    private static void AppendSummary(StringBuilder sb, IReadOnlyList<DashboardAssessment> assessments)
    {
        var byVerdict = assessments.GroupBy(a => a.Verdict).ToDictionary(g => g.Key, g => g.Count());
        int Count(AssessmentVerdict v) => byVerdict.GetValueOrDefault(v);

        sb.AppendLine("Migration assessment");
        sb.AppendLine("====================");
        sb.AppendLine($"Dashboards assessed : {assessments.Count}");
        sb.AppendLine();
        sb.AppendLine($"  Clean             : {Count(AssessmentVerdict.Clean)}   migrate with nothing lost");
        sb.AppendLine($"  Degraded          : {Count(AssessmentVerdict.Degraded)}   migrate, but something does not survive");
        sb.AppendLine($"  Rejected          : {Count(AssessmentVerdict.Rejected)}   Coralogix would refuse these");
        sb.AppendLine($"  Failed            : {Count(AssessmentVerdict.Failed)}   could not be converted at all");
        sb.AppendLine();

        var panels = assessments.Sum(a => a.PanelCount);
        var widgets = assessments.Sum(a => a.WidgetCount);
        sb.AppendLine($"Panels in           : {panels}");
        sb.AppendLine($"Widgets out         : {widgets}");

        if (assessments.Count > 0 && assessments.All(a => !a.ValidationRan))
        {
            sb.AppendLine();
            sb.AppendLine("Note: the cx CLI was not available, so no dashboard was validated against the");
            sb.AppendLine("      Coralogix API. Problems the API would reject are not included below.");
        }

        sb.AppendLine();
    }

    private static void AppendCommonProblems(StringBuilder sb, IReadOnlyList<DashboardAssessment> assessments)
    {
        var findings = assessments
            .SelectMany(a => a.Findings)
            .GroupBy(f => f.Detail)
            .Select(g => (Detail: g.Key, Total: g.Sum(f => f.Count), Dashboards: g.Count()))
            .OrderByDescending(x => x.Total)
            .ToList();

        if (findings.Count == 0)
            return;

        sb.AppendLine("What gets lost");
        sb.AppendLine("--------------");
        foreach (var (detail, total, dashboards) in findings)
            sb.AppendLine($"  {total,5}  across {dashboards,3} dashboard(s)  {detail}");

        sb.AppendLine();
    }

    private static void AppendPerDashboard(StringBuilder sb, IReadOnlyList<DashboardAssessment> assessments)
    {
        sb.AppendLine("Per dashboard");
        sb.AppendLine("-------------");

        // Worst first: a rejection matters more than any number of degradations.
        foreach (var assessment in assessments.OrderByDescending(a => a.Weight).ThenBy(a => a.Title))
        {
            sb.AppendLine($"[{Label(assessment.Verdict)}] {assessment.Title}");

            if (assessment.ConversionError is not null)
            {
                sb.AppendLine($"    conversion failed: {assessment.ConversionError}");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"    {assessment.PanelCount} panel(s) -> {assessment.WidgetCount} widget(s)");

            foreach (var error in assessment.ValidationErrors.Take(5))
                sb.AppendLine($"    REJECTED {error.Location}: {error.Message}");
            if (assessment.ValidationErrors.Count > 5)
                sb.AppendLine($"    REJECTED ...and {assessment.ValidationErrors.Count - 5} more");

            foreach (var finding in assessment.Findings.OrderByDescending(f => f.Count))
                sb.AppendLine($"    - {finding}");

            sb.AppendLine();
        }
    }

    private static string Label(AssessmentVerdict verdict) => verdict switch
    {
        AssessmentVerdict.Clean => "OK      ",
        AssessmentVerdict.Degraded => "DEGRADED",
        AssessmentVerdict.Rejected => "REJECTED",
        _ => "FAILED  "
    };
}
