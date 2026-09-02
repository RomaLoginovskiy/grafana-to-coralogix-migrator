using Newtonsoft.Json.Linq;
using GrafanaToCx.Core.Converter.Semantics;

namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Sanitizes dashboard payloads before upload to Coralogix API.
/// Removes unsupported properties and ensures required fields are present.
/// </summary>
public static class DashboardPayloadSanitizer
{
    private static readonly IQueryShapeValidator QueryShapeValidator = new QueryShapeValidator();
    private static readonly string[] PropertiesToRemove = { "stackedGroupName", "stackedGroupNameField" };
    private static readonly JArray DefaultDataTableColumns = new JArray
    {
        new JObject { ["field"] = "coralogix.text" }
    };

    /// <summary>
    /// Sanitizes a dashboard JObject by removing unsupported properties and ensuring dataTable columns exist.
    /// </summary>
    /// <param name="dashboard">The dashboard to sanitize (will be cloned if modified).</param>
    /// <returns>A sanitized dashboard (may be the same instance if no changes were needed).</returns>
    public static JObject Sanitize(JObject dashboard)
    {
        var cloned = (JObject)dashboard.DeepClone();
        RemovePropertiesRecursively(cloned, PropertiesToRemove);
        RemoveUnsupportedGroupNames(cloned);
        RemoveEmptyGroupingArrays(cloned);
        MigratePieChartDataPrimeQueries(cloned);
        EnsureDataTableColumns(cloned);
        EnsurePieChartLabelDefinition(cloned);
        ValidateQueryShapes(cloned);
        return cloned;
    }

