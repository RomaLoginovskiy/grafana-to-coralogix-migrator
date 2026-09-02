using GrafanaToCx.Core.Converter.PanelConverters;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.Semantics;

public interface IGrafanaSemanticIrBuilder
{
    IReadOnlyList<SemanticQueryIr> BuildFromTargets(IEnumerable<JObject> targets);
}

public sealed class GrafanaSemanticIrBuilder : IGrafanaSemanticIrBuilder
{
    public IReadOnlyList<SemanticQueryIr> BuildFromTargets(IEnumerable<JObject> targets)
    {
        var indexedTargets = targets
            .Select((target, index) => (target, index))
            .ToList();

        var result = new List<SemanticQueryIr>(indexedTargets.Count);
        foreach (var (target, index) in indexedTargets)
        {
            var refId = target.Value<string>("refId") ?? $"T{index}";
            var sourceFamily = ResolveSourceFamily(target);
            var operations = BuildOperations(target);
            result.Add(new SemanticQueryIr(refId, sourceFamily, operations, (JObject)target.DeepClone()));
        }

        return result;
    }

    private static string ResolveSourceFamily(JObject target)
    {
        var dsType = QueryHelpers.DatasourceType(target);
        if (string.Equals(dsType, "prometheus", StringComparison.OrdinalIgnoreCase))
            return "metrics";
        if (string.Equals(dsType, "loki", StringComparison.OrdinalIgnoreCase))
            return "logs";
        if (string.Equals(dsType, "elasticsearch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dsType, "opensearch", StringComparison.OrdinalIgnoreCase))
            return "logs";

        if (target["expr"] is JValue)
            return "metrics";
        if (target["query"] is JValue || target["bucketAggs"] is JArray)
            return "logs";

        return "unknown";
    }

    private static IReadOnlyList<SemanticOperationNode> BuildOperations(JObject target)
    {
        var operations = new List<SemanticOperationNode>();

        var query = target.Value<string>("query");
        if (!string.IsNullOrWhiteSpace(query))
            operations.Add(new FilterOperationNode(query.Trim()));

        var expr = target.Value<string>("expr");
        if (!string.IsNullOrWhiteSpace(expr))
            operations.Add(new FilterOperationNode(expr.Trim()));

        var bucketAggs = target["bucketAggs"] as JArray ?? [];
        var groupFields = bucketAggs
            .Children<JObject>()
            .Where(b => string.Equals(b.Value<string>("type"), "terms", StringComparison.OrdinalIgnoreCase))
            .Select(b => CxFieldHelper.StripLogsFieldSuffixes(b.Value<string>("field") ?? string.Empty))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (groupFields.Count > 0)
            operations.Add(new GroupOperationNode(groupFields));

        var histogram = bucketAggs
            .Children<JObject>()
            .FirstOrDefault(b => string.Equals(b.Value<string>("type"), "date_histogram", StringComparison.OrdinalIgnoreCase));
        var interval = histogram?["settings"]?["interval"]?.ToString();
        if (!string.IsNullOrWhiteSpace(interval))
            operations.Add(new TimeBucketOperationNode(interval));

        var metric = (target["metrics"] as JArray)?.Children<JObject>().FirstOrDefault();
        if (metric != null)
        {
            var type = metric.Value<string>("type") ?? "count";
            var field = CxFieldHelper.StripLogsFieldSuffixes(metric.Value<string>("field") ?? string.Empty);
            var unit = metric["meta"]?["unit"]?.ToString();
            operations.Add(new ReduceOperationNode(type, string.IsNullOrWhiteSpace(field) ? null : field, unit));

            var size = metric["settings"]?["size"]?.Value<int?>();
            if (size is > 0)
                operations.Add(new LimitOperationNode(size.Value));
        }

        return operations;
    }
}
