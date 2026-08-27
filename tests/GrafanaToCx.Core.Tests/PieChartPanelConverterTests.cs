using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.PanelConverters;
using GrafanaToCx.Core.Converter.Transformations;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class PieChartPanelConverterTests
{
    [Fact]
    public void Convert_MetricsPieChart_InfersGroupNames_FromDisplayNameLabelTemplate()
    {
        var panel = new JObject
        {
            ["id"] = 70,
            ["title"] = "Queue - Messages",
            ["type"] = "piechart",
            ["fieldConfig"] = new JObject
            {
                ["defaults"] = new JObject
                {
                    ["displayName"] = "${__field.labels.queue}"
                }
            },
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["expr"] = "rabbitmq_queue_messages{instance=\"$URL\"}",
                    ["datasource"] = new JObject
                    {
                        ["type"] = "prometheus"
                    }
                }
            }
        };

        var converter = new PieChartPanelConverter();
        var widget = converter.Convert(panel, new HashSet<string>());

        Assert.NotNull(widget);
        var groupNames = widget["definition"]?["pieChart"]?["query"]?["metrics"]?["groupNames"] as JArray;
        Assert.NotNull(groupNames);
        Assert.Single(groupNames);
        Assert.Equal("queue", groupNames[0]?.ToString());
    }

    // Each case below mirrors a real Prometheus pie panel found in the local dashboard corpus.
    // The API rejects the widget outright when groupNames is absent or empty, so every one of these
    // must produce a non-empty grouping.

    [Fact]
    public void Convert_MetricsPieChart_DerivesGroupNames_FromPromqlByClause()
    {
        var panel = MetricsPanel("Entity Distribution",
            ("count(calls_total_total{service_name=~\"$service_name\"}) by (service_name)", "{{service_name}}"));

        var groupNames = GroupNamesOf(new PieChartPanelConverter().Convert(panel, new HashSet<string>()));

        Assert.Equal(["service_name"], groupNames);
    }

    [Fact]
    public void Convert_MetricsPieChart_PrefersByClauseOverLegendFormat()
    {
        var panel = MetricsPanel("Error Percentage by Operation",
            ("avg(100 * (sum(calls_total_total{status_code=\"STATUS_CODE_ERROR\"}) by (operation) / sum(calls_total_total) by (operation)))",
             "{{operation}}"));

        Assert.Equal(["operation"], GroupNamesOf(new PieChartPanelConverter().Convert(panel, new HashSet<string>())));
    }

    [Fact]
    public void Convert_MetricsPieChart_DerivesGroupNames_FromLegendFormatWhenNoByClause()
    {
        var panel = MetricsPanel("Pod Used RAM",
            ("k8s_pod_memory_available_By{k8s_pod_name=\"$pod\"}", "Available RAM {{k8s_pod_name}}"),
            ("k8s_pod_memory_usage_By{k8s_pod_name=\"$pod\"}", "Used RAM {{k8s_pod_name}}"));

        Assert.Equal(["k8s_pod_name"], GroupNamesOf(new PieChartPanelConverter().Convert(panel, new HashSet<string>())));
    }

    /// <summary>
    /// The panel that produced the original HTTP 400: five scalar queries whose only differentiator is
    /// plain legend text, so there is no label anywhere to slice by.
    /// </summary>
    [Fact]
    public void Convert_MetricsPieChart_NoGroupableLabel_ConsolidatesQueriesUnderSyntheticLabel()
    {
        var panel = MetricsPanel("Task per status",
            ("sum(kafka_connect_worker_connector_running_task_count{job=\"$job\"})", "running"),
            ("sum(kafka_connect_worker_connector_failed_task_count{job=\"$job\"})", "failed"),
            ("sum(kafka_connect_worker_connector_paused_task_count{job=\"$job\"})", "paused"),
            ("sum(kafka_connect_worker_connector_unassigned_task_count{job=\"$job\"})", "unassigned"),
            ("sum(kafka_connect_worker_connector_destroyed_task_count{job=\"$job\"})", "destroyed"));

        var widget = new PieChartPanelConverter().Convert(panel, new HashSet<string>());

        Assert.Equal(["series"], GroupNamesOf(widget));

        var promql = widget!["definition"]?["pieChart"]?["query"]?["metrics"]?["promqlQuery"]?["value"]?.ToString();
        Assert.NotNull(promql);
        // All five original queries survive as slices rather than four being silently dropped.
        foreach (var legend in new[] { "running", "failed", "paused", "unassigned", "destroyed" })
            Assert.Contains($"\"{legend}\"", promql!, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_MetricsPieChart_NoGroupableLabel_ReportsAnApproximationDiagnostic()
    {
        var panel = MetricsPanel("Memory Total",
            ("system_memory_usage_By{host_name=\"$h\",state=\"free\"}", "Free"),
            ("system_memory_usage_By{host_name=\"$h\",state=\"used\"}", "Used"));

        var diagnostics = new List<PanelConversionDiagnostic>();
        new PieChartPanelConverter(diagnostics.Add).Convert(panel, new HashSet<string>());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("Memory Total", diagnostic.PanelTitle);
        Assert.Equal("approximated", diagnostic.Outcome);
        Assert.Equal("DGR-PIE-010", diagnostic.Code);
    }

    [Fact]
    public void Convert_MetricsPieChart_WithRealGroupLabel_ReportsNoDiagnostic()
    {
        var panel = MetricsPanel("Entity Distribution", ("count(x) by (service_name)", "{{service_name}}"));

        var diagnostics = new List<PanelConversionDiagnostic>();
        new PieChartPanelConverter(diagnostics.Add).Convert(panel, new HashSet<string>());

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("sum(a) by (operation)", "{{operation}}")]
    [InlineData("sum(a)", "{{pod}}")]
    [InlineData("sum(a)", "plain text")]
    [InlineData("sum(a)", null)]
    [InlineData("sum(a)", "")]
    public void Convert_MetricsPieChart_AlwaysEmitsNonEmptyGroupNames(string expr, string? legend)
    {
        var panel = MetricsPanel("Any", (expr, legend));

        Assert.NotEmpty(GroupNamesOf(new PieChartPanelConverter().Convert(panel, new HashSet<string>())));
    }

    /// <summary>The logs branch is unaffected: Coralogix accepts logs pies with no grouping fields.</summary>
    [Fact]
    public void Convert_LogsPieChart_WithoutTermsBucket_RemainsUnchanged()
    {
        var panel = new JObject
        {
            ["id"] = 5,
            ["title"] = "Logs pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "level:error",
                    ["bucketAggs"] = new JArray(),
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" }
                }
            }
        };

        var widget = new PieChartPanelConverter().Convert(panel, new HashSet<string>());

        var logs = widget!["definition"]?["pieChart"]?["query"]?["logs"] as JObject;
        Assert.NotNull(logs);
        Assert.Null(logs!["groupNamesFields"]);
        Assert.Null(widget["definition"]?["pieChart"]?["query"]?["metrics"]);
    }

    /// <summary>
    /// End-to-end through the planner. The Lucene merge path is Elasticsearch-only and every one of its
    /// exits reduces the panel to a single target, which previously left multi-query Prometheus pies with
    /// one slice and no group names.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_ThroughPlanner_MetricsPieChart_KeepsEveryTarget(bool pieChartAllowlisted)
    {
        var panel = MetricsPanel("Task per status",
            ("sum(a{job=\"$job\"})", "running"),
            ("sum(b{job=\"$job\"})", "failed"),
            ("sum(c{job=\"$job\"})", "paused"));

        var mergeOptions = pieChartAllowlisted
            ? new MultiLuceneMergeOptions(["piechart"])
            : MultiLuceneMergeOptions.Disabled;

        var plan = new CompositeTransformationPlanner(mergeOptions).Plan(
            new TransformationContext(panel, (JArray)panel["targets"]!, new JArray()));

        var widget = new PieChartPanelConverter().Convert(panel, new HashSet<string>(), plan);

        Assert.Equal(["series"], GroupNamesOf(widget));

        var promql = widget!["definition"]?["pieChart"]?["query"]?["metrics"]?["promqlQuery"]?["value"]?.ToString();
        Assert.Equal(3, CountOccurrences(promql!, "label_replace"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static JObject MetricsPanel(string title, params (string Expr, string? Legend)[] targets)
    {
        var targetArray = new JArray();
        var refId = 'A';

        foreach (var (expr, legend) in targets)
        {
            var target = new JObject
            {
                ["refId"] = refId.ToString(),
                ["expr"] = expr,
                ["datasource"] = new JObject { ["type"] = "prometheus" }
            };

            if (legend is not null)
                target["legendFormat"] = legend;

            targetArray.Add(target);
            refId++;
        }

        return new JObject
        {
            ["id"] = 1,
            ["title"] = title,
            ["type"] = "piechart",
            ["targets"] = targetArray
        };
    }

    private static string[] GroupNamesOf(JObject? widget)
    {
        var groupNames = widget?["definition"]?["pieChart"]?["query"]?["metrics"]?["groupNames"] as JArray;
        return groupNames?.Select(t => t.ToString()).ToArray() ?? [];
    }
}
