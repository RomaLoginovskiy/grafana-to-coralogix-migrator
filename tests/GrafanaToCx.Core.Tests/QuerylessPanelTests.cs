using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// A text panel legitimately carries no targets. It used to fail the planner's
/// zero-target precondition and convert into a visible error widget.
/// </summary>
public class QuerylessPanelTests
{
    private static JObject TextPanel(string content, JArray? targets = null) => new()
    {
        ["id"] = 6,
        ["type"] = "text",
        ["title"] = "Notes",
        ["options"] = new JObject { ["content"] = content },
        ["targets"] = targets ?? new JArray()
    };

    private static JObject Dashboard(params JObject[] panels) => new()
    {
        ["title"] = "Test",
        ["panels"] = new JArray(panels.Cast<object>().ToArray())
    };

    private static (JObject converted, IReadOnlyList<PanelConversionDiagnostic> diagnostics) Run(JObject dashboard)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var result = converter.ConvertToJObject(dashboard.ToString());
        return (result, converter.ConversionDiagnostics);
    }

    private static List<JObject> Widgets(JObject dashboard) =>
        (dashboard["layout"]?["sections"] as JArray ?? [])
        .Children<JObject>()
        .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
        .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
        .ToList();

    [Fact]
    public void TextPanelWithNoTargets_ConvertsToMarkdown_NotAnErrorWidget()
    {
        var (converted, diagnostics) = Run(Dashboard(TextPanel("## Health check")));

        var widget = Assert.Single(Widgets(converted));
        Assert.NotNull(widget["definition"]?["markdown"]);
        Assert.DoesNotContain(diagnostics, d => d.Code == "UNS-TGT-001");
        Assert.DoesNotContain(diagnostics, d => d.Outcome == "error");
    }

    [Fact]
    public void TextPanelWithHtmlContent_KeepsItsContent()
    {
        const string content = "<div style=\"text-align: center\">Production environment</div>";

        var (converted, _) = Run(Dashboard(TextPanel(content)));

        var markdown = Assert.Single(Widgets(converted))["definition"]?["markdown"];
        Assert.Contains("Production environment", markdown?.ToString());
    }

    [Fact]
    public void TextPanelWithAllTargetsHidden_StillConverts()
    {
        var hidden = new JArray(new JObject { ["refId"] = "A", ["hide"] = true });

        var (_, diagnostics) = Run(Dashboard(TextPanel("## Notes", hidden)));

        Assert.DoesNotContain(diagnostics, d => d.Outcome == "error");
    }

    [Fact]
    public void NonTextPanelWithNoTargets_StillFails()
    {
        // The exemption must not weaken the precondition for panels that need a query.
        var panel = new JObject
        {
            ["id"] = 7,
            ["type"] = "timeseries",
            ["title"] = "Empty",
            ["targets"] = new JArray()
        };

        var (_, diagnostics) = Run(Dashboard(panel));

        Assert.Contains(diagnostics, d => d.Code == "UNS-TGT-001");
    }

    [Fact]
    public void PlannerExemptsTextPanels_ButNotOthers()
    {
        var planner = new MultiTargetSemanticsPlanner();

        var textPlan = planner.Plan(new TransformationContext(TextPanel("x"), [], []));
        Assert.IsType<TransformationPlan.Success>(textPlan);

        var statPanel = new JObject { ["type"] = "stat", ["targets"] = new JArray() };
        var statPlan = planner.Plan(new TransformationContext(statPanel, [], []));
        Assert.IsType<TransformationPlan.Failure>(statPlan);
    }
}
