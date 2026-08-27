using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// A Coralogix gauge carries one query, so a multi-query stat panel loses all but the first.
/// Grafana draws one tile per query on such a panel, so emitting one widget per query is what
/// the user was already looking at — at the cost of more widgets, hence the opt-in.
/// </summary>
public class StatFanOutTests
{
    private static JObject Target(string refId, string? alias, string expr = "sum(up) by (job)")
    {
        var target = new JObject { ["refId"] = refId, ["expr"] = expr };
        if (alias is not null) target["alias"] = alias;
        return target;
    }

    private static JObject StatPanel(string type = "stat", params JObject[] targets) => new()
    {
        ["id"] = 1,
        ["type"] = type,
        ["title"] = "Cluster A",
        ["targets"] = new JArray(targets.Cast<object>().ToArray())
    };

    private static List<JObject> Convert(JObject panel, bool fanOut)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) };
        var result = converter.ConvertToJObject(
            dashboard.ToString(),
            new ConversionOptions { FanOutMultiQueryPanels = fanOut });

        return (result["layout"]?["sections"] as JArray ?? [])
            .Children<JObject>()
            .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
            .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();
    }

    private static JObject FiveQueryStat() => StatPanel("stat",
        Target("A", "Total request"),
        Target("B", "Accepted"),
        Target("C", "Rejected by bank"),
        Target("D", "Rejected by 3C"),
        Target("E", "Warning"));

    [Fact]
    public void Disabled_ByDefault_KeepsOneWidget()
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(FiveQueryStat()) };

        // No options at all — the default must not change existing behaviour.
        var result = converter.ConvertToJObject(dashboard.ToString());

        var widgets = (result["layout"]?["sections"] as JArray ?? [])
            .Children<JObject>()
            .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
            .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();

        Assert.Single(widgets);
        Assert.Equal("Cluster A", widgets[0].Value<string>("title"));
    }

    [Fact]
    public void Off_KeepsOneWidget()
    {
        Assert.Single(Convert(FiveQueryStat(), fanOut: false));
    }

    [Fact]
    public void On_EmitsOneWidgetPerQuery()
    {
        Assert.Equal(5, Convert(FiveQueryStat(), fanOut: true).Count);
    }

    [Fact]
    public void On_TitlesEachWidgetFromItsAlias()
    {
        var titles = Convert(FiveQueryStat(), fanOut: true)
            .Select(w => w.Value<string>("title"))
            .ToList();

        Assert.Contains("Cluster A — Total request", titles);
        Assert.Contains("Cluster A — Accepted", titles);
        Assert.Contains("Cluster A — Rejected by bank", titles);
    }

    [Fact]
    public void On_EachWidgetCarriesItsOwnQuery()
    {
        var panel = StatPanel("stat",
            Target("A", "First", "sum(first_metric)"),
            Target("B", "Second", "sum(second_metric)"));

        var queries = Convert(panel, fanOut: true)
            .Select(w => w["definition"]?["gauge"]?["query"]?.ToString(Newtonsoft.Json.Formatting.None))
            .ToList();

        Assert.All(queries, q => Assert.False(string.IsNullOrWhiteSpace(q)));
        Assert.Equal(2, queries.Distinct().Count());
        Assert.Contains(queries, q => q!.Contains("first_metric"));
        Assert.Contains(queries, q => q!.Contains("second_metric"));
    }

    [Fact]
    public void On_FallsBackToRefId_WhenATargetHasNoAlias()
    {
        var panel = StatPanel("stat", Target("A", null), Target("B", null));

        var titles = Convert(panel, fanOut: true).Select(w => w.Value<string>("title")).ToList();

        Assert.Contains("Cluster A — A", titles);
        Assert.Contains("Cluster A — B", titles);
    }

    [Fact]
    public void On_IgnoresHiddenTargets()
    {
        var hidden = Target("C", "Hidden");
        hidden["hide"] = true;
        var panel = StatPanel("stat", Target("A", "Shown"), Target("B", "Also shown"), hidden);

        var widgets = Convert(panel, fanOut: true);

        Assert.Equal(2, widgets.Count);
        Assert.DoesNotContain(widgets, w => w.Value<string>("title")!.Contains("Hidden"));
    }

    [Fact]
    public void On_LeavesSingleQueryStatsAlone()
    {
        var widgets = Convert(StatPanel("stat", Target("A", "Only")), fanOut: true);

        var widget = Assert.Single(widgets);
        Assert.Equal("Cluster A", widget.Value<string>("title"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("piechart")]
    [InlineData("bargauge")]
    [InlineData("timeseries")]
    public void On_DoesNotFanOutOtherPanelTypes(string panelType)
    {
        // These types join, slice or bucket their queries into one view; N widgets would be wrong.
        var panel = StatPanel(panelType, Target("A", "One"), Target("B", "Two"));

        Assert.Single(Convert(panel, fanOut: true));
    }

    [Fact]
    public void On_ReportsNoDegradation_BecauseNothingIsDropped()
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(FiveQueryStat()) };

        converter.ConvertToJObject(
            dashboard.ToString(),
            new ConversionOptions { FanOutMultiQueryPanels = true });

        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Approximation == "select-one");
    }

    [Fact]
    public void On_DropsRepeatFromClones_SoItIsNotReportedPerSlice()
    {
        var panel = FiveQueryStat();
        panel["repeat"] = "cluster";

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) };

        converter.ConvertToJObject(
            dashboard.ToString(),
            new ConversionOptions { FanOutMultiQueryPanels = true });

        // The repeat belongs to the panel, not to each slice — it must be reported once.
        Assert.Single(converter.DashboardDiagnostics, d => d.ElementKind == "panelRepeat");
    }
}
