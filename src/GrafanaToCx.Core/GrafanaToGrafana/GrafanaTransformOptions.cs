namespace GrafanaToCx.Core.GrafanaToGrafana;

public sealed class GrafanaTransformOptions
{
    /// <summary>Datasources on the destination, from <c>GET /api/datasources</c>.</summary>
    public DatasourceIndex Datasources { get; init; } = DatasourceIndex.Empty;

    /// <summary>
    /// Explicit remap, keyed by source datasource <b>uid</b> or source datasource <b>name</b>
    /// (name keys matter because pre-schemaVersion-33 refs are a bare name string with no uid).
    /// Values are destination uids. Wins over every discovery rule.
    /// </summary>
    public IReadOnlyDictionary<string, string> DatasourceOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, references that match nothing fall back to the destination's default datasource.
    /// Defaults to false: pointing an Elasticsearch panel at Prometheus renders empty with no error,
    /// which is indistinguishable from "no data", whereas an unresolved reference says so on the panel.
    /// </summary>
    public bool AllowTargetDefaultFallback { get; init; }

    /// <summary>Stable, run-independent seed for uid derivation — the source path relative to the import root.</summary>
    public string UidSeed { get; init; } = string.Empty;

    /// <summary>
    /// Source uids claimed by more than one file in this run. Members derive a uid rather than preserving
    /// theirs, so two files never publish over each other.
    /// </summary>
    public IReadOnlySet<string> ContestedSourceUids { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Library panel uids present on the destination. Empty disables the check.</summary>
    public IReadOnlySet<string> TargetLibraryPanelUids { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Plugin ids installed on the destination. Empty disables the check.</summary>
    public IReadOnlySet<string> TargetPluginIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string? TitleOverride { get; init; }

    public int SchemaVersionFloor { get; init; } = 30;

    public bool DropLegacyPanelAlerts { get; init; } = true;
}
