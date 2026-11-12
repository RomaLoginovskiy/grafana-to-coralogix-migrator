using GrafanaToCx.Core.Assessment;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Which panel queries and variables the converter cannot translate, judged by datasource type
/// where there is one and by query shape where the datasource is only a name.
/// </summary>
public class ForeignQueriesTests
{
    [Theory]
    [InlineData("""{"refId":"A","expr":"up"}""", null)]
    [InlineData("""{"refId":"A","datasource":{"type":"prometheus"},"expr":"up"}""", null)]
    [InlineData("""{"refId":"A","bucketAggs":[],"metrics":[],"query":"*"}""", null)]
    [InlineData("""{"refId":"A","datasource":{"type":"loki"},"expr":"{app=\"x\"}"}""", null)]
    [InlineData("""{"refId":"A","measurement":"cpu","select":[]}""", "InfluxDB")]
    [InlineData("""{"refId":"A","rawQuery":true,"query":"SELECT mean(value) FROM cpu"}""", "InfluxDB")]
    [InlineData("""{"refId":"A","dsType":"influxdb"}""", "InfluxDB")]
    [InlineData("""{"refId":"A","datasource":{"type":"influxdb","uid":"x"}}""", "InfluxDB")]
    [InlineData("""{"refId":"A","namespace":"AWS/SQS","metricName":"NumberOfMessagesSent"}""", "CloudWatch")]
    [InlineData("""{"refId":"A","target":"carbon.agents.*.cpuUsage"}""", "Graphite")]
    [InlineData("""{"refId":"A","datasource":{"type":"__expr__"},"expression":"$A + $B"}""", "server-side expression")]
    [InlineData("""{"refId":"A","datasource":{"type":"mysql"},"rawSql":"select 1"}""", "mysql")]
    [InlineData("""{"refId":"A"}""", null)]
    public void ForeignKind_ClassifiesTarget(string targetJson, string? expected)
    {
        Assert.Equal(expected, ForeignQueries.ForeignKind(JObject.Parse(targetJson), panelDatasource: null));
    }

    [Fact]
    public void ForeignKind_FallsBackOnPanelDatasourceType()
    {
        var target = JObject.Parse("""{"refId":"A","alias":"x"}""");

        Assert.Equal("CloudWatch", ForeignQueries.ForeignKind(target, JObject.Parse("""{"type":"cloudwatch"}""")));
    }

    [Theory]
    [InlineData("""{"name":"h","type":"query","query":"SHOW TAG VALUES WITH KEY = \"host\""}""", "InfluxDB")]
    [InlineData("""{"name":"r","type":"query","query":"regions()"}""", "CloudWatch")]
    [InlineData("""{"name":"h","type":"query","query":"label_values(up, host)"}""", null)]
    [InlineData("""{"name":"h","type":"query","query":{"query":"SHOW TAG VALUES WITH KEY = \"host\"","refId":"x"}}""", "InfluxDB")]
    [InlineData("""{"name":"ds","type":"datasource","query":"influxdb"}""", null)]
    public void ForeignVariableKind_ClassifiesQueryVariables(string variableJson, string? expected)
    {
        Assert.Equal(expected, ForeignQueries.ForeignVariableKind(JObject.Parse(variableJson)));
    }

    [Fact]
    public void AssumeTranslated_KeepsRefIdLegendAndHiddenState_AndLeavesSourceUntouched()
    {
        var dashboard = JObject.Parse("""
            {"panels":[
              {"type":"row","panels":[
                {"type":"graph","datasource":"Influx","targets":[
                  {"refId":"A","measurement":"cpu","alias":"user"},
                  {"refId":"B","measurement":"cpu","hide":true},
                  {"refId":"C","expr":"up"}
                ]}
              ]}
            ]}
            """);
        var original = dashboard.DeepClone();

        var rewritten = ForeignQueries.AssumeTranslated(dashboard);

        var panel = (JObject)rewritten["panels"]![0]!["panels"]![0]!;
        var targets = (JArray)panel["targets"]!;
        Assert.Equal("user", targets[0]["legendFormat"]!.ToString());
        Assert.Equal("prometheus", targets[0]["datasource"]!["type"]!.ToString());
        Assert.True(targets[1].Value<bool>("hide"));
        Assert.NotEqual(targets[0]["expr"]!.ToString(), targets[1]["expr"]!.ToString());
        Assert.Equal("up", targets[2]["expr"]!.ToString());
        Assert.Equal("prometheus", panel["datasource"]!["type"]!.ToString());
        Assert.True(JToken.DeepEquals(original, dashboard));
    }
}
