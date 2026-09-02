using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.Converter.Semantics;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

public sealed class GaugePanelConverter : IPanelConverter
{
    private static readonly IAggregationMapper AggregationMapper = new AggregationMapper();

    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
        {
            return null;
        }

        var target = targets[0];

        var defaults = panel["fieldConfig"]?["defaults"] as JObject ?? new JObject();
        var grafanaUnit = defaults.Value<string>("unit") ?? "none";
        var min = defaults.Value<double?>("min") ?? 0;
        var thresholds = ConvertThresholds(defaults["thresholds"] as JObject);
        if (thresholds.Count == 0)
        {
            thresholds.Add(new JObject
            {
                ["from"] = 0,
                ["color"] = "var(--c-severity-log-info)"
            });
        }

        JObject gaugeQuery;
        double max;

        if (IsElasticsearchTarget(target))
        {
            gaugeQuery = BuildLogsQuery(target);
            max = defaults.Value<double?>("max") ?? 100;
        }
        else
        {
            var expr = target.Value<string>("expr") ?? string.Empty;
            var promql = QueryHelpers.CleanQuery(expr, discoveredMetrics);
            max = DetermineMax(defaults.Value<double?>("max"), promql);

            gaugeQuery = new JObject
            {
                ["metrics"] = new JObject
                {
                    ["promqlQuery"] = new JObject { ["value"] = promql },
                    ["aggregation"] = "AGGREGATION_LAST",
                    ["filters"] = new JArray(),
                    ["editorMode"] = "METRICS_QUERY_EDITOR_MODE_TEXT",
                    ["promqlQueryType"] = "PROM_QL_QUERY_TYPE_RANGE"
                }
            };
        }

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = WidgetHelpers.ResolveTitle(panel),
            ["description"] = WidgetHelpers.ResolveDescription(panel),
            ["definition"] = new JObject
            {
                ["gauge"] = new JObject
                {
                    ["query"] = gaugeQuery,
                    ["min"] = min,
                    ["max"] = max,
                    ["showInnerArc"] = false,
                    ["showOuterArc"] = false,
                    ["unit"] = QueryHelpers.MapUnitForGauge(grafanaUnit),
                    ["thresholds"] = thresholds,
                    ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED",
                    ["thresholdBy"] = "THRESHOLD_BY_VALUE",
                    ["customUnit"] = QueryHelpers.GetCustomUnit(grafanaUnit),
                    ["thresholdType"] = "THRESHOLD_TYPE_RELATIVE",
                    ["legend"] = new JObject
                    {
                        ["isVisible"] = true,
                        ["columns"] = new JArray(),
                        ["groupByQuery"] = false,
                        ["placement"] = "LEGEND_PLACEMENT_AUTO"
                    },
                    ["legendBy"] = "LEGEND_BY_GROUPS",
                    ["displaySeriesName"] = true
                }
            }
        };
    }

    private static bool IsElasticsearchTarget(JObject target)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Fallback: has bucketAggs (ES format) but no expr (PromQL/LogQL format).
        return target["bucketAggs"] != null && target["expr"] == null;
    }

    private static JObject BuildLogsQuery(JObject target)
    {
        var luceneQuery = QueryHelpers.NormalizeLuceneQuery(target.Value<string>("query") ?? string.Empty);
        var metrics = target["metrics"] as JArray ?? new JArray();
        var aggregation = AggregationMapper.MapLogsAggregation(metrics);

        var logsQuery = new JObject
        {
            ["logsAggregation"] = aggregation,
            ["filters"] = new JArray()
        };

        if (!string.IsNullOrWhiteSpace(luceneQuery) && luceneQuery != "*")
            logsQuery["luceneQuery"] = new JObject { ["value"] = luceneQuery };

        return new JObject { ["logs"] = logsQuery };
    }

    private static double DetermineMax(double? configuredMax, string query)
    {
        if (configuredMax.HasValue)
        {
            return configuredMax.Value;
        }

        var lowerQuery = query.ToLowerInvariant();
        if (lowerQuery.Contains("duration") || lowerQuery.Contains("_p95") || lowerQuery.Contains("_p99")) return 10;
        if (lowerQuery.Contains("rate") || lowerQuery.Contains("reqs")) return 10000;
        if (lowerQuery.Contains("vus")) return 1000;
        if (lowerQuery.Contains("total")) return 1000000;

        return 100;
    }

    private static JArray ConvertThresholds(JObject? grafanaThresholds)
    {
        var steps = grafanaThresholds?["steps"] as JArray ?? new JArray();
        var result = new JArray();
        var colorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["green"] = "var(--c-severity-log-verbose)",
            ["yellow"] = "var(--c-severity-log-warning)",
            ["orange"] = "var(--c-severity-log-warning)",
            ["red"] = "var(--c-severity-log-error)",
            ["blue"] = "var(--c-severity-log-info)"
        };

        foreach (var step in steps.Children<JObject>())
        {
            var color = step.Value<string>("color") ?? "green";
            var mappedColor = colorMap.TryGetValue(color, out var mapValue)
                ? mapValue
                : "var(--c-severity-log-info)";

            result.Add(new JObject
            {
                // Only a real number may reach a double field. Grafana writes the base step as
                // null and, in hand-edited dashboards, an emptied threshold as "", which the API
                // rejects with `invalid value for double field value: ""`.
                ["from"] = step["value"] is JValue { Type: JTokenType.Integer or JTokenType.Float } numeric
                    ? numeric
                    : new JValue(0),
                ["color"] = mappedColor
            });
        }

        return result;
    }
}
