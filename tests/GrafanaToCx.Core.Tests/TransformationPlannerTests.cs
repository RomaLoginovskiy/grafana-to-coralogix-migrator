using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

[Trait("Category", "Conformance")]
public class TransformationPlannerTests
{
    [Fact]
    public void PieChart_Allowlisted_MultiTarget_BooleanPredicate_ConsolidatesToDataPrime()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 1,
            ["title"] = "Email vs Non-Email",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.Null(query["logs"]);
        Assert.NotNull(query["dataprime"]);
        Assert.Null(query["metrics"]);
        Assert.Null(query["dataPrime"]);

        var dataPrime = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("source logs", dataPrime);
        Assert.Contains("groupby payload.isEmail agg count()", dataPrime);
        var groupNames = query["dataprime"]?["groupNames"] as JArray;
        Assert.NotNull(groupNames);
        Assert.Single(groupNames!);
        Assert.Equal("payload.isEmail", groupNames[0]?.ToString());

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Email vs Non-Email" && d.Code == "DGR-LMG-000");
    }

    [Theory]
    [InlineData("count", null, "count()")]
    [InlineData("sum", "payload.size.keyword", "sum(payload.size)")]
    [InlineData("avg", "payload.duration.keyword", "avg(payload.duration)")]
    [InlineData("max", "payload.latency.keyword", "max(payload.latency)")]
    public void PieChart_Allowlisted_MapsMetricSemanticsToDataPrimeAggregation(string metricType, string? metricField, string expectedAggregation)
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 13,
            ["title"] = "Aggregation Mapping Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { BuildMetric(metricType, metricField) }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { BuildMetric(metricType, metricField) }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var dataPrime = widget["definition"]?["pieChart"]?["query"]?["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains($"groupby payload.isEmail agg {expectedAggregation}", dataPrime);
    }

    [Fact]
    public void PieChart_Allowlisted_WithDateHistogram_DoesNotIncludeTimestampGrouping()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 16,
            ["title"] = "Pie Histogram Ignored",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var dataPrime = widget["definition"]?["pieChart"]?["query"]?["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("groupby payload.isEmail agg count()", dataPrime);
        Assert.DoesNotContain("$m.timestamp /", dataPrime);
    }

    [Fact]
    public void PieChart_Allowlisted_EscapedQuotedPredicate_ConsolidatesToDataPrime()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 15,
            ["title"] = "Escaped Quoted Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "applicationName.keyword:\\\"domain-engine-requests-producer\\\" AND payload.path.keyword:\\\"/v1/domain-engine/requests\\\" AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "applicationName.keyword:\\\"domain-engine-requests-producer\\\" AND payload.path.keyword:\\\"/v1/domain-engine/requests\\\" AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Escaped Quoted Pie" && d.Code == "DGR-LMG-000");
    }

    [Fact]
    public void PieChart_Allowlisted_InvalidVaryingGroupByField_FallsBackToSingleTargetWithDiagnostic()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 17,
            ["title"] = "Invalid Group By Fallback Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND .keyword:true",
                    ["bucketAggs"] = new JArray(),
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND .keyword:false",
                    ["bucketAggs"] = new JArray(),
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Invalid Group By Fallback Pie" && d.Code == "DGR-LMG-011");
    }

    [Fact]
    public void PieChart_Allowlisted_ConsistentTermsBucket_DrivesConsolidatedGroupBy()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 18,
            ["title"] = "Terms Driven Group By Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.channel.keyword" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.channel.keyword" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);

        var dataPrime = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("groupby payload.channel agg count()", dataPrime);
        Assert.DoesNotContain("groupby payload.isEmail agg count()", dataPrime);

        var groupNames = query["dataprime"]?["groupNames"] as JArray;
        Assert.NotNull(groupNames);
        Assert.Single(groupNames!);
        Assert.Equal("payload.channel", groupNames[0]?.ToString());

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Terms Driven Group By Pie" && d.Code == "DGR-LMG-000");
    }

    [Fact]
    public void PieChart_NotAllowlisted_SkipsMergeAndFallsBackToSingleTarget()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 11,
            ["title"] = "Not Allowlisted Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Not Allowlisted Pie" && d.Code == "DGR-LMG-001");
    }

    [Fact]
    public void PieChart_Allowlisted_ParseFailure_SkipsMergeWithDeterministicDiagnostic()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 12,
            ["title"] = "Parse Failure Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo OR payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo OR payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Parse Failure Pie" && d.Code == "DGR-LMG-003");
    }

    [Fact]
    public void PieChart_Allowlisted_MultiDeltaPredicate_SkipsMerge()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 14,
            ["title"] = "Multi Delta Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND env:prod AND payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:bar AND env:qa AND payload.isEmail:false",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Null(query["dataprime"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Multi Delta Pie" && d.Code == "DGR-LMG-004");
    }

    [Fact]
    public void PieChart_Allowlisted_MixedDatasource_SkipsMerge()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 4,
            ["title"] = "Mixed Datasource Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["expr"] = "sum(up)",
                    ["datasource"] = new JObject { ["type"] = "prometheus" }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Mixed Datasource Pie" && d.Code == "DGR-LMG-002");
    }

    [Fact]
    public void PieChart_Allowlisted_AggregationMismatch_SkipsMerge()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 6,
            ["title"] = "Aggregation Mismatch Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "sum", ["field"] = "payload.size.keyword" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);
        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Aggregation Mismatch Pie" && d.Code == "DGR-LMG-005");
    }

    [Fact]
    public void PieChart_Allowlisted_AggregationRequiresField_SkipsMerge()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 7,
            ["title"] = "Aggregation Field Missing Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "sum" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "sum" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);
        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Aggregation Field Missing Pie" && d.Code == "DGR-LMG-006");
    }

    [Fact]
    public void PieChart_HiddenSecondaryTarget_DoesNotForceError()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 5,
            ["title"] = "Hidden Target Pie",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["hide"] = true,
                    ["query"] = "payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);
        Assert.NotNull(widget["definition"]?["pieChart"]);

        var diag = converter.ConversionDiagnostics.FirstOrDefault(
            d => d.PanelTitle == "Hidden Target Pie" && d.Outcome == "error");
        Assert.Null(diag);
    }

    [Fact]
    public void PieChart_SingleTarget_Unchanged()
    {
        var converter = CreateConverter("piechart");
        var panel = new JObject
        {
            ["id"] = 3,
            ["title"] = "Single Slice",
            ["type"] = "piechart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["bucketAggs"] = new JArray { new JObject { ["type"] = "terms", ["field"] = "level" } },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["logs"]);
        Assert.Null(query["dataprime"]);
        Assert.Null(query["dataPrime"]);
    }

    [Fact]
    public void TimeSeries_Allowlisted_DateHistogram_MultiTarget_ConsolidatesToSingleDataprimeDefinition()
    {
        var converter = CreateConverter("timeseries");
        var panel = new JObject
        {
            ["id"] = 101,
            ["title"] = "Latency Over Time",
            ["type"] = "timeseries",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var queryDefinitions = widget["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.NotNull(queryDefinitions);
        Assert.Single(queryDefinitions!);

        var query = queryDefinitions[0]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);

        var dataPrime = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("source logs", dataPrime);
        Assert.Contains("groupby $m.timestamp / 1m, payload.isEmail agg count()", dataPrime);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Latency Over Time" && d.Code == "DGR-LMG-000");
    }

    [Fact]
    public void TimeSeries_Allowlisted_DateHistogramMismatch_SkipsMergeButKeepsEveryTarget()
    {
        var converter = CreateConverter("timeseries");
        var panel = new JObject
        {
            ["id"] = 102,
            ["title"] = "Histogram Mismatch",
            ["type"] = "timeseries",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:foo AND payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:foo AND payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "5m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        // The merge is still skipped — the intervals cannot be reconciled into one DataPrime
        // query — but a lineChart holds a queryDefinitions array, so both targets are kept
        // as separate series rather than one being discarded.
        var queryDefinitions = widget["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.NotNull(queryDefinitions);
        Assert.Equal(2, queryDefinitions!.Count);

        foreach (var definition in queryDefinitions.Children<JObject>())
        {
            var query = definition["query"] as JObject;
            Assert.NotNull(query);
            Assert.NotNull(query!["logs"]);
            Assert.Null(query["dataprime"]);
        }

        // Nothing was dropped, so no degradation is reported.
        Assert.DoesNotContain(converter.ConversionDiagnostics,
            d => d.PanelTitle == "Histogram Mismatch" && d.Code == "DGR-LMG-009");
    }

    [Fact]
    public void TimeSeries_LogsFallback_UsesTimestampGroupBy_WhenNoTermsGroupExists()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 104,
            ["title"] = "Timeseries No Terms",
            ["type"] = "timeseries",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "service:payments",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var logs = widget["definition"]?["lineChart"]?["queryDefinitions"]?[0]?["query"]?["logs"] as JObject;
        Assert.NotNull(logs);
        var groupBys = logs!["groupBys"] as JArray;
        Assert.NotNull(groupBys);
        Assert.Single(groupBys!);
        Assert.Equal("timestamp", groupBys[0]?["keypath"]?[0]?.ToString());
        Assert.Equal("DATASET_SCOPE_METADATA", groupBys[0]?["scope"]?.ToString());
    }

    [Fact]
    public void Graph_Allowlisted_MultiTarget_DoesNotUseMultiLuceneConsolidation()
    {
        var converter = CreateConverter("graph");
        var panel = new JObject
        {
            ["id"] = 103,
            ["title"] = "Graph No Merge",
            ["type"] = "graph",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "payload.isEmail:true",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "payload.isEmail:false",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.isEmail" }
                    },
                    ["metrics"] = new JArray { new JObject { ["type"] = "count" } }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var queryDefinitions = widget["definition"]?["lineChart"]?["queryDefinitions"] as JArray;
        Assert.NotNull(queryDefinitions);
        Assert.Equal(2, queryDefinitions!.Count);
        Assert.All(queryDefinitions.Children<JObject>(), def => Assert.NotNull(def["query"]?["logs"]));

        Assert.DoesNotContain(converter.ConversionDiagnostics, d => d.PanelTitle == "Graph No Merge" && d.Code == "DGR-LMG-000");
    }

    [Fact]
    public void BarChart_Allowlisted_MetricVariation_ConsolidatesToDataPrimeWithTimestampGroupBy()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 201,
            ["title"] = "[DomainEngine Requests Producer] Requests Produced",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["alias"] = "success",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.successCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["alias"] = "failed",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.failedCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Null(query["metrics"]);
        Assert.Null(query["dataPrime"]);

        var dataPrime = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("source logs", dataPrime);
        Assert.Contains("groupby $m.timestamp / 1m", dataPrime);
        Assert.Contains("sum(payload.successCount) as success", dataPrime);
        Assert.Contains("sum(payload.failedCount) as failed", dataPrime);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "[DomainEngine Requests Producer] Requests Produced" && d.Code == "DGR-BMG-000");
    }

    [Fact]
    public void BarChart_Allowlisted_MetricVariation_EscapedQuotedPathPredicate_ConsolidatesToDataPrime()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 211,
            ["title"] = "[DomainEngine Requests Producer] Requests Produced Escaped",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["alias"] = "bannersCount",
                    ["query"] = "applicationName.keyword:\\\"domain-engine-requests-producer\\\" AND payload.path.keyword:\\\"/v1/domain-engine/requests\\\" AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.bannersCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["alias"] = "otherCount",
                    ["query"] = "env:prod AND payload.path.keyword:\"/v1/domain-engine/requests\" AND applicationName.keyword:\"domain-engine-requests-producer\"",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.otherCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Null(query["metrics"]);
        Assert.Null(query["dataPrime"]);

        var dataPrime = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("source logs", dataPrime);
        Assert.Contains("groupby $m.timestamp / 1m", dataPrime);
        Assert.Contains("sum(payload.bannersCount) as bannersCount", dataPrime);
        Assert.Contains("sum(payload.otherCount) as otherCount", dataPrime);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "[DomainEngine Requests Producer] Requests Produced Escaped" && d.Code == "DGR-BMG-000");
    }

    [Fact]
    public void BarChart_Allowlisted_AutoInterval_UsesSuggestedIntervalPlaceholder()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 216,
            ["title"] = "Bar Auto Interval",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "service:payments",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
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
                        new JObject { ["type"] = "count" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var dataPrime = widget["definition"]?["barChart"]?["query"]?["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("groupby $m.timestamp / $p.timeRange.suggestedInterval", dataPrime);
        Assert.DoesNotContain("groupby $m.timestamp / auto", dataPrime);
    }

    [Fact]
    public void BarChart_Allowlisted_DateHistogramAndTerms_ConsolidatesToDataPrimeWithTimestampAndTermsGroupBy()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 213,
            ["title"] = "Bar Terms Grouping Merge",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["alias"] = "success",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.status.keyword" },
                        new JObject { ["type"] = "terms", ["field"] = "payload.region.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.successCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["alias"] = "failed",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.status.keyword" },
                        new JObject { ["type"] = "terms", ["field"] = "payload.region.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.failedCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var dataPrime = widget["definition"]?["barChart"]?["query"]?["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.NotNull(dataPrime);
        Assert.Contains("groupby $m.timestamp / 1m, payload.status, payload.region", dataPrime);
    }

    [Fact]
    public void BarChart_Allowlisted_SingleTarget_DateHistogramAndTerms_ConsolidatesToDataPrime()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 214,
            ["title"] = "Bar Single Target DataPrime",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:DomainEngineResultsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1d" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.status.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.count.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Bar Single Target DataPrime" && d.Code == "DGR-BMG-010");
    }

    [Fact]
    public void BarChart_Allowlisted_MetricVariation_EscapedQuotedPredicateMismatch_SkipsMerge()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 212,
            ["title"] = "Bar Escaped Predicate Mismatch",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["alias"] = "bannersCount",
                    ["query"] = "applicationName.keyword:\\\"domain-engine-requests-producer\\\" AND payload.path.keyword:\\\"/v1/domain-engine/requests\\\" AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.bannersCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["alias"] = "otherCount",
                    ["query"] = "applicationName.keyword:\\\"domain-engine-requests-producer\\\" AND payload.path.keyword:\\\"/v1/domain-engine/requests/v2\\\" AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.otherCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Bar Escaped Predicate Mismatch" && d.Code == "DGR-BMG-001");
    }

    [Fact]
    public void BarChart_NotAllowlisted_MetricVariation_FallsBackToDeterministicSingleTarget()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 202,
            ["title"] = "Non Allowlisted Bar Merge",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["alias"] = "success",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.successCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["alias"] = "failed",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.failedCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Non Allowlisted Bar Merge" && d.Code == "DGR-MTG-001");
    }

    [Fact]
    public void BarChart_SingleTarget_DateHistogram_LogsFallback_UsesTimestampGrouping()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 204,
            ["title"] = "[DomainEngine Results Producer] Sent to DQ",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:DomainEngineResultsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1d" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.count.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);

        var groupNamesFields = query["logs"]?["groupNamesFields"] as JArray;
        Assert.NotNull(groupNamesFields);
        Assert.Single(groupNamesFields!);
        Assert.Equal("timestamp", groupNamesFields[0]?["keypath"]?[0]?.ToString());
        Assert.Equal("DATASET_SCOPE_METADATA", groupNamesFields[0]?["scope"]?.ToString());
    }

    [Fact]
    public void BarChart_LogsFallback_DateHistogramAndTerms_UsesTimestampThenTermsGrouping()
    {
        var converter = CreateConverter();
        var panel = new JObject
        {
            ["id"] = 215,
            ["title"] = "Bar Fallback Timestamp And Terms",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:DomainEngineResultsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1d" }
                        },
                        new JObject { ["type"] = "terms", ["field"] = "payload.status.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.count.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var groupNamesFields = widget["definition"]?["barChart"]?["query"]?["logs"]?["groupNamesFields"] as JArray;
        Assert.NotNull(groupNamesFields);
        Assert.Equal(2, groupNamesFields!.Count);
        Assert.Equal("timestamp", groupNamesFields[0]?["keypath"]?[0]?.ToString());
        Assert.Equal("payload", groupNamesFields[1]?["keypath"]?[0]?.ToString());
        Assert.Equal("status", groupNamesFields[1]?["keypath"]?[1]?.ToString());
    }

    [Fact]
    public void BarChart_Allowlisted_MetricTypeMismatch_SkipsMergeWithDeterministicDiagnostic()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 203,
            ["title"] = "Bar Merge Mismatch",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.successCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "date_histogram",
                            ["field"] = "@timestamp",
                            ["settings"] = new JObject { ["interval"] = "1m" }
                        }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "avg", ["field"] = "payload.failedCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Bar Merge Mismatch" && d.Code == "DGR-BMG-005");
    }

    [Fact]
    public void BarChart_Allowlisted_MissingDateHistogram_SkipsMergeWithDeterministicDiagnostic()
    {
        var converter = CreateConverter("barchart");
        var panel = new JObject
        {
            ["id"] = 205,
            ["title"] = "Bar Missing Histogram",
            ["type"] = "barchart",
            ["targets"] = new JArray
            {
                new JObject
                {
                    ["refId"] = "A",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject { ["type"] = "terms", ["field"] = "payload.outcome.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.successCount.keyword" }
                    }
                },
                new JObject
                {
                    ["refId"] = "B",
                    ["query"] = "app:DomainEngineRequestsProducer AND env:prod",
                    ["datasource"] = new JObject { ["type"] = "elasticsearch" },
                    ["bucketAggs"] = new JArray
                    {
                        new JObject { ["type"] = "terms", ["field"] = "payload.outcome.keyword" }
                    },
                    ["metrics"] = new JArray
                    {
                        new JObject { ["type"] = "sum", ["field"] = "payload.failedCount.keyword" }
                    }
                }
            }
        };

        var result = converter.ConvertToJObject(BuildDashboardJson(panel));
        var widget = GetFirstWidget(result);
        Assert.NotNull(widget);

        var query = widget["definition"]?["barChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query!["logs"]);
        Assert.Null(query["dataprime"]);

        Assert.Contains(converter.ConversionDiagnostics, d => d.PanelTitle == "Bar Missing Histogram" && d.Code == "DGR-BMG-004");
    }

    private static GrafanaToCxConverter CreateConverter(params string[] allowlistedWidgetTypes) =>
        new(NullLogger<GrafanaToCxConverter>.Instance, new MultiLuceneMergeOptions(allowlistedWidgetTypes));

    private static JObject BuildMetric(string metricType, string? metricField)
    {
        var metric = new JObject
        {
            ["type"] = metricType
        };

        if (!string.IsNullOrWhiteSpace(metricField))
            metric["field"] = metricField;

        return metric;
    }

    private static string BuildDashboardJson(JObject panel) =>
        new JObject
        {
            ["dashboard"] = new JObject
            {
                ["title"] = "Test",
                ["panels"] = new JArray { panel }
            }
        }.ToString();

    private static JObject? GetFirstWidget(JObject dashboard)
    {
        var sections = dashboard["layout"]?["sections"] as JArray ?? [];
        foreach (var section in sections.Children<JObject>())
        {
            var rows = section["rows"] as JArray ?? [];
            foreach (var row in rows.Children<JObject>())
            {
                var widgets = row["widgets"] as JArray ?? [];
                var first = widgets.FirstOrDefault();
                if (first is JObject w)
                    return w;
            }
        }
        return null;
    }
}
