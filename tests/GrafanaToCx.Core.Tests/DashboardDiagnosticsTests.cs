using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Losses that are not panels used to leave no trace at all: a dashboard converted
/// cleanly, reported nothing, and quietly differed from the original.
/// </summary>
public class DashboardDiagnosticsTests
{
    private static (JObject converted, IReadOnlyList<DashboardConversionDiagnostic> diagnostics) Run(JObject dashboard)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var result = converter.ConvertToJObject(dashboard.ToString());
        return (result, converter.DashboardDiagnostics);
    }

    private static JObject TimeseriesPanel(string title) => new()
    {
        ["id"] = 1,
        ["type"] = "timeseries",
        ["title"] = title,
        ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
    };

    [Fact]
    public void Annotations_AreReported_ExceptGrafanasBuiltIn()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(),
            ["annotations"] = new JObject
            {
                ["list"] = new JArray(
                    new JObject { ["name"] = "Annotations & Alerts", ["builtIn"] = 1 },
                    new JObject { ["name"] = "Pagerduty alerts" })
            }
        };

        var (_, diagnostics) = Run(dashboard);

        var annotation = Assert.Single(diagnostics, d => d.ElementKind == "annotation");
        Assert.Equal("Pagerduty alerts", annotation.ElementName);
        Assert.Equal(DashboardDiagnosticCodes.Annotation, annotation.Code);
    }

    [Fact]
    public void DashboardLinks_AreReported()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(),
            ["links"] = new JArray(
                new JObject { ["title"] = "Query Analytics", ["type"] = "link" },
                new JObject { ["type"] = "dashboards" })
        };

        var (_, diagnostics) = Run(dashboard);

        var links = diagnostics.Where(d => d.ElementKind == "dashboardLink").ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.ElementName == "Query Analytics");
        Assert.Contains(links, l => l.ElementName == "(dashboards)");
    }

    [Fact]
    public void Transformations_AreReported_WithOwningPanel()
    {
        var panel = TimeseriesPanel("Error rate over time");
        panel["transformations"] = new JArray(
            new JObject { ["id"] = "merge" },
            new JObject { ["id"] = "calculateField" });

        var (_, diagnostics) = Run(new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) });

        var transforms = diagnostics.Where(d => d.ElementKind == "transformation").ToList();
        Assert.Equal(2, transforms.Count);
        Assert.All(transforms, t => Assert.Equal("Error rate over time", t.PanelTitle));
        Assert.Contains(transforms, t => t.ElementName == "merge");
        Assert.Contains(transforms, t => t.ElementName == "calculateField");
    }

    [Fact]
    public void PanelRepeatAndPanelLinks_AreReported()
    {
        var panel = TimeseriesPanel("Uptime");
        panel["repeat"] = "host";
        panel["links"] = new JArray(new JObject { ["title"] = "Drill down" });

        var (_, diagnostics) = Run(new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) });

        var repeat = Assert.Single(diagnostics, d => d.ElementKind == "panelRepeat");
        Assert.Equal("$host", repeat.ElementName);
        Assert.Equal("Uptime", repeat.PanelTitle);

        var link = Assert.Single(diagnostics, d => d.ElementKind == "panelLink");
        Assert.Equal("1 link(s)", link.ElementName);
    }

    [Fact]
    public void DroppedVariables_AreReported_WithAReason()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(),
            ["templating"] = new JObject
            {
                ["list"] = new JArray(
                    new JObject { ["name"] = "Filters", ["type"] = "adhoc" },
                    new JObject { ["name"] = "DS", ["type"] = "datasource" })
            }
        };

        var (_, diagnostics) = Run(dashboard);

        var variables = diagnostics.Where(d => d.ElementKind == "variable").ToList();
        Assert.Equal(2, variables.Count);
        Assert.Contains(variables, v => v.ElementName == "Filters" && v.Reason.Contains("Ad-hoc"));
        Assert.Contains(variables, v => v.ElementName == "DS" && v.Reason.Contains("Datasource picker"));
    }

    [Fact]
    public void ConvertedVariables_AreNotReportedAsLost()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(),
            ["templating"] = new JObject
            {
                ["list"] = new JArray(new JObject
                {
                    ["name"] = "instance",
                    ["type"] = "query",
                    ["query"] = "label_values(up, instance)",
                    ["current"] = new JObject { ["value"] = new JArray("a"), ["text"] = new JArray("a") }
                })
            }
        };

        var (_, diagnostics) = Run(dashboard);

        Assert.DoesNotContain(diagnostics, d => d.ElementKind == "variable");
    }

    [Fact]
    public void CleanDashboard_ReportsNothing()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(TimeseriesPanel("CPU"))
        };

        var (_, diagnostics) = Run(dashboard);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DiagnosticsAreClearedBetweenConversions()
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var withLoss = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(),
            ["links"] = new JArray(new JObject { ["title"] = "Somewhere", ["type"] = "link" })
        };
        var clean = new JObject { ["title"] = "Board", ["panels"] = new JArray() };

        converter.ConvertToJObject(withLoss.ToString());
        Assert.NotEmpty(converter.DashboardDiagnostics);

        converter.ConvertToJObject(clean.ToString());
        Assert.Empty(converter.DashboardDiagnostics);
    }
}
