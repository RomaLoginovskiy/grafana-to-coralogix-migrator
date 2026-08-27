using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// A multi-select Grafana variable stores current.value/current.text as arrays.
/// These used to throw inside ConvertVariable, get swallowed by the catch in
/// ConvertVariables, and drop the variable with no diagnostic.
/// </summary>
public class VariableMultiSelectCurrentTests
{
    private static JArray Convert(params JObject[] variables)
    {
        var sut = new VariableConverter(NullLogger.Instance);
        return sut.ConvertVariables(new JArray(variables.Cast<object>().ToArray()), new HashSet<string>());
    }

    private static JObject? Find(JArray converted, string name) =>
        converted.Children<JObject>().FirstOrDefault(v =>
            string.Equals(v.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase));

    private static JObject EsTermsVariable(string name, JToken current) => new()
    {
        ["name"] = name,
        ["type"] = "query",
        ["multi"] = true,
        ["includeAll"] = true,
        ["query"] = "{\"find\": \"terms\", \"field\": \"host.keyword\"}",
        ["current"] = current
    };

    private static JObject LabelValuesVariable(string name, JToken current) => new()
    {
        ["name"] = name,
        ["type"] = "query",
        ["multi"] = true,
        ["query"] = "label_values(up, instance)",
        ["current"] = current
    };

    [Fact]
    public void ElasticsearchTerms_WithArrayValuedCurrent_IsConverted()
    {
        var current = new JObject
        {
            ["text"] = new JArray("All"),
            ["value"] = new JArray("$__all")
        };

        var converted = Convert(EsTermsVariable("host", current));

        var variable = Find(converted, "host");
        Assert.NotNull(variable);
        Assert.NotNull(variable["source"]?["query"]?["logsQuery"]);
    }

    [Fact]
    public void LabelValues_WithArrayValuedCurrent_IsConverted()
    {
        var current = new JObject
        {
            ["text"] = new JArray("host-a", "host-b"),
            ["value"] = new JArray("host-a", "host-b")
        };

        var converted = Convert(LabelValuesVariable("instance", current));

        var variable = Find(converted, "instance");
        Assert.NotNull(variable);
        var labelValue = variable["source"]?["query"]?["metricsQuery"]?["type"]?["labelValue"];
        Assert.Equal("up", labelValue?["metricName"]?.Value<string>("stringValue"));
        Assert.Equal("instance", labelValue?["labelName"]?.Value<string>("stringValue"));
    }

    [Fact]
    public void ScalarValuedCurrent_StillConverts()
    {
        var current = new JObject { ["text"] = "host-a", ["value"] = "host-a" };

        var converted = Convert(LabelValuesVariable("instance", current));

        Assert.NotNull(Find(converted, "instance"));
    }

    [Fact]
    public void ArrayValuedCurrent_SelectsMultiStringValue()
    {
        // An array current means multi-select, which must not collapse to a single value.
        var current = new JObject
        {
            ["text"] = new JArray("host-a", "host-b"),
            ["value"] = new JArray("host-a", "host-b")
        };

        var variable = Find(Convert(LabelValuesVariable("instance", current)), "instance");

        Assert.NotNull(variable!["value"]?["multiString"]);
        Assert.Null(variable["value"]?["singleString"]);
    }

    [Fact]
    public void EveryVariableOnAMultiSelectDashboard_Survives()
    {
        // Mirrors a real dashboard: five Elasticsearch terms variables, all multi-select.
        var current = new JObject { ["text"] = new JArray("All"), ["value"] = new JArray("$__all") };
        var names = new[] { "host", "cluster", "node", "region", "tier" };

        var converted = Convert(names.Select(n => EsTermsVariable(n, current)).ToArray());

        foreach (var name in names)
            Assert.NotNull(Find(converted, name));
    }

    [Fact]
    public void IntervalVariable_WithArrayValuedCurrent_DoesNotThrow()
    {
        var variable = new JObject
        {
            ["name"] = "step",
            ["type"] = "interval",
            ["current"] = new JObject { ["value"] = new JArray("10m") }
        };

        Assert.NotNull(Find(Convert(variable), "step"));
    }
}
