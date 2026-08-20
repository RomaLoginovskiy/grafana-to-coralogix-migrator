using System.Text;
using System.Text.RegularExpressions;

namespace GrafanaToCx.Core.Converter;

/// <param name="Expr">PromQL expression for a single Grafana target.</param>
/// <param name="Label">Legend text for that target, used as the synthesized label value.</param>
public sealed record PromqlSeriesTarget(string Expr, string? Label);

public sealed record PromqlConsolidationResult(string Expr, string GroupLabel, int SeriesCount);

/// <summary>
/// Merges several single-series PromQL queries into one query carrying a synthetic grouping label.
/// </summary>
/// <remarks>
/// <para>
/// Grafana pie charts often express slices as N separate scalar queries distinguished only by their legend
/// text — e.g. five <c>sum(...)</c> queries labelled running/failed/paused. A Coralogix metrics pie takes a
/// single PromQL query plus label names to slice by, so that shape has no direct equivalent.
/// </para>
/// <para>
/// <c>label_replace</c> closes the gap: it stamps a constant label onto each sub-query, and <c>or</c> unions
/// the results into one vector. The pie then groups by that synthetic label and renders one slice per
/// original query, preserving both the values and the legend text.
/// </para>
/// </remarks>
public static partial class PromqlSeriesConsolidator
{
    public const string DefaultGroupLabel = "series";

    /// <summary>Used when the preferred label name already appears in the source queries.</summary>
    private const string FallbackGroupLabel = "cx_series";

    public static PromqlConsolidationResult? Consolidate(
        IReadOnlyList<PromqlSeriesTarget> targets,
        string groupLabel = DefaultGroupLabel)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var usable = targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Expr))
            .ToList();

        if (usable.Count == 0)
            return null;

        var resolvedLabel = ResolveGroupLabel(usable, groupLabel);
        var builder = new StringBuilder();

        for (var i = 0; i < usable.Count; i++)
        {
            if (i > 0)
                builder.Append(" or ");

            var value = ResolveSeriesValue(usable[i], i);

            builder
                .Append("label_replace(")
                .Append(usable[i].Expr.Trim())
                .Append(", \"").Append(resolvedLabel).Append('"')
                .Append(", \"").Append(EscapeLiteral(value)).Append('"')
                .Append(", \"\", \"\")");
        }

        return new PromqlConsolidationResult(builder.ToString(), resolvedLabel, usable.Count);
    }

    /// <summary>
    /// Picks a label name that does not already appear in the source expressions, so the synthetic label
    /// cannot silently overwrite a real one.
    /// </summary>
    private static string ResolveGroupLabel(IReadOnlyList<PromqlSeriesTarget> targets, string preferred)
    {
        var candidate = PromqlGroupNameExtractor.IsValidLabelName(preferred) ? preferred : DefaultGroupLabel;

        var collides = targets.Any(t =>
            Regex.IsMatch(t.Expr, $@"\b{Regex.Escape(candidate)}\s*(=|!=|=~|!~)"));

        return collides ? FallbackGroupLabel : candidate;
    }

    /// <summary>
    /// Legend text is preferred; a bare metric name is the next best human-readable value, and an ordinal
    /// is the last resort so every slice still gets a distinct label.
    /// </summary>
    private static string ResolveSeriesValue(PromqlSeriesTarget target, int index)
    {
        var label = StripLabelTemplates(target.Label);
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        var metricName = MetricNameRegex().Match(target.Expr);
        if (metricName.Success)
            return metricName.Groups[1].Value;

        return $"series {index + 1}";
    }

    /// <summary>
    /// Removes <c>{{label}}</c> placeholders. Consolidation only runs when no real grouping label was
    /// found, but a legend may still carry one alongside literal text.
    /// </summary>
    private static string StripLabelTemplates(string? legend) =>
        string.IsNullOrWhiteSpace(legend)
            ? string.Empty
            : LegendTemplateRegex().Replace(legend, string.Empty).Trim();

    private static string EscapeLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    // First metric-name-looking identifier that is followed by a selector or call paren.
    [GeneratedRegex(@"\b([a-zA-Z_:][a-zA-Z0-9_:]*)\s*\{")]
    private static partial Regex MetricNameRegex();

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex LegendTemplateRegex();
}
