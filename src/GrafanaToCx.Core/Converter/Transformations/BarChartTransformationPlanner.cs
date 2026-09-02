using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Parsing;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace GrafanaToCx.Core.Converter.Transformations;

public sealed class BarChartTransformationPlanner : ITransformationPlanner
{
    private static readonly ILuceneLikeExpressionParser LuceneParser = new LuceneLikeExpressionParser();
    private readonly MultiLuceneMergeOptions _mergeOptions;

    public BarChartTransformationPlanner(MultiLuceneMergeOptions mergeOptions)
    {
        _mergeOptions = mergeOptions;
    }

    public TransformationPlan Plan(TransformationContext context)
    {
        if (!string.Equals(context.PanelType, "barchart", StringComparison.OrdinalIgnoreCase))
            return new TransformationPlan.Success();

        var visibleTargets = VisibleTargetSelector.Resolve(context.Targets);
        if (visibleTargets.Count == 0)
            return new TransformationPlan.Success(SelectedTargets: visibleTargets);

        if (!_mergeOptions.IsAllowlistedType(context.PanelType))
            return new TransformationPlan.Success(SelectedTargets: visibleTargets);

        return PlanBarChartConsolidation(context.PanelTitle, visibleTargets);
    }

    private static TransformationPlan PlanBarChartConsolidation(string panelTitle, IReadOnlyList<JObject> visibleTargets)
    {
        if (!visibleTargets.All(IsElasticsearchTarget))
        {
            if (visibleTargets.Count == 1)
                return new TransformationPlan.Success(SelectedTargets: visibleTargets);

            return BuildSkipPlan(
                visibleTargets,
                "DGR-BMG-002",
                "Bar multi-metric merge skipped: visible targets are not all Elasticsearch/OpenSearch queries.");
        }

        if (!TryResolveBaseLucene(visibleTargets, out var baseLucene, out var luceneCode, out var luceneReason))
            return BuildSkipPlan(visibleTargets, luceneCode, luceneReason);

        if (!TryResolveRequiredDateHistogram(visibleTargets, out var histogram, out var histogramCode, out var histogramReason))
            return BuildSkipPlan(visibleTargets, histogramCode, histogramReason);

        if (!TryResolveTermsGrouping(visibleTargets, out var termsGrouping, out var groupingCode, out var groupingReason))
            return BuildSkipPlan(visibleTargets, groupingCode, groupingReason);

        if (!TryResolveMetricVariationAggregations(visibleTargets, out var aggregations, out var metricCode, out var metricReason))
            return BuildSkipPlan(visibleTargets, metricCode, metricReason);

        var payload = BuildBarChartConsolidatedPayload(baseLucene, histogram!, termsGrouping, aggregations);
        var isMultiTarget = visibleTargets.Count > 1;
        var reason = isMultiTarget
            ? $"Bar multi-metric merge applied for allowlisted panel '{panelTitle}'."
            : $"Bar single-target DataPrime consolidation applied for allowlisted panel '{panelTitle}'.";
        var code = isMultiTarget ? "DGR-BMG-000" : "DGR-BMG-010";
        var approximation = isMultiTarget ? "merge-multi-lucene-metric-variation" : "merge-single-lucene-metric";

        return new TransformationPlan.Success(
            ConsolidatedQueryPayload: payload,
            SelectedTargets: visibleTargets,
            Decision: new PanelConversionDecision(
                "fallback",
                reason,
                code,
                [],
                approximation,
                0.93));
    }

    private static bool TryResolveBaseLucene(
        IReadOnlyList<JObject> targets,
        out string baseLucene,
        out string code,
        out string reason)
    {
        baseLucene = string.Empty;
        code = string.Empty;
        reason = string.Empty;

        if (targets.Count == 1)
        {
            baseLucene = QueryHelpers.NormalizeLuceneQuery(targets[0].Value<string>("query") ?? string.Empty).Trim();
            return true;
        }

        return TryResolveCanonicalLucene(targets, out baseLucene, out code, out reason);
    }

    private static bool TryResolveRequiredDateHistogram(
        IReadOnlyList<JObject> targets,
        out DateHistogramSemantics? histogram,
        out string code,
        out string reason)
    {
        histogram = null;
        code = string.Empty;
        reason = string.Empty;

        if (!TryResolveDateHistogram(targets, out var resolved, out code, out reason))
            return false;

        if (resolved == null)
        {
            code = "DGR-BMG-004";
            reason = "Bar multi-metric merge skipped: date_histogram is required and must be consistent across visible targets.";
            return false;
        }

        histogram = resolved;
        return true;
    }

