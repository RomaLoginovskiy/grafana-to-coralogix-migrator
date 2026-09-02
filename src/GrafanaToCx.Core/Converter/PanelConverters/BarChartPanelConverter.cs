using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.Converter.Semantics;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

/// <summary>
/// Converts Grafana barchart panels to Coralogix BarChart widgets.
/// Supports both Elasticsearch (logs query with groupNamesFields) and
/// Prometheus (metrics query with promqlQuery).
/// </summary>
public sealed class BarChartPanelConverter : IPanelConverter
{
    private static readonly IAggregationMapper AggregationMapper = new AggregationMapper();

    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
            return null;

        var target = targets[0];

        var grafanaUnit = panel["fieldConfig"]?["defaults"]?["unit"]?.ToString() ?? "none";

        var barQuery = TryBuildConsolidatedDataprimeQuery(plan, out var dataprimeQuery)
            ? dataprimeQuery
            : IsElasticsearchTarget(target)
                ? BuildLogsQuery(target)
                : BuildMetricsQuery(target, discoveredMetrics);

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = WidgetHelpers.ResolveTitle(panel),
            ["description"] = WidgetHelpers.ResolveDescription(panel),
            ["definition"] = new JObject
            {
                ["barChart"] = new JObject
                {
                    ["query"] = barQuery,
                    ["maxBarsPerChart"] = 10,
                    ["colorScheme"] = "classic",
                    ["unit"] = QueryHelpers.MapUnit(grafanaUnit),
                    ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED",
                    ["scaleType"] = "SCALE_TYPE_LINEAR",
                    ["sortBy"] = "SORT_BY_TYPE_VALUE",
                    ["stackDefinition"] = new JObject
                    {
                        ["maxSlicesPerBar"] = 5
                    }
                }
            }
        };
    }

    private static bool TryBuildConsolidatedDataprimeQuery(TransformationPlan? plan, out JObject query)
    {
        query = new JObject();
        if (plan is not TransformationPlan.Success { ConsolidatedQueryPayload: not null } success)
            return false;

        var dataprime = success.ConsolidatedQueryPayload!["dataprime"] as JObject;
        var text = dataprime?["dataprimeQuery"]?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        query = new JObject
        {
            ["dataprime"] = new JObject
            {
                ["dataprimeQuery"] = new JObject
                {
                    ["text"] = text
                },
                ["filters"] = new JArray()
            }
        };

        return true;
    }

    private static JObject BuildLogsQuery(JObject target)
    {
        var groupNamesFields = new JArray();
        var bucketAggs = target["bucketAggs"] as JArray ?? new JArray();
        var hasDateHistogram = false;

        foreach (var bucket in bucketAggs.Children<JObject>())
        {
            var bucketType = bucket.Value<string>("type");
            if (string.Equals(bucketType, "date_histogram", StringComparison.OrdinalIgnoreCase))
            {
                hasDateHistogram = true;
                continue;
            }

            if (string.Equals(bucketType, "terms", StringComparison.OrdinalIgnoreCase))
            {
                var field = bucket.Value<string>("field") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(field))
                    groupNamesFields.Add(CxFieldHelper.ToGroupByField(field));
            }
        }

        if (hasDateHistogram)
            groupNamesFields.Insert(0, CxFieldHelper.ToGroupByField("timestamp"));

        var aggregation = AggregationMapper.MapLogsAggregation(target["metrics"] as JArray ?? new JArray());
        var luceneQuery = QueryHelpers.NormalizeLuceneQuery(target.Value<string>("query") ?? string.Empty);

        var logsQuery = new JObject
        {
            ["aggregation"] = aggregation,
            ["filters"] = new JArray()
        };

        if (groupNamesFields.Count > 0)
            logsQuery["groupNamesFields"] = groupNamesFields;

        if (!string.IsNullOrWhiteSpace(luceneQuery) && luceneQuery != "*")
            logsQuery["luceneQuery"] = new JObject { ["value"] = luceneQuery };

        return new JObject { ["logs"] = logsQuery };
    }

    private static JObject BuildMetricsQuery(JObject target, ISet<string> discoveredMetrics)
    {
        var expr = target.Value<string>("expr") ?? string.Empty;
        var promql = QueryHelpers.CleanQuery(expr, discoveredMetrics);

        return new JObject
        {
            ["metrics"] = new JObject
            {
                ["promqlQuery"] = new JObject { ["value"] = promql },
                ["aggregation"] = "AGGREGATION_LAST",
                ["editorMode"] = "METRICS_QUERY_EDITOR_MODE_TEXT",
                ["filters"] = new JArray()
            }
        };
    }

    private static bool IsElasticsearchTarget(JObject target)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return target["bucketAggs"] != null && target["expr"] == null;
    }
}
