using GrafanaToCx.Core.Converter;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Turns raw source dashboard JSON into a body the destination will accept, and says whether the
/// result is publishable.
/// </summary>
/// <remarks>
/// Validation lives here rather than in the orchestrator because what counts as a valid payload is a
/// property of the destination: <c>DashboardValidator</c> checks the Coralogix widget shape and means
/// nothing for a Grafana dashboard.
/// </remarks>
public interface IDashboardTransformer
{
    /// <summary>Shown in log and console output, e.g. "Coralogix" or "Grafana".</summary>
    string TargetDisplayName { get; }

    /// <summary>Never throws for content reasons — a rejected dashboard comes back with a ValidationError.</summary>
    TransformOutcome Transform(string sourceJson, DashboardTransformContext context);
}

/// <param name="RelativePath">Source path relative to the import root. Stable across runs, so it can seed derived identity.</param>
/// <param name="ContestedSourceUids">
/// Source uids claimed by more than one file in this run. Members must not preserve their uid, or two
/// files would publish over each other.
/// </param>
public sealed record DashboardTransformContext(
    string RelativePath,
    string? FolderId,
    string? DashboardNameOverride,
    IReadOnlySet<string>? ContestedSourceUids = null);

/// <param name="StableId">An identifier the target matches on, carried inside the payload (Grafana uid). Null for Coralogix.</param>
/// <param name="Diagnostics">
/// Returned by value rather than read off the transformer. The Coralogix converter exposes a mutable list
/// it refills on every call, which forces callers to snapshot defensively; carrying diagnostics in the
/// result makes that aliasing hazard structurally impossible.
/// </param>
/// <param name="ValidationError">Non-null means the dashboard must not be published. Becomes a critical failure.</param>
public sealed record TransformOutcome(
    JObject Dashboard,
    string DashboardName,
    string? StableId,
    IReadOnlyList<PanelConversionDiagnostic> Diagnostics,
    string? ValidationError = null)
{
    public static TransformOutcome Invalid(
        string validationError, IReadOnlyList<PanelConversionDiagnostic>? diagnostics = null) =>
        new(new JObject(), string.Empty, null, diagnostics ?? [], validationError);
}