    private static bool TryResolveDateHistogram(
        IReadOnlyList<JObject> targets,
        out DateHistogramSemantics? histogram,
        out string code,
        out string reason)
    {
        histogram = null;
        code = string.Empty;
        reason = string.Empty;

        var parsed = new List<DateHistogramSemantics?>(targets.Count);
        foreach (var target in targets)
        {
            if (!TryExtractDateHistogram(target, out var semantics))
            {
                code = "DGR-LMG-007";
                reason = "Multi-Lucene merge skipped: date_histogram definition is invalid or incomplete.";
                return false;
            }

            parsed.Add(semantics);
        }

        var hasHistogram = parsed.Any(p => p != null);
        if (!hasHistogram)
            return true;

        if (parsed.Any(p => p == null))
        {
            code = "DGR-LMG-008";
            reason = "Multi-Lucene merge skipped: date_histogram presence is inconsistent across visible targets.";
            return false;
        }

        var definitions = parsed.OfType<DateHistogramSemantics>().ToList();
        var fieldCount = definitions.Select(d => d.Field).Distinct(StringComparer.Ordinal).Count();
        var intervalCount = definitions.Select(d => d.Interval).Distinct(StringComparer.Ordinal).Count();
        if (fieldCount != 1 || intervalCount != 1)
        {
            code = "DGR-LMG-009";
            reason = "Multi-Lucene merge skipped: date_histogram field/interval semantics are inconsistent across visible targets.";
            return false;
        }

        histogram = definitions[0];
        return true;
    }

    private static bool TryExtractDateHistogram(JObject target, out DateHistogramSemantics? histogram)
    {
        histogram = null;
        var buckets = target["bucketAggs"] as JArray;
        if (buckets == null || buckets.Count == 0)
            return true;

        var dateBuckets = buckets
            .Children<JObject>()
            .Where(b => string.Equals(b.Value<string>("type"), "date_histogram", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dateBuckets.Count == 0)
            return true;
        if (dateBuckets.Count > 1)
            return false;

        var bucket = dateBuckets[0];
        var field = PanelConverters.CxFieldHelper.StripLogsFieldSuffixes(bucket.Value<string>("field") ?? string.Empty);
        var intervalRaw = bucket["settings"]?["interval"]?.ToString() ?? string.Empty;
        var interval = NormalizeDateHistogramInterval(intervalRaw);
        if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(interval))
            return false;

        histogram = new DateHistogramSemantics(field, interval);
        return true;
    }

    private static bool TryResolveTermsGrouping(
        IReadOnlyList<JObject> targets,
        out IReadOnlyList<string> termsGrouping,
        out string code,
        out string reason)
    {
        termsGrouping = [];
        code = string.Empty;
        reason = string.Empty;

        IReadOnlyList<string>? canonical = null;
        foreach (var target in targets)
        {
            if (!TryExtractTermsGrouping(target, out var terms))
            {
                code = "DGR-BMG-011";
                reason = "Bar metric merge skipped: terms bucket semantics are invalid or incomplete.";
                return false;
            }

            if (canonical == null)
            {
                canonical = terms;
                continue;
            }

            if (!canonical.SequenceEqual(terms, StringComparer.Ordinal))
            {
                code = "DGR-BMG-012";
                reason = "Bar metric merge skipped: terms bucket semantics are inconsistent across visible targets.";
                return false;
            }
        }

        termsGrouping = canonical ?? [];
        return true;
    }

    private static bool TryExtractTermsGrouping(JObject target, out IReadOnlyList<string> termsGrouping)
    {
        termsGrouping = [];
        var buckets = target["bucketAggs"] as JArray;
        if (buckets == null || buckets.Count == 0)
            return true;

        var terms = new List<string>();
        foreach (var bucket in buckets.Children<JObject>())
        {
            if (!string.Equals(bucket.Value<string>("type"), "terms", StringComparison.OrdinalIgnoreCase))
                continue;

            var rawField = bucket.Value<string>("field") ?? string.Empty;
            var field = PanelConverters.CxFieldHelper.StripLogsFieldSuffixes(rawField).Trim();
            if (string.IsNullOrWhiteSpace(field))
                return false;

            terms.Add(field);
        }

        termsGrouping = terms;
        return true;
    }

