namespace GrafanaToCx.Core.Converter;

/// <summary>
/// Records an element that did not survive conversion but is not a panel — an annotation,
/// a link, a variable, a transformation. <see cref="PanelConversionDiagnostic"/> is keyed on
/// panel title and type, which are meaningless for dashboard-wide elements, so these are
/// collected separately and reported in their own section.
/// </summary>
public sealed record DashboardConversionDiagnostic(
    string ElementKind,
    string ElementName,
    string Reason,
    string Code,
    /// <summary>Set when the element belongs to a panel (a transformation, link or repeat).</summary>
    string? PanelTitle = null,
    string Outcome = "dropped");

/// <summary>
/// Diagnostic codes for dashboard-level losses. Kept alongside the panel codes
/// (UNS-*/DGR-*) so a report reader sees one consistent vocabulary.
/// </summary>
public static class DashboardDiagnosticCodes
{
    public const string Annotation = "UNS-ANN-001";
    public const string DashboardLink = "UNS-DLK-001";
    public const string PanelLink = "UNS-PLK-001";
    public const string PanelRepeat = "UNS-RPT-001";
    public const string Transformation = "UNS-TRF-001";
    public const string Variable = "UNS-VAR-001";
    public const string QueryMatcher = "DGR-VAR-002";
}
