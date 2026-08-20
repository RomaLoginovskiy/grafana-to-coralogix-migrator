using GrafanaToCx.Core.Migration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.GrafanaToGrafana;

/// <summary>
/// Adapts <see cref="GrafanaDashboardTransform"/> to the target-agnostic transformer port.
/// </summary>
/// <remarks>
/// Holds the per-run inputs the transform needs but the orchestrator has no reason to know about — the
/// destination's datasource index, the configured overrides — so the port stays free of Grafana concepts.
/// </remarks>
public sealed class GrafanaTransformer(
    IGrafanaDashboardTransform transform,
    DatasourceIndex datasources,
    IReadOnlyDictionary<string, string> datasourceOverrides,
    bool allowTargetDefaultFallback = false) : IDashboardTransformer
{
    public string TargetDisplayName => "Grafana";

    public TransformOutcome Transform(string sourceJson, DashboardTransformContext context)
    {
        JObject source;
        try
        {
            source = JObject.Parse(sourceJson);
        }
        catch (JsonException ex)
        {
            return TransformOutcome.Invalid($"source is not valid JSON: {ex.Message}");
        }

        var result = transform.Transform(source, new GrafanaTransformOptions
        {
            Datasources = datasources,
            DatasourceOverrides = datasourceOverrides,
            AllowTargetDefaultFallback = allowTargetDefaultFallback,
            UidSeed = context.RelativePath,
            ContestedSourceUids = context.ContestedSourceUids ?? new HashSet<string>(StringComparer.Ordinal),
            TitleOverride = context.DashboardNameOverride
        });

        if (string.IsNullOrWhiteSpace(result.Title))
            return TransformOutcome.Invalid("dashboard has no title", ReportDiagnostics(result));

        if (result.Dashboard["panels"] is not JArray)
            return TransformOutcome.Invalid("dashboard has no 'panels' array", ReportDiagnostics(result));

        return new TransformOutcome(
            result.Dashboard, result.Title, result.Uid, ReportDiagnostics(result));
    }

    /// <summary>
    /// Info-level entries are dropped: a large dashboard remaps hundreds of datasource references, and
    /// listing each one would bury the warnings that need a human.
    /// </summary>
    private static IReadOnlyList<Converter.PanelConversionDiagnostic> ReportDiagnostics(GrafanaTransformResult result) =>
        result.Diagnostics
            .Where(d => d.Severity != TransformSeverity.Info)
            .Select(d => d.ToPanelDiagnostic())
            .ToList();
}
