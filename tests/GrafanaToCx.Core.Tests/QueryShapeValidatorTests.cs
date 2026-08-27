using GrafanaToCx.Core.Converter.Semantics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GrafanaToCx.Core.Tests;

public class QueryShapeValidatorTests
{
    [Fact]
    public void ValidateDashboard_FlagsLogsGroupNames_AsUnsupported()
    {
        var dashboard = new JObject
        {
            ["name"] = "shape-check",
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
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        var validator = new QueryShapeValidator();

        var errors = validator.ValidateDashboard(dashboard);

        Assert.Contains(errors, e => e.Message.Contains("logs.groupNames is unsupported in logs branch", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDashboard_AllowsDataprimeWithoutGroupNames()
    {
        var dashboard = new JObject
        {
            ["name"] = "shape-check",
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
                                                    ["dataprime"] = new JObject
                                                    {
                                                        ["dataprimeQuery"] = new JObject
                                                        {
                                                            ["text"] = "source logs | agg count()"
                                                        },
                                                        ["filters"] = new JArray()
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

        var validator = new QueryShapeValidator();

        var errors = validator.ValidateDashboard(dashboard);

        Assert.Empty(errors);
    }

    // A pie chart slices by label values, so the API rejects a metrics query carrying no group names
    // ("group_names cannot be empty"). Catching it here turns an upload-time HTTP 400 into a local failure.

    [Fact]
    public void ValidateDashboard_FlagsPieChartMetricsWithoutGroupNames()
    {
        var errors = new QueryShapeValidator().ValidateDashboard(
            WidgetDashboard("pieChart", MetricsQuery(groupNames: null)));

        Assert.Contains(errors, e => e.Message.Contains("metrics.groupNames must be a non-empty array", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDashboard_FlagsPieChartMetricsWithEmptyGroupNames()
    {
        var errors = new QueryShapeValidator().ValidateDashboard(
            WidgetDashboard("pieChart", MetricsQuery(groupNames: new JArray())));

        Assert.Contains(errors, e => e.Message.Contains("metrics.groupNames must be a non-empty array", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateDashboard_AllowsPieChartMetricsWithGroupNames()
    {
        var errors = new QueryShapeValidator().ValidateDashboard(
            WidgetDashboard("pieChart", MetricsQuery(groupNames: new JArray { "operation" })));

        Assert.Empty(errors);
    }

    /// <summary>
    /// Scoped to pie charts on purpose — the live account holds bar charts with empty metrics grouping,
    /// so applying the rule broadly would reject payloads Coralogix accepts.
    /// </summary>
    [Theory]
    [InlineData("barChart")]
    [InlineData("gauge")]
    [InlineData("dataTable")]
    public void ValidateDashboard_AllowsOtherWidgetsWithoutMetricsGroupNames(string widgetType)
    {
        var errors = new QueryShapeValidator().ValidateDashboard(
            WidgetDashboard(widgetType, MetricsQuery(groupNames: null)));

        Assert.Empty(errors);
    }

    private static JObject MetricsQuery(JArray? groupNames)
    {
        var metrics = new JObject
        {
            ["promqlQuery"] = new JObject { ["value"] = "sum(up)" },
            ["filters"] = new JArray()
        };

        if (groupNames is not null)
            metrics["groupNames"] = groupNames;

        return new JObject { ["metrics"] = metrics };
    }

    private static JObject WidgetDashboard(string widgetType, JObject query) => new()
    {
        ["name"] = "shape-check",
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
                                        [widgetType] = new JObject { ["query"] = query }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    };
}
