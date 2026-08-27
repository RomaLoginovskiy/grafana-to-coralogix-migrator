using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// When the multi-Lucene merge bails out, a pie chart has no choice but to keep one target —
/// its widget carries a single query. A line chart carries a queryDefinitions array, so it
/// should keep every target instead of inheriting the pie chart's limitation.
/// </summary>
public class LineChartRetainsTargetsTests
{
    private static readonly MultiLuceneMergeOptions Allowlisted =
        new(["timeseries", "piechart", "barchart"]);

    private static JObject EsTarget(string refId, string query) => new()
    {
        ["refId"] = refId,
        ["query"] = query,
        ["timeField"] = "@timestamp",
        ["metrics"] = new JArray(new JObject { ["id"] = "1", ["type"] = "count" }),
        ["bucketAggs"] = new JArray(new JObject
        {
            ["id"] = "2",
            ["type"] = "date_histogram",
            ["field"] = "@timestamp",
            ["settings"] = new JObject { ["interval"] = "auto" }
        }),
        ["datasource"] = new JObject { ["type"] = "elasticsearch" }
    };

    /// <summary>Targets differing by more than one predicate — the merge cannot align them.</summary>
    private static JObject UnmergeablePanel(string panelType) => new()
    {
        ["id"] = 1,
        ["type"] = panelType,
        ["title"] = "Requests",
        ["targets"] = new JArray(
            EsTarget("A", "service: checkout AND region: eu"),
            EsTarget("B", "tier: premium AND status: 500"),
            EsTarget("C", "cache: warm AND method: POST"))
    };

    private static (JObject converted, GrafanaToCxConverter converter) Convert(JObject panel)
    {
        var converter = new GrafanaToCxConverter(
            NullLogger<GrafanaToCxConverter>.Instance, Allowlisted);
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) };
        return (converter.ConvertToJObject(dashboard.ToString()), converter);
    }

    private static JObject FirstWidget(JObject dashboard) =>
        (dashboard["layout"]?["sections"] as JArray ?? [])
        .Children<JObject>()
        .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
        .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
        .First();

    [Fact]
    public void Timeseries_WhenMergeBailsOut_KeepsEveryTarget()
    {
        var (converted, _) = Convert(UnmergeablePanel("timeseries"));

        var queryDefinitions = FirstWidget(converted)["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.NotNull(queryDefinitions);
        Assert.Equal(3, queryDefinitions!.Count);
    }

    [Fact]
    public void Timeseries_WhenMergeBailsOut_EmitsNoSelectOneDegradation()
    {
        var (_, converter) = Convert(UnmergeablePanel("timeseries"));

        // Nothing was lost, so nothing should be reported as dropped.
        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Approximation == "select-one");
        Assert.DoesNotContain(converter.ConversionDiagnostics,
            d => d.Code is not null && d.Code.StartsWith("DGR-LMG"));
    }

    [Fact]
    public void Timeseries_KeepsQueriesDistinct_NotDuplicated()
    {
        var (converted, _) = Convert(UnmergeablePanel("timeseries"));

        var queryDefinitions = (FirstWidget(converted)["definition"]?["lineChart"]?["queryDefinitions"] as JArray)!;
        var rendered = queryDefinitions.Children<JObject>()
            .Select(q => q["query"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "")
            .ToList();

        Assert.All(rendered, r => Assert.NotEqual("", r));
        Assert.Equal(rendered.Count, rendered.Distinct().Count());
    }

    [Fact]
    public void PieChart_WhenMergeBailsOut_StillCollapsesToOneTarget()
    {
        // The pie widget carries a single query object, so this degradation is correct.
        var (converted, converter) = Convert(UnmergeablePanel("piechart"));

        Assert.NotNull(FirstWidget(converted)["definition"]?["pieChart"]?["query"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.Approximation == "select-one");
    }

    [Fact]
    public void Timeseries_WithNonElasticsearchTargets_AlsoKeepsEveryTarget()
    {
        var panel = new JObject
        {
            ["id"] = 1,
            ["type"] = "timeseries",
            ["title"] = "Mixed",
            ["targets"] = new JArray(
                EsTarget("A", "service: checkout"),
                new JObject
                {
                    ["refId"] = "B",
                    ["expr"] = "sum(rate(http_requests_total[5m]))",
                    ["datasource"] = new JObject { ["type"] = "prometheus" }
                })
        };

        var (converted, converter) = Convert(panel);

        var queryDefinitions = FirstWidget(converted)["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.Equal(2, queryDefinitions!.Count);
        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.Approximation == "select-one");
    }

    [Fact]
    public void SingleTargetTimeseries_IsUnaffected()
    {
        var panel = new JObject
        {
            ["id"] = 1,
            ["type"] = "timeseries",
            ["title"] = "One",
            ["targets"] = new JArray(EsTarget("A", "service: checkout"))
        };

        var (converted, _) = Convert(panel);

        var queryDefinitions = FirstWidget(converted)["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.Single(queryDefinitions!);
    }

    [Fact]
    public void MergeableTimeseries_StillMerges()
    {
        // Targets differing by exactly one predicate on the same field remain eligible,
        // so the consolidation path must not be disturbed by the retain-all change.
        var panel = new JObject
        {
            ["id"] = 1,
            ["type"] = "timeseries",
            ["title"] = "Requests",
            ["targets"] = new JArray(
                EsTarget("A", "service: checkout AND region: eu"),
                EsTarget("B", "service: checkout AND region: us"))
        };

        var (_, converter) = Convert(panel);

        Assert.Contains(converter.ConversionDiagnostics, d => d.Code == "DGR-LMG-000");
    }
}
