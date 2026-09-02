using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.PanelConverters;

public static class WidgetHelpers
{
    public static JObject IdObject() =>
        new JObject { ["value"] = Guid.NewGuid().ToString() };

    /// <summary>
    /// The title Coralogix should show for a Grafana panel: the panel's own title with variable
    /// references normalized so they interpolate, or a fallback naming the panel id.
    /// </summary>
    /// <remarks>
    /// Shared rather than inlined per converter. This expression was duplicated in every panel
    /// converter, which is why titles kept their raw <c>$name</c> references: normalizing them
    /// meant finding eight identical copies, and any one missed would still ship literal text.
    /// </remarks>
    public static string ResolveTitle(JObject panel) =>
        panel.Value<string>("title") is { Length: > 0 } title
            ? QueryHelpers.NormalizeNamePlaceholders(title)
            : $"Panel #{panel.Value<int>("id")}";

    /// <summary>
    /// The description Coralogix should show for a Grafana panel: HTML stripped, then variable
    /// references normalized. Coralogix interpolates a description the same way it does a title.
    /// </summary>
    public static string ResolveDescription(JObject panel) =>
        QueryHelpers.NormalizeNamePlaceholders(
            QueryHelpers.CleanHtml(panel.Value<string>("description") ?? string.Empty));
}