    private static void ValidateQueryShapes(JObject dashboard)
    {
        var errors = QueryShapeValidator.ValidateDashboard(dashboard);
        if (errors.Count == 0)
            return;

        var summary = string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}"));
        throw new InvalidOperationException($"Invalid dashboard query shape. {summary}");
    }

    /// <summary>
    /// Coralogix upload API rejects pieChart.query.dataPrime. Preserve it by:
    /// 1) removing unsupported dataPrime from pieChart.query
    /// 2) injecting an adjacent markdown widget that keeps the DataPrime query text
    /// </summary>
    private static void MigratePieChartDataPrimeQueries(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var prop in obj.Properties())
            {
                if (prop.Name == "widgets" && prop.Value is JArray widgets)
                {
                    ProcessWidgetsArrayForDataPrime(widgets);
                    continue;
                }

                MigratePieChartDataPrimeQueries(prop.Value);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                MigratePieChartDataPrimeQueries(item);
            }
        }
    }

    private static void ProcessWidgetsArrayForDataPrime(JArray widgets)
    {
        for (var i = 0; i < widgets.Count; i++)
        {
            if (widgets[i] is not JObject widget)
                continue;

            if (!TryExtractAndRemoveDataPrime(widget, out var dataPrimeQuery))
                continue;

            var title = widget["title"]?.ToString() ?? "Widget";
            var markdownWidget = new JObject
            {
                ["id"] = new JObject { ["value"] = Guid.NewGuid().ToString() },
                ["title"] = $"{title} (DataPrime Query)",
                ["description"] = "Preserved from converter output: API does not accept pieChart.query.dataPrime directly.",
                ["definition"] = new JObject
                {
                    ["markdown"] = new JObject
                    {
                        ["markdownText"] =
                            "## DataPrime Query (Preserved)\n\n```dataprime\n" +
                            dataPrimeQuery +
                            "\n```"
                    }
                }
            };

            widgets.Insert(i + 1, markdownWidget);
            i++;
        }
    }

    private static bool TryExtractAndRemoveDataPrime(JObject widget, out string dataPrimeQuery)
    {
        dataPrimeQuery = string.Empty;

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        var dataPrime = query?["dataPrime"] as JObject;
        var value = dataPrime?["value"]?.ToString();

        if (string.IsNullOrWhiteSpace(value))
            return false;

        dataPrimeQuery = value;
        query!.Property("dataPrime")?.Remove();
        PreserveAsModernDataprimeWhenBranchless(query, value);
        return true;
    }

    /// <summary>
    /// Removing the legacy <c>dataPrime</c> branch can leave a query with no active branch at all, which the
    /// upload API and <see cref="QueryShapeValidator"/> both reject ("exactly one branch must be active").
    /// When that happens the legacy text is re-homed onto the supported <c>dataprime</c> branch so the widget
    /// keeps querying instead of shipping an empty query object. Queries that still carry a logs or metrics
    /// branch are left alone — adding a branch there would make two active at once.
    /// </summary>
    private static void PreserveAsModernDataprimeWhenBranchless(JObject query, string dataPrimeQuery)
    {
        var hasBranch = query["logs"] is JObject
            || query["metrics"] is JObject
            || query["dataprime"] is JObject;

        if (hasBranch)
            return;

        query["dataprime"] = new JObject
        {
            ["dataprimeQuery"] = new JObject { ["text"] = dataPrimeQuery },
            ["filters"] = new JArray()
        };
    }

    private static void RemovePropertiesRecursively(JToken token, string[] propertyNames)
    {
        if (token is JObject obj)
        {
            var propertiesToRemove = new List<JProperty>();
            foreach (var prop in obj.Properties())
            {
                if (Array.Exists(propertyNames, name => string.Equals(prop.Name, name, StringComparison.Ordinal)))
                {
                    propertiesToRemove.Add(prop);
                }
                else
                {
                    RemovePropertiesRecursively(prop.Value, propertyNames);
                }
            }

            foreach (var prop in propertiesToRemove)
            {
                prop.Remove();
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                RemovePropertiesRecursively(item, propertyNames);
            }
        }
    }

    private static void RemoveUnsupportedGroupNames(JToken token)
    {
        if (token is JObject obj)
        {
            RemoveGroupNamesFromQueryBranches(obj);
            foreach (var prop in obj.Properties())
                RemoveUnsupportedGroupNames(prop.Value);
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
                RemoveUnsupportedGroupNames(item);
        }
    }

    private static void RemoveGroupNamesFromQueryBranches(JObject obj)
    {
        if (obj["logs"] is JObject logs)
            logs.Property("groupNames")?.Remove();
    }

    private static void EnsureDataTableColumns(JToken token)
    {
        if (token is JObject obj)
        {
            if (obj["dataTable"] is JObject dataTable)
            {
                var columns = dataTable["columns"] as JArray;
                if (columns == null || columns.Count == 0)
                {
                    dataTable["columns"] = DefaultDataTableColumns.DeepClone() as JArray ?? DefaultDataTableColumns;
                }
            }

            foreach (var prop in obj.Properties())
            {
                EnsureDataTableColumns(prop.Value);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                EnsureDataTableColumns(item);
            }
        }
    }

    private static void EnsurePieChartLabelDefinition(JToken token)
    {
        if (token is JObject obj)
        {
            if (obj["pieChart"] is JObject pieChart)
            {
                if (pieChart["labelDefinition"] == null)
                {
                    pieChart["labelDefinition"] = new JObject
                    {
                        ["labelSource"] = "LABEL_SOURCE_INNER",
                        ["isVisible"] = true,
                        ["showName"] = true,
                        ["showValue"] = true,
                        ["showPercentage"] = true
                    };
                }
            }

            foreach (var prop in obj.Properties())
            {
                EnsurePieChartLabelDefinition(prop.Value);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                EnsurePieChartLabelDefinition(item);
            }
        }
    }

    private static void RemoveEmptyGroupingArrays(JToken token)
    {
        if (token is JObject obj)
        {
            RemoveEmptyArrayProperty(obj, "groupNamesFields");
            RemoveEmptyArrayProperty(obj, "groupBys");

            foreach (var prop in obj.Properties().ToList())
            {
                RemoveEmptyGroupingArrays(prop.Value);
            }
        }
        else if (token is JArray array)
        {
            foreach (var item in array)
            {
                RemoveEmptyGroupingArrays(item);
            }
        }
    }

    private static void RemoveEmptyArrayProperty(JObject obj, string propertyName)
    {
        if (obj[propertyName] is JArray arr && arr.Count == 0)
            obj.Property(propertyName)?.Remove();
    }

}
