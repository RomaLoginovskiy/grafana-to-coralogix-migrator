using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

/// <summary>
/// Publishes a planned set of local Grafana dashboard files to a destination, with checkpoint/resume,
/// retry classification, and a written report.
/// </summary>
/// <remarks>
/// The destination is reached only through <see cref="IDashboardTransformer"/> and
/// <see cref="IDashboardPublisher"/>, so this one loop serves both Coralogix custom dashboards and
/// Coralogix-hosted Grafana.
/// <para>
/// Deliberately separate from <see cref="MigrationOrchestrator"/> rather than a generalization of it:
/// that type's overwrite behaviour is pinned by a documented contract
/// (<c>spec/components/migration-orchestrator-overwrite.md</c>) with named regression criteria, and the
/// two flows differ in source, folder derivation, and backup requirements.
/// </para>
/// </remarks>
public sealed class ImportOrchestrator
{
    private readonly IDashboardTransformer _transformer;
    private readonly IDashboardPublisher _publisher;
    private readonly CheckpointStore _checkpoint;
    private readonly MigrationReport _report;
    private readonly ImportSettings _settings;
    private readonly IImportSourceReader _sourceReader;
    private readonly ILogger<ImportOrchestrator> _logger;

    private readonly Dictionary<CatalogKey, string> _catalogIndex = new(CatalogKey.Comparer);

    public ImportOrchestrator(
        IDashboardTransformer transformer,
        IDashboardPublisher publisher,
        CheckpointStore checkpoint,
        MigrationReport report,
        ImportSettings settings,
        ILogger<ImportOrchestrator> logger,
        IImportSourceReader? sourceReader = null)
    {
        _transformer = transformer;
        _publisher = publisher;
        _checkpoint = checkpoint;
        _report = report;
        _settings = settings;
        _logger = logger;
        _sourceReader = sourceReader ?? new FileImportSourceReader();
    }

    /// <summary>
    /// Throws when the import checkpoint would overwrite the migrate checkpoint.
    /// </summary>
    public static void GuardCheckpointPath(string importCheckpointFile, string migrationCheckpointFile)
    {
        if (string.IsNullOrWhiteSpace(importCheckpointFile) || string.IsNullOrWhiteSpace(migrationCheckpointFile))
            return;

        var importPath = Path.GetFullPath(importCheckpointFile);
        var migratePath = Path.GetFullPath(migrationCheckpointFile);

        if (string.Equals(importPath, migratePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Import checkpoint '{importCheckpointFile}' resolves to the migration checkpoint file. " +
                "Import must use a separate checkpoint — set import.checkpointFile to a different path.");
        }
    }

