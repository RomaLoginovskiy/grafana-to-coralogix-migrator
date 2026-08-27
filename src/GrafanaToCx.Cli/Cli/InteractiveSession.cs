using Newtonsoft.Json;

namespace GrafanaToCx.Cli.Cli;

/// <summary>
/// The answers an operator already gave in the interactive console, remembered so the menus can offer
/// them back as prompt defaults instead of asking again.
/// </summary>
/// <remarks>
/// <para>
/// Holds no credentials, by design. The Coralogix and Grafana API keys live only in the in-memory
/// <see cref="SessionConfig"/> for the life of the process: this record is written to a file under the
/// user's home directory, and a session that could be resumed straight into a live tenant without the
/// operator re-supplying a key would turn "remember my last root directory" into a secret at rest.
/// Resuming therefore always re-asks for the key (or reads <c>CX_API_KEY</c>).
/// </para>
/// <para>
/// Mutable rather than a record: handlers record answers onto the live instance as they collect them, so
/// an action interrupted halfway still leaves what was already answered. Property names are the wire
/// format — nothing in this repo configures a naming strategy, so these serialize PascalCase, unlike the
/// camelCase <c>migration-settings.json</c> that the case-insensitive configuration binder reads.
/// </para>
/// <para>
/// Every remembered answer is null-omitted so that adding a field later leaves existing session files
/// loadable and unchanged, the same reason <c>CheckpointEntry</c>'s import-only fields are.
/// </para>
/// </remarks>
public sealed class InteractiveSession
{
    /// <summary>
    /// Short hex id, also the file stem. Short because the operator types it after <c>--resume</c>;
    /// <see cref="SessionStore.Resolve"/> accepts any unambiguous prefix of it.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Drives both the <c>--continue</c> ordering and the newest-first resume picker.</summary>
    public DateTimeOffset LastUsedAt { get; set; }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Coralogix region chosen at startup. The endpoint is deliberately not stored alongside it: it is
    /// derived from the region by <c>RegionMapper.Resolve</c>, and keeping both invites a resumed session
    /// whose endpoint and region disagree about which tenant it means.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Region { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SettingsFile { get; set; }

    // ── Option 7 — Grafana Import ─────────────────────────────────────────────

    /// <summary>
    /// Separate from <see cref="Region"/>: the target Grafana is picked independently of the Coralogix
    /// REST endpoint the rest of the session talks to.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GrafanaImportRegion { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GrafanaImportRootDir { get; set; }

    /// <summary>
    /// Nullable so "never answered" stays distinct from "answered no" — collapsing the two would make a
    /// fresh session's remembered default indistinguishable from a deliberate opt-out, which is the same
    /// distinction <c>ParsedArgs.GetBoolOrNull</c> exists to preserve for paired --x/--no-x flags.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? GrafanaImportRecursive { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? GrafanaImportDryRun { get; set; }

    // ── Option 3 — Import ─────────────────────────────────────────────────────

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ImportRootDir { get; set; }

    // ── Option 1 — Convert ────────────────────────────────────────────────────

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ConvertInput { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ConvertOutput { get; set; }

    // ── Option 2 — Push ───────────────────────────────────────────────────────

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? PushInput { get; set; }

    // ── Option 6 — Cleanup ────────────────────────────────────────────────────

    /// <summary>
    /// The directory only, never the full backup path. That default is a timestamped filename recomputed
    /// on every entry, so remembering it whole would offer back a name that already exists on disk and
    /// make the "overwrite?" confirmation fire on every run.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? CleanupBackupDirectory { get; set; }
}
