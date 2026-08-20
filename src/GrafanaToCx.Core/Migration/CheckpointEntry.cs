using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GrafanaToCx.Core.Migration;

public enum CheckpointStatus
{
    Pending,
    Completed,
    FailedCritical,
    FailedRetryable
}

public sealed class CheckpointEntry
{
    public string GrafanaUid { get; set; } = string.Empty;
    public string GrafanaTitle { get; set; } = string.Empty;
    public string FolderTitle { get; set; } = string.Empty;

    [JsonConverter(typeof(StringEnumConverter))]
    public CheckpointStatus Status { get; set; } = CheckpointStatus.Pending;

    public string? CxDashboardId { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }

    // Fields below are populated by the import flow only. They are omitted when null so that
    // checkpoint files written by migrate stay byte-identical to what it wrote before.

    /// <summary>
    /// Source file path relative to the import root, using '/' separators. Required to identify entries
    /// keyed by path (dashboards with a missing or colliding uid).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SourcePath { get; set; }

    /// <summary>
    /// Coralogix folder this entry's dashboard was published into. Lets a stale
    /// <see cref="CxDashboardId"/> be validated against its folder before being used as a replace target.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CxFolderId { get; set; }

    /// <summary>
    /// SHA-256 of the source file bytes, for detecting edits between runs.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SourceHash { get; set; }
}
