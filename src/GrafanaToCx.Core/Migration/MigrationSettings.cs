namespace GrafanaToCx.Core.Migration;

public sealed class MigrationSettings
{
    public GrafanaSettings Grafana { get; init; } = new();
    public CoralogixSettings Coralogix { get; init; } = new();
    public MigrationCredentialsSettings Credentials { get; init; } = new();
    public MigrationRunSettings Migration { get; init; } = new();

    /// <summary>
    /// Settings for the local-file <c>import</c> flow. Deliberately separate from <see cref="Migration"/>
    /// so import never shares a checkpoint file with migrate.
    /// </summary>
    public ImportSettings Import { get; init; } = new();

    /// <summary>
    /// Settings for the <c>grafana-import</c> flow, which publishes local dashboard exports into a
    /// Coralogix-hosted Grafana. Separate from <see cref="Import"/> so the two never share a checkpoint.
    /// </summary>
    public GrafanaImportSettings GrafanaImport { get; init; } = new();
}

public sealed class MigrationCredentialsSettings
{
    public string? GrafanaApiKey { get; init; }
    public string? CxApiKey { get; init; }
}

public sealed class GrafanaSettings
{
    public string Region { get; init; } = "eu2";
    public List<string> Folders { get; init; } = [];
}

public sealed class CoralogixSettings
{
    public string Region { get; init; } = "eu2";
    public string? FolderId { get; init; }
    public bool IsLocked { get; init; }
    public bool MigrateFolderStructure { get; init; }

    /// <summary>
    /// When MigrateFolderStructure is true, auto-created CX folders are nested under this parent folder ID.
    /// </summary>
    public string? ParentFolderId { get; init; }

    /// <summary>
    /// Explicit per-folder mapping: Grafana folder title → Coralogix folder ID.
    /// When present for a folder, takes priority over MigrateFolderStructure and FolderId.
    /// A null value means "no folder" for that Grafana folder.
    /// </summary>
    public Dictionary<string, string?> FolderMappings { get; init; } = [];

    /// <summary>
    /// When true, dashboards that already exist in Coralogix (matched by name or checkpoint ID) are replaced
    /// instead of skipped. Completed checkpoint entries are re-processed.
    /// </summary>
    public bool OverwriteExisting { get; init; }
}

public sealed class MigrationRunSettings
{
    public string CheckpointFile { get; init; } = "migration-checkpoint.json";
    public string ReportFile { get; init; } = "migration-report.txt";

    /// <summary>
    /// Path for the pre-migration backup ZIP archive. Set to empty string to disable backup.
    /// </summary>
    public string BackupFile { get; init; } = "grafana-backup.zip";

    public int MaxRetries { get; init; } = 5;
    public int InitialRetryDelaySeconds { get; init; } = 2;
    public MultiLuceneMergeSettings MultiLuceneMerge { get; init; } = new();

    /// <summary>
    /// When true, a stat panel with several queries becomes one widget per query rather than
    /// keeping the first and dropping the rest. Preserves the data; adds widgets and so changes
    /// the layout of dashboards built around the idiom. On by default.
    /// </summary>
    /// <remarks>
    /// The initialiser is required, not decorative: this is bound from a settings file that
    /// usually omits the key, and a bare <c>init</c> bool would then deserialize to false and
    /// silently restore the lossy behaviour.
    /// </remarks>
    public bool FanOutMultiQueryPanels { get; init; } = true;

    /// <summary>
    /// Optional cx CLI profile used for pre-upload validation. Needed when the account
    /// authenticates via OAuth; with a key-authenticated account the migration's own
    /// CX_API_KEY is used and this can stay empty. Validation is skipped entirely when the
    /// cx CLI is not installed.
    /// </summary>
    public string? CxCliProfile { get; init; }
}

public sealed class MultiLuceneMergeSettings
{
    public List<string> AllowlistedWidgetTypes { get; init; } = [];
}
