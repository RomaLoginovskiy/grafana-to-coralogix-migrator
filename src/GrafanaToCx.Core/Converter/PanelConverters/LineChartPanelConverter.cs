using System.Text.RegularExpressions;
using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Semantics;
using GrafanaToCx.Core.Converter.Transformations;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

public sealed class LineChartPanelConverter : IPanelConverter
{
    private const string LegendCalcDiagnosticCode = "DGR-LGD-001";

    private readonly ILogqlToLuceneTranslator _logqlTranslator;
    private readonly Action<PanelConversionDiagnostic>? _diagnosticSink;
    private static readonly IAggregationMapper AggregationMapper = new AggregationMapper();

    /// <summary>
    /// Grafana legend calculations that Coralogix can render, keyed to their
    /// <c>LegendColumn</c> enum member. Anything absent here has no equivalent and is reported
    /// rather than substituted — showing the wrong statistic silently is worse than omitting it.
    /// </summary>
    private static readonly Dictionary<string, string> LegendColumnMapping =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mean"] = "LEGEND_COLUMN_AVG",
            ["avg"] = "LEGEND_COLUMN_AVG",
            ["min"] = "LEGEND_COLUMN_MIN",
            ["max"] = "LEGEND_COLUMN_MAX",
            ["sum"] = "LEGEND_COLUMN_SUM",
            ["last"] = "LEGEND_COLUMN_LAST",
            ["lastNotNull"] = "LEGEND_COLUMN_LAST"
        };

    public LineChartPanelConverter(
        ILogqlToLuceneTranslator? logqlTranslator = null,
        Action<PanelConversionDiagnostic>? diagnosticSink = null)
    {
        _logqlTranslator = logqlTranslator ?? new LogqlToLuceneTranslator();
        _diagnosticSink = diagnosticSink;
    }

    // PromQL always has a metric name immediately before {. LogQL never does.
    private static readonly Regex PromQlMetricNameRegex =
        new(@"\b[a-zA-Z_:][a-zA-Z0-9_:]*\{", RegexOptions.Compiled);

    // Inner aggregation function in a LogQL metric expression.
    private static readonly Regex LogQlInnerFunctionRegex =
        new(@"\b(bytes_rate|bytes_over_time|absent_over_time|count_over_time|rate|avg_over_time|min_over_time|max_over_time|first_over_time|last_over_time|quantile_over_time)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
        {
            return null;
        }

        var grafanaUnit = panel["fieldConfig"]?["defaults"]?["unit"]?.ToString() ?? "none";
        var queryDefinitions = new JArray();
        var visibleIndex = 0;

        if (TryBuildConsolidatedDataprimeQueryDefinition(panel, targets, plan, grafanaUnit, out var consolidatedDefinition))
        {
            queryDefinitions.Add(consolidatedDefinition);
            visibleIndex++;
        }
        else
        {
            foreach (var target in targets)
            {
                if (target.Value<bool?>("hide") == true)
                {
                    continue;
                }

                var refId = target.Value<string>("refId") ?? "A";

                // Elasticsearch / OpenSearch: uses query + bucketAggs + metrics (no expr field).
                if (IsElasticsearchQuery(target))
                {
                    var esDef = BuildElasticsearchQueryDefinition(target, refId, visibleIndex, grafanaUnit, panel);
                    if (esDef != null)
                    {
                        queryDefinitions.Add(esDef);
                        visibleIndex++;
                    }
                    continue;
                }

                var expr = target.Value<string>("expr");
                if (string.IsNullOrWhiteSpace(expr))
                {
                    continue;
                }

                if (IsLokiQuery(target, expr))
                {
                    var logDef = BuildLogQueryDefinition(_logqlTranslator, expr, refId, target, visibleIndex, grafanaUnit, panel);
                    if (logDef != null)
                    {
                        queryDefinitions.Add(logDef);
                        visibleIndex++;
                    }
                    continue;
                }

                var query = QueryHelpers.CleanQuery(expr, discoveredMetrics);
                var legend = target.Value<string>("legendFormat") ?? refId;
                var seriesName = GenerateSeriesName(legend, query, refId);

                queryDefinitions.Add(new JObject
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["query"] = new JObject
                    {
                        ["metrics"] = new JObject
                        {
                            ["promqlQuery"] = new JObject { ["value"] = query },
                            ["filters"] = new JArray(),
                            ["editorMode"] = "METRICS_QUERY_EDITOR_MODE_TEXT",
                            ["seriesLimitType"] = "METRICS_SERIES_LIMIT_TYPE_BY_SERIES_COUNT"
                        }
                    },
                    ["seriesCountLimit"] = "20",
                    ["unit"] = QueryHelpers.MapUnit(grafanaUnit),
                    ["scaleType"] = "SCALE_TYPE_LINEAR",
                    ["name"] = seriesName,
                    ["isVisible"] = true,
                    ["colorScheme"] = GetColorScheme(visibleIndex, panel),
                    ["resolution"] = new JObject { ["bucketsPresented"] = 96 },
                    ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED",
                    ["customUnit"] = QueryHelpers.GetCustomUnit(grafanaUnit),
                    ["hashColors"] = false
                });

                visibleIndex++;
            }
        }

        if (queryDefinitions.Count == 0)
        {
            return null;
        }

        var legendOptions = panel["options"]?["legend"] as JObject ?? new JObject();
        var placement = legendOptions.Value<string>("placement") == "right"
            ? "LEGEND_PLACEMENT_SIDE"
            : "LEGEND_PLACEMENT_AUTO";

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = WidgetHelpers.ResolveTitle(panel),
            ["description"] = WidgetHelpers.ResolveDescription(panel),
            ["definition"] = new JObject
            {
                ["lineChart"] = new JObject
                {
                    ["legend"] = new JObject
                    {
                        ["isVisible"] = legendOptions.Value<bool?>("showLegend") ?? true,
                        ["columns"] = GetLegendColumns(legendOptions, panel),
                        ["groupByQuery"] = true,
                        ["placement"] = placement
                    },
                    ["tooltip"] = new JObject
                    {
                        ["showLabels"] = true,
                        ["type"] = "TOOLTIP_TYPE_ALL"
                    },
                    ["queryDefinitions"] = queryDefinitions,
                    ["stackedLine"] = "STACKED_LINE_UNSPECIFIED",
                    ["connectNulls"] = false
                }
            }
        };
    }

    private static bool TryBuildConsolidatedDataprimeQueryDefinition(
        JObject panel,
        IReadOnlyList<JObject> targets,
        TransformationPlan? plan,
        string grafanaUnit,
        out JObject definition)
    {
        definition = new JObject();
        if (plan is not TransformationPlan.Success { ConsolidatedQueryPayload: not null } success)
            return false;

        var dataprime = success.ConsolidatedQueryPayload!["dataprime"] as JObject;
        var text = dataprime?["dataprimeQuery"]?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var dataprimeQuery = new JObject
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

        var title = panel.Value<string>("title") ?? string.Empty;
        var alias = targets.FirstOrDefault()?.Value<string>("alias");
        var seriesName = !string.IsNullOrWhiteSpace(alias)
            ? alias
            : (!string.IsNullOrWhiteSpace(title) ? title : "A");

        definition = BuildQueryDefinition(dataprimeQuery, 0, grafanaUnit, seriesName, panel);
        return true;
    }

    private static string GenerateSeriesName(string legendFormat, string query, string refId)
    {
        if (!string.IsNullOrWhiteSpace(legendFormat)
            && legendFormat != "__auto"
            && legendFormat != "{{}}"
            && !legendFormat.StartsWith("{{", StringComparison.Ordinal))
        {
            var name = legendFormat
                .Replace("$quantile_stat", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("$testid", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim('_')
                .Trim();

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return QueryHelpers.DeriveSeriesNameFromQuery(query, refId);
    }

    private JArray GetLegendColumns(JObject legendOptions, JObject panel)
    {
        var calcs = legendOptions["calcs"] as JArray ?? new JArray();

        var result = new JArray();
        foreach (var calc in calcs.Select(c => c.ToString()))
        {
            if (LegendColumnMapping.TryGetValue(calc, out var mapped))
            {
                if (!result.Any(existing => string.Equals(existing.ToString(), mapped, StringComparison.Ordinal)))
                {
                    result.Add(mapped);
                }

                continue;
            }

            _diagnosticSink?.Invoke(new PanelConversionDiagnostic(
                panel.Value<string>("title") ?? string.Empty,
                panel.Value<string>("type") ?? "timeseries",
                "dropped",
                $"Legend calculation '{calc}' has no Coralogix legend column and was omitted. "
                + "The chart still renders; only this legend statistic is missing.",
                LegendCalcDiagnosticCode));
        }

        return result;
    }

    /// <summary>
    /// Returns true when the target uses a Loki datasource or its expression is a LogQL metric query.
    /// PromQL metric expressions always have an identifier immediately before {, LogQL never does.
    /// </summary>
    private static bool IsLokiQuery(JObject target, string expr)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("loki", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (dsType?.Equals("prometheus", StringComparison.OrdinalIgnoreCase) == true) return false;

        // Heuristic: if the expression contains { but no PromQL metric name before it, it's LogQL.
        return expr.Contains('{') && !PromQlMetricNameRegex.IsMatch(expr);
    }

    private static JObject? BuildLogQueryDefinition(
        ILogqlToLuceneTranslator logqlTranslator,
        string logqlExpr, string refId, JObject target,
        int visibleIndex, string grafanaUnit, JObject panel)
    {
        var luceneQuery = QueryHelpers.NormalizeLuceneQuery(logqlTranslator.Convert(logqlExpr));
        var groupByFields = logqlTranslator.ExtractGroupByFields(logqlExpr);
        var aggregation = BuildLogsAggregation(logqlExpr);

        var legend = target.Value<string>("legendFormat") ?? refId;
        var seriesName = GenerateSeriesName(legend, logqlExpr, refId);

        var logsQuery = BuildLineChartLogsQuery(luceneQuery, aggregation, groupByFields);

        return BuildQueryDefinition(
            new JObject { ["logs"] = logsQuery },
            visibleIndex,
            grafanaUnit,
            seriesName,
            panel);
    }

    /// <summary>
    /// Maps the innermost LogQL range aggregation to a Coralogix logs aggregation object.
    /// bytes_rate / bytes_over_time → sum of __size__
    /// everything else (rate, count_over_time, …) → count
    /// </summary>
    private static JObject BuildLogsAggregation(string logqlExpr)
    {
        var inner = LogQlInnerFunctionRegex.Match(logqlExpr);
        if (inner.Success)
        {
            var fn = inner.Groups[1].Value.ToLowerInvariant();
            if (fn is "bytes_rate" or "bytes_over_time")
                return new JObject { ["sum"] = new JObject { ["field"] = "__size__" } };
        }

        return AggregationMapper.MapLogsAggregation(new JArray(new JObject { ["type"] = "count" }));
    }

    private static bool IsElasticsearchQuery(JObject target)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Fallback heuristic: has bucketAggs (ES format) but no expr (PromQL/LogQL format).
        return target["bucketAggs"] != null && target["expr"] == null;
    }

    private static JObject? BuildElasticsearchQueryDefinition(
        JObject target, string refId, int visibleIndex, string grafanaUnit, JObject panel)
    {
        var luceneQuery = QueryHelpers.NormalizeLuceneQuery(target.Value<string>("query") ?? string.Empty);

        // Extract group-by fields from terms bucketAggs (date_histogram is the time axis, skip it).
        var groupByFields = new List<string>();
        var bucketAggs = target["bucketAggs"] as JArray ?? new JArray();
        foreach (var bucket in bucketAggs.Children<JObject>())
        {
            if (bucket.Value<string>("type")?.Equals("terms", StringComparison.OrdinalIgnoreCase) == true)
            {
                var field = bucket.Value<string>("field");
                if (!string.IsNullOrWhiteSpace(field))
                    groupByFields.Add(field);
            }
        }

        var metrics = target["metrics"] as JArray ?? new JArray();
        var aggregation = BuildElasticsearchAggregation(metrics);

        var alias = target.Value<string>("alias");
        var seriesName = !string.IsNullOrWhiteSpace(alias) ? alias
            : panel.Value<string>("title") is { Length: > 0 } t ? t
            : refId;

        var logsQuery = BuildLineChartLogsQuery(luceneQuery, aggregation, groupByFields);

        return BuildQueryDefinition(
            new JObject { ["logs"] = logsQuery },
            visibleIndex,
            grafanaUnit,
            seriesName,
            panel);
    }

    private static JObject BuildElasticsearchAggregation(JArray metrics)
    {
        return AggregationMapper.MapLogsAggregation(metrics);
    }

    /// <summary>
    /// Builds the "logs" query object for a lineChart queryDefinition, matching the CX API contract:
    ///   aggregations  — array (not singular aggregation)
    ///   groupBy       — always empty array (legacy field)
    ///   groupBys      — keypath/scope objects derived from field names
    ///   luceneQuery   — included only when there is a non-trivial filter
    /// </summary>
    private static JObject BuildLineChartLogsQuery(
        string luceneQuery, JObject aggregation, IReadOnlyList<string> groupByFields)
    {
        var normalizedGroupFields = groupByFields
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToList();

        // CX rejects empty group names for logs time-series queries; default to timestamp bucket.
        if (normalizedGroupFields.Count == 0)
            normalizedGroupFields.Add("timestamp");

        var logsQuery = new JObject
        {
            ["groupBy"] = new JArray(),
            ["aggregations"] = new JArray { aggregation },
            ["filters"] = new JArray(),
            ["groupBys"] = new JArray(normalizedGroupFields.Select(CxFieldHelper.ToGroupByField))
        };

        if (!string.IsNullOrWhiteSpace(luceneQuery) && luceneQuery != "*")
            logsQuery["luceneQuery"] = new JObject { ["value"] = luceneQuery };

        return logsQuery;
    }

    private static JObject BuildQueryDefinition(
        JObject query, int visibleIndex, string grafanaUnit, string seriesName, JObject panel)
    {
        return new JObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["query"] = query,
            ["seriesCountLimit"] = "20",
            ["unit"] = QueryHelpers.MapUnit(grafanaUnit),
            ["scaleType"] = "SCALE_TYPE_LINEAR",
            ["name"] = seriesName,
            ["isVisible"] = true,
            ["colorScheme"] = GetColorScheme(visibleIndex, panel),
            ["resolution"] = new JObject { ["bucketsPresented"] = 96 },
            ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED",
            ["customUnit"] = QueryHelpers.GetCustomUnit(grafanaUnit),
            ["hashColors"] = false
        };
    }

    private static string GetColorScheme(int index, JObject panel)
    {
        var overrides = panel["fieldConfig"]?["overrides"] as JArray;
        if (overrides != null)
        {
            foreach (var matcherText in overrides
                         .Children<JObject>()
                         .Select(o => o["matcher"]?["options"]?.ToString() ?? string.Empty))
            {
                var lower = matcherText.ToLowerInvariant();
                if (lower is "error" or "errors" or "failed" or "failure")
                {
                    return "negative";
                }
            }
        }

        var schemes = new[] { "classic", "cold", "severity", "classic" };
        return schemes[index % schemes.Length];
    }
}
