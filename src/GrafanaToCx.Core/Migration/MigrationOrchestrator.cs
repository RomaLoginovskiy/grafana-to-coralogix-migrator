using GrafanaToCx.Core.ApiClient;
using GrafanaToCx.Core.Converter;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Migration;

public sealed class MigrationOrchestrator
{
    private readonly IGrafanaClient _grafanaClient;
    private readonly IGrafanaToCxConverter _converter;
    private readonly ICoralogixDashboardsClient _cxClient;
    private readonly ICoralogixFoldersClient? _cxFoldersClient;
    private readonly GrafanaDashboardBackupService? _backupService;
    private readonly CxCliDashboardChecker? _cxChecker;
    private readonly DashboardValidator _validator;
    private readonly CheckpointStore _checkpoint;
    private readonly MigrationReport _report;
    private readonly MigrationSettings _settings;
    private readonly ILogger<MigrationOrchestrator> _logger;
    private List<DashboardCatalogItem>? _catalogCache;

    public MigrationOrchestrator(
        IGrafanaClient grafanaClient,
        IGrafanaToCxConverter converter,
        ICoralogixDashboardsClient cxClient,
        DashboardValidator validator,
        CheckpointStore checkpoint,
        MigrationReport report,
        MigrationSettings settings,
        ILogger<MigrationOrchestrator> logger,
        ICoralogixFoldersClient? cxFoldersClient = null,
        GrafanaDashboardBackupService? backupService = null,
        CxCliDashboardChecker? cxChecker = null)
    {
        _grafanaClient = grafanaClient;
        _converter = converter;
        _cxClient = cxClient;
        _cxFoldersClient = cxFoldersClient;
        _backupService = backupService;
        _cxChecker = cxChecker;
        _validator = validator;
        _checkpoint = checkpoint;
        _report = report;
        _settings = settings;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _checkpoint.LoadAsync(ct);
        _logger.LogInformation("Checkpoint loaded from '{File}'.", _settings.Migration.CheckpointFile);

        var folders = await _grafanaClient.GetFoldersAsync(_settings.Grafana.Folders, ct);
        _logger.LogInformation("Found {Count} folder(s) to process.", folders.Count);

        if (_settings.Coralogix.OverwriteExisting)
        {
            _catalogCache = await _cxClient.GetCatalogItemsAsync(ct);
            _logger.LogInformation("Overwrite mode: loaded {Count} dashboard(s) from CX catalog.", _catalogCache.Count);
        }

        if (_backupService is not null && !string.IsNullOrEmpty(_settings.Migration.BackupFile))
        {
            try
            {
                await _backupService.BackupAsync(folders, _settings.Migration.BackupFile, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Backup failed — migration will continue without a backup.");
            }
        }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            string? cxFolderId;

            if (_settings.Coralogix.FolderMappings.TryGetValue(folder.Title, out var mappedId))
            {
                cxFolderId = mappedId;
            }
            else if (_settings.Coralogix.MigrateFolderStructure && _cxFoldersClient is not null)
            {
                cxFolderId = await _cxFoldersClient.GetOrCreateFolderAsync(
                    folder.Title, _settings.Coralogix.ParentFolderId, ct);
                if (cxFolderId is null)
                {
                    _logger.LogWarning(
                        "Failed to create CX folder for '{FolderTitle}'. Dashboards will use the default folder.",
                        folder.Title);
                    cxFolderId = _settings.Coralogix.FolderId;
                }
            }
            else
            {
                cxFolderId = _settings.Coralogix.FolderId;
            }

            var options = new ConversionOptions
            {
                FolderId = cxFolderId,
                FanOutMultiQueryPanels = _settings.Migration.FanOutMultiQueryPanels
            };
            await ProcessFolderAsync(folder, options, ct);
        }

        var reportText = _report.Build();
        await _report.SaveAsync(_settings.Migration.ReportFile, ct);
        Console.WriteLine(reportText);
    }

