using GrafanaToCx.Core.Converter;
using GrafanaToCx.Core.Converter.Transformations;
using GrafanaToCx.Core.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests.Golden;

public class GoldenSemanticConversionTests
{
    [Fact]
    [Trait("Category", "Golden")]
    public void PieBooleanSplit_Fixture_ProducesCanonicalDataprimeBranch()
    {
        var input = TestFixtureLoader.LoadFixture("golden_pie_boolean_split.json");

        // Consolidation is opt-in per widget type: the converter defaults to MultiLuceneMergeOptions.Disabled,
        // so without an allowlist the planner takes the DGR-LMG-001 "not allowlisted" exit and keeps only the
        // first target. "piechart" is allowlisted in the shipped migration-settings.json, so this mirrors a
        // real migration run rather than granting the test a capability the product does not have.
        var converter = new GrafanaToCxConverter(
            NullLogger<GrafanaToCxConverter>.Instance,
            new MultiLuceneMergeOptions(["piechart"]));

        var output = converter.ConvertToJObject(input.ToString());
        var widget = ExtractFirstWidget(output);
        Assert.NotNull(widget);

        var query = widget["definition"]?["pieChart"]?["query"] as JObject;
        Assert.NotNull(query);
        Assert.NotNull(query["dataprime"]);
        Assert.Null(query["logs"]);
        Assert.Null(query["metrics"]);

        var dataprimeText = query["dataprime"]?["dataprimeQuery"]?["text"]?.ToString();
        Assert.Contains("groupby payload.isEmail", dataprimeText);
        Assert.Contains("agg count()", dataprimeText);

        // DGR-LMG-000 is the planner's "merge applied" code; the two boolean targets collapsed into one query.
        Assert.Contains(converter.ConversionDiagnostics, d => d.Code == "DGR-LMG-000");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public void Validator_RejectsInvalidOneofShape()
    {
        var dashboard = new JObject
        {
            ["name"] = "invalid-shape",
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
                                                    ["logs"] = new JObject { ["filters"] = new JArray() },
                                                    ["metrics"] = new JObject
                                                    {
                                                        ["promqlQuery"] = new JObject { ["value"] = "sum(up)" },
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

        Assert.Throws<InvalidOperationException>(() => DashboardPayloadSanitizer.Sanitize(dashboard));
    }

    private static JObject ExtractFirstWidget(JObject dashboard)
    {
        return (JObject)dashboard["layout"]!["sections"]![0]!["rows"]![0]!["widgets"]![0]!;
    }
}
