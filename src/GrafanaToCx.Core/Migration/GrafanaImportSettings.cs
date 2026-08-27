namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Settings for the <c>grafana-import</c> flow: local Grafana dashboard exports published into a
/// Coralogix-hosted Grafana via <c>POST /api/dashboards/db</c>.
/// </summary>
/// <remarks>
/// <see cref="CheckpointFile"/> must differ from both <see cref="MigrationRunSettings.CheckpointFile"/>
/// and <see cref="ImportSettings.CheckpointFile"/>; <see cref="ImportOrchestrator.GuardCheckpointPaths"/>
/// enforces that before any work runs.
/// </remarks>
public sealed class GrafanaImportSettings
{
    /// <summary>Coralogix region, resolved to a Grafana base URL by <see cref="RegionMapper.ResolveGrafana"/>.</summary>
    public string Region { get; init; } = "eu1";

    /// <summary>Explicit Grafana API base URL. Overrides <see cref="Region"/> when non-empty.</summary>
    public string? Endpoint { get; init; }

    public string CheckpointFile { get; init; } = "grafana-import-checkpoint.json";
    public string ReportFile { get; init; } = "grafana-import-report.txt";

    public int MaxRetries { get; init; } = 5;
    public int InitialRetryDelaySeconds { get; init; } = 2;

    /// <summary>
    /// Whether to revisit dashboards already marked completed in the checkpoint. It does <b>not</b> control
    /// the <c>overwrite</c> flag on the save request, which is always true — see
    /// <c>GrafanaApiClient.PublishAsync</c> for why.
    /// </summary>
    public bool OverwriteExisting { get; init; } = true;

    /// <summary>Default for <c>--dry-run</c> when the flag is not passed.</summary>
    public bool DryRun { get; init; }

    /// <summary>Commit message recorded in each dashboard's Grafana version history.</summary>
    public string Message { get; init; } = "Imported by grafana-to-cx grafana-import";

    /// <summary>
    /// Source datasource uid (or legacy datasource name) to destination uid. Applied ahead of discovery
    /// from <c>GET /api/datasources</c>, so an explicit entry always wins.
    /// </summary>
    public Dictionary<string, string> DatasourceUidMap { get; init; } = [];

    /// <summary>
    /// When true, datasource references that match nothing fall back to the destination's default.
    /// Off by default: a panel pointed at the wrong backend renders empty, which is indistinguishable
    /// from "no data", whereas an unresolved reference says so on the panel.
    /// </summary>
    public bool AllowTargetDefaultFallback { get; init; }

    /// <summary>
    /// Same rules as <see cref="ImportSettings.Grouping"/>, but recursive by default: Grafana backup trees
    /// are one directory per team, so a top-level-only scan finds nothing.
    /// </summary>
    public FolderGroupingSettings Grouping { get; init; } = new() { Recursive = true };

    /// <summary>Adapts to the shape <c>ImportFlow</c> and <see cref="ImportOrchestrator"/> already consume.</summary>
    public ImportSettings ToImportSettings(
        FolderGroupingSettings? groupingOverride = null, bool? overwriteOverride = null) => new()
    {
        CheckpointFile = CheckpointFile,
        ReportFile = ReportFile,
        MaxRetries = MaxRetries,
        InitialRetryDelaySeconds = InitialRetryDelaySeconds,
        OverwriteExisting = overwriteOverride ?? OverwriteExisting,
        IsLocked = false,
        Grouping = groupingOverride ?? Grouping
    };
}
