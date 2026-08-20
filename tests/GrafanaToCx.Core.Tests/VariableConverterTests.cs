using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class VariableConverterTests
{
    private static GrafanaToCxConverter CreateConverter() =>
        new(NullLogger<GrafanaToCxConverter>.Instance);

    private static string BuildDashboardJsonWithVariables(params JObject[] variables) =>
        new JObject
        {
            ["dashboard"] = new JObject
            {
                ["title"] = "Variable Test Dashboard",
                ["panels"] = new JArray(),
                ["templating"] = new JObject
                {
                    ["list"] = new JArray(variables)
                }
            }
        }.ToString();

    private static JObject? GetVariable(JObject convertedDashboard, string variableName)
    {
        var variables = convertedDashboard["variablesV2"] as JArray;
        return variables?
            .Children<JObject>()
            .FirstOrDefault(v => string.Equals(v.Value<string>("name"), variableName, StringComparison.Ordinal));
    }

    [Fact]
    public void ConvertToJObject_QueryVariableWithoutLabelValues_UsesOptionsFallback()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "instanceUrl",
            ["type"] = "query",
            ["label"] = "Instance URL",
            ["query"] = "instances_for_service",
            ["current"] = new JObject
            {
                ["text"] = "https://a.example",
                ["value"] = "https://a.example"
            },
            ["options"] = new JArray
            {
                new JObject { ["text"] = "https://a.example", ["value"] = "https://a.example", ["selected"] = true },
                new JObject { ["text"] = "https://b.example", ["value"] = "https://b.example", ["selected"] = false }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "instanceUrl");

        Assert.NotNull(convertedVariable);
        Assert.Equal("VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE", convertedVariable!["displayType"]?.ToString());
        var staticValues = convertedVariable["source"]?["static"]?["values"] as JArray;
        Assert.NotNull(staticValues);
        Assert.Equal(2, staticValues!.Count);
        Assert.Equal("https://a.example", staticValues[0]?["value"]?.ToString());
        Assert.Equal("https://b.example", staticValues[1]?["value"]?.ToString());
    }

    [Fact]
    public void ConvertToJObject_QueryVariableWithoutOptions_UsesCurrentValueFallback()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "instanceUrl",
            ["type"] = "query",
            ["query"] = "some_backend_query",
            ["current"] = new JObject
            {
                ["text"] = "https://prod.example",
                ["value"] = "https://prod.example"
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "instanceUrl");

        Assert.NotNull(convertedVariable);
        var staticValues = convertedVariable!["source"]?["static"]?["values"] as JArray;
        Assert.NotNull(staticValues);
        Assert.Single(staticValues!);
        Assert.Equal("https://prod.example", staticValues[0]?["value"]?.ToString());
    }

    [Fact]
    public void ConvertToJObject_QueryVariableSimpleCsvQuery_UsesQueryValuesFallback()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "environment",
            ["type"] = "query",
            ["query"] = "dev, staging , prod"
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "environment");

        Assert.NotNull(convertedVariable);
        var staticValues = convertedVariable!["source"]?["static"]?["values"] as JArray;
        Assert.NotNull(staticValues);
        Assert.Equal(3, staticValues!.Count);
        Assert.Equal("dev", staticValues[0]?["value"]?.ToString());
        Assert.Equal("staging", staticValues[1]?["value"]?.ToString());
        Assert.Equal("prod", staticValues[2]?["value"]?.ToString());
    }

    [Fact]
    public void ConvertToJObject_QueryVariableWithCurrentArray_UsesMultiAllValue()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "region",
            ["type"] = "query",
            ["query"] = "regions_for_service",
            ["current"] = new JObject
            {
                ["text"] = new JArray("us-east-1", "eu-west-1"),
                ["value"] = new JArray("us-east-1", "eu-west-1")
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "region");

        Assert.NotNull(convertedVariable);
        var staticValues = convertedVariable!["source"]?["static"]?["values"] as JArray;
        Assert.NotNull(staticValues);
        Assert.Equal(2, staticValues!.Count);
        Assert.NotNull(convertedVariable["value"]?["multiString"]?["all"]);
    }

    [Fact]
    public void ConvertToJObject_QueryVariableWithMetricsFunction_RemainsSkipped()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "metric_selector",
            ["type"] = "query",
            ["query"] = "metrics(http_requests_total)"
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "metric_selector");

        Assert.Null(convertedVariable);
    }

    [Fact]
    public void ConvertToJObject_ElasticsearchTermsQueryObject_PreservesInstanceUrlVariable()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "instanceUrl",
            ["type"] = "query",
            ["datasource"] = new JObject
            {
                ["type"] = "elasticsearch",
                ["uid"] = "es-main"
            },
            ["query"] = new JObject
            {
                ["find"] = "terms",
                ["field"] = "instanceUrl.keyword"
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));
        var convertedVariable = GetVariable(result, "instanceUrl");

        Assert.NotNull(convertedVariable);
        Assert.Equal("VARIABLE_DISPLAY_TYPE_V2_LABEL_VALUE", convertedVariable!["displayType"]?.ToString());
        Assert.NotNull(convertedVariable["source"]?["query"]?["logsQuery"]?["type"]?["fieldValue"]);
    }

    // Multi-select variables store current.value as an array. Reading it as a string throws, and the
    // converter's catch turned that into a silently dropped variable — leaving every ${name} reference
    // in the dashboard's queries unresolved.

    [Fact]
    public void ConvertToJObject_MultiSelectLabelValuesVariable_IsNotDropped()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "job",
            ["type"] = "query",
            ["label"] = "Job",
            ["multi"] = true,
            ["query"] = new JObject
            {
                ["query"] = "label_values(job)",
                ["refId"] = "StandardVariableQuery"
            },
            ["current"] = new JObject
            {
                ["selected"] = true,
                ["text"] = new JArray { "ep-connect-general-01-metric" },
                ["value"] = new JArray { "ep-connect-general-01-metric" }
            }
        };

        var dashboard = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));

        var converted = GetVariable(dashboard, "job");
        Assert.NotNull(converted);
        Assert.Equal("Job", converted!["displayName"]?.ToString());
        Assert.Equal("job", converted["source"]?["query"]?["metricsQuery"]?["type"]?["labelValue"]?["labelName"]?["stringValue"]?.ToString());
        Assert.NotNull(converted["value"]?["multiString"]);
    }

    [Fact]
    public void ConvertToJObject_MultiSelectVariableWithTwoArgLabelValues_KeepsMetricAndLabel()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "topic",
            ["type"] = "query",
            ["multi"] = true,
            ["query"] = new JObject { ["query"] = "label_values(kafka_log_log_value, topic)" },
            ["current"] = new JObject
            {
                ["text"] = new JArray { "EVT_A", "EVT_B" },
                ["value"] = new JArray { "EVT_A", "EVT_B" }
            }
        };

        var dashboard = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));

        var labelValue = GetVariable(dashboard, "topic")?["source"]?["query"]?["metricsQuery"]?["type"]?["labelValue"];
        Assert.NotNull(labelValue);
        Assert.Equal("kafka_log_log_value", labelValue!["metricName"]?["stringValue"]?.ToString());
        Assert.Equal("topic", labelValue["labelName"]?["stringValue"]?.ToString());
    }

    [Fact]
    public void ConvertToJObject_SingleSelectLabelValuesVariable_StillUsesSingleStringValue()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "ConnectorGroup",
            ["type"] = "query",
            ["multi"] = false,
            ["query"] = new JObject { ["query"] = "label_values(kafka_connect_connector_status, connector)" },
            ["current"] = new JObject
            {
                ["selected"] = false,
                ["text"] = "REGION-A-SINK",
                ["value"] = "REGION-A-SINK"
            }
        };

        var dashboard = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));

        var value = GetVariable(dashboard, "ConnectorGroup")?["value"];
        Assert.NotNull(value);
        Assert.Null(value!["multiString"]);
        Assert.Equal("REGION-A-SINK", value["singleString"]?["value"]?["value"]?.ToString());
    }

    [Fact]
    public void ConvertToJObject_MultiSelectElasticsearchTermsVariable_IsNotDropped()
    {
        var converter = CreateConverter();
        var grafanaVariable = new JObject
        {
            ["name"] = "service",
            ["type"] = "query",
            ["multi"] = true,
            ["query"] = new JObject { ["find"] = "terms", ["field"] = "service.name.keyword" },
            ["current"] = new JObject
            {
                ["text"] = new JArray { "checkout" },
                ["value"] = new JArray { "checkout" }
            }
        };

        var dashboard = converter.ConvertToJObject(BuildDashboardJsonWithVariables(grafanaVariable));

        var converted = GetVariable(dashboard, "service");
        Assert.NotNull(converted);
        Assert.NotNull(converted!["source"]?["query"]?["logsQuery"]?["type"]?["fieldValue"]);
        Assert.NotNull(converted["value"]?["multiString"]);
    }

    /// <summary>
    /// Every variable referenced by a query must exist, or the Coralogix dashboard renders with an
    /// unresolved placeholder and no picker.
    /// </summary>
    [Fact]
    public void ConvertToJObject_AllSourceVariables_SurviveConversion()
    {
        var converter = CreateConverter();

        JObject MultiQueryVariable(string name, string query) => new()
        {
            ["name"] = name,
            ["type"] = "query",
            ["multi"] = true,
            ["query"] = new JObject { ["query"] = query },
            ["current"] = new JObject { ["text"] = new JArray { "a" }, ["value"] = new JArray { "a" } }
        };

        var dashboard = converter.ConvertToJObject(BuildDashboardJsonWithVariables(
            MultiQueryVariable("topic", "label_values(kafka_log_log_value, topic)"),
            MultiQueryVariable("job", "label_values(job)"),
            MultiQueryVariable("connector", "label_values(kafka_connect_connector_status, connector)"),
            new JObject { ["name"] = "Filters", ["type"] = "adhoc" }));

        foreach (var name in new[] { "topic", "job", "connector" })
            Assert.NotNull(GetVariable(dashboard, name));

        // adhoc has no Coralogix equivalent and is intentionally skipped.
        Assert.Null(GetVariable(dashboard, "Filters"));
    }
}
