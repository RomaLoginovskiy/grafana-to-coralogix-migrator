using GrafanaToCx.Core.Converter;

namespace GrafanaToCx.Core.GrafanaToGrafana;

public enum TransformSeverity { Info, Warn, Error }

/// <summary>
/// One thing the transform did, or refused to do, at a specific JSON path.
/// </summary>
/// <remarks>
/// Keyed by JSON path rather than panel title because the interesting unit here is a datasource
/// reference or a dashboard-level field, not a panel. <see cref="ToPanelDiagnostic"/> projects onto the
/// shape <c>MigrationReport</c> already renders, so the report needs no changes.
/// </remarks>
public sealed record GrafanaTransformDiagnostic(
    string Code,
    TransformSeverity Severity,
    string Subject,
    string Path,
    string Outcome,
    string Reason,
    string? SourceValue = null,
    string? TargetValue = null)
{
    public PanelConversionDiagnostic ToPanelDiagnostic() =>
        new(PanelTitle: Path, PanelType: Subject, Outcome: Outcome, Reason: Reason, Code: Code);

    public static GrafanaTransformDiagnostic Info(
        string Code, string Subject, string Path, string Outcome, string Reason,
        string? SourceValue = null, string? TargetValue = null) =>
        new(Code, TransformSeverity.Info, Subject, Path, Outcome, Reason, SourceValue, TargetValue);

    public static GrafanaTransformDiagnostic Warn(
        string Code, string Subject, string Path, string Outcome, string Reason,
        string? SourceValue = null, string? TargetValue = null) =>
        new(Code, TransformSeverity.Warn, Subject, Path, Outcome, Reason, SourceValue, TargetValue);
}

/// <summary>Diagnostic codes emitted by <see cref="GrafanaDashboardTransform"/>.</summary>
public static class TransformCodes
{
    public const string DatasourceUnresolved = "G2G-DS-001";
    public const string DatasourceRemapped = "G2G-DS-002";
    public const string DatasourceAmbiguous = "G2G-DS-003";
    public const string InputUnresolved = "G2G-DS-004";
    public const string DatasourceVariableLegacy = "G2G-DS-005";

    public const string UidDerivedFromPath = "G2G-UID-001";
    public const string UidInvalid = "G2G-UID-002";
    public const string UidContested = "G2G-UID-003";

    public const string ForeignIdDropped = "G2G-ID-001";
    public const string EnvelopeFieldStripped = "G2G-ENVELOPE-001";
    public const string SchemaVersionOld = "G2G-SCHEMA-001";
    public const string LegacyAlertDropped = "G2G-ALERT-001";
    public const string LibraryPanelMissing = "G2G-LIB-001";
    public const string PluginMissing = "G2G-PLUGIN-001";
}
