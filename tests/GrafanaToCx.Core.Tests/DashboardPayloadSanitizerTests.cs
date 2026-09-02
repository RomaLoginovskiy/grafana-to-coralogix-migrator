using GrafanaToCx.Core.ApiClient;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrafanaToCx.Core.Tests;

public class DashboardPayloadSanitizerTests
{
    [Fact]
    public void Sanitize_RemovesStackedGroupName_FromRoot()
    {
        var dashboard = new JObject
        {
            ["name"] = "Test Dashboard",
            ["stackedGroupName"] = "group1"
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        Assert.Null(result["stackedGroupName"]);
        Assert.Equal("Test Dashboard", result["name"]?.ToString());
    }

    [Fact]
    public void Sanitize_RemovesStackedGroupNameField_FromRoot()
    {
        var dashboard = new JObject
        {
            ["name"] = "Test Dashboard",
            ["stackedGroupNameField"] = "field1"
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        Assert.Null(result["stackedGroupNameField"]);
        Assert.Equal("Test Dashboard", result["name"]?.ToString());
    }

    [Fact]
    public void Sanitize_RemovesStackedGroupName_Recursively()
    {
        var dashboard = new JObject
        {
            ["name"] = "Test Dashboard",
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["id"] = "widget1",
                    ["stackedGroupName"] = "group1",
                    ["nested"] = new JObject
                    {
                        ["stackedGroupName"] = "group2"
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widget = result["widgets"]?[0] as JObject;
        Assert.NotNull(widget);
        Assert.Null(widget["stackedGroupName"]);
        Assert.Null(widget["nested"]?["stackedGroupName"]);
        Assert.Equal("widget1", widget["id"]?.ToString());
    }

    [Fact]
    public void Sanitize_EnsuresDataTableColumns_WhenMissing()
    {
        var dashboard = new JObject
        {
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["dataTable"] = new JObject
                        {
                            ["query"] = LogsQuery()
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var dataTable = result["widgets"]?[0]?["definition"]?["dataTable"] as JObject;
        Assert.NotNull(dataTable);
        var columns = dataTable["columns"] as JArray;
        Assert.NotNull(columns);
        Assert.Single(columns);
        Assert.Equal("coralogix.text", columns[0]?["field"]?.ToString());
    }

    [Fact]
    public void Sanitize_EnsuresDataTableColumns_WhenEmpty()
    {
        var dashboard = new JObject
        {
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["dataTable"] = new JObject
                        {
                            ["query"] = LogsQuery(),
                            ["columns"] = new JArray()
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var dataTable = result["widgets"]?[0]?["definition"]?["dataTable"] as JObject;
        Assert.NotNull(dataTable);
        var columns = dataTable["columns"] as JArray;
        Assert.NotNull(columns);
        Assert.Single(columns);
        Assert.Equal("coralogix.text", columns[0]?["field"]?.ToString());
    }

    [Fact]
    public void Sanitize_PreservesExistingDataTableColumns()
    {
        var dashboard = new JObject
        {
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["dataTable"] = new JObject
                        {
                            ["query"] = LogsQuery(),
                            ["columns"] = new JArray
                            {
                                new JObject { ["field"] = "custom.field" }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var dataTable = result["widgets"]?[0]?["definition"]?["dataTable"] as JObject;
        Assert.NotNull(dataTable);
        var columns = dataTable["columns"] as JArray;
        Assert.NotNull(columns);
        Assert.Single(columns);
        Assert.Equal("custom.field", columns[0]?["field"]?.ToString());
    }

    [Fact]
    public void Sanitize_HandlesMultipleDataTables()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["dataTable"] = new JObject
                                            {
                                                ["query"] = LogsQuery()
                                            }
                                        }
                                    },
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["dataTable"] = new JObject
                                            {
                                                ["query"] = MetricsQuery()
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Equal(2, widgets.Count);

        var dataTable1 = widgets[0]?["definition"]?["dataTable"] as JObject;
        var dataTable2 = widgets[1]?["definition"]?["dataTable"] as JObject;
        Assert.NotNull(dataTable1);
        Assert.NotNull(dataTable2);

        var columns1 = dataTable1["columns"] as JArray;
        var columns2 = dataTable2["columns"] as JArray;
        Assert.NotNull(columns1);
        Assert.NotNull(columns2);
        Assert.Single(columns1);
        Assert.Single(columns2);
    }

    [Fact]
    public void Sanitize_CombinesRemovalAndColumnFix()
    {
        var dashboard = new JObject
        {
            ["name"] = "Test",
            ["stackedGroupName"] = "group1",
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["dataTable"] = new JObject
                        {
                            ["query"] = LogsQuery(),
                            ["stackedGroupName"] = "widgetGroup"
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        Assert.Null(result["stackedGroupName"]);
        var dataTable = result["widgets"]?[0]?["definition"]?["dataTable"] as JObject;
        Assert.NotNull(dataTable);
        Assert.Null(dataTable["stackedGroupName"]);
        var columns = dataTable["columns"] as JArray;
        Assert.NotNull(columns);
        Assert.Single(columns);
    }

    [Fact]
    public void Sanitize_EnsuresPieChartLabelDefinition_WhenMissing()
    {
        var dashboard = new JObject
        {
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["pieChart"] = new JObject
                        {
                            ["query"] = LogsQuery()
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var pieChart = result["widgets"]?[0]?["definition"]?["pieChart"] as JObject;
        Assert.NotNull(pieChart);
        var labelDefinition = pieChart["labelDefinition"] as JObject;
        Assert.NotNull(labelDefinition);
        Assert.Equal("LABEL_SOURCE_INNER", labelDefinition["labelSource"]?.ToString());
        Assert.True(labelDefinition["isVisible"]?.Value<bool>());
        Assert.True(labelDefinition["showName"]?.Value<bool>());
        Assert.True(labelDefinition["showValue"]?.Value<bool>());
        Assert.True(labelDefinition["showPercentage"]?.Value<bool>());
    }

    [Fact]
    public void Sanitize_PreservesExistingPieChartLabelDefinition()
    {
        var dashboard = new JObject
        {
            ["widgets"] = new JArray
            {
                new JObject
                {
                    ["definition"] = new JObject
                    {
                        ["pieChart"] = new JObject
                        {
                            ["query"] = LogsQuery(),
                            ["labelDefinition"] = new JObject
                            {
                                ["labelSource"] = "LABEL_SOURCE_STACK",
                                ["isVisible"] = false,
                                ["showName"] = false,
                                ["showValue"] = true,
                                ["showPercentage"] = false
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var pieChart = result["widgets"]?[0]?["definition"]?["pieChart"] as JObject;
        Assert.NotNull(pieChart);
        var labelDefinition = pieChart["labelDefinition"] as JObject;
        Assert.NotNull(labelDefinition);
        Assert.Equal("LABEL_SOURCE_STACK", labelDefinition["labelSource"]?.ToString());
        Assert.False(labelDefinition["isVisible"]?.Value<bool>());
        Assert.False(labelDefinition["showName"]?.Value<bool>());
        Assert.True(labelDefinition["showValue"]?.Value<bool>());
        Assert.False(labelDefinition["showPercentage"]?.Value<bool>());
    }

    [Fact]
    public void Sanitize_EnsuresPieChartLabelDefinition_Recursively()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = LogsQuery()
                                            }
                                        }
                                    },
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = MetricsQuery(withGroupNames: true)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Equal(2, widgets.Count);

        var pieChart1 = widgets[0]?["definition"]?["pieChart"] as JObject;
        var pieChart2 = widgets[1]?["definition"]?["pieChart"] as JObject;
        Assert.NotNull(pieChart1);
        Assert.NotNull(pieChart2);

        var labelDefinition1 = pieChart1["labelDefinition"] as JObject;
        var labelDefinition2 = pieChart2["labelDefinition"] as JObject;
        Assert.NotNull(labelDefinition1);
        Assert.NotNull(labelDefinition2);
        Assert.Equal("LABEL_SOURCE_INNER", labelDefinition1["labelSource"]?.ToString());
        Assert.Equal("LABEL_SOURCE_INNER", labelDefinition2["labelSource"]?.ToString());
    }

    [Fact]
    public void Sanitize_MigratesPieChartDataPrime_ToAdjacentMarkdownWidget()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["id"] = new JObject { ["value"] = "pie-1" },
                                        ["title"] = "Pie With DataPrime",
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["logs"] = new JObject
                                                    {
                                                        ["aggregation"] = new JObject { ["count"] = new JObject() },
                                                        ["filters"] = new JArray(),
                                                        ["groupNamesFields"] = new JArray()
                                                    },
                                                    ["dataPrime"] = new JObject
                                                    {
                                                        ["value"] = "source logs | groupby payload.isEmail agg count()"
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Equal(2, widgets.Count);

        var pieWidget = widgets[0] as JObject;
        Assert.NotNull(pieWidget);
        Assert.Null(pieWidget["definition"]?["pieChart"]?["query"]?["dataPrime"]);

        var markdownWidget = widgets[1] as JObject;
        Assert.NotNull(markdownWidget);
        Assert.Equal("Pie With DataPrime (DataPrime Query)", markdownWidget["title"]?.ToString());
        var markdownText = markdownWidget["definition"]?["markdown"]?["markdownText"]?.ToString();
        Assert.NotNull(markdownText);
        Assert.Contains("source logs | groupby payload.isEmail agg count()", markdownText);
    }

    [Fact]
    public void Sanitize_LegacyDataPrimeMigration_IsIdempotent()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["id"] = new JObject { ["value"] = "pie-2" },
                                        ["title"] = "Pie Legacy DataPrime",
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["dataPrime"] = new JObject
                                                    {
                                                        ["value"] = "source logs | groupby payload.isEmail agg count()"
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var first = DashboardPayloadSanitizer.Sanitize(dashboard);
        var second = DashboardPayloadSanitizer.Sanitize(first);

        var widgets = second["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Equal(2, widgets.Count);
        Assert.Null(widgets[0]?["definition"]?["pieChart"]?["query"]?["dataPrime"]);
        Assert.Equal("Pie Legacy DataPrime (DataPrime Query)", widgets[1]?["title"]?.ToString());
    }

    [Fact]
    public void Sanitize_DoesNotMigrateModernDataprimeShape()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["id"] = new JObject { ["value"] = "pie-3" },
                                        ["title"] = "Pie Modern Dataprime",
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
                                                        },
                                                        ["filters"] = new JArray(),
                                                        ["groupNames"] = new JArray { "payload.isEmail" }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Single(widgets);
        Assert.NotNull(widgets[0]?["definition"]?["pieChart"]?["query"]?["dataprime"]);
        Assert.Null(widgets[0]?["definition"]?["pieChart"]?["query"]?["dataPrime"]);
    }

    [Fact]
    public void Sanitize_RemovesLogsGroupNames_Recursively()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["logs"] = new JObject
                                                    {
                                                        ["filters"] = new JArray(),
                                                        ["groupNames"] = new JArray { "service.name" }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["lineChart"] = new JObject
                                            {
                                                ["queryDefinitions"] = new JArray
                                                {
                                                    new JObject
                                                    {
                                                        ["id"] = "a",
                                                        ["query"] = new JObject
                                                        {
                                                            ["logs"] = new JObject
                                                            {
                                                                ["filters"] = new JArray(),
                                                                ["groupNames"] = new JArray { "k8s.namespace.name" }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        Assert.Null(result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"]?[0]?["definition"]?["pieChart"]?["query"]?["logs"]?["groupNames"]);
        Assert.Null(result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"]?[1]?["definition"]?["lineChart"]?["queryDefinitions"]?[0]?["query"]?["logs"]?["groupNames"]);
    }

    [Fact]
    public void Sanitize_RemovesGroupNames_ForLogsOnly_AndPreservesDataprimeAndMetrics()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["logs"] = new JObject
                                                    {
                                                        ["filters"] = new JArray(),
                                                        ["groupNames"] = new JArray { "service.name" }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    new JObject
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
                                                            ["text"] = "source logs | groupby $l.applicationname aggregate count() as c"
                                                        },
                                                        ["filters"] = new JArray(),
                                                        ["groupNames"] = new JArray { "$l.applicationname" }
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["pieChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["metrics"] = new JObject
                                                    {
                                                        ["promqlQuery"] = new JObject
                                                        {
                                                            ["value"] = "sum(rate(http_requests_total[5m]))"
                                                        },
                                                        ["filters"] = new JArray(),
                                                        ["groupNames"] = new JArray { "service" }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);
        Assert.Equal(3, widgets.Count);
        Assert.Null(widgets[0]?["definition"]?["pieChart"]?["query"]?["logs"]?["groupNames"]);
        Assert.Equal("$l.applicationname", widgets[1]?["definition"]?["pieChart"]?["query"]?["dataprime"]?["groupNames"]?[0]?.ToString());
        Assert.Equal("service", widgets[2]?["definition"]?["pieChart"]?["query"]?["metrics"]?["groupNames"]?[0]?.ToString());
    }

    [Fact]
    public void Sanitize_RemovesEmptyGroupingArrays_Recursively()
    {
        var dashboard = new JObject
        {
            ["layout"] = new JObject
            {
                ["sections"] = new JArray
                {
                    new JObject
                    {
                        ["rows"] = new JArray
                        {
                            new JObject
                            {
                                ["widgets"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["barChart"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["logs"] = new JObject
                                                    {
                                                        ["groupNamesFields"] = new JArray(),
                                                        ["filters"] = new JArray()
                                                    }
                                                }
                                            }
                                        }
                                    },
                                    new JObject
                                    {
                                        ["definition"] = new JObject
                                        {
                                            ["dataTable"] = new JObject
                                            {
                                                ["query"] = new JObject
                                                {
                                                    ["logs"] = new JObject
                                                    {
                                                        ["filters"] = new JArray(),
                                                        ["grouping"] = new JObject
                                                        {
                                                            ["groupBys"] = new JArray(),
                                                            ["aggregations"] = new JArray()
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);
        var widgets = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"] as JArray;
        Assert.NotNull(widgets);

        var firstWidgetGroupNamesFields = widgets[0]?["definition"]?["barChart"]?["query"]?["logs"]?["groupNamesFields"] as JArray;
        Assert.Null(firstWidgetGroupNamesFields);

        Assert.Null(widgets[1]?["definition"]?["dataTable"]?["query"]?["logs"]?["grouping"]?["groupBys"]);
        var secondWidgetGroupNamesFields = widgets[1]?["definition"]?["dataTable"]?["query"]?["logs"]?["groupNamesFields"] as JArray;
        Assert.Null(secondWidgetGroupNamesFields);
    }

    /// <summary>
    /// Stripping the unsupported legacy <c>dataPrime</c> branch used to leave <c>query</c> with no branch at
    /// all, and Sanitize then threw on its own output ("exactly one branch must be active"), aborting the
    /// upload of the whole dashboard. The text must land on the supported <c>dataprime</c> branch so the
    /// widget still queries — the markdown copy alone is a breadcrumb, not a working query.
    /// </summary>
    [Fact]
    public void Sanitize_LegacyDataPrimeOnlyQuery_IsRehomedOntoModernDataprimeBranch()
    {
        var dashboard = PieWidgetDashboard(new JObject
        {
            ["dataPrime"] = new JObject { ["value"] = "source logs | groupby payload.isEmail agg count()" }
        });

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var query = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"]?[0]
            ?["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.Null(query["dataPrime"]);
        Assert.Equal(
            "source logs | groupby payload.isEmail agg count()",
            query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString());
        Assert.IsType<JArray>(query["dataprime"]?["filters"]);
    }

    /// <summary>
    /// A query that still has a real branch must keep it: re-homing the legacy text there too would make two
    /// branches active at once, which the API rejects.
    /// </summary>
    [Fact]
    public void Sanitize_LegacyDataPrimeAlongsideLogs_LeavesLogsBranchAlone()
    {
        var dashboard = PieWidgetDashboard(new JObject
        {
            ["logs"] = new JObject { ["filters"] = new JArray() },
            ["dataPrime"] = new JObject { ["value"] = "source logs | agg count()" }
        });

        var result = DashboardPayloadSanitizer.Sanitize(dashboard);

        var query = result["layout"]?["sections"]?[0]?["rows"]?[0]?["widgets"]?[0]
            ?["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.Null(query["dataPrime"]);
        Assert.NotNull(query["logs"]);
        Assert.Null(query["dataprime"]);
    }

    private static JObject PieWidgetDashboard(JObject query) => new()
    {
        ["layout"] = new JObject
        {
            ["sections"] = new JArray
            {
                new JObject
                {
                    ["rows"] = new JArray
                    {
                        new JObject
                        {
                            ["widgets"] = new JArray
                            {
                                new JObject
                                {
                                    ["id"] = new JObject { ["value"] = "pie-legacy" },
                                    ["title"] = "Pie",
                                    ["definition"] = new JObject
                                    {
                                        ["pieChart"] = new JObject { ["query"] = query }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };

    /// <summary>
    /// A logs query shaped the way every panel converter emits one. The <c>filters</c> array is not
    /// decoration: <see cref="GrafanaToCx.Core.Converter.Semantics.QueryShapeValidator"/> runs inside
    /// Sanitize and rejects a logs branch without it, so a barer literal would not survive the call.
    /// </summary>
    private static JObject LogsQuery() => new()
    {
        ["logs"] = new JObject { ["filters"] = new JArray() }
    };

    /// <param name="withGroupNames">
    /// Required for pie charts only — the API rejects a pie metrics query with empty group names,
    /// since a pie has nothing to slice by. Bar charts, gauges and data tables accept empty grouping.
    /// </param>
    private static JObject MetricsQuery(bool withGroupNames = false)
    {
        var metrics = new JObject
        {
            ["promqlQuery"] = new JObject { ["value"] = "sum(up)" },
            ["filters"] = new JArray()
        };

        if (withGroupNames)
            metrics["groupNames"] = new JArray { "service.name" };

        return new JObject { ["metrics"] = metrics };
    }
}
