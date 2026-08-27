using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// No Grafana transformation is applied — Coralogix has no post-query stage. But reporting
/// every one as a loss overstates the damage: a frame-joining transformation on a panel with
/// one Elasticsearch query has nothing to join, so nothing is lost.
/// </summary>
public class TransformationLossReportingTests
{
    private static JObject EsTarget(string refId = "A") => new()
    {
        ["refId"] = refId,
        ["query"] = "message: RequestReceived",
        ["timeField"] = "@timestamp",
        ["metrics"] = new JArray(new JObject { ["id"] = "1", ["type"] = "count" }),
        ["bucketAggs"] = new JArray(new JObject
        {
            ["id"] = "2",
            ["type"] = "terms",
            ["field"] = "host.keyword"
        }),
        ["datasource"] = new JObject { ["type"] = "elasticsearch" }
    };

    private static JObject PromTarget(string refId = "A") => new()
    {
        ["refId"] = refId,
        ["expr"] = "sum(rate(http_requests_total[5m])) by (instance)",
        ["datasource"] = new JObject { ["type"] = "prometheus" }
    };

    private static IReadOnlyList<DashboardConversionDiagnostic> Run(
        string panelType, JArray targets, params string[] transformationIds)
    {
        var panel = new JObject
        {
            ["id"] = 1,
            ["type"] = panelType,
            ["title"] = "Requests table",
            ["targets"] = targets,
            ["transformations"] = new JArray(
                transformationIds.Select(id => (object)new JObject { ["id"] = id }).ToArray())
        };

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        converter.ConvertToJObject(
            new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) }.ToString());

        return converter.DashboardDiagnostics;
    }

    private static IEnumerable<DashboardConversionDiagnostic> Transformations(
        IReadOnlyList<DashboardConversionDiagnostic> diagnostics) =>
        diagnostics.Where(d => d.ElementKind == "transformation");

    [Theory]
    [InlineData("merge")]
    [InlineData("joinByField")]
    public void FrameJoin_OnSingleElasticsearchQuery_IsNotReported(string id)
    {
        // One Elasticsearch query returns one frame — there is nothing to join.
        var diagnostics = Run("table", new JArray(EsTarget()), id);

        Assert.Empty(Transformations(diagnostics));
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("joinByField")]
    public void FrameJoin_OnMultipleQueries_IsReportedAsUnreproducible(string id)
    {
        var diagnostics = Run("table", new JArray(EsTarget("A"), EsTarget("B")), id);

        var reported = Assert.Single(Transformations(diagnostics));
        Assert.Equal(id, reported.ElementName);
        Assert.Contains("single query", reported.Reason);
    }

    [Fact]
    public void FrameJoin_OnSinglePrometheusQuery_IsStillReported()
    {
        // A Prometheus query returns one frame per series, which merge would have combined.
        var diagnostics = Run("table", new JArray(PromTarget()), "merge");

        Assert.Single(Transformations(diagnostics));
    }

    [Theory]
    [InlineData("organize")]
    [InlineData("calculateField")]
    [InlineData("sortBy")]
    [InlineData("groupBy")]
    [InlineData("filterFieldsByName")]
    public void OtherTransformations_AreAlwaysReported(string id)
    {
        var diagnostics = Run("table", new JArray(EsTarget()), id);

        var reported = Assert.Single(Transformations(diagnostics));
        Assert.Equal(id, reported.ElementName);
        Assert.Contains("not applied", reported.Reason);
    }

    [Fact]
    public void MixedTransformations_ReportOnlyTheOnesThatCost()
    {
        var diagnostics = Run("table", new JArray(EsTarget()), "merge", "organize", "sortBy");

        var names = Transformations(diagnostics).Select(d => d.ElementName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("organize", names);
        Assert.Contains("sortBy", names);
        Assert.DoesNotContain("merge", names);
    }

    [Fact]
    public void ReportedTransformations_NameTheOwningPanel()
    {
        var diagnostics = Run("table", new JArray(EsTarget()), "organize");

        Assert.Equal("Requests table", Assert.Single(Transformations(diagnostics)).PanelTitle);
    }

    [Fact]
    public void PanelWithNoTransformations_ReportsNothing()
    {
        Assert.Empty(Transformations(Run("table", new JArray(EsTarget()))));
    }
}
