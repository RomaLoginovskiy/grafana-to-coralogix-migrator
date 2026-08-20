namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Settings for the local-file <c>import</c> flow.
/// </summary>
/// <remarks>
/// <see cref="CheckpointFile"/> and <see cref="ReportFile"/> intentionally differ from
/// <see cref="MigrationRunSettings"/>. The Playwright migration suite resolves dashboard IDs out of
/// <c>migration-checkpoint.json</c>, so import writing to that file would corrupt it.
/// <see cref="ImportOrchestrator"/> enforces this with a hard guard.
/// </remarks>
public sealed class ImportSettings
{
    public string CheckpointFile { get; init; } = "import-checkpoint.json";
    public string ReportFile { get; init; } = "import-report.txt";

    public int MaxRetries { get; init; } = 5;
    public int InitialRetryDelaySeconds { get; init; } = 2;

    /// <summary>
    /// Defaults to <c>true</c>, unlike <see cref="CoralogixSettings.OverwriteExisting"/>. The import flow has
    /// always replaced dashboards matching on name + folder, so defaulting to false would silently change
    /// behaviour for existing callers of <c>cx import</c>.
    /// </summary>
    public bool OverwriteExisting { get; init; } = true;

    public bool IsLocked { get; init; }

    public FolderGroupingSettings Grouping { get; init; } = new();
}

/// <summary>
/// Controls how source filenames are mapped to Coralogix folder names.
/// </summary>
public sealed class FolderGroupingSettings
{
    public string Separator { get; init; } = " - ";
    public int SegmentCount { get; init; } = 2;

    /// <summary>
    /// 1-based index of the first filename segment used as the folder name. Defaults to 1 — the leading
    /// segments. Raise it when the team name sits in the middle of the filename rather than at the front.
    /// </summary>
    public int SegmentStart { get; init; } = 1;

    /// <summary>
    /// When false (default), only the top directory is scanned — matching the previous import behaviour.
    /// </summary>
    public bool Recursive { get; init; }

    /// <summary>
    /// Folder for files whose names yield no prefix. Null means "import with no folder".
    /// </summary>
    public string? UngroupedFolderName { get; init; }
}
