using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.Migration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

public class UnitTest1
{
    [Fact]
    public void UnsupportedType_LeavesNotMigratedPlaceholder_ByDefault()
    {
        var converter = CreateConverter();

        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 1,
                ["title"] = "Status Map",
                ["type"] = "flant-statusmap-panel",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["expr"] = "up"
                    }
                }
            }));

        // The panel is still skipped, but a data panel leaves a marker so the gap is visible.
        var widget = Assert.Single(ExtractWidgets(result));
        Assert.Contains("Not migrated", widget["definition"]?["markdown"]?["markdownText"]?.ToString());
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "flant-statusmap-panel" && d.Outcome == "skipped");
    }

    [Fact]
    public void Timeseries_UsesAllowedLineChartFallback()
    {
        var converter = CreateConverter();

        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 2,
                ["title"] = "CPU",
                ["type"] = "timeseries",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["expr"] = "sum(rate(container_cpu_usage_seconds_total[5m]))"
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        Assert.NotNull(widgets[0]["definition"]?["lineChart"]);
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "timeseries" && d.Outcome == "fallback");
    }

    [Fact]
    public void UnsupportedType_CanUseForcedFallback_WhenConfigured()
    {
        var converter = CreateConverter();

        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 3,
                ["title"] = "Unknown Visual",
                ["type"] = "custom-panel-type",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["expr"] = "sum(up)"
                    }
                }
            }),
            new ConversionOptions
            {
                SkipUnsupportedPanels = false
            });

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        Assert.NotNull(widgets[0]["definition"]?["lineChart"]);
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "custom-panel-type" && d.Outcome == "fallback");
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "custom-panel-type" && d.Code == "DGR-SHF-001");
    }

    [Fact]
    public void StatusHistory_DateHistogramAndNumericMetric_UsesBarChartFallback()
    {
        var converter = CreateConverter();
        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 4,
                ["title"] = "Status History Aggregated",
                ["type"] = "status-history",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                        ["query"] = "service:payments",
                        ["bucketAggs"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "date_histogram",
                                ["field"] = "@timestamp",
                                ["settings"] = new JObject { ["interval"] = "1m" }
                            },
                            new JObject
                            {
                                ["type"] = "terms",
                                ["field"] = "status.keyword"
                            }
                        },
                        ["metrics"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "1",
                                ["type"] = "avg",
                                ["field"] = "duration.keyword"
                            }
                        }
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        Assert.NotNull(widgets[0]["definition"]?["barChart"]);
        Assert.Null(widgets[0]["definition"]?["lineChart"]);
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "status-history" && d.Code == "DGR-STH-001");
    }

    [Fact]
    public void StatusHistory_AmbiguousShape_UsesTableFallback()
    {
        var converter = CreateConverter();
        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 5,
                ["title"] = "Status History Ambiguous",
                ["type"] = "status-history",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["query"] = "service:payments"
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        Assert.NotNull(widgets[0]["definition"]?["dataTable"]);
        Assert.Null(widgets[0]["definition"]?["lineChart"]);
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "status-history" && d.Code == "DGR-STH-002");
    }

    [Fact]
    public void StatusHistory_MultiTarget_ReductionIsDeterministicAndDiagnosticBacked()
    {
        var converter = CreateConverter();
        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 6,
                ["title"] = "Status History Multi",
                ["type"] = "status-history",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                        ["query"] = "service:payments AND region:us",
                        ["bucketAggs"] = new JArray
                        {
                            new JObject { ["type"] = "terms", ["field"] = "status.keyword" }
                        },
                        ["metrics"] = new JArray
                        {
                            new JObject { ["id"] = "1", ["type"] = "count" }
                        }
                    },
                    new JObject
                    {
                        ["refId"] = "B",
                        ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                        ["query"] = "service:payments AND region:eu",
                        ["bucketAggs"] = new JArray
                        {
                            new JObject { ["type"] = "terms", ["field"] = "status.keyword" }
                        },
                        ["metrics"] = new JArray
                        {
                            new JObject { ["id"] = "1", ["type"] = "count" }
                        }
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        var logsQuery = widgets[0]["definition"]?["barChart"]?["query"]?["logs"] as JObject;
        Assert.NotNull(logsQuery);
        Assert.Equal(
            "service:payments AND region:us",
            logsQuery!["luceneQuery"]?["value"]?.ToString());

        var reductionDiagnostic = Assert.Single(
            converter.ConversionDiagnostics.Where(d => d.PanelType == "status-history" && d.Code == "DGR-MTG-001"));
        Assert.Contains("B", reductionDiagnostic.DroppedSemantics ?? []);
    }

    [Fact]
    public void StatusHistory_Allowlisted_UsesBarChartDataPrimeQuery()
    {
        var converter = CreateConverter("status-history");
        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 7,
                ["title"] = "Status History DataPrime",
                ["type"] = "status-history",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                        ["query"] = "service:payments",
                        ["bucketAggs"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "date_histogram",
                                ["field"] = "@timestamp",
                                ["settings"] = new JObject { ["interval"] = "1m" }
                            },
                            new JObject
                            {
                                ["type"] = "terms",
                                ["field"] = "status.keyword"
                            }
                        },
                        ["metrics"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "1",
                                ["type"] = "count"
                            }
                        }
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        var query = widgets[0]["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Contains(
            converter.ConversionDiagnostics,
            d => d.PanelType == "status-history" && d.Code == "DGR-STH-003");
    }

    [Fact]
    public void StatusHistory_Allowlisted_AutoInterval_UsesSuggestedIntervalPlaceholder()
    {
        var converter = CreateConverter("status-history");
        var result = converter.ConvertToJObject(
            BuildDashboardJson(new JObject
            {
                ["id"] = 8,
                ["title"] = "Status History Auto Interval",
                ["type"] = "status-history",
                ["targets"] = new JArray
                {
                    new JObject
                    {
                        ["refId"] = "A",
                        ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                        ["query"] = "service:payments",
                        ["bucketAggs"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "date_histogram",
                                ["field"] = "@timestamp",
                                ["settings"] = new JObject { ["interval"] = "auto" }
                            }
                        },
                        ["metrics"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "1",
                                ["type"] = "count"
                            }
                        }
                    }
                }
            }));

        var widgets = ExtractWidgets(result);
        Assert.Single(widgets);
        var dataPrime = widgets[0]["definition"]?["barChart"]?["query"]?["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("groupby $m.timestamp / $p.timeRange.suggestedInterval", dataPrime);
        Assert.DoesNotContain("groupby $m.timestamp / auto", dataPrime);
    }

    [Theory]
    [InlineData("stat", "gauge")]
    [InlineData("bargauge", "gauge")]
    [InlineData("table", "dataTable")]
    [InlineData("piechart", "pieChart")]
    [InlineData("barchart", "barChart")]
    [InlineData("timeseries", "lineChart")]
    public void Comparator_TypeMapping_CoversExtendedWidgetTypes(string grafanaType, string expectedCxType)
    {
        Assert.Equal(expectedCxType, DashboardComparator.GetExpectedCxType(grafanaType));
    }

    [Fact]
    public void Comparator_RecognizesPieAndBarQueries()
    {
        var pieWidget = new JObject
        {
            ["definition"] = new JObject
            {
                ["pieChart"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["metrics"] = new JObject
                        {
                            ["promqlQuery"] = new JObject { ["value"] = "sum(up)" }
                        }
                    }
                }
            }
        };

        var barWidget = new JObject
        {
            ["definition"] = new JObject
            {
                ["barChart"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["logs"] = new JObject
                        {
                            ["aggregations"] = new JArray { new JObject { ["count"] = new JObject() } }
                        }
                    }
                }
            }
        };

        Assert.True(DashboardComparator.HasValidQuery(pieWidget, "pieChart"));
        Assert.True(DashboardComparator.HasValidQuery(barWidget, "barChart"));
    }

    [Fact]
    public void Comparator_RecognizesDataPrimeQueries_ForPieAndBar()
    {
        var pieWidget = new JObject
        {
            ["definition"] = new JObject
            {
                ["pieChart"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["dataprime"] = new JObject
                        {
                            ["dataprimeQuery"] = new JObject
                            {
                                ["text"] = "source logs | groupby payload.isEmail agg count()"
                            }
                        }
                    }
                }
            }
        };

        var barWidget = new JObject
        {
            ["definition"] = new JObject
            {
                ["barChart"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["dataPrime"] = new JObject
                        {
                            ["value"] = "source logs | groupby level agg count()"
                        }
                    }
                }
            }
        };

        Assert.True(DashboardComparator.HasValidQuery(pieWidget, "pieChart"));
        Assert.True(DashboardComparator.HasValidQuery(barWidget, "barChart"));
    }

    private static GrafanaToCxConverter CreateConverter() =>
        new(NullLogger<GrafanaToCxConverter>.Instance);

    private static GrafanaToCxConverter CreateConverter(params string[] allowlistedWidgetTypes) =>
        new(NullLogger<GrafanaToCxConverter>.Instance, new MultiLuceneMergeOptions(allowlistedWidgetTypes));

    private static string BuildDashboardJson(JObject panel)
    {
        return new JObject
        {
            ["dashboard"] = new JObject
            {
                ["title"] = "Test Dashboard",
                ["panels"] = new JArray { panel }
            }
        }.ToString();
    }

    private static List<JObject> ExtractWidgets(JObject dashboard)
    {
        var sections = dashboard["layout"]?["sections"] as JArray ?? [];
        return sections
            .Children<JObject>()
            .SelectMany(section => (section["rows"] as JArray ?? []).Children<JObject>())
            .SelectMany(row => (row["widgets"] as JArray ?? []).Children<JObject>())
            .ToList();
    }
}
