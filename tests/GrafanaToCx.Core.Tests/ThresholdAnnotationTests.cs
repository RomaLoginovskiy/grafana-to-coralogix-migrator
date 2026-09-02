using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

/// <summary>
/// Grafana draws thresholds as horizontal lines on a time series; the line chart converter
/// never read them, so they were dropped silently. Coralogix expresses them as horizontal
/// manual annotations scoped to the widget.
/// </summary>
public class ThresholdAnnotationTests
{
    private static JObject Panel(string mode, params (string Color, object? Value)[] steps)
    {
        var stepArray = new JArray(steps.Select(s => (object)new JObject
        {
            ["color"] = s.Color,
            ["value"] = s.Value is null ? JValue.CreateNull() : JToken.FromObject(s.Value)
        }).ToArray());

        return new JObject
        {
            ["id"] = 1,
            ["type"] = "timeseries",
            ["title"] = "CPU",
            ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" }),
            ["fieldConfig"] = new JObject
            {
                ["defaults"] = new JObject
                {
                    ["thresholds"] = new JObject { ["mode"] = mode, ["steps"] = stepArray }
                }
            }
        };
    }

    private static JArray Convert(JObject panel)
    {
        var dashboard = new JObject { ["title"] = "Board", ["panels"] = new JArray(panel) };
        var converter = new GrafanaToCxConverter(NullLogger<GrafanaToCxConverter>.Instance);
        return (JArray)converter.ConvertToJObject(dashboard.ToString())["annotations"]!;
    }

    [Fact]
    public void AbsoluteThreshold_BecomesAHorizontalAnnotation()
    {
        var annotation = Assert.Single(Convert(Panel("absolute", ("green", null), ("red", 80))));

        Assert.Equal(80.0, annotation["source"]?["manual"]?["strategy"]?["instant"]?.Value<double>("value"));
        Assert.Equal("ANNOTATION_ORIENTATION_HORIZONTAL",
            annotation["source"]?["manual"]?.Value<string>("orientation"));
        Assert.Equal("ANNOTATION_COLOR_RED", annotation.Value<string>("color"));
    }

    [Fact]
    public void Annotation_IsScopedToItsWidget_AsPlainIdStrings()
    {
        // widgetIds is a list of id strings, not the {value:...} wrapper used elsewhere.
        var ids = Convert(Panel("absolute", ("red", 80)))[0]["scope"]?["specificWidgets"]?["widgetIds"] as JArray;

        var id = Assert.Single(ids!);
        Assert.Equal(JTokenType.String, id.Type);
        Assert.False(string.IsNullOrWhiteSpace(id.Value<string>()));
    }

    [Fact]
    public void BaseStep_WithNoValue_IsIgnored()
    {
        // The null-valued step colours the area below the first threshold; it marks no level.
        Assert.Single(Convert(Panel("absolute", ("green", null), ("red", 80))));
    }

    [Fact]
    public void PercentageThresholds_AreNotConverted()
    {
        // A percentage threshold is a share of the series range, which a fixed value cannot express.
        Assert.Empty(Convert(Panel("percentage", ("red", null), ("green", 80))));
    }

    [Fact]
    public void TransparentSteps_AreSkipped()
    {
        // Transparent marks the gap between bands rather than a line worth drawing.
        var annotations = Convert(Panel("absolute", ("red", null), ("transparent", 30), ("green", 80)));

        var annotation = Assert.Single(annotations);
        Assert.Equal("ANNOTATION_COLOR_GREEN", annotation.Value<string>("color"));
    }

    [Fact]
    public void SeveralThresholds_EachBecomeAnAnnotation()
    {
        Assert.Equal(2, Convert(Panel("absolute", ("green", null), ("orange", 70), ("red", 90))).Count);
    }

    [Fact]
    public void PanelWithNoThresholds_YieldsNothing()
    {
        var panel = new JObject
        {
            ["id"] = 1, ["type"] = "timeseries", ["title"] = "CPU",
            ["targets"] = new JArray(new JObject { ["refId"] = "A", ["expr"] = "up" })
        };

        Assert.Empty(Convert(panel));
    }

    [Fact]
    public void GaugePanels_AreLeftAlone_TheyCarryThresholdsNatively()
    {
        var panel = Panel("absolute", ("red", 80));
        panel["type"] = "stat";

        Assert.Empty(Convert(panel));
    }

    [Theory]
    [InlineData("red", "ANNOTATION_COLOR_RED")]
    [InlineData("semi-dark-red", "ANNOTATION_COLOR_RED")]
    [InlineData("light-orange", "ANNOTATION_COLOR_ORANGE")]
    [InlineData("dark-green", "ANNOTATION_COLOR_GREEN")]
    [InlineData("#FF0000", "ANNOTATION_COLOR_DEFAULT")]
    [InlineData("", "ANNOTATION_COLOR_DEFAULT")]
    public void GrafanaPaletteQualifiers_CollapseToTheHue(string grafana, string expected)
    {
        Assert.Equal(expected, ThresholdAnnotations.MapColor(grafana));
    }

    // ── the 100-character name cap ───────────────────────────────────────────

    private static JArray ConvertTitled(string title, params (string Color, object? Value)[] steps)
    {
        var panel = Panel("absolute", steps);
        panel["title"] = title;
        return Convert(panel);
    }

    /// <summary>
    /// Coralogix rejects an annotation name over 100 characters, which took down whole dashboards
    /// whose line charts had long titles.
    /// </summary>
    [Fact]
    public void ALongPanelTitle_IsTrimmedToTheNameLimit()
    {
        var title = new string('x', 140);

        var annotations = ConvertTitled(title, ("red", 90));

        var name = Assert.Single(annotations).Value<string>("name");
        Assert.NotNull(name);
        Assert.True(name!.Length <= 100, $"name was {name.Length} characters");
    }

    /// <summary>
    /// The value must survive the trim: it is the only thing telling apart the several annotations
    /// one panel emits, so shortening the tail would collapse them into one repeated name.
    /// </summary>
    [Fact]
    public void TrimmingKeepsTheThresholdValue_SoSiblingsStayDistinct()
    {
        var title = new string('x', 140);

        var annotations = ConvertTitled(title, ("red", 90), ("orange", 75));

        var names = annotations.Children<JObject>().Select(a => a.Value<string>("name")!).ToList();

        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.True(n.Length <= 100, $"name was {n.Length} characters"));
        Assert.Contains(names, n => n.EndsWith("threshold 90", StringComparison.Ordinal));
        Assert.Contains(names, n => n.EndsWith("threshold 75", StringComparison.Ordinal));
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void AShortPanelTitle_IsLeftAlone()
    {
        var name = Assert.Single(ConvertTitled("CPU", ("red", 90))).Value<string>("name");

        Assert.Equal("CPU threshold 90", name);
    }
}
