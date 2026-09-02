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
    public void UngroupedMetricsPieChart_IsConsolidated_NotSkipped()
    {
        // Coralogix rejects empty group_names outright. This panel used to be dropped for that reason;
        // PromqlSeriesConsolidator now stamps a synthetic "series" label on with label_replace, which
        // satisfies the API without losing the panel. Skipping is no longer the right answer.
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

        var pie = Assert.Single(widgets, w => w["definition"]?["pieChart"] is not null);

        // The payload must still be valid: a non-empty grouping is what the API actually enforces.
        var groupNames = pie["definition"]?["pieChart"]?["query"]?["metrics"]?["groupNames"] as JArray;
        Assert.NotNull(groupNames);
        Assert.NotEmpty(groupNames!);
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

    // ── shapes that took whole dashboards down in the 2026-09-02 migration ────

    private static JObject SingleStatDashboard(JObject defaults, JArray? templating = null) => new()
    {
        ["title"] = "Board",
        ["templating"] = templating is null ? new JObject() : new JObject { ["list"] = templating },
        ["panels"] = new JArray(new JObject
        {
            ["id"] = 1,
            ["type"] = "stat",
            ["title"] = "Requests",
            ["fieldConfig"] = new JObject { ["defaults"] = defaults },
            ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
        })
    };

    private static JObject? FirstGauge(JObject converted) =>
        converted["layout"]?["sections"]?.Children<JObject>()
            .SelectMany(sec => sec["rows"]?.Children<JObject>() ?? [])
            .SelectMany(row => row["widgets"]?.Children<JObject>() ?? [])
            .Select(w => w["definition"]?["gauge"] as JObject)
            .FirstOrDefault(g => g is not null);

    /// <summary>
    /// Grafana's variable editor writes an empty <c>label</c> rather than omitting it, so the
    /// null-coalesce onto <c>name</c> never fired and the API rejected the dashboard with
    /// "variable display name cannot be empty".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VariableWithABlankLabel_FallsBackToItsName(string label)
    {
        var converted = Convert(SingleStatDashboard(
            new JObject(),
            new JArray(new JObject
            {
                ["name"] = "cluster",
                ["type"] = "custom",
                ["label"] = label,
                ["query"] = "a,b",
                ["options"] = new JArray(
                    new JObject { ["value"] = "a", ["selected"] = true },
                    new JObject { ["value"] = "b" }),
                ["current"] = new JObject { ["value"] = "a" }
            })));

        var cluster = (converted["variablesV2"] as JArray ?? [])
            .Children<JObject>()
            .Single(v => v.Value<string>("name") == "cluster");

        Assert.Equal("cluster", cluster.Value<string>("displayName"));
    }

    [Fact]
    public void EveryVariable_ShipsANonBlankDisplayName()
    {
        var converted = Convert(SingleStatDashboard(
            new JObject(),
            new JArray(new JObject
            {
                ["name"] = "cluster", ["type"] = "custom", ["label"] = "",
                ["query"] = "a,b",
                ["options"] = new JArray(
                    new JObject { ["value"] = "a", ["selected"] = true },
                    new JObject { ["value"] = "b" }),
                ["current"] = new JObject { ["value"] = "a" }
            })));

        foreach (var variable in (converted["variablesV2"] as JArray ?? []).Children<JObject>())
        {
            Assert.False(string.IsNullOrWhiteSpace(variable.Value<string>("displayName")));
        }
    }

    /// <summary>
    /// A gauge rejects UNIT_UNSPECIFIED outright ("gauge unit must be specified"), and the override
    /// table used to map these three straight back onto it.
    /// </summary>
    [Theory]
    [InlineData("reqps")]
    [InlineData("rps")]
    [InlineData("ops")]
    public void RateUnitsOnAGauge_BecomeCustomRatherThanUnspecified(string grafanaUnit)
    {
        var converted = Convert(SingleStatDashboard(new JObject { ["unit"] = grafanaUnit }));
        var gauge = FirstGauge(converted);

        Assert.NotNull(gauge);
        Assert.Equal("UNIT_CUSTOM", gauge!.Value<string>("unit"));
        // UNIT_CUSTOM is the only setting under which customUnit renders.
        Assert.False(string.IsNullOrEmpty(gauge.Value<string>("customUnit")));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("short")]
    [InlineData("something-grafana-invented")]
    public void AUnitlessGauge_IsANumberNotBytes(string grafanaUnit)
    {
        var gauge = FirstGauge(Convert(SingleStatDashboard(new JObject { ["unit"] = grafanaUnit })));

        Assert.NotNull(gauge);
        Assert.Equal("UNIT_NUMBER", gauge!.Value<string>("unit"));
    }

    /// <summary>
    /// A hand-edited Grafana threshold can carry <c>"value": ""</c>, which the API rejected with
    /// `invalid value for double field value: ""`.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void ANonNumericThresholdStep_DoesNotReachTheDoubleField(string badValue)
    {
        var gauge = FirstGauge(Convert(SingleStatDashboard(new JObject
        {
            ["thresholds"] = new JObject
            {
                ["steps"] = new JArray(
                    new JObject { ["color"] = "green", ["value"] = JValue.CreateNull() },
                    new JObject { ["color"] = "red", ["value"] = badValue })
            }
        })));

        Assert.NotNull(gauge);
        foreach (var step in (gauge!["thresholds"] as JArray ?? []).Children<JObject>())
        {
            var from = step["from"];
            Assert.NotNull(from);
            Assert.True(from!.Type is JTokenType.Integer or JTokenType.Float,
                $"threshold 'from' was {from.Type} ('{from}'), which the API rejects as a double");
        }
    }

    /// <summary>
    /// schemaVersion 33 and earlier store <c>datasource</c> as a bare string, and some dashboards
    /// store JSON null. Indexing either threw "Cannot access child value on ... JValue" and killed
    /// the whole conversion before it reached the API.
    /// </summary>
    [Theory]
    [InlineData("timeseries")]
    [InlineData("stat")]
    [InlineData("table")]
    [InlineData("piechart")]
    [InlineData("barchart")]
    [InlineData("logs")]
    public void ALegacyStringDatasource_DoesNotCrashTheConversion(string panelType)
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1,
                ["type"] = panelType,
                ["title"] = "Panel",
                ["datasource"] = "$datasource",
                ["targets"] = new JArray(
                    new JObject { ["refId"] = "A", ["expr"] = "up", ["datasource"] = "$datasource" },
                    new JObject { ["refId"] = "B", ["expr"] = "up", ["datasource"] = JValue.CreateNull() })
            })
        };

        var converted = Convert(dashboard);

        Assert.NotNull(converted["layout"]);
    }

    [Fact]
    public void AMultiTargetPanelWithNullDatasources_DoesNotCrashTheConversion()
    {
        var dashboard = new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1,
                ["type"] = "timeseries",
                ["title"] = "Panel",
                ["targets"] = new JArray(
                    new JObject { ["refId"] = "A", ["expr"] = "up", ["datasource"] = JValue.CreateNull() },
                    new JObject { ["refId"] = "B", ["expr"] = "rate(x[5m])", ["datasource"] = JValue.CreateNull() })
            })
        };

        Assert.NotNull(Convert(dashboard)["layout"]);
    }

    /// <summary>
    /// LEGEND_COLUMN_FIRST is not a member of the Coralogix LegendColumn enum, so a chart carrying
    /// a "first" legend calc was rejected outright.
    /// </summary>
    [Fact]
    public void UnmappableLegendCalcs_AreDroppedAndReported()
    {
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        var converted = converter.ConvertToJObject(new JObject
        {
            ["title"] = "Board",
            ["panels"] = new JArray(new JObject
            {
                ["id"] = 1, ["type"] = "timeseries", ["title"] = "CPU",
                ["options"] = new JObject
                {
                    ["legend"] = new JObject
                    {
                        ["showLegend"] = true,
                        ["calcs"] = new JArray("first", "firstNotNull", "lastNotNull", "max")
                    }
                },
                ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
            })
        }.ToString());

        var columns = converted["layout"]!["sections"]!.Children<JObject>()
            .SelectMany(sec => sec["rows"]!.Children<JObject>())
            .SelectMany(row => row["widgets"]!.Children<JObject>())
            .Select(w => w["definition"]?["lineChart"]?["legend"]?["columns"] as JArray)
            .First(c => c is not null)!
            .Select(c => c.ToString())
            .ToList();

        Assert.DoesNotContain("LEGEND_COLUMN_FIRST", columns);
        Assert.Contains("LEGEND_COLUMN_MAX", columns);
        // lastNotNull has an exact equivalent and used to be discarded in silence.
        Assert.Contains("LEGEND_COLUMN_LAST", columns);
        Assert.Equal(columns.Count, columns.Distinct().Count());

        Assert.Equal(2, converter.ConversionDiagnostics.Count(d => d.Code == "DGR-LGD-001"));
    }
}
