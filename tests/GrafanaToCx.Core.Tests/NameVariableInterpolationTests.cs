using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Coralogix interpolates <c>${name}</c> in a widget title, section header and description, so a
/// Grafana title has to reach it in that form. An unbraced <c>$name</c> is shown as literal text,
/// which is what dashboards were migrating with: tests/e2e/artifacts/K8S_Dashboard/comparison.json
/// carries panel names like "Memory Usage【$Node】" verbatim.
/// </summary>
/// <remarks>
/// Expected strings are written out in full rather than produced by calling the normalizer. A test
/// that recomputes the transformation it is checking passes whether or not the converter applies
/// it, which is how this repo previously shipped a green suite over a broken report path.
/// </remarks>
public class NameVariableInterpolationTests
{
    private static JObject Panel(int id, string title, string? description = null) => new()
    {
        ["id"] = id,
        ["type"] = "timeseries",
        ["title"] = title,
        ["description"] = description ?? string.Empty,
        ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
    };

    private static JObject Variable(string name) => new()
    {
        ["name"] = name,
        ["type"] = "custom",
        ["multi"] = false,
        ["includeAll"] = false,
        ["options"] = new JArray(new JObject { ["value"] = "a", ["text"] = "a" }),
        ["current"] = new JObject { ["value"] = "a", ["text"] = "a" }
    };

