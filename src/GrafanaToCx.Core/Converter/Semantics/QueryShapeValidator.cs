using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.Semantics;

public sealed record QueryShapeValidationError(string Path, string Message);

public interface IQueryShapeValidator
{
    IReadOnlyList<QueryShapeValidationError> ValidateDashboard(JObject dashboard);
}

public sealed class QueryShapeValidator : IQueryShapeValidator
{
    public IReadOnlyList<QueryShapeValidationError> ValidateDashboard(JObject dashboard)
    {
        var errors = new List<QueryShapeValidationError>();
        ValidateWidgetQueries(dashboard, errors);
        return errors;
    }

    private static void ValidateWidgetQueries(JToken token, List<QueryShapeValidationError> errors, string path = "$")
    {
        if (token is JObject obj)
        {
            if (obj["pieChart"]?["query"] is JObject pieQuery)
                ValidateQueryObject(pieQuery, $"{path}.pieChart.query", errors, requireMetricsGroupNames: true);

            if (obj["barChart"]?["query"] is JObject barQuery)
                ValidateQueryObject(barQuery, $"{path}.barChart.query", errors);

            if (obj["gauge"]?["query"] is JObject gaugeQuery)
                ValidateQueryObject(gaugeQuery, $"{path}.gauge.query", errors);

            if (obj["dataTable"]?["query"] is JObject tableQuery)
                ValidateQueryObject(tableQuery, $"{path}.dataTable.query", errors);

            if (obj["lineChart"]?["queryDefinitions"] is JArray queryDefinitions)
            {
                var i = 0;
                foreach (var queryDef in queryDefinitions.Children<JObject>())
                {
                    if (queryDef["query"] is JObject lineQuery)
                        ValidateQueryObject(lineQuery, $"{path}.lineChart.queryDefinitions[{i}].query", errors);

                    i++;
                }
            }

            foreach (var property in obj.Properties())
                ValidateWidgetQueries(property.Value, errors, $"{path}.{property.Name}");
        }
        else if (token is JArray array)
        {
            for (var i = 0; i < array.Count; i++)
                ValidateWidgetQueries(array[i]!, errors, $"{path}[{i}]");
        }
    }

    /// <param name="requireMetricsGroupNames">
    /// Set for pie charts only. The API rejects a pie chart metrics query that carries no group names
    /// ("group_names cannot be empty") because a pie has nothing to slice by without one. Bar charts and
    /// gauges accept an empty grouping, so the rule is scoped rather than applied to every metrics branch.
    /// </param>
    private static void ValidateQueryObject(
        JObject query,
        string path,
        List<QueryShapeValidationError> errors,
        bool requireMetricsGroupNames = false)
    {
        var hasLogs = query["logs"] is JObject;
        var hasMetrics = query["metrics"] is JObject;
        var hasDataprime = query["dataprime"] is JObject;
        var hasLegacyDataPrime = query["dataPrime"] is JObject;

        if (hasLegacyDataPrime)
        {
            errors.Add(new QueryShapeValidationError(path, "Legacy dataPrime branch is not allowed; use dataprime."));
        }

        var activeCount = (hasLogs ? 1 : 0) + (hasMetrics ? 1 : 0) + (hasDataprime ? 1 : 0);
        if (activeCount != 1)
        {
            errors.Add(new QueryShapeValidationError(path, "Exactly one branch must be active: logs | metrics | dataprime."));
            return;
        }

        if (hasLogs && query["logs"] is JObject logs)
        {
            if (logs["filters"] is not JArray)
                errors.Add(new QueryShapeValidationError(path, "logs.filters must be an array."));

            if (logs["groupNames"] is not null)
                errors.Add(new QueryShapeValidationError(path, "logs.groupNames is unsupported in logs branch; use dataprime.groupNames."));
        }

        if (hasMetrics && query["metrics"] is JObject metrics)
        {
            var value = metrics["promqlQuery"]?["value"]?.ToString();
            if (string.IsNullOrWhiteSpace(value))
                errors.Add(new QueryShapeValidationError(path, "metrics.promqlQuery.value is required."));

            if (metrics["filters"] is not JArray)
                errors.Add(new QueryShapeValidationError(path, "metrics.filters must be an array."));

            if (requireMetricsGroupNames && metrics["groupNames"] is not JArray { Count: > 0 })
                errors.Add(new QueryShapeValidationError(path, "metrics.groupNames must be a non-empty array."));
        }

        if (hasDataprime && query["dataprime"] is JObject dataprime)
        {
            var text = dataprime["dataprimeQuery"]?["text"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
                errors.Add(new QueryShapeValidationError(path, "dataprime.dataprimeQuery.text is required."));

            if (dataprime["filters"] is not JArray)
                errors.Add(new QueryShapeValidationError(path, "dataprime.filters must be an array."));
        }
    }
}
