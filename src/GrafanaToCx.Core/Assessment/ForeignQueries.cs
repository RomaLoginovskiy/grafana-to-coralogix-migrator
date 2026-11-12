using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Assessment;

/// <summary>
/// Finds panel queries and query variables written for datasources the converter does not
/// translate (InfluxDB, CloudWatch, Graphite, ...). Left alone, the converter drops those
/// panels silently or gives them a placeholder query, so without this an assessment reports
/// them as clean.
///
/// Also produces the "assume queries translated" view of a dashboard: each such query swapped
/// for a stand-in PromQL query, so the assessment judges only the dashboard mechanics that are
/// left once the data and its naming are sorted out (an integration problem, not a dashboard one).
/// </summary>
public static class ForeignQueries
{
    private static readonly HashSet<string> TranslatedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "prometheus", "loki", "elasticsearch", "opensearch", "grafana-opensearch-datasource"
    };

    // Grafana's own pseudo-datasources carry no query to translate.
    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datasource", "grafana", "-- Grafana --", "-- Dashboard --", "-- Mixed --"
    };

    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["influxdb"] = "InfluxDB",
        ["cloudwatch"] = "CloudWatch",
        ["graphite"] = "Graphite",
        ["__expr__"] = "server-side expression",
    };

    private const string StandInMetric = "stand_in";

    /// <summary>
    /// The datasource kind a panel target is written for, when the converter cannot translate
    /// it; null when it can, or when there is nothing to go on.
    /// </summary>
    public static string? ForeignKind(JObject target, JToken? panelDatasource)
    {
        var type = DatasourceType(target["datasource"])
                   ?? target.Value<string>("dsType")
                   ?? DatasourceType(panelDatasource);

        // Most older dashboards name their datasource ("ESB-PROD - Default") without a type,
        // so fall back on the query's shape.
        type ??= TargetShape(target);

        if (type is null || TranslatedTypes.Contains(type) || IgnoredTypes.Contains(type))
            return null;

        return DisplayName(type);
    }

    /// <summary>The datasource kind a query variable is written for, when it is not translated.</summary>
    public static string? ForeignVariableKind(JObject variable)
    {
        if (!string.Equals(variable.Value<string>("type"), "query", StringComparison.OrdinalIgnoreCase))
            return null;

        var type = DatasourceType(variable["datasource"]) ?? VariableQueryShape(QueryText(variable));
        if (type is null || TranslatedTypes.Contains(type) || IgnoredTypes.Contains(type))
            return null;

        return DisplayName(type);
    }

    /// <summary>Panels with at least one untranslated query, counted per datasource kind.</summary>
    public static IReadOnlyList<AssessmentFinding> CountPanels(JObject dashboard) =>
        Panels(dashboard)
            .SelectMany(p => VisibleTargets(p)
                .Select(t => ForeignKind(t, p["datasource"]))
                .OfType<string>()
                .Distinct())
            .GroupBy(kind => kind)
            .Select(g => new AssessmentFinding(
                "query not translated",
                $"panels query {g.Key}, which is not translated",
                g.Count()))
            .ToList();

    /// <summary>Query variables written for an untranslated datasource, counted per kind.</summary>
    public static IReadOnlyList<AssessmentFinding> CountVariables(JObject dashboard) =>
        Variables(dashboard)
            .Select(ForeignVariableKind)
            .OfType<string>()
            .GroupBy(kind => kind)
            .Select(g => new AssessmentFinding(
                "variable query not translated",
                $"variables query {g.Key}, which is not translated",
                g.Count()))
            .ToList();

    /// <summary>
    /// A copy of the dashboard with every untranslated query replaced by a stand-in PromQL
    /// query, and every untranslated query variable by a stand-in label_values variable.
    /// refId, legend and hidden state are kept, so multi-query and layout mechanics still apply.
    /// </summary>
    public static JObject AssumeTranslated(JObject dashboard)
    {
        var copy = (JObject)dashboard.DeepClone();

        foreach (var panel in Panels(copy))
        {
            if (panel["targets"] is not JArray targets)
                continue;

            var rewrote = false;
            for (var i = 0; i < targets.Count; i++)
            {
                if (targets[i] is not JObject target || ForeignKind(target, panel["datasource"]) is null)
                    continue;

                var refId = target.Value<string>("refId") ?? $"Q{i}";
                var standIn = new JObject
                {
                    ["refId"] = refId,
                    // Distinct per query, so nothing downstream mistakes two of them for duplicates.
                    ["expr"] = $"{StandInMetric}_{SanitizeLabel(refId)}",
                    ["datasource"] = new JObject { ["type"] = "prometheus" },
                    ["legendFormat"] = target.Value<string>("alias") ?? refId,
                };
                if (target.Value<bool?>("hide") == true)
                    standIn["hide"] = true;

                targets[i] = standIn;
                rewrote = true;
            }

            // A panel-level datasource is what targets without their own fall back on.
            var panelType = DatasourceType(panel["datasource"]);
            if (rewrote && (panelType is null || !TranslatedTypes.Contains(panelType)))
                panel["datasource"] = new JObject { ["type"] = "prometheus" };
        }

        foreach (var variable in Variables(copy))
        {
            if (ForeignVariableKind(variable) is null)
                continue;

            var name = variable.Value<string>("name") ?? "value";
            var query = $"label_values({StandInMetric}, {SanitizeLabel(name)})";
            variable["query"] = query;
            variable["definition"] = query;
            variable["datasource"] = new JObject { ["type"] = "prometheus" };
        }

        return copy;
    }

    private static string? DatasourceType(JToken? datasource) => datasource switch
    {
        JObject o => o.Value<string>("type"),
        // A bare string is a datasource name (or a variable such as $environment), not a type,
        // except for Grafana's built-in names.
        JValue { Type: JTokenType.String } v when IgnoredTypes.Contains(v.ToString()) => v.ToString(),
        _ => null
    };

    private static string? TargetShape(JObject target)
    {
        if (!string.IsNullOrWhiteSpace(target.Value<string>("expr"))) return "prometheus";
        if (target["bucketAggs"] is not null) return "elasticsearch";
        if (target["namespace"] is not null && target["metricName"] is not null) return "cloudwatch";
        if (target["measurement"] is not null || target["select"] is not null || target["policy"] is not null
            || (target.Value<bool?>("rawQuery") == true && target["query"] is not null))
            return "influxdb";
        if (target["target"] is JValue { Type: JTokenType.String }) return "graphite";
        return null;
    }

    private static string? VariableQueryShape(string query)
    {
        var q = query.TrimStart();
        if (Regex.IsMatch(q, @"^(SHOW|SELECT)\s", RegexOptions.IgnoreCase)) return "influxdb";
        if (Regex.IsMatch(q, @"^(regions|namespaces|metrics|dimension_keys|dimension_values|ebs_volume_ids|ec2_instance_attribute|resource_arns|statistics)\(",
                RegexOptions.IgnoreCase))
            return "cloudwatch";
        return null;
    }

    private static string QueryText(JObject variable) => variable["query"] switch
    {
        JObject o => o.Value<string>("query") ?? string.Empty,
        JValue v => v.ToString(),
        _ => string.Empty
    };

    private static string DisplayName(string type) =>
        DisplayNames.TryGetValue(type, out var name) ? name : type;

    private static string SanitizeLabel(string value) => Regex.Replace(value, @"\W", "_");

    private static IEnumerable<JObject> VisibleTargets(JObject panel) =>
        (panel["targets"] as JArray ?? [])
        .Children<JObject>()
        .Where(t => t.Value<bool?>("hide") != true);

    private static IEnumerable<JObject> Variables(JObject dashboard) =>
        (dashboard["templating"]?["list"] as JArray ?? []).Children<JObject>();

    /// <summary>Non-row panels, including those inside collapsed rows.</summary>
    private static IEnumerable<JObject> Panels(JObject dashboard)
    {
        foreach (var panel in (dashboard["panels"] as JArray ?? []).Children<JObject>())
        {
            if (string.Equals(panel.Value<string>("type"), "row", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var nested in (panel["panels"] as JArray ?? []).Children<JObject>())
                    yield return nested;
                continue;
            }

            yield return panel;
        }
    }
}