    private static JObject Convert(JArray panels, params string[] variableNames)
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = panels,
            ["templating"] = new JObject
            {
                ["list"] = new JArray(variableNames.Select(Variable).Cast<object>().ToArray())
            }
        };

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        return converter.ConvertToJObject(dashboard.ToString());
    }

    private static List<JObject> Widgets(JObject dashboard) =>
        (dashboard["layout"]?["sections"] as JArray ?? [])
        .Children<JObject>()
        .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
        .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
        .ToList();

    private static string? TitleOf(JObject dashboard) => Widgets(dashboard).Single().Value<string>("title");

    [Fact]
    public void BareReference_IsBraced()
    {
        var result = Convert(new JArray(Panel(1, "Duration (Latency) - p$percentile")), "percentile");
        Assert.Equal("Duration (Latency) - p${percentile}", TitleOf(result));
    }

    [Fact]
    public void BareReference_MidSentence_IsBraced()
    {
        var result = Convert(new JArray(Panel(1, "Pods for $deployment deployment")), "deployment");
        Assert.Equal("Pods for ${deployment} deployment", TitleOf(result));
    }

    [Fact]
    public void FormatModifier_IsStripped()
    {
        // Coralogix has no ${x:fmt} syntax and treats the leftover modifier as unfinished, falling
        // back to rendering the raw template.
        var result = Convert(new JArray(Panel(1, "Spans ${metric:text}")), "metric");
        Assert.Equal("Spans ${metric}", TitleOf(result));
    }

    [Fact]
    public void AlreadyBracedReference_IsUnchanged()
    {
        var result = Convert(new JArray(Panel(1, "Spans ${metric}")), "metric");
        Assert.Equal("Spans ${metric}", TitleOf(result));
    }

    [Fact]
    public void MultipleReferences_AreAllBraced()
    {
        var result = Convert(
            new JArray(Panel(1, "$NameSpace：Network Overview for $Node")), "NameSpace", "Node");
        Assert.Equal("${NameSpace}：Network Overview for ${Node}", TitleOf(result));
    }

    /// <remarks>
    /// Coralogix documents interval-based predefined variables as unsupported in a name, so there
    /// is no form of this that resolves. Left exactly as authored rather than rewritten into a
    /// reference that cannot work.
    /// </remarks>
    [Fact]
    public void GrafanaBuiltIn_IsLeftAlone()
    {
        var result = Convert(new JArray(Panel(1, "Rate over $__rate_interval")));
        Assert.Equal("Rate over $__rate_interval", TitleOf(result));
    }

    /// <remarks>
    /// <c>interval</c> is skipped when normalizing a query, but not a name: the converter always
    /// emits a variable called <c>interval</c> into variablesV2, so the braced form resolves and
    /// renders the selected step. 20 titles in the 640-dashboard corpus read "[$interval]".
    /// </remarks>
    [Fact]
    public void IntervalReference_IsBraced_AndResolvesToTheEmittedVariable()
    {
        var result = Convert(new JArray(Panel(1, "Uptime [$interval]")));

        Assert.Equal("Uptime [${interval}]", TitleOf(result));

        var names = (result["variablesV2"] as JArray ?? [])
            .Children<JObject>()
            .Select(v => v.Value<string>("name"))
            .ToList();
        Assert.Contains("interval", names);
    }

    /// <remarks>
    /// Unlike <c>interval</c>, this one is dropped by the variable converter, so bracing it would
    /// dangle and manufacture a ".*" placeholder variable.
    /// </remarks>
    [Fact]
    public void DeliberatelyDroppedVariable_IsLeftLiteral()
    {
        var result = Convert(new JArray(Panel(1, "Latency $quantile_stat")));
        Assert.Equal("Latency $quantile_stat", TitleOf(result));
    }

    [Fact]
    public void Description_IsNormalized()
    {
        var result = Convert(new JArray(Panel(1, "Latency", "Latency for $service")), "service");
        Assert.Equal("Latency for ${service}", Widgets(result).Single().Value<string>("description"));
    }

    [Fact]
    public void SectionName_FromRowTitle_IsNormalized()
    {
        var panels = new JArray(
            new JObject { ["id"] = 10, ["type"] = "row", ["title"] = "Nodes for $Node" },
            Panel(1, "Latency"));

        var result = Convert(panels, "Node");

        var section = (result["layout"]?["sections"] as JArray ?? []).Children<JObject>().Single();
        Assert.Equal("Nodes for ${Node}", section["options"]?["custom"]?.Value<string>("name"));
    }

    [Fact]
    public void MarkdownBody_IsNormalized()
    {
        var panels = new JArray(new JObject
        {
            ["id"] = 1,
            ["type"] = "text",
            ["title"] = "Notes",
            ["options"] = new JObject { ["content"] = "Showing $service" }
        });

        var result = Convert(panels, "service");

        var markdown = Widgets(result).Single()["definition"]?["markdown"];
        Assert.Equal("Showing ${service}", markdown?.Value<string>("markdownText"));
    }

    /// <remarks>
    /// The report exists so someone can find the panel in their Grafana export. Naming it
    /// "${Node}" would name a string that appears nowhere in that file.
    /// </remarks>
    [Fact]
    public void DiagnosticText_KeepsTheAuthoredTitle()
    {
        var panel = Panel(1, "Memory Usage【$Node】");
        panel["links"] = new JArray(new JObject { ["title"] = "runbook", ["url"] = "https://example.com" });

        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(panel),
            ["templating"] = new JObject { ["list"] = new JArray(Variable("Node")) }
        };

        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        converter.ConvertToJObject(dashboard.ToString());

        var linkLoss = Assert.Single(converter.DashboardDiagnostics.Where(d => d.ElementKind == "panelLink"));
        Assert.Equal("Memory Usage【$Node】", linkLoss.PanelTitle);
    }

    [Fact]
    public void BracedReferenceInATitle_DoesNotBecomeAPlaceholderVariable()
    {
        // A normalized reference to a variable that does exist must not trip the dangling-reference
        // placeholder, or every interpolated title would add a junk ".*" variable.
        var result = Convert(new JArray(Panel(1, "Latency for $service")), "service");

        var names = (result["variablesV2"] as JArray ?? [])
            .Children<JObject>()
            .Select(v => v.Value<string>("name"))
            .ToList();

        Assert.Single(names.Where(n => n == "service"));
        Assert.DoesNotContain(".*", names);
    }
}
