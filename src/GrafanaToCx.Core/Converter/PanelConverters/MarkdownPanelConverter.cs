using GrafanaToCx.Core.Converter.Transformations;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

public sealed class MarkdownPanelConverter : IPanelConverter
{
    public JObject? Convert(JObject panel, ISet<string> discoveredMetrics, TransformationPlan? plan = null)
    {
        var content = panel["options"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(content))
        {
            content = panel["content"]?.ToString();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = string.Empty,
            ["definition"] = new JObject
            {
                ["markdown"] = new JObject
                {
                    ["markdownText"] = QueryHelpers.CleanHtml(content)
                }
            }
        };
    }

    public static int CalculateHeight(JObject panel)
    {
        var content = panel["options"]?["content"]?.ToString() ?? panel["content"]?.ToString() ?? string.Empty;
        var lines = content.Count(c => c == '\n') + 1;
        if (lines <= 3) return 8;
        if (lines <= 10) return 15;
        return 23;
    }

    /// <summary>
    /// Creates a markdown widget marking the place of a panel whose Grafana type has no
    /// converter. Distinct from <see cref="CreateErrorWidget"/>: nothing went wrong here,
    /// the type is simply not supported, and the wording should not read as a failure.
    /// </summary>
    public static JObject CreateNotMigratedWidget(string panelTitle, string panelType)
    {
        var markdown =
            $"## Not migrated\n\n" +
            $"This panel used the Grafana `{QueryHelpers.CleanHtml(panelType)}` panel type, " +
            "which has no Coralogix equivalent in this converter. Its queries were not carried across.";

        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = panelTitle,
            ["description"] = $"Unsupported Grafana panel type '{panelType}'.",
            ["definition"] = new JObject
            {
                ["markdown"] = new JObject
                {
                    ["markdownText"] = markdown
                }
            }
        };
    }

    /// <summary>
    /// Creates a markdown widget explaining a panel conversion failure.
    /// Used for strict fail semantics when transformation/query pattern is unsupported.
    /// </summary>
    public static JObject CreateErrorWidget(string panelTitle, string panelType, string reason)
    {
        var markdown = $"## Conversion failed: {panelType}\n\n{QueryHelpers.CleanHtml(reason)}";
        return new JObject
        {
            ["id"] = WidgetHelpers.IdObject(),
            ["title"] = panelTitle,
            ["description"] = "Panel could not be converted; see content for details.",
            ["definition"] = new JObject
            {
                ["markdown"] = new JObject
                {
                    ["markdownText"] = markdown
                }
            }
        };
    }
}