    private static bool TryResolveMetricVariationAggregations(
        IReadOnlyList<JObject> targets,
        out IReadOnlyList<BarAggregationSeries> aggregations,
        out string code,
        out string reason)
    {
        aggregations = [];
        code = string.Empty;
        reason = string.Empty;

        var parsedMetrics = new List<(string Type, string Field, int Percentile, string RefId, string Alias)>(targets.Count);
        foreach (var target in targets)
        {
            var metric = (target["metrics"] as JArray)?.Children<JObject>().FirstOrDefault();
            if (metric == null)
            {
                code = "DGR-BMG-006";
                reason = "Bar multi-metric merge skipped: at least one target has no metric definition.";
                return false;
            }

            var type = (metric.Value<string>("type") ?? "count").ToLowerInvariant();
            var field = PanelConverters.CxFieldHelper.StripLogsFieldSuffixes(metric.Value<string>("field") ?? string.Empty);
            var percentile = metric["settings"]?["percent"]?.Value<int?>()
                             ?? metric["settings"]?["percents"]?[0]?.Value<int?>()
                             ?? 95;
            var refId = target.Value<string>("refId") ?? string.Empty;
            var alias = target.Value<string>("alias") ?? string.Empty;

            parsedMetrics.Add((type, field, percentile, refId, alias));
        }

        var metricType = parsedMetrics[0].Type;
        var metricPercent = parsedMetrics[0].Percentile;
        if (parsedMetrics.Any(m => !string.Equals(m.Type, metricType, StringComparison.Ordinal)))
        {
            code = "DGR-BMG-005";
            reason = "Bar multi-metric merge skipped: metric types are incompatible across visible targets.";
            return false;
        }

        if (string.Equals(metricType, "percentile", StringComparison.Ordinal) &&
            parsedMetrics.Any(m => m.Percentile != metricPercent))
        {
            code = "DGR-BMG-007";
            reason = "Bar multi-metric merge skipped: percentile values are inconsistent across visible targets.";
            return false;
        }

        var requiresField = metricType is "sum" or "avg" or "min" or "max" or "percentile" or "distinct";
        if (requiresField && parsedMetrics.Any(m => string.IsNullOrWhiteSpace(m.Field)))
        {
            code = "DGR-BMG-008";
            reason = "Bar multi-metric merge skipped: one or more metrics require a field but no field is defined.";
            return false;
        }

        var supported = metricType is "count" or "sum" or "avg" or "min" or "max" or "percentile" or "distinct";
        if (!supported)
        {
            code = "DGR-BMG-009";
            reason = "Bar multi-metric merge skipped: unsupported metric type for DataPrime metric-variation merge.";
            return false;
        }

        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BarAggregationSeries>(parsedMetrics.Count);
        foreach (var metric in parsedMetrics)
        {
            var expression = BuildAggregationExpression(metric.Type, metric.Field, metric.Percentile);
            var alias = BuildAggregationAlias(metric, usedAliases);
            result.Add(new BarAggregationSeries(expression, alias));
        }

        aggregations = result;
        return true;
    }

    private static string BuildAggregationExpression(string metricType, string field, int percentile)
    {
        return metricType switch
        {
            "count" => "count()",
            "sum" => $"sum({field})",
            "avg" => $"avg({field})",
            "min" => $"min({field})",
            "max" => $"max({field})",
            "percentile" => $"percentile({field}, {percentile})",
            "distinct" => $"distinct({field})",
            _ => "count()"
        };
    }

