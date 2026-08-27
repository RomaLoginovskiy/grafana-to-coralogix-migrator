using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.Converter.Semantics;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

/// <summary>
/// Converts Grafana piechart panels to Coralogix PieChart widgets.
///
/// Supports both Elasticsearch (logs query with groupNamesFields) and
/// Prometheus (metrics query with promqlQuery).
///
/// Multi-target Elasticsearch panels use the first target's Lucene query;
/// the shared groupBy field preserves the grouping dimension.
/// When a transformation plan provides ConsolidatedQueryPayload (e.g. from
/// PieMultiQueryConsolidationPlanner), that payload is used instead.
/// </summary>
public sealed class PieChartPanelConverter : IPanelConverter
{
    private static readonly IAggregationMapper AggregationMapper = new AggregationMapper();

    private readonly Action<PanelConversionDiagnostic>? _diagnosticSink;

    public PieChartPanelConverter(Action<PanelConversionDiagnostic>? diagnosticSink = null)
    {
        _diagnosticSink = diagnosticSink;
    }

    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
            return null;

        var target = targets[0];

        var grafanaUnit = panel["fieldConfig"]?["defaults"]?["unit"]?.ToString() ?? "none";
        var legendOptions = panel["options"]?["legend"] as JObject ?? new JObject();

