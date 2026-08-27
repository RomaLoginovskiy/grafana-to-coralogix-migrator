using System.Text.RegularExpressions;

namespace GrafanaToCx.Core.Converter;

/// <summary>
/// Extracts Coralogix <c>groupNames</c> (PromQL label names) from a Grafana panel's query and display hints.
/// </summary>
/// <remarks>
/// A Coralogix pie chart backed by a metrics query slices by label values, so it requires at least one
/// label name. Grafana expresses that grouping in three different places depending on how the panel was
/// authored, and none of them is the query result itself — hence three extraction sources.
/// </remarks>
public static partial class PromqlGroupNameExtractor
{
    /// <summary>
    /// Label names from a PromQL <c>by (...)</c> clause.
    /// </summary>
    /// <remarks>
    /// <c>without (...)</c> is deliberately ignored: it names the labels to <em>drop</em>, so the remaining
    /// grouping labels are whatever the metric happens to carry and cannot be known statically.
    /// </remarks>
    public static IReadOnlyList<string> FromByClause(string? promql)
    {
        if (string.IsNullOrWhiteSpace(promql))
            return [];

        var labels = new List<string>();

        foreach (Match match in ByClauseRegex().Matches(promql))
        {
            foreach (var raw in match.Groups[1].Value.Split(','))
            {
                var label = raw.Trim();
                if (IsValidLabelName(label) && !labels.Contains(label, StringComparer.Ordinal))
                    labels.Add(label);
            }
        }

        return labels;
    }

    /// <summary>
    /// Label names referenced by a Grafana legend format, e.g. <c>{{operation}}</c> or
    /// <c>Used RAM {{k8s_pod_name}}</c>.
    /// </summary>
    public static IReadOnlyList<string> FromLegendFormat(string? legendFormat)
    {
        if (string.IsNullOrWhiteSpace(legendFormat))
            return [];

        var labels = new List<string>();

        foreach (Match match in LegendLabelRegex().Matches(legendFormat))
        {
            var label = match.Groups[1].Value.Trim();
            if (IsValidLabelName(label) && !labels.Contains(label, StringComparer.Ordinal))
                labels.Add(label);
        }

        return labels;
    }

    /// <summary>
    /// Label name from a Grafana display-name template such as <c>${__field.labels.queue}</c>.
    /// Tolerates surrounding text, unlike the original exact-match implementation.
    /// </summary>
    public static string? FromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var match = DisplayNameLabelRegex().Match(displayName);
        if (!match.Success)
            return null;

        var label = match.Groups[1].Value.Trim();
        return IsValidLabelName(label) ? label : null;
    }

    /// <summary>
    /// Prometheus label naming rules: <c>[a-zA-Z_][a-zA-Z0-9_]*</c>.
    /// </summary>
    public static bool IsValidLabelName(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;

        if (label[0] is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'))
            return false;

        foreach (var c in label)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }

    // `by` must be a standalone keyword so metric names such as `system_memory_usage_By` do not match.
    [GeneratedRegex(@"\bby\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ByClauseRegex();

    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}")]
    private static partial Regex LegendLabelRegex();

    [GeneratedRegex(@"\$\{__field\.labels\.([^}]+)\}")]
    private static partial Regex DisplayNameLabelRegex();
}