    private static string BuildAggregationAlias(
        (string Type, string Field, int Percentile, string RefId, string Alias) metric,
        HashSet<string> usedAliases)
    {
        var preferred = !string.IsNullOrWhiteSpace(metric.Alias)
            ? metric.Alias
            : (!string.IsNullOrWhiteSpace(metric.RefId) ? metric.RefId : metric.Field);

        var normalized = Regex.Replace(preferred, @"[^a-zA-Z0-9_]", "_");
        normalized = normalized.Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "series";

        var candidate = normalized;
        var suffix = 2;
        while (!usedAliases.Add(candidate))
        {
            candidate = $"{normalized}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static bool TryResolveCanonicalLucene(
        IReadOnlyList<JObject> targets,
        out string canonicalLucene,
        out string code,
        out string reason)
    {
        canonicalLucene = string.Empty;
        code = string.Empty;
        reason = string.Empty;

        string? first = null;
        foreach (var target in targets)
        {
            var rawQuery = target.Value<string>("query") ?? string.Empty;
            var normalizedQuery = QueryHelpers.NormalizeLuceneQuery(rawQuery).Trim();
            if (!TryBuildCanonicalPredicateKey(target, normalizedQuery, out var key))
            {
                code = "DGR-BMG-003";
                reason = "Bar multi-metric merge skipped: at least one Lucene target failed strict parsing and boundary-safe predicate canonicalization.";
                return false;
            }

            if (first == null)
            {
                first = key;
                canonicalLucene = normalizedQuery;
                continue;
            }

            if (!string.Equals(first, key, StringComparison.Ordinal))
            {
                code = "DGR-BMG-001";
                reason = "Bar multi-metric merge skipped: visible targets must share the same normalized Lucene predicates.";
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildCanonicalPredicateKey(JObject target, string normalizedQuery, out string key)
    {
        key = string.Empty;
        if (TryParseTargetPredicates(target, out var predicates))
        {
            key = BuildSignatureKey(predicates.Select(p => p.Signature));
            return true;
        }

        if (!TryBuildBoundaryCanonicalPredicateKey(normalizedQuery, out var fallbackKey))
            return false;

        key = fallbackKey;
        return true;
    }

    private static bool TryBuildBoundaryCanonicalPredicateKey(string query, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(query) || query == "*")
        {
            key = string.Empty;
            return true;
        }

        var masked = MaskQuotedSegments(query);
        if (masked.IndexOf('(') >= 0 || masked.IndexOf(')') >= 0)
            return false;
        if (Regex.IsMatch(masked, @"\b(?:OR|NOT)\b", RegexOptions.IgnoreCase))
            return false;

        var andMatches = Regex.Matches(masked, @"\s+AND\s+", RegexOptions.IgnoreCase);
        var signatures = new List<string>(andMatches.Count + 1);
        var start = 0;
        foreach (Match andMatch in andMatches)
        {
            var clause = query[start..andMatch.Index].Trim();
            if (!TryBuildBoundaryPredicateSignature(clause, out var signature))
                return false;
            signatures.Add(signature);
            start = andMatch.Index + andMatch.Length;
        }

        var trailingClause = query[start..].Trim();
        if (!TryBuildBoundaryPredicateSignature(trailingClause, out var trailingSignature))
            return false;
        signatures.Add(trailingSignature);

        key = BuildSignatureKey(signatures);
        return true;
    }

    private static string BuildSignatureKey(IEnumerable<string> signatures) =>
        string.Join("|", signatures.OrderBy(s => s, StringComparer.Ordinal));

    private static bool TryBuildBoundaryPredicateSignature(string clause, out string signature)
    {
        signature = string.Empty;
        if (string.IsNullOrWhiteSpace(clause))
            return false;

        var colonIndex = FindFirstUnquotedColon(clause);
        if (colonIndex <= 0 || colonIndex >= clause.Length - 1)
            return false;

        var field = clause[..colonIndex].Trim();
        var value = clause[(colonIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(field) || field.Any(char.IsWhiteSpace))
            return false;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = NormalizeBoundaryPredicateValue(value);
        if (string.IsNullOrWhiteSpace(value) || IsUnsupportedPredicateValue(value))
            return false;

        signature = $"{field}:{value}";
        return true;
    }

    private static int FindFirstUnquotedColon(string text)
    {
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && !IsEscaped(text, i))
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && c == ':')
                return i;
        }

        return -1;
    }

    private static string NormalizeBoundaryPredicateValue(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("\\\"", StringComparison.Ordinal) &&
            normalized.EndsWith("\\\"", StringComparison.Ordinal) &&
            normalized.Length >= 4)
        {
            normalized = normalized[2..^2];
        }
        else if (normalized.StartsWith('"') && normalized.EndsWith('"') && normalized.Length >= 2)
        {
            normalized = normalized[1..^1];
        }

        normalized = normalized
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Trim();

        return normalized;
    }

