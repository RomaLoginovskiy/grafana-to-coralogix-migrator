using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

/// <summary>
/// Turns a Grafana panel's threshold steps into Coralogix horizontal annotations.
///
/// Grafana draws thresholds as horizontal lines on a time series; Coralogix expresses the same
/// thing as a dashboard annotation with <c>manual</c> source, horizontal orientation and an
/// instant value, scoped to the widget it belongs to. Without this the thresholds are dropped
/// silently — the line chart converter never reads them.
/// </summary>
public static class ThresholdAnnotations
{
    /// <summary>Grafana's named palette collapsed onto the annotation colour enum.</summary>
    private static readonly Dictionary<string, string> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "RED",
        ["green"] = "GREEN",
        ["blue"] = "BLUE",
        ["orange"] = "ORANGE",
        ["yellow"] = "YELLOW",
        ["purple"] = "PURPLE",
        ["cyan"] = "CYAN",
        ["magenta"] = "MAGENTA"
    };

    /// <summary>
    /// A step with this colour marks the gap between bands rather than a line worth drawing.
    /// </summary>
    private const string TransparentColor = "transparent";

    /// <summary>
    /// Builds one annotation per threshold step the panel defines.
    ///
    /// Only <c>absolute</c> thresholds are usable: a <c>percentage</c> threshold is a share of
    /// the series range, which a fixed annotation value cannot express.
    /// </summary>
    public static IReadOnlyList<JObject> Build(JObject panel, string widgetId, string panelTitle)
    {
        var thresholds = panel["fieldConfig"]?["defaults"]?["thresholds"] as JObject;
        if (thresholds is null)
            return [];

        if (!string.Equals(thresholds.Value<string>("mode"), "absolute", StringComparison.OrdinalIgnoreCase))
            return [];

        var annotations = new List<JObject>();
        foreach (var step in (thresholds["steps"] as JArray ?? []).Children<JObject>())
        {
            // The base step carries a null value — it colours the area below the first
            // threshold rather than marking a level.
            if (step["value"] is not JValue { Type: JTokenType.Integer or JTokenType.Float } value)
                continue;

            var color = step.Value<string>("color") ?? string.Empty;
            if (string.Equals(color, TransparentColor, StringComparison.OrdinalIgnoreCase))
                continue;

            annotations.Add(BuildAnnotation(
                $"{panelTitle} threshold {value.ToString(Newtonsoft.Json.Formatting.None)}",
                Convert.ToDouble(value.Value),
                MapColor(color),
                widgetId));
        }

        return annotations;
    }

    private static JObject BuildAnnotation(string name, double value, string color, string widgetId) => new()
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["enabled"] = true,
        ["source"] = new JObject
        {
            ["manual"] = new JObject
            {
                ["strategy"] = new JObject
                {
                    ["instant"] = new JObject
                    {
                        ["unit"] = "UNIT_UNSPECIFIED",
                        ["value"] = value
                    }
                },
                ["messageTemplate"] = string.Empty,
                ["orientation"] = "ANNOTATION_ORIENTATION_HORIZONTAL"
            }
        },
        // Scoped to the one widget, so a threshold does not bleed across the dashboard the
        // way a Grafana dashboard-wide annotation would.
        // widgetIds is a list of plain id strings, not the {value:...} wrapper used elsewhere.
        ["scope"] = new JObject
        {
            ["specificWidgets"] = new JObject
            {
                ["widgetIds"] = new JArray(widgetId)
            }
        },
        ["color"] = color
    };

    /// <summary>
    /// Grafana qualifies its palette (<c>semi-dark-red</c>, <c>light-orange</c>); the annotation
    /// enum has one entry per hue, so the qualifier is dropped and the hue matched.
    /// </summary>
    public static string MapColor(string grafanaColor)
    {
        if (string.IsNullOrWhiteSpace(grafanaColor))
            return "ANNOTATION_COLOR_DEFAULT";

        foreach (var (hue, mapped) in ColorMap)
        {
            if (grafanaColor.Contains(hue, StringComparison.OrdinalIgnoreCase))
                return $"ANNOTATION_COLOR_{mapped}";
        }

        return "ANNOTATION_COLOR_DEFAULT";
    }
}
