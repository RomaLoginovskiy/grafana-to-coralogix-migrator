using System.Net;
using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.ApiClient;

/// <summary>
/// Writes a transformed dashboard to its destination, and reports what is already there.
/// </summary>
/// <remarks>
/// The port exists so <c>ImportOrchestrator</c>'s checkpoint / retry / report / catalog loop is written
/// once and serves both Coralogix custom dashboards and Coralogix-hosted Grafana. Everything
/// destination-specific — how "replace" differs from "create", which status codes mean what —
/// lives behind this interface.
/// </remarks>
public interface IDashboardPublisher
{
    /// <summary>Shown in log and console output, e.g. "Coralogix" or "Grafana".</summary>
    string TargetDisplayName { get; }

    /// <summary>
    /// Everything currently on the target, for name+folder identity. Called exactly once per run.
    /// </summary>
    Task<IReadOnlyList<TargetDashboard>> GetCatalogAsync(CancellationToken ct = default);

    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct = default);
}

/// <param name="Id">The target's own identifier — a Coralogix dashboard id, or a Grafana dashboard uid.</param>
public sealed record TargetDashboard(string Id, string Name, string? FolderId);

/// <param name="Dashboard">The transformed dashboard body, ready to send.</param>
/// <param name="StableId">
/// An identifier carried inside the payload that the target itself matches on — the Grafana uid.
/// Null for targets with no such concept, which must then be matched via <paramref name="ExistingTargetId"/>.
/// </param>
/// <param name="ExistingTargetId">
/// What the orchestrator's catalog snapshot believes is already there under this name and folder.
/// Advisory: a publisher whose target matches on <paramref name="StableId"/> may ignore it.
/// </param>
public sealed record PublishRequest(
    JObject Dashboard,
    string DashboardName,
    string? StableId,
    string? FolderId,
    string? ExistingTargetId,
    bool IsLocked);

/// <param name="StatusCode">
/// Carried so <see cref="RetryPolicy.Classify"/> can separate retryable from critical failures.
/// Null means the request never produced a response (network error) — treated as retryable.
/// </param>
/// <param name="RetryAfter">
/// A server-stated wait, from the <c>Retry-After</c> header. Preferred over the computed backoff, because
/// a target that says how long it wants to be left alone knows better than an exponential guess.
/// </param>
public sealed record PublishResult(
    bool Success,
    string? TargetId,
    HttpStatusCode? StatusCode,
    string? ErrorMessage,
    TimeSpan? RetryAfter = null)
{
    public static PublishResult Succeeded(string targetId) => new(true, targetId, null, null);

    public static PublishResult Failed(
        HttpStatusCode statusCode, string errorMessage, TimeSpan? retryAfter = null) =>
        new(false, null, statusCode, errorMessage, retryAfter);

    public static PublishResult NetworkError(string errorMessage) => new(false, null, null, errorMessage);
}