    private static string MaskQuotedSegments(string text)
    {
        var chars = text.ToCharArray();
        var inQuotes = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '"' && !IsEscaped(text, i))
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
                chars[i] = 'Q';
        }

        return new string(chars);
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }

    private static bool TryParseTargetPredicates(JObject target, out IReadOnlyList<LucenePredicate> predicates)
    {
        predicates = [];
        var rawQuery = target.Value<string>("query") ?? string.Empty;
        var normalized = QueryHelpers.NormalizeLuceneQuery(rawQuery).Trim();

        var parseResult = LuceneParser.Parse(normalized);
        if (parseResult.Errors.Count > 0)
            return false;

        var output = new List<LucenePredicate>();
        if (parseResult.Root == null)
        {
            predicates = output;
            return true;
        }

        if (!TryCollectConjunctivePredicates(parseResult.Root, output))
            return false;

        predicates = output;
        return true;
    }

    private static bool TryCollectConjunctivePredicates(ExpressionAstNode node, List<LucenePredicate> output)
    {
        switch (node)
        {
            case ExpressionBinaryNode { Operator: ExpressionBinaryOperator.And } andNode:
                return TryCollectConjunctivePredicates(andNode.Left, output)
                    && TryCollectConjunctivePredicates(andNode.Right, output);

            case ExpressionPredicateNode predicateNode:
                if (!string.Equals(predicateNode.Operator, ":", StringComparison.Ordinal))
                    return false;
                if (string.IsNullOrWhiteSpace(predicateNode.Field))
                    return false;
                if (IsUnsupportedPredicateValue(predicateNode.Value))
                    return false;

                output.Add(new LucenePredicate(
                    predicateNode.Field,
                    predicateNode.Operator,
                    predicateNode.Value,
                    predicateNode.IsQuoted));
                return true;

            default:
                return false;
        }
    }

    private static bool IsUnsupportedPredicateValue(string value)
    {
        return value.Contains('*')
               || value.Contains('[')
               || value.Contains(']')
               || value.Contains('{')
               || value.Contains('}')
               || value.Contains(" TO ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsElasticsearchTarget(JObject target)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (dsType?.Equals("elasticsearch", StringComparison.OrdinalIgnoreCase) == true ||
            dsType?.Equals("opensearch", StringComparison.OrdinalIgnoreCase) == true)
            return true;
        return target["bucketAggs"] != null && target["expr"] == null;
    }

    private static TransformationPlan.Success BuildSkipPlan(
        IReadOnlyList<JObject> visibleTargets,
        string code,
        string reason)
    {
        var selected = visibleTargets.Count > 0
            ? new List<JObject> { visibleTargets[0] }
            : new List<JObject>();
        var dropped = visibleTargets.Skip(1).Select(GetTargetIdentity).ToList();

        return new TransformationPlan.Success(
            SelectedTargets: selected,
            Decision: new PanelConversionDecision(
                "fallback",
                reason,
                code,
                dropped,
                "select-one",
                0.72));
    }

    private static string GetTargetIdentity(JObject target) =>
        target.Value<string>("refId")
        ?? target.Value<string>("query")
        ?? "<unknown-target>";

    private static string EscapeForDataPrimeLiteral(string lucene) =>
        lucene.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string NormalizeDateHistogramInterval(string intervalRaw)
    {
        var normalized = QueryHelpers.NormalizeVariablePlaceholders(intervalRaw).Trim();
        return string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase)
            ? "$p.timeRange.suggestedInterval"
            : normalized;
    }

    private static JObject BuildBarChartConsolidatedPayload(
        string baseLucene,
        DateHistogramSemantics histogram,
        IReadOnlyList<string> termsGrouping,
        IReadOnlyList<BarAggregationSeries> aggregations)
    {
        var aggregationClause = string.Join(", ", aggregations.Select(a => $"{a.Expression} as {a.Alias}"));
        var groupBySegments = new List<string> { $"$m.timestamp / {histogram.Interval}" };
        groupBySegments.AddRange(termsGrouping);
        var groupByClause = string.Join(", ", groupBySegments);
        var dataPrimeQuery = string.IsNullOrWhiteSpace(baseLucene)
            ? $"source logs | groupby {groupByClause} agg {aggregationClause}"
            : $"source logs | lucene '{EscapeForDataPrimeLiteral(baseLucene)}' | groupby {groupByClause} agg {aggregationClause}";

        return new JObject
        {
            ["dataprime"] = new JObject
            {
                ["dataprimeQuery"] = new JObject
                {
                    ["text"] = dataPrimeQuery
                },
                ["filters"] = new JArray()
            }
        };
    }

    private sealed record LucenePredicate(string Field, string Operator, string Value, bool IsQuoted)
    {
        public string Signature => $"{Field}{Operator}{Value}";
    }

    private sealed record BarAggregationSeries(string Expression, string Alias);

    private sealed record DateHistogramSemantics(string Field, string Interval);
}
