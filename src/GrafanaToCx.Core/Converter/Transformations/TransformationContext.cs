using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter.Transformations;

/// <summary>
/// Context passed to transformation planners: panel JSON, targets, and parsed transformations.
/// </summary>
public sealed class TransformationContext
{
    public JObject Panel { get; }
    public JArray Targets { get; }
    public JArray Transformations { get; }

    /// <summary>Canonical panel type; legacy identifiers are normalised so planners match on one name.</summary>
    public string PanelType { get; }

    /// <summary>The type as Grafana wrote it, for diagnostics.</summary>
    public string RawPanelType { get; }

    public string PanelTitle { get; }

    public TransformationContext(JObject panel, JArray targets, JArray transformations)
    {
        Panel = panel;
        Targets = targets;
        Transformations = transformations;
        RawPanelType = panel.Value<string>("type") ?? string.Empty;
        PanelType = PanelTypes.Normalize(RawPanelType);
        PanelTitle = panel.Value<string>("title") is { Length: > 0 } t ? t : $"Panel #{panel.Value<int>("id")}";
    }

    /// <summary>
    /// Extracts transformations from panel. Supports both root-level and data.spec.transformations.
    /// </summary>
    public static JArray GetTransformations(JObject panel)
    {
        var root = panel["transformations"] as JArray;
        if (root != null && root.Count > 0)
            return root;

        var spec = panel["data"]?["spec"]?["transformations"] as JArray;
        return spec ?? new JArray();
    }

    public TransformationContext WithTargets(IReadOnlyList<JObject> targets)
    {
        return new TransformationContext(Panel, new JArray(targets), Transformations);
    }
}
