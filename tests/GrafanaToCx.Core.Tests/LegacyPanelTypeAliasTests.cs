using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// grafana-piechart-panel is the superseded community pie plugin. Its targets already carry
/// the shape the native piechart converter reads, so it was dropped purely on the type string.
/// </summary>
public class LegacyPanelTypeAliasTests
{
    private static JObject ElasticsearchTarget(string refId) => new()
    {
        ["refId"] = refId,
        ["alias"] = "",
        ["query"] = "message: RequestReceived",
        ["timeField"] = "@timestamp",
        ["metrics"] = new JArray(new JObject { ["id"] = "1", ["type"] = "count" }),
        ["bucketAggs"] = new JArray(new JObject
        {
            ["id"] = "2",
            ["type"] = "terms",
            ["field"] = "host.keyword",
            ["settings"] = new JObject { ["size"] = "10", ["order"] = "desc" }
        })
    };

    private static JObject PiePanel(string type, params string[] refIds) => new()
    {
        ["id"] = 1,
        ["type"] = type,
        ["title"] = "Top 10 hosts",
        ["datasource"] = "Logs",
        ["targets"] = new JArray(refIds.Select(ElasticsearchTarget).Cast<object>().ToArray())
    };

    private static (List<JObject> widgets, GrafanaToCxConverter converter) Convert(JObject panel)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) };
        var result = converter.ConvertToJObject(dashboard.ToString());

        var widgets = (result["layout"]?["sections"] as JArray ?? [])
            .Children<JObject>()
            .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
            .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();

        return (widgets, converter);
    }

    [Fact]
    public void LegacyPiePanel_ConvertsToAPieChartWidget()
    {
        var (widgets, _) = Convert(PiePanel("grafana-piechart-panel", "A"));

        var widget = Assert.Single(widgets);
        Assert.NotNull(widget["definition"]?["pieChart"]);
        Assert.Equal("Top 10 hosts", widget.Value<string>("title"));
    }

    [Fact]
    public void LegacyPiePanel_CarriesItsQuery()
    {
        var (widgets, _) = Convert(PiePanel("grafana-piechart-panel", "A"));

        var query = widgets[0]["definition"]?["pieChart"]?["query"];
        Assert.NotNull(query);
        Assert.Contains("RequestReceived", query!.ToString());
    }

    [Fact]
    public void LegacyPiePanel_IsNoLongerReportedAsUnsupported()
    {
        var (_, converter) = Convert(PiePanel("grafana-piechart-panel", "A"));

        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Code == "UNS-PNL-001");
    }

    [Fact]
    public void Diagnostics_NameTheTypeGrafanaWrote_NotTheCanonicalOne()
    {
        // Multiple targets force a diagnostic; it must not claim the panel was a "piechart".
        var (_, converter) = Convert(PiePanel("grafana-piechart-panel", "A", "B", "C"));

        Assert.NotEmpty(converter.ConversionDiagnostics);
        Assert.All(converter.ConversionDiagnostics,
            d => Assert.Equal("grafana-piechart-panel", d.PanelType));
    }

    [Fact]
    public void NativePieChart_IsUnaffected()
    {
        var (widgets, converter) = Convert(PiePanel("piechart", "A"));

        Assert.NotNull(Assert.Single(widgets)["definition"]?["pieChart"]);
        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Code == "UNS-PNL-001");
    }

    [Theory]
    [InlineData("grafana-piechart-panel", "piechart")]
    [InlineData("GRAFANA-PIECHART-PANEL", "piechart")]
    [InlineData("table-old", "table")]
    [InlineData("piechart", "piechart")]
    [InlineData("table", "table")]
    [InlineData("timeseries", "timeseries")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_MapsOnlyKnownAliases(string? input, string expected)
    {
        Assert.Equal(expected, PanelTypes.Normalize(input));
    }

    [Fact]
    public void IsAlias_IdentifiesLegacyTypes()
    {
        Assert.True(PanelTypes.IsAlias("grafana-piechart-panel"));
        Assert.True(PanelTypes.IsAlias("table-old"));
        Assert.False(PanelTypes.IsAlias("piechart"));
        Assert.False(PanelTypes.IsAlias("table"));
        Assert.False(PanelTypes.IsAlias(null));
    }

    // ── table-old ────────────────────────────────────────────────────────────

    /// <summary>
    /// table-old stores column presentation as columns/styles rather than field config, but the
    /// table converter builds its columns from the query's bucketAggs and metrics, so that
    /// difference never reaches the output.
    /// </summary>
    private static JObject LegacyTablePanel() => new()
    {
        ["id"] = 2,
        ["type"] = "table-old",
        ["title"] = "Requests table",
        ["datasource"] = "Logs",
        ["targets"] = new JArray(ElasticsearchTarget("A")),
        // Presentation the modern table type does not use.
        ["columns"] = new JArray(new JObject { ["text"] = "Count", ["value"] = "count" }),
        ["styles"] = new JArray(new JObject { ["pattern"] = "Count", ["type"] = "number" })
    };

    [Fact]
    public void LegacyTablePanel_ConvertsToADataTableWidget()
    {
        var (widgets, _) = Convert(LegacyTablePanel());

        var widget = Assert.Single(widgets);
        Assert.NotNull(widget["definition"]?["dataTable"]);
        Assert.Equal("Requests table", widget.Value<string>("title"));
    }

    [Fact]
    public void LegacyTablePanel_DerivesColumnsFromTheQuery()
    {
        var (widgets, _) = Convert(LegacyTablePanel());

        var columns = widgets[0]["definition"]?["dataTable"]?["columns"] as JArray;
        Assert.NotNull(columns);
        Assert.NotEmpty(columns!);
    }

    [Fact]
    public void LegacyTablePanel_CarriesItsQuery()
    {
        var (widgets, _) = Convert(LegacyTablePanel());

        var query = widgets[0]["definition"]?["dataTable"]?["query"];
        Assert.NotNull(query);
        Assert.Contains("RequestReceived", query!.ToString());
    }

    [Fact]
    public void LegacyTablePanel_IsNoLongerReportedAsUnsupported()
    {
        var (_, converter) = Convert(LegacyTablePanel());

        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Code == "UNS-PNL-001");
    }
}
