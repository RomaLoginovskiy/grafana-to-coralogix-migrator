using GrafanaToCx.Core.Converter.Transformations;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

/// <summary>
/// Converts Grafana "logs" panels to a Coralogix dataTable widget showing raw log lines.
///
/// Query translation:
///   Loki (LogQL) expr → Lucene via LogqlToLuceneConverter
///   Elasticsearch query → used as-is (already Lucene)
/// </summary>
public sealed class LogsPanelConverter : IPanelConverter
{
    private readonly ILogqlToLuceneTranslator _logqlTranslator;

    public LogsPanelConverter(ILogqlToLuceneTranslator? logqlTranslator = null)
    {
        _logqlTranslator = logqlTranslator ?? new LogqlToLuceneTranslator();
    }

    // Standard CX log columns mirroring Grafana's default logs panel fields.
    private static readonly string[] DefaultColumns =
    [
        "coralogix.timestamp",
        "coralogix.metadata.severity",
        "coralogix.text",
        "coralogix.metadata.applicationName",
        "coralogix.metadata.subsystemName"
    ];

    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var targets = PanelTargetSelector.ResolveVisibleTargets(panel, plan);
        if (targets.Count == 0)
            return null;

        var target = targets[0];

        var luceneQuery = QueryHelpers.NormalizeLuceneQuery(ExtractLuceneQuery(target));
        var columns = BuildColumns(luceneQuery);

        var logsQuery = new JObject { ["filters"] = new JArray() };
        if (!string.IsNullOrWhiteSpace(luceneQuery) && luceneQuery != "*")
            logsQuery["luceneQuery"] = new JObject { ["value"] = luceneQuery };

        var sortOrder = panel["options"]?["sortOrder"]?.ToString() ?? "Descending";
        var orderDirection = sortOrder.Equals("Ascending", StringComparison.OrdinalIgnoreCase)
            ? "ORDER_DIRECTION_ASC"
            : "ORDER_DIRECTION_DESC";

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = WidgetHelpers.ResolveTitle(panel),
            ["description"] = WidgetHelpers.ResolveDescription(panel),
            ["definition"] = new JObject
            {
                ["dataTable"] = new JObject
                {
                    ["query"] = new JObject { ["logs"] = logsQuery },
                    ["resultsPerPage"] = 100,
                    ["rowStyle"] = "ROW_STYLE_ONE_LINE",
                    ["columns"] = columns,
                    ["orderBy"] = new JObject
                    {
                        ["field"] = "coralogix.timestamp",
                        ["orderDirection"] = orderDirection
                    },
                    ["dataModeType"] = "DATA_MODE_TYPE_HIGH_UNSPECIFIED"
                }
            }
        };
    }

    private string ExtractLuceneQuery(JObject target)
    {
        // Elasticsearch / OpenSearch datasource: query field is already Lucene.
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
        {
            return target.Value<string>("query") ?? string.Empty;
        }

        // Loki datasource: translate LogQL expr to Lucene.
        var expr = target.Value<string>("expr") ?? string.Empty;
        return string.IsNullOrWhiteSpace(expr) ? string.Empty : _logqlTranslator.Convert(expr);
    }

    private static JArray BuildColumns(string luceneQuery)
    {
        var columns = new JArray();
        foreach (var field in DefaultColumns)
            columns.Add(new JObject { ["field"] = field });
        return columns;
    }
}