    /// <summary>
    /// Throws when any two of the configured checkpoint paths resolve to the same file.
    /// </summary>
    /// <remarks>
    /// The Playwright migration suite resolves dashboard IDs out of <c>migration-checkpoint.json</c> and
    /// fails on duplicate completed titles, which sharing the file would eventually produce. There are now
    /// three destinations (migrate, import, grafana-import), so the check is N-way rather than pairwise.
    /// Checked before any work so a misconfiguration cannot partially execute.
    /// Null and whitespace entries are ignored — an unconfigured flow cannot collide with anything.
    /// </remarks>
    public static void GuardCheckpointPaths(params string?[] checkpointFiles)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in checkpointFiles)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;

            var full = Path.GetFullPath(file);
            if (seen.TryGetValue(full, out var first))
            {
                throw new InvalidOperationException(
                    $"Checkpoint '{file}' resolves to the same file as '{first}'. " +
                    "migrate, import and grafana-import must each use a separate checkpoint.");
            }

            seen[full] = file;
        }
    }

    public async Task<ImportRunSummary> RunAsync(
        ImportPlan plan,
        string migrationCheckpointFile = "migration-checkpoint.json",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        GuardCheckpointPath(_settings.CheckpointFile, migrationCheckpointFile);

        await _checkpoint.LoadAsync(ct);
        _logger.LogInformation("Import checkpoint loaded from '{File}'.", _settings.CheckpointFile);

        await BuildCatalogIndexAsync(ct);

        var keys = ImportCheckpointKey.ResolveKeys(plan.Items
            .Select(i => new ImportKeyCandidate(i.RelativePath, i.Uid, i.CxFolderId))
            .ToList());

        var contestedUids = ResolveContestedUids(plan);

        var completed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();

            var key = keys[ImportCheckpointKey.NormalizePath(item.RelativePath)];
            var existing = _checkpoint.Get(key);

            if (ShouldSkipAsCompleted(existing))
            {
                _logger.LogInformation("Skipping '{Title}' — already completed.", item.Title);
                skipped++;
                _report.Add(BuildReportEntry(item, existing!, []));
                continue;
            }

            if (ShouldSkipUntilRetry(existing))
            {
                _logger.LogInformation("Skipping '{Title}' — retry not due until {NextRetry}.",
                    item.Title, existing!.NextRetryAt);
                skipped++;
                _report.Add(BuildReportEntry(item, existing, []));
                continue;
            }

            var entry = existing ?? new CheckpointEntry();
            entry.GrafanaUid = item.Uid ?? string.Empty;
            entry.GrafanaTitle = item.EffectiveName;
            entry.FolderTitle = item.FolderDisplayName;
            entry.SourcePath = ImportCheckpointKey.NormalizePath(item.RelativePath);
            entry.CxFolderId = item.CxFolderId;
            entry.LastAttemptAt = DateTimeOffset.UtcNow;

            var diagnostics = await AttemptImportAsync(item, entry, contestedUids, ct);

            _checkpoint.Upsert(key, entry);
            await _checkpoint.SaveAsync(ct);
            _report.Add(BuildReportEntry(item, entry, diagnostics));

            if (entry.Status == CheckpointStatus.Completed) completed++;
            else failed++;
        }

        if (!string.IsNullOrWhiteSpace(_settings.ReportFile))
        {
            await _report.SaveAsync(_settings.ReportFile, ct);
            _logger.LogInformation("Import report written to '{File}'.", _settings.ReportFile);
        }

        return new ImportRunSummary(completed, skipped, failed);
    }

    private async Task<IReadOnlyList<PanelConversionDiagnostic>> AttemptImportAsync(
        ImportPlanItem item,
        CheckpointEntry entry,
        IReadOnlySet<string> contestedUids,
        CancellationToken ct)
    {
        _logger.LogInformation("Importing '{Title}' from '{Path}'...", item.Title, item.RelativePath);

        try
        {
            string json;
            try
            {
                json = await _sourceReader.ReadAsync(item.AbsolutePath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, $"Could not read source file: {ex.Message}");
                return [];
            }

            TransformOutcome outcome;
            try
            {
                outcome = _transformer.Transform(json, new DashboardTransformContext(
                    item.RelativePath, item.CxFolderId, item.DashboardNameOverride, contestedUids));
            }
            catch (Exception ex)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, $"Conversion error: {ex.Message}");
                return [];
            }

            var diagnostics = outcome.Diagnostics;

            if (outcome.ValidationError is not null)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, $"Validation failed: {outcome.ValidationError}");
                return diagnostics;
            }

            var dashboardName = string.IsNullOrWhiteSpace(outcome.DashboardName)
                ? item.EffectiveName
                : outcome.DashboardName;

            string? existingId = null;
            if (_settings.OverwriteExisting)
            {
                existingId = FindExistingTargetId(entry, dashboardName, item.CxFolderId);
                if (existingId is null)
                {
                    _logger.LogInformation("Dashboard '{Name}' not found in {Target} — creating new.",
                        dashboardName, _publisher.TargetDisplayName);
                }
            }

            var result = await _publisher.PublishAsync(new PublishRequest(
                outcome.Dashboard,
                dashboardName,
                outcome.StableId,
                item.CxFolderId,
                existingId,
                _settings.IsLocked), ct);

            if (result.Success && result.TargetId is not null)
                MarkCompleted(entry, result.TargetId, dashboardName, item.CxFolderId);
            else
                ApplyPublishFailure(entry, result);

            return diagnostics;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Import cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error importing '{Title}'.", item.Title);
            MarkFailed(entry, CheckpointStatus.FailedCritical, $"Unexpected error: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Source uids claimed by more than one file in this run. All claimants are reported, not just the
    /// later ones, so a transformer that derives a replacement identity produces the same result whatever
    /// order the files were enumerated in — the same rule <see cref="ImportCheckpointKey.ResolveKeys"/> uses.
    /// </summary>
    private static IReadOnlySet<string> ResolveContestedUids(ImportPlan plan) =>
        plan.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Uid))
            .GroupBy(i => i.Uid!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    private async Task BuildCatalogIndexAsync(CancellationToken ct)
    {
        _catalogIndex.Clear();

        var catalog = await _publisher.GetCatalogAsync(ct);
        foreach (var item in catalog)
        {
            // Later duplicates lose — the first match is what a name+folder lookup would have found.
            _catalogIndex.TryAdd(new CatalogKey(item.Name, item.FolderId), item.Id);
        }

        _logger.LogInformation("{Target} catalog snapshot taken at {Timestamp} ({Count} dashboard(s)).",
            _publisher.TargetDisplayName, DateTimeOffset.UtcNow, _catalogIndex.Count);
    }

    private string? FindExistingTargetId(CheckpointEntry entry, string dashboardName, string? folderId)
    {
        if (!string.IsNullOrEmpty(entry.CxDashboardId))
            return entry.CxDashboardId;

        if (string.IsNullOrEmpty(dashboardName))
            return null;

        return _catalogIndex.TryGetValue(new CatalogKey(dashboardName, folderId), out var id) ? id : null;
    }

    private void MarkCompleted(CheckpointEntry entry, string cxDashboardId, string dashboardName, string? folderId)
    {
        entry.Status = CheckpointStatus.Completed;
        entry.CxDashboardId = cxDashboardId;
        entry.CxFolderId = folderId;
        entry.ErrorMessage = null;
        entry.NextRetryAt = null;

        // Keep the snapshot current so a second file converting to the same name replaces this dashboard
        // rather than creating a duplicate.
        _catalogIndex[new CatalogKey(dashboardName, folderId)] = cxDashboardId;
    }

    private void ApplyPublishFailure(CheckpointEntry entry, PublishResult result)
    {
        var error = result.ErrorMessage ?? "Unknown publish error";

        if (result.StatusCode.HasValue &&
            RetryPolicy.Classify(result.StatusCode.Value) == FailureKind.Critical)
        {
            MarkFailed(entry, CheckpointStatus.FailedCritical, error);
            return;
        }

        entry.RetryCount++;

        // Retries are scheduled across runs, not looped in-process, so nothing else would ever stop a
        // permanently-broken dashboard from being reattempted on every future run.
        if (entry.RetryCount >= _settings.MaxRetries)
        {
            MarkFailed(entry, CheckpointStatus.FailedCritical,
                $"{error} (gave up after {entry.RetryCount} retryable attempt(s); import.maxRetries is {_settings.MaxRetries})");

            _logger.LogWarning("Dashboard '{Title}' failed {Count} time(s) — giving up.",
                entry.GrafanaTitle, entry.RetryCount);
            return;
        }

        // A server that states how long it wants to be left alone knows better than an exponential guess.
        var delay = result.RetryAfter
                    ?? RetryPolicy.ComputeDelay(entry.RetryCount, _settings.InitialRetryDelaySeconds);

        MarkFailed(entry, CheckpointStatus.FailedRetryable, error);
        entry.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);

        _logger.LogWarning("Dashboard '{Title}' failed (retryable). Next retry at {NextRetry}.",
            entry.GrafanaTitle, entry.NextRetryAt);
    }

    private bool ShouldSkipAsCompleted(CheckpointEntry? entry) =>
        !_settings.OverwriteExisting && entry?.Status == CheckpointStatus.Completed;

    private static bool ShouldSkipUntilRetry(CheckpointEntry? entry) =>
        entry?.Status == CheckpointStatus.FailedRetryable &&
        entry.NextRetryAt.HasValue &&
        entry.NextRetryAt.Value > DateTimeOffset.UtcNow;

    private static void MarkFailed(CheckpointEntry entry, CheckpointStatus status, string error)
    {
        entry.Status = status;
        entry.ErrorMessage = error;

        // Cleared unconditionally because entries are reused across runs: a retryable failure followed by
        // a critical one would otherwise keep advertising a retry that will never be attempted. The
        // retryable path re-sets it immediately after.
        entry.NextRetryAt = null;
    }

    private static MigrationReportEntry BuildReportEntry(
        ImportPlanItem item,
        CheckpointEntry entry,
        IReadOnlyList<PanelConversionDiagnostic> diagnostics) =>
        new()
        {
            FolderTitle = item.FolderDisplayName,
            DashboardTitle = item.EffectiveName,
            Status = entry.Status,
            CxDashboardId = entry.CxDashboardId,
            ErrorMessage = entry.ErrorMessage,
            ConversionDiagnostics = diagnostics
        };

    private readonly record struct CatalogKey(string Name, string? FolderId)
    {
        public static readonly IEqualityComparer<CatalogKey> Comparer = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<CatalogKey>
        {
            public bool Equals(CatalogKey x, CatalogKey y) =>
                string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(x.FolderId), Normalize(y.FolderId), StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(CatalogKey obj) =>
                HashCode.Combine(
                    obj.Name?.ToLowerInvariant(),
                    Normalize(obj.FolderId).ToLowerInvariant());

            private static string Normalize(string? folderId) =>
                string.IsNullOrWhiteSpace(folderId) ? ImportCheckpointKey.NoFolder : folderId;
        }
    }
}
