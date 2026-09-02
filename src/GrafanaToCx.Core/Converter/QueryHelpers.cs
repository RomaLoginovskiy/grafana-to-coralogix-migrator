using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

public static class QueryHelpers
{
    private static readonly Dictionary<string, string> UnitMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "UNIT_UNSPECIFIED",
        ["short"] = "UNIT_UNSPECIFIED",
        ["bytes"] = "UNIT_BYTES",
        ["decbytes"] = "UNIT_BYTES",
        ["bits"] = "UNIT_UNSPECIFIED",
        ["Bps"] = "UNIT_UNSPECIFIED",
        ["binBps"] = "UNIT_UNSPECIFIED",
        ["bytes/sec"] = "UNIT_UNSPECIFIED",
        ["percent"] = "UNIT_PERCENT",
        ["percentunit"] = "UNIT_PERCENT",
        ["s"] = "UNIT_SECONDS",
        ["ms"] = "UNIT_MILLISECONDS",
        ["us"] = "UNIT_MICROSECONDS",
        ["µs"] = "UNIT_MICROSECONDS",
        ["ns"] = "UNIT_NANOSECONDS",
        ["reqps"] = "UNIT_UNSPECIFIED",
        ["rps"] = "UNIT_UNSPECIFIED",
        ["ops"] = "UNIT_UNSPECIFIED"
    };

    private static readonly Dictionary<string, string> CustomUnitMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reqps"] = "req/s",
        ["rps"] = "req/s",
        ["ops"] = "ops/s",
        ["VUs"] = "VUs",
        ["bits"] = "bits",
        ["Bps"] = "bytes/s",
        ["binBps"] = "bytes/s",
        ["bytes/sec"] = "bytes/s"
    };

    // Gauge panels reject UNIT_UNSPECIFIED — map units that need a concrete fallback.
    private static readonly Dictionary<string, string> GaugeUnitOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bps"] = "UNIT_BYTES",
        ["binBps"] = "UNIT_BYTES",
        ["bytes/sec"] = "UNIT_BYTES"
    };

    private static readonly Dictionary<string, string> TimeFrameMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["now-1h"] = "3600s",
        ["now-3h"] = "10800s",
        ["now-6h"] = "21600s",
        ["now-12h"] = "43200s",
        ["now-24h"] = "86400s",
        ["now-1d"] = "86400s",
        ["now-7d"] = "604800s",
        ["now-30d"] = "2592000s"
    };

    /// <summary>
    /// Unit for widgets typed <c>common.Unit</c> — line, bar and pie charts. That enum has no
    /// neutral numeric member, so an unrecognised unit stays <c>UNIT_UNSPECIFIED</c>, which those
    /// widgets accept.
    /// </summary>
    public static string MapUnit(string grafanaUnit) =>
        UnitMapping.TryGetValue(grafanaUnit, out var value) ? value : "UNIT_UNSPECIFIED";

    /// <summary>
    /// Unit for the gauge widget, typed <c>Gauge.Unit</c>. Gauges reject <c>UNIT_UNSPECIFIED</c>
    /// outright ("gauge unit must be specified"), so this never returns it: a unit that carries
    /// custom text becomes <c>UNIT_CUSTOM</c>, which is the only setting under which
    /// <see cref="GetCustomUnit"/>'s value renders at all, and anything else falls back to
    /// <c>UNIT_NUMBER</c>.
    /// </summary>
    /// <remarks>
    /// <c>UNIT_NUMBER</c> exists only in <c>Gauge.Unit</c>, so this must not feed a common.Unit
    /// widget — those call <see cref="MapUnit"/>.
    /// </remarks>
    public static string MapUnitForGauge(string grafanaUnit)
    {
        if (GaugeUnitOverrides.TryGetValue(grafanaUnit, out var overrideValue))
            return overrideValue;

        if (!string.IsNullOrEmpty(GetCustomUnit(grafanaUnit)))
            return "UNIT_CUSTOM";

        var mapped = MapUnit(grafanaUnit);
        return mapped == "UNIT_UNSPECIFIED" ? "UNIT_NUMBER" : mapped;
    }

    public static string GetCustomUnit(string grafanaUnit) =>
        CustomUnitMapping.TryGetValue(grafanaUnit, out var value) ? value : string.Empty;

    /// <summary>
    /// Type of a Grafana target's datasource, or <c>null</c> when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// <c>datasource</c> is polymorphic. schemaVersion 33 and earlier store a bare name or uid
    /// string such as <c>"$datasource"</c>, and some dashboards store JSON null. Indexing either
    /// with a string key throws <c>Cannot access child value on Newtonsoft.Json.Linq.JValue</c>,
    /// and <c>?.</c> does not save the null case because JSON null surfaces as a
    /// <see cref="JValue"/> rather than a C# null. The legacy string form names a datasource, never
    /// a type, so there is nothing to recover from it — callers treat null as "not Elasticsearch,
    /// not Loki" and fall through to their existing default.
    /// </remarks>
    public static string? DatasourceType(JToken? target) =>
        ((target as JObject)?["datasource"] as JObject)?["type"]?.ToString();

    public static string MapTimeFrame(string? grafanaFrom) =>
        grafanaFrom != null && TimeFrameMapping.TryGetValue(grafanaFrom, out var value) ? value : "3600s";

    /// <summary>
    /// Grafana built-in variable names that should not be wrapped in braces.
    /// These are replaced with literal values in CleanQuery before normalization.
    /// </summary>
    private static readonly HashSet<string> GrafanaBuiltInVariables = new(StringComparer.Ordinal)
    {
        "__rate_interval",
        "__auto_interval_interval",
        "__auto_interval",
        "__range",
        "__from",
        "__to",
        "interval",
        "quantile_stat"
    };

    /// <summary>
    /// Normalizes variable placeholders in query strings: $identifier → ${identifier}.
    /// Already braced ${identifier} is left unchanged. Grafana built-ins are skipped.
    /// </summary>
    public static string NormalizeVariablePlaceholders(string query) =>
        BraceUnbracedReferences(query, GrafanaBuiltInVariables.Contains);

    private static string BraceUnbracedReferences(string text, Func<string, bool> skip)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        return Regex.Replace(text, @"\$([a-zA-Z_][a-zA-Z0-9_]*)", match =>
        {
            var name = match.Groups[1].Value;
            return skip(name) ? match.Value : $"${{{name}}}";
        });
    }

    /// <summary>
    /// Variable names that must stay literal in a name, as opposed to in a query.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="GrafanaBuiltInVariables"/> on purpose. The <c>__</c>-prefixed names
    /// are Coralogix's own predefined variables, which it documents as unsupported in a name, and
    /// <c>quantile_stat</c> is deliberately dropped by the variable converter — bracing either
    /// would produce a reference that resolves to nothing, and for the dropped one a junk
    /// placeholder variable as well.
    ///
    /// <c>interval</c> is excluded from this set even though it is skipped in queries: the
    /// converter always emits a real variable of that name into <c>variablesV2</c>, so
    /// <c>${interval}</c> in a title resolves like any other user variable and renders the selected
    /// step. 20 titles across the 640-dashboard corpus depend on that.
    /// </remarks>
    private static bool IsLiteralOnlyInNames(string name) =>
        name.StartsWith("__", StringComparison.Ordinal) || name == "quantile_stat";

    /// <summary>
    /// Grafana's variable format modifiers, such as <c>${metric:text}</c> or <c>${pod:csv}</c>.
    /// </summary>
    private static readonly Regex VariableFormatModifier = new(
        @"\$\{(?<name>[a-zA-Z_][a-zA-Z0-9_]*):[^}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes variable placeholders in a name — a widget title, section name, description or
    /// markdown body. Coralogix interpolates <c>${name}</c> in those fields, so an unbraced Grafana
    /// <c>$name</c> renders as literal text instead of a value.
    /// </summary>
    /// <remarks>
    /// Format modifiers are dropped, since Coralogix has no equivalent syntax and treats the
    /// leftover <c>:text</c> as unfinished, falling back to showing the raw template. Stripping has
    /// to happen before <see cref="NormalizeVariablePlaceholders"/>, which only matches the unbraced
    /// form and would therefore walk straight past an already-braced modifier.
    ///
    /// The names left literal are <see cref="IsLiteralOnlyInNames"/>, not the wider query skip set:
    /// a reference that cannot resolve is worse braced than raw, but <c>interval</c> does resolve
    /// here because the converter emits a variable of that name.
    /// </remarks>
    public static string NormalizeNamePlaceholders(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name ?? string.Empty;

        var withoutModifiers = VariableFormatModifier.Replace(name, match =>
        {
            var variableName = match.Groups["name"].Value;
            return IsLiteralOnlyInNames(variableName) ? match.Value : $"${{{variableName}}}";
        });

        return BraceUnbracedReferences(withoutModifiers, IsLiteralOnlyInNames);
    }

    /// <summary>
    /// Normalizes Lucene queries for Coralogix logs paths:
    /// 1) normalizes Grafana variable placeholders ($x -> ${x})
    /// 2) strips terminal .keyword from field names in predicate keys (field.keyword:value -> field:value)
    /// Quoted predicate values are left unchanged.
    /// </summary>
    public static string NormalizeLuceneQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query ?? string.Empty;

        var normalizedVariables = NormalizeVariablePlaceholders(query);
        return StripKeywordSuffixFromLuceneFieldNames(normalizedVariables);
    }

    private static string StripKeywordSuffixFromLuceneFieldNames(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        const string keywordSuffix = ".keyword";
        var replacements = new List<int>();
        var inQuotes = false;

        for (var i = 0; i < query.Length; i++)
        {
            if (query[i] == '"' && !IsEscaped(query, i))
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes || query[i] != ':')
                continue;

            var fieldEnd = i - 1;
            while (fieldEnd >= 0 && char.IsWhiteSpace(query[fieldEnd]))
                fieldEnd--;

            if (fieldEnd < 0)
                continue;

            var fieldStart = fieldEnd;
            while (fieldStart >= 0 && IsLuceneFieldCharacter(query[fieldStart]))
                fieldStart--;
            fieldStart++;

            if (fieldStart > fieldEnd)
                continue;

            var field = query[fieldStart..(fieldEnd + 1)];
            if (field.Length <= keywordSuffix.Length || !field.EndsWith(keywordSuffix, StringComparison.OrdinalIgnoreCase))
                continue;

            replacements.Add(fieldEnd - keywordSuffix.Length + 1);
        }

        if (replacements.Count == 0)
            return query;

        var builder = new System.Text.StringBuilder(query);
        for (var i = replacements.Count - 1; i >= 0; i--)
            builder.Remove(replacements[i], keywordSuffix.Length);

        return builder.ToString();
    }

    private static bool IsLuceneFieldCharacter(char c)
    {
        return char.IsLetterOrDigit(c) || c is '_' or '.' or '@' or '$';
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashCount++;

        return slashCount % 2 == 1;
    }

    public static string CleanQuery(string query, ISet<string> discoveredMetrics)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "up";
        }

        var metricMatch = Regex.Match(query, @"([a-zA-Z_:][a-zA-Z0-9_:]*)\{");
        if (metricMatch.Success)
        {
            discoveredMetrics.Add(metricMatch.Groups[1].Value);
        }

        query = query
            .Replace("$__rate_interval", "5m", StringComparison.Ordinal)
            .Replace("$__auto_interval_interval", "5m", StringComparison.Ordinal)
            .Replace("$__auto_interval", "5m", StringComparison.Ordinal)
            .Replace("$__range", "5m", StringComparison.Ordinal)
            .Replace("$__from", "now-1h", StringComparison.Ordinal)
            .Replace("$__to", "now", StringComparison.Ordinal)
            .Replace("$interval", "5m", StringComparison.Ordinal)
            .Replace("$quantile_stat", "p95", StringComparison.Ordinal)
            .Replace("${quantile_stat}", "p95", StringComparison.Ordinal);

        query = Regex.Replace(query, "testid=~\"\\$testid\"", "testid=~${testid}");
        query = Regex.Replace(query, "testid=~\"\\$\\{testid\\}\"", "testid=~${testid}");
        query = Regex.Replace(query, @"\[5m\]|\[1m\]|\[15m\]", "[${interval}]");

        query = NormalizeVariablePlaceholders(query);
        query = Regex.Replace(query, "=~\"\\$\\{([a-zA-Z_][a-zA-Z0-9_]*)\\}\"", "=~${$1}");
        query = Regex.Replace(query, "=~\"\\$([a-zA-Z_][a-zA-Z0-9_]*)\"", "=~${$1}");
        query = Regex.Replace(query, "!~\"\\$\\{([a-zA-Z_][a-zA-Z0-9_]*)\\}\"", "!~${$1}");
        query = Regex.Replace(query, "!~\"\\$([a-zA-Z_][a-zA-Z0-9_]*)\"", "!~${$1}");

        return query;
    }

    public static string CleanHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        text = Regex.Replace(
            text,
            "<a\\s+[^>]*href=[\"']([^\"']+)[\"'][^>]*>([^<]+)</a>",
            "[$2]($1)",
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    public static string DeriveSeriesNameFromQuery(string query, string refId)
    {
        var k6Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["k6_vus"] = "VUs",
            ["k6_vus_max"] = "Max VUs",
            ["k6_http_reqs_total"] = "HTTP Requests",
            ["k6_http_req_duration"] = "HTTP Duration",
            ["k6_http_req_duration_p95"] = "HTTP Duration P95",
            ["k6_http_req_duration_p99"] = "HTTP Duration P99",
            ["k6_http_req_duration_avg"] = "HTTP Duration Avg",
            ["k6_http_req_duration_min"] = "HTTP Duration Min",
            ["k6_http_req_duration_max"] = "HTTP Duration Max",
            ["k6_http_req_failed"] = "HTTP Failed",
            ["k6_data_sent"] = "Data Sent",
            ["k6_data_received"] = "Data Received",
            ["k6_iteration_duration"] = "Iteration Duration",
            ["k6_iterations"] = "Iterations"
        };

        foreach (var pair in k6Names)
        {
            if (!query.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Contains("irate(", StringComparison.OrdinalIgnoreCase)
                || query.Contains("rate(", StringComparison.OrdinalIgnoreCase))
            {
                return $"{pair.Value}/s";
            }

            if (query.Contains("expected_response=\"false\"", StringComparison.OrdinalIgnoreCase))
            {
                return $"{pair.Value} (Errors)";
            }

            if (query.Contains("expected_response=\"true\"", StringComparison.OrdinalIgnoreCase))
            {
                return $"{pair.Value} (Success)";
            }

            return pair.Value;
        }

        var metricMatch = Regex.Match(query, @"(\w+)\{");
        if (metricMatch.Success)
        {
            var metricName = metricMatch.Groups[1].Value;
            var title = string.Join(" ", metricName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

            if (query.Contains("irate(", StringComparison.OrdinalIgnoreCase)
                || query.Contains("rate(", StringComparison.OrdinalIgnoreCase))
            {
                return $"{title}/s";
            }

            return title;
        }

        return $"Series {refId}";
    }
}
