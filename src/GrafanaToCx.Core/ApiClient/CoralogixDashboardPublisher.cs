using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Publishes to Coralogix custom dashboards: replace when the catalog already holds a matching
/// dashboard, otherwise create.
/// </summary>
/// <remarks>
/// The replace-then-create fallback lives here rather than in the orchestrator because it is a property
/// of this API — a stale dashboard id (deleted out of band, or moved) makes <c>ReplaceDashboard</c> fail
/// in a way that a create would recover from. Targets that match on a payload-carried id have no
/// equivalent sequence.
/// </remarks>
public sealed class CoralogixDashboardPublisher(
    ICoralogixDashboardsClient client,
    ILogger<CoralogixDashboardPublisher> logger) : IDashboardPublisher
{
    public string TargetDisplayName => "Coralogix";

    public async Task<IReadOnlyList<TargetDashboard>> GetCatalogAsync(CancellationToken ct = default)
    {
        var catalog = await client.GetCatalogItemsAsync(ct);
        return catalog.Select(item => new TargetDashboard(item.Id, item.Name, item.FolderId)).ToList();
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct = default)
    {
        if (request.ExistingTargetId is not null)
            return await ReplaceAsync(request, request.ExistingTargetId, ct);

        return await UploadAsync(request, ct);
    }

    private async Task<PublishResult> ReplaceAsync(
        PublishRequest request, string existingId, CancellationToken ct)
    {
        var dashboardWithId = (JObject)request.Dashboard.DeepClone();
        dashboardWithId["id"] = existingId;

        var success = await client.ReplaceDashboardAsync(
            dashboardWithId, request.IsLocked, request.FolderId, ct);

        if (success)
        {
            logger.LogInformation("Dashboard '{Name}' replaced — CX ID: {CxId}.",
                request.DashboardName, existingId);
            return PublishResult.Succeeded(existingId);
        }

        logger.LogWarning(
            "Replace failed for dashboard '{Name}' using CX ID '{CxId}'. Starting fallback create via upload.",
            request.DashboardName, existingId);

        var fallback = await UploadAsync(request, ct);

        if (fallback.Success && fallback.TargetId is not null)
        {
            logger.LogInformation(
                "Fallback create succeeded for dashboard '{Name}' after replace target '{CxId}' failed. New CX ID: {NewCxId}.",
                request.DashboardName, existingId, fallback.TargetId);
        }

        return fallback;
    }

    private async Task<PublishResult> UploadAsync(PublishRequest request, CancellationToken ct)
    {
        // UploadDashboardAsync rather than CreateDashboardAsync: the latter returns a bare string with no
        // status code, which makes retryable-vs-critical classification impossible.
        var result = await client.UploadDashboardAsync(
            request.Dashboard, request.IsLocked, request.FolderId, ct);

        if (result.Success && result.DashboardId is not null)
        {
            logger.LogInformation("Dashboard '{Name}' imported — CX ID: {CxId}.",
                request.DashboardName, result.DashboardId);
            return PublishResult.Succeeded(result.DashboardId);
        }

        return new PublishResult(false, null, result.StatusCode, result.ErrorMessage);
    }
}