    private async Task ProcessFolderAsync(GrafanaFolder folder, ConversionOptions options, CancellationToken ct)
    {
        var dashboardRefs = await _grafanaClient.GetDashboardsInFolderAsync(folder.Id, ct);
        _logger.LogInformation("Folder '{Folder}': {Count} dashboard(s).", folder.Title, dashboardRefs.Count);

        foreach (var dashRef in dashboardRefs)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessDashboardAsync(folder, dashRef, options, ct);
        }
    }

    private async Task ProcessDashboardAsync(
        GrafanaFolder folder,
        GrafanaDashboardRef dashRef,
        ConversionOptions options,
        CancellationToken ct)
    {
        var existing = _checkpoint.Get(dashRef.Uid);

        if (ShouldSkipAsCompleted(existing))
        {
            _logger.LogInformation("Skipping '{Title}' — already completed.", dashRef.Title);
            _report.Add(BuildReportEntry(folder.Title, existing!));
            return;
        }

        if (ShouldSkipUntilRetry(existing))
        {
            _logger.LogInformation("Skipping '{Title}' — next retry scheduled for {NextRetry}.",
                dashRef.Title, existing!.NextRetryAt);
            _report.Add(BuildReportEntry(folder.Title, existing!));
            return;
        }

        var entry = existing ?? new CheckpointEntry
        {
            GrafanaUid = dashRef.Uid,
            GrafanaTitle = dashRef.Title,
            FolderTitle = folder.Title
        };

        entry.LastAttemptAt = DateTimeOffset.UtcNow;

        await AttemptMigrationAsync(entry, options, ct);

        _checkpoint.Upsert(entry);
        await _checkpoint.SaveAsync(ct);
        _report.Add(BuildReportEntry(
            folder.Title, entry, _converter.ConversionDiagnostics, _converter.DashboardDiagnostics));
    }

    private async Task AttemptMigrationAsync(CheckpointEntry entry, ConversionOptions options, CancellationToken ct)
    {
        _logger.LogInformation("Migrating '{Title}' ({Uid})...", entry.GrafanaTitle, entry.GrafanaUid);

        try
        {
            var grafanaResponse = await _grafanaClient.GetDashboardByUidAsync(entry.GrafanaUid, ct);
            if (grafanaResponse is null)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, "Failed to download Grafana dashboard.");
                return;
            }

            JObject converted;
            try
            {
                converted = _converter.ConvertToJObject(grafanaResponse.ToString(Formatting.None), options);
            }
            catch (Exception ex)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, $"Conversion error: {ex.Message}");
                return;
            }

            var validation = _validator.Validate(converted);
            if (!validation.IsValid)
            {
                MarkFailed(entry, CheckpointStatus.FailedCritical, $"Validation failed: {validation.ErrorMessage}");
                return;
            }

            if (!await PassesCxCheckAsync(entry, converted, ct))
                return;

            if (_settings.Coralogix.OverwriteExisting)
            {
                var existingId = FindExistingCxId(entry, converted, options.FolderId);
                if (existingId is not null)
                {
                    await ReplaceExistingDashboardAsync(entry, converted, existingId, options.FolderId, ct);
                    return;
                }

                _logger.LogInformation(
                    "Dashboard '{Title}' not found in CX — creating new.", entry.GrafanaTitle);
            }

            await UploadDashboardAsync(entry, converted, options.FolderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Migration cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error migrating '{Title}'.", entry.GrafanaTitle);
            MarkFailed(entry, CheckpointStatus.FailedCritical, $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates against the live API before uploading, when the cx CLI is available. A
    /// dashboard the API would reject is failed here rather than sent and refused, so the
    /// reason lands in the report instead of an upload error. Warnings do not block.
    /// </summary>
    private async Task<bool> PassesCxCheckAsync(CheckpointEntry entry, JObject converted, CancellationToken ct)
    {
        if (_cxChecker is null || !_cxChecker.IsInstalled)
            return true;

        var result = await _cxChecker.CheckAsync(converted, ct);
        if (!result.Ran)
        {
            _logger.LogDebug("cx check skipped for '{Title}': {Reason}", entry.GrafanaTitle, result.SkipReason);
            return true;
        }

        foreach (var warning in result.Issues.Where(i => !i.IsError))
        {
            _logger.LogWarning("cx check warning on '{Title}' at {Location}: {Message}",
                entry.GrafanaTitle, warning.Location, warning.Message);
        }

        if (!result.HasErrors)
            return true;

        var errors = result.Issues.Where(i => i.IsError).ToList();
        var summary = string.Join("; ", errors.Take(3).Select(e => $"{e.Location}: {e.Message}"));
        if (errors.Count > 3)
            summary += $" (+{errors.Count - 3} more)";

        _logger.LogError("cx check rejected '{Title}': {Summary}", entry.GrafanaTitle, summary);
        MarkFailed(entry, CheckpointStatus.FailedCritical, $"Coralogix validation failed: {summary}");
        return false;
    }

    private string? FindExistingCxId(CheckpointEntry entry, JObject converted, string? folderId)
    {
        if (!string.IsNullOrEmpty(entry.CxDashboardId))
            return entry.CxDashboardId;

        if (_catalogCache is null)
            return null;

        var dashboardName = converted["name"]?.ToString();
        if (string.IsNullOrEmpty(dashboardName))
            return null;

        var match = _catalogCache.FirstOrDefault(item =>
            string.Equals(item.Name, dashboardName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

        return match?.Id;
    }

    private async Task ReplaceExistingDashboardAsync(
        CheckpointEntry entry,
        JObject converted,
        string existingId,
        string? folderId,
        CancellationToken ct)
    {
        var dashboardWithId = (JObject)converted.DeepClone();
        dashboardWithId["id"] = existingId;

        var success = await _cxClient.ReplaceDashboardAsync(dashboardWithId, _settings.Coralogix.IsLocked, ct: ct);

        if (success)
        {
            entry.Status = CheckpointStatus.Completed;
            entry.CxDashboardId = existingId;
            entry.ErrorMessage = null;
            entry.NextRetryAt = null;
            _logger.LogInformation("Dashboard '{Title}' replaced — CX ID: {CxId}.",
                entry.GrafanaTitle, existingId);
        }
        else
        {
            _logger.LogWarning(
                "Replace failed for dashboard '{Title}' ({Uid}) using CX ID '{CxId}'. Starting fallback create via upload.",
                entry.GrafanaTitle,
                entry.GrafanaUid,
                existingId);
            await UploadDashboardAsync(entry, converted, folderId, ct, isFallbackFromReplace: true, replaceTargetId: existingId);
        }
    }

    private async Task UploadDashboardAsync(
        CheckpointEntry entry,
        JObject converted,
        string? folderId,
        CancellationToken ct,
        bool isFallbackFromReplace = false,
        string? replaceTargetId = null)
    {
        var uploadResult = await _cxClient.UploadDashboardAsync(converted, _settings.Coralogix.IsLocked, folderId, ct);

        if (uploadResult.Success && uploadResult.DashboardId is not null)
        {
            entry.Status = CheckpointStatus.Completed;
            entry.CxDashboardId = uploadResult.DashboardId;
            entry.ErrorMessage = null;
            entry.NextRetryAt = null;

            if (isFallbackFromReplace)
            {
                _logger.LogInformation(
                    "Fallback create succeeded for dashboard '{Title}' ({Uid}) after replace target '{CxId}' failed. New CX ID: {NewCxId}.",
                    entry.GrafanaTitle,
                    entry.GrafanaUid,
                    replaceTargetId,
                    uploadResult.DashboardId);
            }
            else
            {
                _logger.LogInformation("Dashboard '{Title}' migrated — CX ID: {CxId}.",
                    entry.GrafanaTitle, uploadResult.DashboardId);
            }

            return;
        }

        ApplyUploadFailure(entry, uploadResult);
    }

    private void ApplyUploadFailure(CheckpointEntry entry, DashboardUploadResult uploadResult)
    {
        var error = uploadResult.ErrorMessage ?? "Unknown upload error";

        if (uploadResult.StatusCode.HasValue &&
            RetryPolicy.Classify(uploadResult.StatusCode.Value) == FailureKind.Critical)
        {
            MarkFailed(entry, CheckpointStatus.FailedCritical, error);
            return;
        }

        entry.RetryCount++;
        var delay = RetryPolicy.ComputeDelay(entry.RetryCount, _settings.Migration.InitialRetryDelaySeconds);
        MarkFailed(entry, CheckpointStatus.FailedRetryable, error);
        entry.NextRetryAt = DateTimeOffset.UtcNow.Add(delay);
        _logger.LogWarning("Dashboard '{Title}' failed (retryable). Next retry at {NextRetry}.",
            entry.GrafanaTitle, entry.NextRetryAt);
    }

    private bool ShouldSkipAsCompleted(CheckpointEntry? entry) =>
        !_settings.Coralogix.OverwriteExisting && entry?.Status == CheckpointStatus.Completed;

    private static bool ShouldSkipUntilRetry(CheckpointEntry? entry) =>
        entry?.Status == CheckpointStatus.FailedRetryable &&
        entry.NextRetryAt.HasValue &&
        entry.NextRetryAt.Value > DateTimeOffset.UtcNow;

    private static void MarkFailed(CheckpointEntry entry, CheckpointStatus status, string error)
    {
        entry.Status = status;
        entry.ErrorMessage = error;
    }

    private static MigrationReportEntry BuildReportEntry(
        string folderTitle,
        CheckpointEntry entry,
        IReadOnlyList<PanelConversionDiagnostic>? conversionDiagnostics = null,
        IReadOnlyList<DashboardConversionDiagnostic>? dashboardDiagnostics = null) =>
        new()
        {
            FolderTitle = folderTitle,
            DashboardTitle = entry.GrafanaTitle,
            Status = entry.Status,
            CxDashboardId = entry.CxDashboardId,
            ErrorMessage = entry.ErrorMessage,
            ConversionDiagnostics = conversionDiagnostics ?? [],
            DashboardDiagnostics = dashboardDiagnostics ?? []
        };
}
