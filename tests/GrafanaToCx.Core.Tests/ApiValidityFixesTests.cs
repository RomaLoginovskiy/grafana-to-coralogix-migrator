using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Three shapes the Coralogix API rejects outright, each of which took a whole dashboard down.
/// </summary>
public class ApiValidityFixesTests
{
    private static JObject Convert(JObject dashboard)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        return converter.ConvertToJObject(dashboard.ToString());
    }

    private static (JObject converted, IReadOnlyList<DashboardConversionDiagnostic> diagnostics) Run(JObject dashboard)
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        return (converter.ConvertToJObject(dashboard.ToString()), converter.DashboardDiagnostics);
    }

    // ── trailing .* on a variable matcher ────────────────────────────────────

    [Fact]
    public void TrailingRegexSuffix_IsDropped_BecauseItCannotSurviveUnquoting()
    {
        // `label=~${var}.*` is a PromQL syntax error, so the suffix cannot simply be kept.
        var rewritten = PromqlVariableMatchers.Rewrite("""up{instance=~"${server}.*"}""", new HashSet<string>());

        Assert.Equal("up{instance=~${server}}", rewritten);
    }

    [Fact]
    public void DroppingTheSuffix_IsReported()
    {
        var (_, diagnostics) = Run(new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "timeseries", ["title"] = "CPU",
                ["targets"] = new JArray(new JObject
                    { ["refId"] = "A", ["expr"] = """up{instance=~"$server.*"}""" })
            }),
            ["templating"] = new JObject
            {
                ["list"] = new JArray(new JObject
                {
                    ["name"] = "server", ["type"] = "query",
                    ["query"] = "label_values(up, instance)",
                    ["current"] = new JObject { ["value"] = "a", ["text"] = "a" }
                })
            }
        });

        var reported = Assert.Single(diagnostics, d => d.ElementKind == "queryMatcher");
        Assert.Contains("exact match", reported.Reason);
    }

    // ── pie chart with nothing to group by ───────────────────────────────────

    [Fact]
    public void UngroupedMetricsPieChart_IsSkipped_NotEmittedInvalid()
    {
        // A pie over a scalar has one slice; Coralogix rejects empty group_names outright.
        var converted = Convert(new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "piechart", ["title"] = "Total",
                ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "sum(amount)" })
            })
        });

        var widgets = (converted["layout"]?["sections"] as JArray ?? [])
            .Children<JObject>()
            .SelectMany(s => (s["rows"] as JArray ?? []).Children<JObject>())
            .SelectMany(r => (r["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();

        Assert.DoesNotContain(widgets, w => w["definition"]?["pieChart"] is not null);
    }

    [Fact]
    public void GroupedMetricsPieChart_StillConverts()
    {
        var converted = Convert(new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "piechart", ["title"] = "By type",
                ["targets"] = new JArray(new JObject
                    { ["refId"] = "A", ["expr"] = "sum(amount) by (card_type)" })
            })
        });

        var groupNames = converted.Descendants().OfType<JObject>()
            .FirstOrDefault(o => o["groupNames"] is JArray)?["groupNames"] as JArray;

        Assert.Equal("card_type", groupNames?[0].Value<string>());
    }

    // ── dangling variable references ─────────────────────────────────────────

    private static JObject DashboardReferencingAnUnconvertibleVariable() => new()
    {
        ["title"] = "Board",
        ["panels"] = new JArray(new JObject
        {
            ["id"] = 1, ["type"] = "timeseries", ["title"] = "Queues",
            ["targets"] = new JArray(new JObject
                { ["refId"] = "A", ["expr"] = """rate(q{vhost=~"$vhost"}[5m])""" })
        }),
        ["templating"] = new JObject
        {
            ["list"] = new JArray(new JObject
            {
                // query_result with regex extraction: no rule for it, and no options to fall back on.
                ["name"] = "vhost", ["type"] = "query",
                ["query"] = "query_result(up)", ["regex"] = "/vhost=\"(?<value>[^\"]+)/g",
                ["options"] = new JArray(), ["current"] = new JObject()
            })
        }
    };

    [Fact]
    public void ReferenceToAnUnconvertibleVariable_GetsAPlaceholder()
    {
        // Without one the API rejects the dashboard and every widget on it is lost.
        var (converted, _) = Run(DashboardReferencingAnUnconvertibleVariable());

        var names = (converted["variablesV2"] as JArray ?? [])
            .Children<JObject>().Select(v => v.Value<string>("name")).ToList();

        Assert.Contains("vhost", names);
    }

    [Fact]
    public void ThePlaceholder_IsReportedSoItGetsPopulated()
    {
        var (_, diagnostics) = Run(DashboardReferencingAnUnconvertibleVariable());

        var reported = Assert.Single(
            diagnostics, d => d.ElementKind == "variable" && d.Outcome == "placeholder");
        Assert.Equal("vhost", reported.ElementName);
    }

    [Fact]
    public void GrafanaBuiltIns_DoNotGetPlaceholders()
    {
        // ${__interval} and friends are resolved by Coralogix itself.
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "timeseries", ["title"] = "CPU",
                ["targets"] = new JArray(new JObject
                    { ["refId"] = "A", ["expr"] = "rate(up[${__interval}])" })
            })
        };

        var (converted, _) = Run(dashboard);
        var names = (converted["variablesV2"] as JArray ?? [])
            .Children<JObject>().Select(v => v.Value<string>("name")).ToList();

        Assert.DoesNotContain("__interval", names);
    }

    [Fact]
    public void DashboardWithNoDanglingReferences_GainsNoPlaceholders()
    {
        var (converted, diagnostics) = Run(new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "timeseries", ["title"] = "CPU",
                ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
            })
        });

        // Only the synthetic interval variable the converter always adds.
        Assert.Single(converted["variablesV2"] as JArray ?? []);
        Assert.DoesNotContain(diagnostics, d => d.Outcome == "placeholder");
    }
}
