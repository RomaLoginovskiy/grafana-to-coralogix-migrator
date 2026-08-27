using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Coralogix substitutes ${var} itself and expands a multi-value variable into a regex
/// alternation, so a quoted placeholder is rejected outright by the API
/// ("null is not allowed here" / "multi value is not allowed here").
/// </summary>
public class PromqlVariableMatcherTests
{
    private static readonly HashSet<string> NoMulti = [];
    private static readonly HashSet<string> ServerIsMulti = ["server"];

    [Theory]
    [InlineData("""up{instance="${server}"}""", "up{instance=${server}}")]
    [InlineData("""up{instance!="${server}"}""", "up{instance!=${server}}")]
    [InlineData("""up{instance=~"${server}"}""", "up{instance=~${server}}")]
    [InlineData("""up{instance!~"${server}"}""", "up{instance!~${server}}")]
    public void QuotedPlaceholders_AreUnquoted(string input, string expected)
    {
        Assert.Equal(expected, PromqlVariableMatchers.Rewrite(input, NoMulti));
    }

    [Theory]
    [InlineData("""up{instance="${server}"}""", "up{instance=~${server}}")]
    [InlineData("""up{instance!="${server}"}""", "up{instance!~${server}}")]
    public void MultiValueVariables_UpgradeToRegexOperators(string input, string expected)
    {
        Assert.Equal(expected, PromqlVariableMatchers.Rewrite(input, ServerIsMulti));
    }

    [Fact]
    public void AlreadyUnquoted_IsLeftAlone()
    {
        const string query = "up{instance=${server}}";
        Assert.Equal(query, PromqlVariableMatchers.Rewrite(query, NoMulti));
    }

    [Fact]
    public void PlaceholderWithASuffixInsideQuotes_LosesTheSuffix()
    {
        // `label=~${var}.*` is a PromQL syntax error, so the suffix cannot be carried over.
        // The narrowing from prefix- to exact-match is reported by the converter.
        Assert.Equal(
            "up{instance=~${server}}",
            PromqlVariableMatchers.Rewrite("""up{instance=~"${server}.*"}""", NoMulti));
    }

    [Fact]
    public void LiteralMatchers_AreNotTouched()
    {
        const string query = """up{mode="idle", job="node"}""";
        Assert.Equal(query, PromqlVariableMatchers.Rewrite(query, NoMulti));
    }

    [Fact]
    public void SeveralMatchersInOneQuery_AreAllHandled()
    {
        var rewritten = PromqlVariableMatchers.Rewrite(
            """sum(rate(m{mode="idle", app="${server}", sub="${team}"}[5m]))""",
            ServerIsMulti);

        Assert.Contains("""mode="idle" """.Trim(), rewritten);
        Assert.Contains("app=~${server}", rewritten);
        Assert.Contains("sub=${team}", rewritten);
    }

    [Fact]
    public void IntervalPlaceholdersSurvive()
    {
        const string query = "rate(m{job=\"x\"}[${interval}])";
        Assert.Equal(query, PromqlVariableMatchers.Rewrite(query, NoMulti));
    }

    // ── end to end through the converter ─────────────────────────────────────

    private static JObject ConvertWithVariable(string promql, bool multi)
    {
        var panel = new JObject
        {
            ["id"] = 1,
            ["type"] = "timeseries",
            ["title"] = "CPU",
            ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = promql })
        };
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(panel),
            ["templating"] = new JObject
            {
                ["list"] = new JArray(new JObject
                {
                    ["name"] = "server",
                    ["type"] = "query",
                    ["query"] = "label_values(up, instance)",
                    ["multi"] = multi,
                    ["includeAll"] = multi,
                    ["current"] = new JObject { ["value"] = "host-a", ["text"] = "host-a" }
                })
            }
        };

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        return converter.ConvertToJObject(dashboard.ToString());
    }

    private static string PromqlOf(JObject dashboard) =>
        dashboard.Descendants()
            .OfType<JObject>()
            .Where(o => o["promqlQuery"] is JObject)
            .Select(o => o["promqlQuery"]!.Value<string>("value") ?? "")
            .First();

    [Fact]
    public void EndToEnd_SingleValueVariable_IsUnquoted()
    {
        var converted = ConvertWithVariable("""up{instance="$server"}""", multi: false);

        Assert.Contains("instance=${server}", PromqlOf(converted));
        Assert.DoesNotContain("\"${server}\"", PromqlOf(converted));
    }

    [Fact]
    public void EndToEnd_MultiValueVariable_UsesRegexOperator()
    {
        var converted = ConvertWithVariable("""up{instance="$server"}""", multi: true);

        Assert.Contains("instance=~${server}", PromqlOf(converted));
    }
}