        JObject pieQuery;
        if (plan is TransformationPlan.Success success && success.ConsolidatedQueryPayload != null)
        {
            pieQuery = NormalizeConsolidatedQueryPayload(success.ConsolidatedQueryPayload);
        }
        else
        {
            var built = IsElasticsearchTarget(target)
                ? BuildLogsQuery(target)
                : BuildMetricsQuery(panel, targets, discoveredMetrics);

            // An ungrouped metrics query cannot make a pie chart; let the converter skip the
            // panel and report it rather than emitting something the API will reject.
            if (built is null)
                return null;

            pieQuery = built;
        }

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = panel.Value<string>("title") is { Length: > 0 } t ? t : $"Panel #{panel.Value<int>("id")}",
            ["description"] = QueryHelpers.CleanHtml(panel.Value<string>("description") ?? string.Empty),
            ["definition"] = new JObject
            {
                ["pieChart"] = new JObject
                {
                    ["query"] = pieQuery,
                    ["maxSlicesPerChart"] = 24,
                    ["minSlicePercentage"] = 0,
                    ["showLegend"] = legendOptions.Value<bool?>("showLegend") ?? true,
                    ["colorScheme"] = "classic",
                    ["unit"] = QueryHelpers.MapUnitForGauge(grafanaUnit),
                    ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED",
                    ["stackDefinition"] = new JObject
                    {
                        ["maxSlicesPerStack"] = 8
                    },
                    ["labelDefinition"] = new JObject
                    {
                        ["labelSource"] = "LABEL_SOURCE_INNER",
                        ["isVisible"] = true,
                        ["showName"] = true,
                        ["showValue"] = true,
                        ["showPercentage"] = true
                    }
                }
            }
        };
    }

    private static JObject BuildLogsQuery(JObject target)
    {
        var groupNamesFields = new JArray();
        var bucketAggs = target["bucketAggs"] as JArray ?? new JArray();

        foreach (var bucket in bucketAggs.Children<JObject>())
        {
            if (!string.Equals(bucket.Value<string>("type"), "terms", StringComparison.OrdinalIgnoreCase))
                continue;
            var field = bucket.Value<string>("field") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(field))
                groupNamesFields.Add(CxFieldHelper.ToGroupByField(field));
        }

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

    private JObject? BuildMetricsQuery(JObject panel, IReadOnlyList<JObject> targets, ISet<string> discoveredMetrics)
    {
        var series = targets
            .Select(t => new PromqlSeriesTarget(
                QueryHelpers.CleanQuery(t.Value<string>("expr") ?? string.Empty, discoveredMetrics),
                t.Value<string>("legendFormat")))
            .Where(t => !string.IsNullOrWhiteSpace(t.Expr))
            .ToList();

        var groupNames = ResolveGroupNames(panel, series);

        if (groupNames.Count > 0)
            return BuildMetricsQueryObject(series[0].Expr, groupNames);

        // No label to slice by. Stamp one on with label_replace so each original query becomes a slice,
        // rather than dropping every target but the first and emitting an unusable empty grouping.
        var consolidated = PromqlSeriesConsolidator.Consolidate(series);

        // Nothing usable to chart. Coralogix rejects a metrics pie with empty group_names, so signal
        // upward and let the caller skip the panel rather than emit a widget that fails validation.
        if (consolidated is null)
            return null;

        ReportConsolidation(panel, consolidated);
        return BuildMetricsQueryObject(consolidated.Expr, [consolidated.GroupLabel]);
    }

    /// <summary>
    /// Grouping labels, in descending order of how directly they express intent:
    /// the PromQL <c>by (...)</c> clause, then <c>{{label}}</c> legend references, then the
    /// <c>${__field.labels.X}</c> display-name template.
    /// </summary>
    private static List<string> ResolveGroupNames(JObject panel, IReadOnlyList<PromqlSeriesTarget> series)
    {
        var fromByClause = series
            .SelectMany(s => PromqlGroupNameExtractor.FromByClause(s.Expr))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fromByClause.Count > 0)
            return fromByClause;

        var fromLegend = series
            .SelectMany(s => PromqlGroupNameExtractor.FromLegendFormat(s.Label))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (fromLegend.Count > 0)
            return fromLegend;

        var fromDisplayName = PromqlGroupNameExtractor.FromDisplayName(
            panel["fieldConfig"]?["defaults"]?["displayName"]?.ToString());

        return fromDisplayName is null ? [] : [fromDisplayName];
    }

    private static JObject BuildMetricsQueryObject(string promql, IReadOnlyList<string> groupNames)
    {
        var metricsQuery = new JObject
        {
            ["promqlQuery"] = new JObject { ["value"] = promql },
            ["aggregation"] = "AGGREGATION_LAST",
            ["editorMode"] = "METRICS_QUERY_EDITOR_MODE_TEXT",
            ["filters"] = new JArray(),
            ["groupNames"] = new JArray(groupNames)
        };

        return new JObject { ["metrics"] = metricsQuery };
    }

    /// <summary>
    /// Labels from a PromQL grouping clause — both <c>sum(...) by (a, b)</c> and
    /// <c>sum by (a, b) (...)</c>. <c>without</c> is not usable here: it names the labels to
    /// drop rather than the ones to group by.
    /// </summary>
    public static IReadOnlyList<string> ExtractPromqlGroupingLabels(string promql)
    {
        if (string.IsNullOrWhiteSpace(promql))
            return [];

        var match = System.Text.RegularExpressions.Regex.Match(
            promql,
            @"\bby\s*\(\s*(?<labels>[^)]*?)\s*\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
            return [];

        return match.Groups["labels"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(label => label.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Reports that several queries were merged under a synthesized label. Only worth reporting when
    /// more than one query was folded together; a single query keeps its original meaning.
    /// </summary>
    private void ReportConsolidation(JObject panel, PromqlConsolidationResult consolidated)
    {
        if (_diagnosticSink is null || consolidated.SeriesCount < 2)
            return;

        _diagnosticSink(new PanelConversionDiagnostic(
            panel.Value<string>("title") ?? string.Empty,
            "piechart",
            "approximated",
            $"Panel had no group-by label; {consolidated.SeriesCount} queries were merged into one and " +
            $"grouped by a synthesized '{consolidated.GroupLabel}' label taken from each query's legend.",
            "DGR-PIE-010",
            [],
            "promql-label-replace-consolidation",
            0.9));
    }

    private static bool IsElasticsearchTarget(JObject target)
    {
        var dsType = target["datasource"]?["type"]?.ToString();
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return target["bucketAggs"] != null && target["expr"] == null;
    }

    /// <summary>
    /// Converts legacy consolidated payload shape:
    /// { logs: {...}, dataPrime: { value: "..." } }
    /// into API-supported DataPrime shape:
    /// { logs: {...}, dataprime: { dataprimeQuery: { text: "..." }, filters: [] } }
    /// </summary>
    private static JObject NormalizeConsolidatedQueryPayload(JObject payload)
    {
        var normalized = (JObject)payload.DeepClone();

        var dataPrimeValue = normalized["dataPrime"]?["value"]?.ToString();
        if (string.IsNullOrWhiteSpace(dataPrimeValue))
            return normalized;

        var dataprime = new JObject
        {
            ["dataprimeQuery"] = new JObject
            {
                ["text"] = dataPrimeValue
            },
            ["filters"] = new JArray()
        };

        // PieChart.query uses oneof semantics in API contract; keep only dataprime.
        return new JObject
        {
            ["dataprime"] = dataprime
        };
    }
}
