using GrafanaToCx.Core.Converter;

namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Adapts the Grafana → Coralogix converter to the target-agnostic transformer port.
/// </summary>
/// <remarks>
/// Owns the <see cref="DashboardValidator"/> call because the Coralogix widget shape is exactly what
/// that validator checks. Also owns the diagnostics snapshot: <see cref="IGrafanaToCxConverter"/>
/// clears and refills its <see cref="IGrafanaToCxConverter.ConversionDiagnostics"/> list on the next
/// call, so holding the reference would make every report entry show the last dashboard's diagnostics.
/// </remarks>
public sealed class CoralogixTransformer(IGrafanaToCxConverter converter, DashboardValidator validator)
    : IDashboardTransformer
{
    public string TargetDisplayName => "Coralogix";

    public TransformOutcome Transform(string sourceJson, DashboardTransformContext context)
    {
        var options = new ConversionOptions
        {
            FolderId = context.FolderId,
            DashboardName = context.DashboardNameOverride,
            FanOutMultiQueryPanels = context.FanOutMultiQueryPanels
        };

        var converted = converter.ConvertToJObject(sourceJson, options);
        var diagnostics = converter.ConversionDiagnostics.ToList();

        var validation = validator.Validate(converted);
        if (!validation.IsValid)
            return new TransformOutcome(converted, string.Empty, null, diagnostics, validation.ErrorMessage);

        // Coralogix identity is (name, folder); there is no payload-carried id for the target to match on.
        var name = converted["name"]?.ToString() ?? string.Empty;
        return new TransformOutcome(converted, name, StableId: null, diagnostics);
    }
}
