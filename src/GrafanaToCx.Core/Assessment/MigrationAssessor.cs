using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Migration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Assessment;

/// <summary>
/// Converts dashboards without uploading anything, and reports what would and would not survive.
///
/// Answers the question you want answered before committing to a migration: of these boards,
/// which come across cleanly, which lose something, and which would be refused outright.
/// </summary>
public sealed class MigrationAssessor
{
    private readonly IGrafanaToCxConverter _converter;
    private readonly CxCliDashboardChecker? _checker;
    private readonly bool _assumeQueriesTranslated;

    /// <param name="assumeQueriesTranslated">
    /// Judge only dashboard mechanics: queries for datasources the converter does not translate
    /// are swapped for stand-ins first, as if the data had been migrated and named sensibly.
    /// </param>
    public MigrationAssessor(
        IGrafanaToCxConverter converter,
        CxCliDashboardChecker? checker = null,
        bool assumeQueriesTranslated = false)
    {
        _converter = converter;
        _checker = checker;
        _assumeQueriesTranslated = assumeQueriesTranslated;
    }

    public async Task<DashboardAssessment> AssessAsync(
        string source,
        string grafanaJson,
        CancellationToken ct = default)
    {
        JObject sourceDashboard;
        try
        {
            var parsed = JObject.Parse(grafanaJson);
            sourceDashboard = parsed["dashboard"] as JObject ?? parsed;
        }
        catch (Exception ex)
        {
            return new DashboardAssessment
            {
                Source = source,
                Title = Path.GetFileNameWithoutExtension(source),
                ConversionError = $"Not valid JSON: {ex.Message}"
            };
        }

        var title = sourceDashboard.Value<string>("title") is { Length: > 0 } t
            ? t
            : Path.GetFileNameWithoutExtension(source);

        var panelCount = CountPanels(sourceDashboard);

        IReadOnlyList<AssessmentFinding> untranslated = [];
        IReadOnlyList<AssessmentFinding> assumedTranslated = [];
        if (_assumeQueriesTranslated)
        {
            assumedTranslated = [..ForeignQueries.CountPanels(sourceDashboard), ..ForeignQueries.CountVariables(sourceDashboard)];
            grafanaJson = ForeignQueries.AssumeTranslated(sourceDashboard).ToString(Formatting.None);
        }
        else
        {
            // Variables are already reported, generically, by the converter's own diagnostics.
            untranslated = ForeignQueries.CountPanels(sourceDashboard);
        }

        JObject converted;
        try
        {
            converted = _converter.ConvertToJObject(grafanaJson);
        }
        catch (Exception ex)
        {
            return new DashboardAssessment
            {
                Source = source,
                Title = title,
                PanelCount = panelCount,
                ConversionError = $"{ex.GetType().Name}: {ex.Message}"
            };
        }

        var findings = untranslated.Concat(CollectFindings()).ToList();

        var validationErrors = Array.Empty<CxCheckIssue>() as IReadOnlyList<CxCheckIssue>;
        var validationRan = false;
        if (_checker is { IsInstalled: true })
        {
            var result = await _checker.CheckAsync(converted, ct);
            validationRan = result.Ran;
            if (result.Ran)
                validationErrors = result.Issues.Where(i => i.IsError).ToList();
        }

        return new DashboardAssessment
        {
            Source = source,
            Title = title,
            PanelCount = panelCount,
            WidgetCount = CountWidgets(converted),
            Findings = findings,
            AssumedTranslated = assumedTranslated,
            ValidationErrors = validationErrors,
            ValidationRan = validationRan
        };
    }

    /// <summary>
    /// Turns the converter's diagnostics into findings phrased for someone deciding whether to
    /// migrate, rather than someone debugging the converter.
    /// </summary>
    private IReadOnlyList<AssessmentFinding> CollectFindings()
    {
        var findings = new List<AssessmentFinding>();

        var droppedPanels = _converter.ConversionDiagnostics
            .Where(d => d.Code == "UNS-PNL-001")
            .GroupBy(d => d.PanelType);
        foreach (var group in droppedPanels)
        {
            findings.Add(new AssessmentFinding(
                "panel dropped",
                $"'{group.Key}' panels have no Coralogix equivalent",
                group.Count()));
        }

        var reducedToOne = _converter.ConversionDiagnostics
            .Count(d => d.Approximation == "select-one");
        if (reducedToOne > 0)
        {
            findings.Add(new AssessmentFinding(
                "queries dropped",
                "widgets keep only their first query (Coralogix gauge/table/pie/bar hold one)",
                reducedToOne));
        }

        var statusHistory = _converter.ConversionDiagnostics.Count(d => d.Code == "DGR-STH-002");
        if (statusHistory > 0)
        {
            findings.Add(new AssessmentFinding(
                "visual changed",
                "status-history panels become tables",
                statusHistory));
        }

        foreach (var group in _converter.DashboardDiagnostics.GroupBy(d => d.ElementKind))
        {
            findings.Add(new AssessmentFinding(
                group.Key,
                DescribeElementKind(group.Key),
                group.Count()));
        }

        return findings;
    }

    private static string DescribeElementKind(string kind) => kind switch
    {
        "transformation" => "transformations are not applied; numbers may differ",
        "variable" => "variables could not be converted",
        "panelRepeat" => "repeated panels are not expanded",
        "rowRepeat" => "repeated rows are not expanded into repeating sections",
        "annotation" => "annotations are not carried across",
        "dashboardLink" => "dashboard links are not carried across",
        "panelLink" => "panel links are not carried across",
        "queryMatcher" => "a prefix match on a variable became an exact match",
        _ => $"{kind} is not carried across"
    };

    private static int CountPanels(JObject dashboard) =>
        FlattenPanels(dashboard["panels"] as JArray ?? []).Count();

    /// <summary>Mirrors the converter: non-row panels, plus the children of collapsed rows.</summary>
    private static IEnumerable<JObject> FlattenPanels(JArray panels)
    {
        foreach (var panel in panels.Children<JObject>())
        {
            if (string.Equals(panel.Value<string>("type"), "row", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nested in (panel["panels"] as JArray ?? []).Children<JObject>())
                {
                    if (!string.Equals(nested.Value<string>("type"), "row", StringComparison.OrdinalIgnoreCase))
                        yield return nested;
                }

                continue;
            }

            yield return panel;
        }
    }

    private static int CountWidgets(JObject converted) =>
        (converted["layout"]?["sections"] as JArray ?? [])
        .Children<JObject>()
        .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
        .Sum(r => (r["widgets"] as JArray ?? []).Count);
}
