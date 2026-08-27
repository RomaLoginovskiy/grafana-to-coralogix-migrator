using GrafanaToCx.Core.Assessment;
using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Assessing a set of dashboards before committing to a migration: which come across cleanly,
/// which lose something, and which would be refused.
/// </summary>
public class MigrationAssessorTests
{
    private static MigrationAssessor Assessor() =>
        new(new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance));

    private static JObject Dashboard(string title, params JObject[] panels) => new()
    {
        ["title"] = title,
        ["panels"] = new JArray(panels.Cast<object>().ToArray())
    };

    private static JObject Timeseries(string title) => new()
    {
        ["id"] = 1,
        ["type"] = "timeseries",
        ["title"] = title,
        ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
    };

    private static Task<DashboardAssessment> Assess(JObject dashboard) =>
        Assessor().AssessAsync("board.json", dashboard.ToString());

    [Fact]
    public async Task CleanDashboard_IsReportedClean()
    {
        var assessment = await Assess(Dashboard("Board", Timeseries("CPU")));

        Assert.Equal(AssessmentVerdict.Clean, assessment.Verdict);
        Assert.Empty(assessment.Findings);
        Assert.Equal("Board", assessment.Title);
    }

    [Fact]
    public async Task PanelAndWidgetCounts_AreReported()
    {
        var assessment = await Assess(Dashboard("Board", Timeseries("CPU"), Timeseries("Memory")));

        Assert.Equal(2, assessment.PanelCount);
        Assert.Equal(2, assessment.WidgetCount);
    }

    [Fact]
    public async Task UnsupportedPanel_IsReportedWithItsType()
    {
        var panel = Timeseries("Map");
        panel["type"] = "grafana-worldmap-panel";

        var assessment = await Assess(Dashboard("Board", panel));

        Assert.Equal(AssessmentVerdict.Degraded, assessment.Verdict);
        Assert.Contains(assessment.Findings, f => f.Detail.Contains("grafana-worldmap-panel"));
    }

    [Fact]
    public async Task LostTransformations_AreReportedInUserTerms()
    {
        var panel = Timeseries("Rate");
        panel["transformations"] = new JArray(new JObject { ["id"] = "calculateField" });

        var assessment = await Assess(Dashboard("Board", panel));

        var finding = Assert.Single(assessment.Findings, f => f.Category == "transformation");
        Assert.Contains("numbers may differ", finding.Detail);
    }

    [Fact]
    public async Task FindingsAreCounted_NotJustListed()
    {
        var panels = Enumerable.Range(1, 3).Select(i =>
        {
            var p = Timeseries($"Panel {i}");
            p["id"] = i;
            p["type"] = "grafana-worldmap-panel";
            return p;
        }).ToArray();

        var assessment = await Assess(Dashboard("Board", panels));

        Assert.Equal(3, Assert.Single(assessment.Findings, f => f.Category == "panel dropped").Count);
    }

    [Fact]
    public async Task UnparseableInput_IsReportedAsFailed_NotThrown()
    {
        var assessment = await Assessor().AssessAsync("broken.json", "{ not json");

        Assert.Equal(AssessmentVerdict.Failed, assessment.Verdict);
        Assert.Contains("Not valid JSON", assessment.ConversionError);
    }

    [Fact]
    public async Task PanelsInsideCollapsedRows_AreCounted()
    {
        var row = new JObject
        {
            ["type"] = "row",
            ["collapsed"] = true,
            ["panels"] = new JArray(Timeseries("Nested"))
        };

        var assessment = await Assess(Dashboard("Board", row));

        Assert.Equal(1, assessment.PanelCount);
    }

    [Fact]
    public async Task WithoutTheCxCli_ValidationIsMarkedAsNotRun()
    {
        // The report must not imply a dashboard would upload when nothing checked it.
        var assessment = await Assess(Dashboard("Board", Timeseries("CPU")));

        Assert.False(assessment.ValidationRan);
        Assert.Empty(assessment.ValidationErrors);
    }

    // ── report rendering ─────────────────────────────────────────────────────

    [Fact]
    public async Task Report_CountsEachVerdict()
    {
        var clean = await Assess(Dashboard("Clean", Timeseries("CPU")));
        var broken = await Assessor().AssessAsync("broken.json", "{ not json");

        var report = AssessmentReport.Build([clean, broken]);

        Assert.Contains("Dashboards assessed : 2", report);
        Assert.Contains("Clean             : 1", report);
        Assert.Contains("Failed            : 1", report);
    }

    [Fact]
    public async Task Report_GroupsCommonProblemsAcrossDashboards()
    {
        var panel = Timeseries("Map");
        panel["type"] = "grafana-worldmap-panel";
        var a = await Assess(Dashboard("A", panel));
        var b = await Assess(Dashboard("B", panel));

        var report = AssessmentReport.Build([a, b]);

        Assert.Contains("What gets lost", report);
        Assert.Contains("across   2 dashboard(s)", report);
    }

    [Fact]
    public async Task Report_WarnsWhenNothingWasValidated()
    {
        var report = AssessmentReport.Build([await Assess(Dashboard("Board", Timeseries("CPU")))]);

        Assert.Contains("cx CLI was not available", report);
    }

    [Fact]
    public async Task MarkdownReport_UsesTables()
    {
        var report = AssessmentReport.Build(
            [await Assess(Dashboard("Board", Timeseries("CPU")))],
            AssessmentReportFormat.Markdown);

        Assert.Contains("# Migration assessment", report);
        Assert.Contains("| Verdict | Count | Meaning |", report);
        Assert.Contains("| Verdict | Dashboard | Panels | Widgets | Problems |", report);
    }

    [Fact]
    public async Task MarkdownReport_EscapesPipesInTitles()
    {
        // An unescaped pipe in a dashboard name would split the table cell.
        var assessment = await Assess(Dashboard("Sales | EMEA", Timeseries("CPU")));

        var report = AssessmentReport.Build([assessment], AssessmentReportFormat.Markdown);

        Assert.Contains(@"Sales \| EMEA", report);
    }

    [Fact]
    public async Task MarkdownReport_MarksACleanDashboardWithNoProblems()
    {
        var report = AssessmentReport.Build(
            [await Assess(Dashboard("Board", Timeseries("CPU")))],
            AssessmentReportFormat.Markdown);

        Assert.Contains("| Clean | Board | 1 | 1 | — |", report);
    }

    [Fact]
    public async Task Report_ListsWorstDashboardsFirst()
    {
        var bad = Timeseries("Map");
        bad["type"] = "grafana-worldmap-panel";

        var clean = await Assess(Dashboard("Clean board", Timeseries("CPU")));
        var degraded = await Assess(Dashboard("Degraded board", bad));

        var report = AssessmentReport.Build([clean, degraded]);

        Assert.True(report.IndexOf("Degraded board", StringComparison.Ordinal)
                    < report.IndexOf("Clean board", StringComparison.Ordinal));
    }
}
